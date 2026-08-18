# Guía del Implementador — Sistema Municipal LPR/ANPR

> Audiencia: desarrolladores que van a levantar el entorno o construir las siguientes fases. Para el detalle de esquemas/contratos ver [TechnicalDocumentation.md](TechnicalDocumentation.md); para el razonamiento de diseño ver [ArchitectureGuide.md](ArchitectureGuide.md).

## 1. Requisitos previos

- .NET 9 SDK
- Docker Desktop (con WSL2/virtualización habilitada en Windows)
- `dotnet-ef` como herramienta global: `dotnet tool install --global dotnet-ef`

## 2. Levantar el entorno local

```bash
cd C:\Ric68\SistemaMunicipaLPR
docker compose up -d
```

Verifica que los 4 contenedores estén sanos (`docker ps` — SQL Server, Redis, RabbitMQ y Keycloak tienen healthcheck configurado).

## 3. Configurar Keycloak (una sola vez)

Entra a `http://localhost:8080` con `admin` / `Lpr#Dev_2026!` y:

1. **Crear el realm** `sistema-lpr` (debe coincidir exactamente con `Keycloak:Authority` en `src/Api.Web/appsettings.json`).
2. **Crear el cliente** `api-web` dentro de ese realm (debe coincidir con `Keycloak:Audience`).
3. **Crear los realm roles:** `SuperAdmin`, `SupervisorC4`, `OperadorC4`, `PatrullaMovil`, `AuditorForense`.
4. **Crear usuarios de prueba** y asignarles uno o más de esos roles (Users → tu usuario → Role mapping).

Sin este paso, cualquier llamada autenticada a `Api.Web` fallará la validación del JWT.

## 4. Aplicar las migraciones de base de datos

```bash
dotnet ef database update --project src/Api.Web --context LprDbContext
```

(`Program.cs` también llama `Database.Migrate()` automáticamente al arrancar — este paso manual es solo para adelantarlo o depurar.) La tabla de políticas de Casbin se crea sola (`EnsureCreated()`) y se siembra con la matriz de permisos por defecto en cada arranque.

## 5. Correr la API

```bash
dotnet run --project src/Api.Web
```

Prueba rápida sin token (debe dar 401): `GET https://localhost:{puerto}/api/blacklist`.
Con un token válido de Keycloak (`Authorization: Bearer <token>`) y un usuario con rol `OperadorC4` o superior, debe dar 200.

## 6. Cómo extender el sistema

### Agregar un nuevo evento
1. Definir el `record` en `Core.Contracts` (records inmutables, sin lógica).
2. Si el evento va en el camino caliente (< 300 ms), **no** incluir payloads grandes (imágenes, blobs) — usar una referencia y subir el contenido de forma asíncrona, como se hizo con `PlateReadEvent.ImageReference`.

### Agregar una tabla/entidad
1. POCO en `Core.Domain` (sin atributos de EF Core — todo el mapeo va en `LprDbContext.OnModelCreating` vía Fluent API).
2. Configurar en `Api.Web/Data/LprDbContext.cs`.
3. `dotnet ef migrations add <Nombre> --project src/Api.Web --context LprDbContext`.
4. Si la tabla es de alto volumen con fines analíticos/forenses, considerar un índice columnstore (ver el patrón ya usado para `LecturasHistoricas` — se agrega a mano con `migrationBuilder.Sql()` después de generar la migración, EF Core no tiene API fluida para esto).

### Proteger un nuevo endpoint
```csharp
[Authorize(Policy = "Casbin")]
[CasbinResource("<objeto>", "<accion>")]
public IActionResult MiAccion() => ...
```
Si el objeto/acción es nuevo, agregar las filas correspondientes por rol en `CasbinPolicySeeder.DefaultPolicies` (el seeder es idempotente, seguro de re-ejecutar).

## 7. Fase 2 — cache Redis + mensajería + SignalR (construida 2026-08-18, sin compilar/verificar)

**Importante:** este código se escribió sin poder compilarlo (el entorno donde se escribió no tenía SDK de .NET 9 ni acceso a NuGet) — el primer paso al retomar es `dotnet build` de la solución completa y arreglar lo que salga. Esto aplica doblemente a la migración a DotNetCore.CAP del mismo día (ver §8) — tampoco se pudo compilar del lado del asistente.

Lo que se construyó, y cómo encaja:

1. **`BlacklistCacheService`** (`IHostedService`, en `Service.Inference/BlacklistCacheService.cs`): carga inicial de `VehiculosRobados.Estado = Activo` al set de Redis `blacklist:active-plates` al arrancar (llave centralizada en `Redis/BlacklistRedisKeys.cs`); refresco delta cada 5 min como respaldo (reemplazo atómico vía transacción Redis `DEL`+`SADD`, para no dejar la cache vacía a medio refresh).
2. **`BlacklistEntryAddedConsumer`/`BlacklistEntryRemovedConsumer`** (`Service.Inference/Consumers/`): invalidación inmediata del set de Redis. Nada los dispara todavía — `BlacklistController` en `Api.Web` sigue siendo el stub de referencia (`Post() => Accepted()`), no persiste ni publica nada. Construir esa parte (alta/baja real de `VehiculoRobado` + publish del evento) queda fuera del alcance de Fase 2 tal como se definió.
3. **`PlateReadConsumer`** (`Service.Inference/Consumers/PlateReadConsumer.cs`, DotNetCore.CAP `ICapSubscribe`): un solo `SISMEMBER` contra Redis. Si hay match, publica `BlacklistHitSavedEvent` vía `ICapPublisher.PublishAsync(EventTopics.BlacklistHitSaved, ...)` (la notificación por SignalR y la persistencia corren como suscriptores *separados* de ese evento — ver siguiente punto). Si no hay match, por default no escribe nada a SQL — ver "Decisión: log solo de matches" abajo.
4. **`BlacklistHitPersistenceConsumer`** (`Service.Inference/Consumers/`) y **`AlertNotificationConsumer`** (`Api.Web/Consumers/`): dos suscriptores **independientes** de `BlacklistHitSavedEvent` (mismo topic `EventTopics.BlacklistHitSaved`, en `Core.Contracts/EventTopics.cs`), uno en cada servicio. Cada servicio se registra en CAP con su propio `DefaultGroupName` (`"service-inference"` en `Service.Inference/Program.cs`, `"api-web"` en `Api.Web/Program.cs`) — CAP entrega una copia del mensaje por grupo, así que ambos corren en paralelo sin bloquearse entre sí:
   - `BlacklistHitPersistenceConsumer` (Service.Inference): resuelve `CamaraId` (por `Codigo`) y `VehiculoRobadoId` (por `PlateText`), inserta `LecturaHistorica` + `Alerta` vía Dapper, deduplicando por `EventId` (atrapa `SqlException` 2601/2627 de violación del índice único).
   - `AlertNotificationConsumer` (Api.Web): empuja el mensaje `"AlertaBlacklist"` a todos los clientes de `AlertHub` vía `IHubContext<AlertHub>`.
5. **`AlertHub`** (`Api.Web/Hubs/AlertHub.cs`): SignalR, ruta `/hubs/alerts`, `[Authorize]` (requiere JWT válido, mismo mecanismo de `access_token` en query string que ya soportaba `Program.cs`). Backplane de Redis vía `AddStackExchangeRedis(...)`, ya cableado en `Program.cs`.

### Decisión: log solo de matches (configurable)

A pedido explícito del usuario (2026-08-18): `LecturasHistoricas` solo registra lecturas que **sí** coinciden con la blacklist — no se guarda cada lectura para evitar acumular información innecesaria. Esto es configurable en `src/Service.Inference/appsettings.json`:

```json
"PlateReadLogging": { "OnlyLogMatches": true }
```

`true` (default): `PlateReadConsumer` no toca SQL cuando no hay match; solo `BlacklistHitPersistenceConsumer` inserta `LecturaHistorica` (y solo para matches). `false`: `PlateReadConsumer` también inserta una `LecturaHistorica` por cada lectura sin match (`EsCoincidenciaBlacklist = 0`). **Nota:** esto deja el default real en contradicción con la descripción de la tabla en [TechnicalDocumentation.md §4](TechnicalDocumentation.md#4-modelo-de-datos-coredomain--apiwebdatalprdbcontextcs) ("log de cada lectura, match o no") — pendiente de alinear esa descripción, o de decidir si se documenta como "configurable, default: solo matches".

### Limpieza pendiente

`src/Service.Inference/Worker.cs` (el `BackgroundService` de plantilla que solo logueaba "Worker running at...") ya no está registrado en `Program.cs` — lo reemplazó `BlacklistCacheService`. El archivo se puede borrar, quedó como código muerto.

### Cómo probarlo manualmente: `tools/VerifyFase2`

Nada en el sistema publica `PlateReadEvent` todavía — el simulador de carga es Fase 3, y `BlacklistController` sigue siendo un stub que no publica `BlacklistEntryAddedEvent`/`RemovedEvent`. Para probar el camino completo de Fase 2 a mano se armó `tools/VerifyFase2` (proyecto consola desechable, mismo patrón que `tools/VerifyGeo` de Fase 1 — usa el `LprDbContext` real vía `ProjectReference` a `Api.Web.csproj`, y desde la migración a CAP, un `Host` mínimo con `AddCap(...)` propio para publicar contra el mismo RabbitMQ/SQL Server del `docker-compose.yml`).

**Nota post-migración a CAP:** a diferencia de MassTransit (publish directo al bus), CAP escribe primero el mensaje en su tabla de outbox transaccional (`cap.Published`, en la misma base `SistemaLPR`) y un dispatcher en background lo envía a RabbitMQ poco después. Por eso `PublishAsync` en este tool levanta el host, publica, espera ~5 segundos, y solo entonces lo detiene — si se detuviera de inmediato, el dispatcher no alcanzaría a correr y el mensaje quedaría pendiente en la tabla hasta que otro proceso con CAP configurado arrancara.

**Build (ya viene con `.csproj` y `Program.cs` listos, no hace falta `dotnet new`):**
```powershell
cd C:\Ric68\SistemaMunicipaLPR\tools\VerifyFase2
dotnet build
```

**Flujo de prueba** (con `docker compose up -d`, `dotnet run --project src\Api.Web` y `dotnet run --project src\Service.Inference` corriendo en sus propias ventanas):

1. `dotnet run -- seed` — crea la `Camara` (`Codigo = CAM-TEST-01`) y el `VehiculoRobado` de prueba (`PlateText = TEST1234`, `Estado = Activo`). Idempotente, seguro de re-correr.
2. Confirma en el log de `Service.Inference` que `BlacklistCacheService` ya cargó la placa (`"Blacklist cache refrescada: N placas activas."` con `N >= 1`) — si `Service.Inference` ya estaba corriendo antes del `seed`, reinícialo para forzar la carga inicial en vez de esperar el refresco delta de 5 min.
3. `dotnet run -- publish` — publica un `PlateReadEvent` con la placa de prueba (debe dar match). Verifica en orden:
   - Log de `PlateReadConsumer` (`Service.Inference`) reportando el match.
   - Log de `BlacklistHitPersistenceConsumer` (`Service.Inference`) — `"Alerta registrada..."` — y una fila nueva en `LecturasHistoricas`/`Alertas` en SQL.
   - Log de `AlertNotificationConsumer` (`Api.Web`) — `"Alerta empujada por SignalR..."` — y, si tienes un cliente SignalR conectado a `/hubs/alerts` con un token válido, el mensaje `"AlertaBlacklist"`.
4. `dotnet run -- publish-nomatch` — publica un `PlateReadEvent` con una placa que no está en la blacklist (`NOMATCH99`). Con `PlateReadLogging:OnlyLogMatches = true` (default), no debe generarse ninguna fila nueva en `LecturasHistoricas` ni ninguna alerta.
5. `dotnet run -- cleanup` — borra la `Camara`, el `VehiculoRobado` y cualquier `LecturaHistorica`/`Alerta` generada por las pruebas.

## 8. Notas operativas conocidas

- **`dotnet run --project src/Api.Web` (o `Service.Inference`) truena con `MassTransit.ConfigurationException: ... License must be specified with SetLicense/SetLicenseLocation or by setting the MT_LICENSE/MT_LICENSE_PATH environment variables`** (encontrado en la verificación real de Fase 2, 2026-08-18): `MassTransit.RabbitMQ` en su línea `9.x` (la que quedó pineada al construir Fase 2) exige configurar una licencia para poder arrancar el bus, incluso en local — cambio de modelo de negocio de MassTransit, no un bug de este proyecto.
  - **Primer intento (descartado):** bajar `MassTransit.RabbitMQ` a la última versión mayor 8.x (licencia MIT/Apache, sin este requerimiento). Llegó a aplicarse en ambos `.csproj`, pero el usuario decidió no depender de una serie de MassTransit sin soporte activo del proyecto y reemplazar la librería por completo — ver el punto siguiente.
  - **Decisión final tomada (2026-08-18): migración completa de MassTransit a DotNetCore.CAP.** CAP (MIT, sin licencia comercial) es la librería de mensajería usada de aquí en adelante en `Api.Web` y `Service.Inference`. Diferencias clave a tener en cuenta al tocar este código:
    - **Modelo de suscripción:** en vez de `IConsumer<T>` + `Consume(ConsumeContext<T>)`, cada consumer implementa el marcador `ICapSubscribe` y expone un método público (cualquier nombre — se usó `HandleAsync` por convención) decorado con `[CapSubscribe("<topic>")]`, recibiendo el mensaje deserializado directo como parámetro.
    - **Topics en vez de tipos:** CAP enruta por nombre de topic (`string`), no por el tipo .NET del mensaje como hacía MassTransit. Los nombres de topic viven centralizados en `Core.Contracts/EventTopics.cs` (`PlateRead`, `BlacklistHitSaved`, `BlacklistEntryAdded`, `BlacklistEntryRemoved`) — usar siempre esas constantes, tanto al publicar como al suscribir, para no desalinear el string a mano en dos lugares.
    - **Publish:** en vez de resolver `IPublishEndpoint`/`IBus` y llamar `Publish<T>(evt)`, se resuelve `ICapPublisher` (inyectado por DI) y se llama `PublishAsync(topic, evt)`.
    - **"Cola por servicio" → `DefaultGroupName`:** MassTransit le daba automáticamente su propia cola a cada consumer (vía `ConfigureEndpoints`), lo que permitía que `Service.Inference` y `Api.Web` recibieran cada uno su propia copia de `BlacklistHitSavedEvent`. CAP necesita esto explícito: cada servicio configura `x.DefaultGroupName = "..."` en su `AddCap(...)` (`"service-inference"` y `"api-web"` respectivamente) — si dos servicios comparten el mismo `DefaultGroupName` sobre el mismo topic, CAP los trata como competidores por el mismo mensaje (uno de los dos no lo recibe), no como suscriptores independientes.
    - **Outbox transaccional:** CAP requiere configurar un almacén de persistencia además del transporte — aquí `x.UseSqlServer(connectionString)` (la misma base `SistemaLPR`) para su tabla `cap.Published`/`cap.Received`, y `x.UseRabbitMQ(o => {...})` para el transporte. Un publish no sale directo al broker: se escribe primero en `cap.Published` dentro de la misma conexión/transacción, y un dispatcher en background lo envía a RabbitMQ poco después (típicamente sub-segundo, pero no instantáneo — importa para herramientas de prueba de vida corta, ver la nota sobre `tools/VerifyFase2` en §7). No se detectó (ni se esperaba) el mismo bug de `EnsureCreated()`/"la base ya tiene tablas" que afectó a `CasbinDbContext<int>` — CAP crea su propio esquema con su propio mecanismo de bootstrap, independiente del de EF Core.
    - **Registro en DI:** las clases con `[CapSubscribe]` deben registrarse explícitamente en el contenedor (`AddTransient<TConsumer>()`) para que CAP las descubra al arrancar — a diferencia de `AddConsumer<T>()` de MassTransit, `AddCap(...)` no las registra por sí solo.
    - **Paquetes:** se quitó `MassTransit.RabbitMQ` de ambos `.csproj` y se agregaron `DotNetCore.CAP`, `DotNetCore.CAP.RabbitMQ` y `DotNetCore.CAP.SqlServer`, todos `Version="8.*"` (flotante a propósito — no se pudo confirmar el número de patch exacto disponible al momento de escribir esto, sin acceso a NuGet del lado del asistente).
  - Archivos tocados por esta migración: `Core.Contracts/EventTopics.cs` (nuevo), los cuatro consumers de `Service.Inference/Consumers/`, `Service.Inference/Program.cs`, `Api.Web/Consumers/AlertNotificationConsumer.cs`, `Api.Web/Program.cs`, ambos `.csproj`, y `tools/VerifyFase2/Program.cs` (su función `PublishAsync` ahora levanta su propio `Host` con `AddCap(...)` en vez de un `IBusControl` de MassTransit standalone).
  - **Sin verificar:** ninguno de estos archivos se pudo compilar del lado del asistente (mismo problema de siempre — sin SDK/NuGet accesibles). Primer paso al retomar: `dotnet restore` + `dotnet build` de la solución completa, y luego `dotnet run --project src/Api.Web` y `dotnet run --project src/Service.Inference` para confirmar que ninguno truena al arrancar `AddCap(...)`.
- Este entorno de desarrollo (sandbox) no tuvo Docker disponible durante la construcción de Fase 0/1 — el `docker compose up -d` y la configuración de Keycloak **no se probaron de punta a punta durante la construcción**. Verificación en progreso desde el 2026-08-18 en una laptop con Docker Desktop (ver [fases.md](fases.md) para el estado exacto de qué ya se confirmó).
- Varios paquetes NuGet de Microsoft empezaron a publicar versiones que exigen `net10.0`; si `dotnet add package <algo-de-Microsoft>` falla con `NU1202`, buscar la última versión `9.0.x` explícita en vez de dejar que tome la última disponible (ver [TechnicalDocumentation.md §6](TechnicalDocumentation.md#6-paquetes-nuget-relevantes-y-notas-de-versión)).
- **`dotnet ef` falla con `Unable to retrieve project metadata. Ensure it's an SDK-style project.`** aunque el `.csproj` sea correcto (SDK-style, bien formado): este mensaje es genérico y engañoso — `dotnet-ef` dispara internamente un build de diseño del proyecto para leer su metadata, y si ese build falla por cualquier motivo, lo reporta siempre con este mismo texto en vez del error real.
  - **Causa más común:** desalineación de versiones entre el SDK de .NET instalado y el tool global `dotnet-ef`. `dotnet tool install --global dotnet-ef` sin fijar versión siempre toma la última publicada (por ejemplo, instala `10.x` aunque el proyecto sea `net9.0` y el SDK instalado sea `9.x`) — esa combinación no es soportada y el build de diseño falla silenciosamente.
  - **Diagnóstico:**
    ```bash
    dotnet --list-sdks   # confirmar que aparece una línea 9.x.x
    dotnet ef --version  # debe coincidir en versión mayor con el SDK y con Microsoft.EntityFrameworkCore.Design (9.0.19)
    ```
  - **Fix:** fijar el tool a la misma versión que `Microsoft.EntityFrameworkCore.Design`/`.Tools` en el `.csproj`:
    ```bash
    dotnet tool uninstall --global dotnet-ef
    dotnet tool install --global dotnet-ef --version 9.0.19
    ```
  - Confirmado en la verificación real del 2026-08-18: con SDK `9.x` y `dotnet-ef` en `10.x` fallaba con este error; al fijar `dotnet-ef` a `9.0.19` la migración `InitialLprSchema` aplicó limpio contra SQL Server.
- **`dotnet run --project src/Api.Web` falla al arrancar con `SqlException: Invalid object name 'casbin_rule'`** (justo después de "No migrations were applied. The database is already up to date." en el log): bug real de orden de arranque en `Program.cs`, encontrado en la verificación end-to-end del 2026-08-18, ya corregido en el código.
  - **Causa:** `CasbinDbContext<int>.Database.EnsureCreated()` solo crea su propio esquema cuando la base de datos física tiene **cero** tablas. Como `CasbinDbContext<int>` comparte la base `SistemaLPR` con `LprDbContext` (ver `appsettings.json`), si `LprDbContext.Database.Migrate()` corre primero y crea `Camaras`/`VehiculosRobados`/etc., `EnsureCreated()` ve que la base "ya tiene tablas" y **no crea** `casbin_rule` — el enforcer de Casbin truena al intentar cargar políticas en el primer arranque.
  - **Fix aplicado:** en `Program.cs` se invirtió el orden — `CasbinDbContext<int>.Database.EnsureCreated()` corre **antes** que `LprDbContext.Database.Migrate()`, así `EnsureCreated()` ve la base todavía vacía y sí crea `casbin_rule`.
  - **Importante — no es retroactivo:** si tu base ya quedó bootstrapeada con el orden viejo (le faltará `casbin_rule` aunque las demás tablas existan), el swap de orden por sí solo no la arregla. Hay que resetear una vez el volumen de SQL Server (no toca Redis/RabbitMQ/Keycloak, la config del realm no se pierde):
    ```powershell
    docker compose stop sqlserver
    docker compose rm -f sqlserver
    docker volume ls            # busca el volumen *_sqlserver_data
    docker volume rm <nombre_del_volumen_sqlserver_data>
    docker compose up -d sqlserver
    ```
    Después, `dotnet run --project src/Api.Web` reconstruye todo desde cero en el orden correcto.
