# Guía del Implementador — Sistema Municipal LPR/ANPR

> Audiencia: desarrolladores que van a levantar el entorno o construir las siguientes fases. Para el detalle de esquemas/contratos ver [TechnicalDocumentation.md](TechnicalDocumentation.md); para el razonamiento de diseño ver [ArchitectureGuide.md](ArchitectureGuide.md).

## 1. Requisitos previos

- .NET 9 SDK
- Docker Desktop (con WSL2/virtualización habilitada en Windows)
- `dotnet-ef` como herramienta global: `dotnet tool install --global dotnet-ef --version 9.0.19`

## 2. Levantar el entorno local

```bash
cd C:\Ric68\SistemaMunicipaLPR
docker compose up -d
```

Verifica que los 4 contenedores estén sanos (`docker ps` — MySQL, Redis, RabbitMQ y Keycloak tienen healthcheck configurado).

## 3. Configurar Keycloak (una sola vez)

Entra a `http://localhost:8080` con `admin` / `Lpr#Dev_2026!` y:

1. **Crear el realm** `sistema-lpr` (debe coincidir exactamente con `Keycloak:Authority` en `src/Api.Web/appsettings.json`).
2. **Crear el cliente** `api-web` dentro de ese realm (debe coincidir con `Keycloak:Audience`), con Client authentication y Direct access grants activados, y un mapper de Audience en `api-web-dedicated` apuntando a `api-web`.
3. **Crear los realm roles:** `SuperAdmin`, `SupervisorC4`, `OperadorC4`, `PatrullaMovil`, `AuditorForense`.
4. **Crear usuarios de prueba** y asignarles uno o más de esos roles (Users → tu usuario → Role mapping).

Sin este paso, cualquier llamada autenticada a `Api.Web` fallará la validación del JWT.

## 4. Aplicar las migraciones de base de datos

```bash
dotnet ef database update --project src/Api.Web --context LprDbContext
```

`Program.cs` también aplica las migraciones automáticamente al arrancar (este paso manual es solo para adelantarlo o depurar). La tabla de políticas de Casbin (`casbin_rule`) se crea sola (`EnsureCreated()`, antes de que corran las migraciones de `LprDbContext` — ambos contextos comparten la base `SistemaLPR`) y se siembra con la matriz de permisos por defecto en cada arranque.

**Nota sobre Fase 3.5:** el esquema de `LprDbContext` cambió al retirar el subsistema de blacklist propio (se eliminó `VehiculosRobados`, `Alertas.VehiculoRobadoId` pasó a `Alertas.RedListVehicleId`, ver §11) y la migración `InitialLprSchema` original se regeneró desde cero — este repo no trae ninguna migración pre-generada. `tools/setup-new-machine.ps1` la genera sola si no encuentra ninguna en `src/Api.Web/Migrations`. Si tu máquina ya tenía una base `SistemaLPR` de antes de este cambio, hay que reiniciar el volumen de MySQL una vez antes de que la migración regenerada aplique limpio — mismo procedimiento que la sección "`MySqlException: Table 'SistemaLPR.casbin_rule' doesn't exist`" en §8.

## 5. Correr la API

```bash
dotnet run --project src/Api.Web
dotnet run --project src/Service.Inference
```

Ambos deben arrancar sin errores contra la infraestructura del paso 2 (MySQL, Redis, RabbitMQ). `Api.Web` además crea `casbin_rule` y aplica las migraciones de `LprDbContext` al arrancar (ver §4). `Service.Inference` intenta conectarse al hub de SignalR de RedLists (`RedListCacheService`, ver §7) — si RedLists no está corriendo todavía el intento falla con un log de error, pero **no es fatal**: la caché sigue refrescándose completa cada 5 minutos como respaldo hasta que RedLists esté disponible.

**Nota:** hoy no hay ningún endpoint REST de negocio protegido por Casbin en `Api.Web` — Fase 4 (Frontend C4) todavía no empezó, y el único controller que existía (`BlacklistController`) se retiró en Fase 3.5 (ver §11). El único controller presente es `WeatherForecastController`, la plantilla default de ASP.NET Core, sin `[CasbinResource]`. Para probar el flujo Keycloak→Casbin en aislado antes de que exista un endpoint real de negocio, agrega temporalmente `[Authorize(Policy = "Casbin")]` + `[CasbinResource("<objeto>", "<accion>")]` a ese controller (ver §6, "Proteger un nuevo endpoint") y usa una de las filas ya sembradas en `CasbinPolicySeeder.DefaultPolicies` (p. ej. `("OperadorC4", "camaras", "read")`) para decidir qué objeto/acción probar. Para verificar el pipeline completo de matching de punta a punta — que sí funciona hoy end-to-end, sin pasar por REST — usa `tools/VerifyFase2` (§9).

## 6. Cómo extender el sistema

### Agregar un nuevo evento
1. Definir el `record` en `Core.Contracts` (records inmutables, sin lógica).
2. Decidir el camino de transporte (ver §7): eventos de bajo volumen van por DotNetCore.CAP (agregar su topic a `EventTopics.cs`); eventos de muy alto volumen y sin necesidad de durabilidad/retry van directo por RabbitMQ.Client (agregar su cola a `RawQueues.cs`).
3. Si el evento va en el camino caliente (< 300 ms), **no** incluir payloads grandes (imágenes, blobs) — usar una referencia y subir el contenido de forma asíncrona, como se hizo con `PlateReadEvent.ImageReference`.

### Agregar una tabla/entidad
1. POCO en `Core.Domain` (sin atributos de EF Core — todo el mapeo va en `LprDbContext.OnModelCreating` vía Fluent API).
2. Configurar en `Api.Web/Data/LprDbContext.cs`.
3. `dotnet ef migrations add <Nombre> --project src/Api.Web --context LprDbContext`.
4. Si la tabla es de alto volumen con fines analíticos/forenses, considerar un índice compuesto (ver el patrón ya usado para `LecturasHistoricas`: `HasIndex(l => new { l.TimestampUtc, l.PlateText })` en `LprDbContext.OnModelCreating`).

### Proteger un nuevo endpoint
```csharp
[Authorize(Policy = "Casbin")]
[CasbinResource("<objeto>", "<accion>")]
public IActionResult MiAccion() => ...
```
Si el objeto/acción es nuevo, agregar las filas correspondientes por rol en `CasbinPolicySeeder.DefaultPolicies` (el seeder es idempotente, seguro de re-ejecutar).

## 7. Arquitectura de mensajería: DotNetCore.CAP + RabbitMQ.Client + SignalR (cliente)

El sistema usa tres caminos distintos de integración por evento/dato, según el volumen y las garantías que necesita cada uno:

| | DotNetCore.CAP | RabbitMQ.Client directo | SignalR (cliente, hacia RedLists) |
|---|---|---|---|
| **Uso** | `BlacklistHitSavedEvent` | `PlateReadEvent` | Invalidación de `redlist:active-plates` en Redis |
| **Volumen** | Bajo | Alto (~500/seg agregado) | Bajo (solo altas/bajas/recuperaciones en RedLists) |
| **Garantías** | Outbox transaccional (`cap.Published`/`cap.Received` en MySQL), reintentos automáticos, idempotencia | Ninguna más allá de lo que da RabbitMQ (cola durable + ack manual) | Ninguna por sí sola — por eso se combina con un refresco delta completo cada 5 min (ver abajo) |
| **Por qué** | El costo por mensaje del outbox transaccional no es un problema a este volumen, y sí aporta durabilidad/reintentos reales | `PlateReadEvent` es una señal efímera de altísimo volumen, sin escritura local que necesite atomicidad con el publish — el costo del outbox de CAP no se justifica y no lo sostiene a este volumen | RedLists es un repo/deploy separado (no un topic propio de este sistema) — este servicio se conecta como cliente a SU hub, no publica ni suscribe nada por CAP/RabbitMQ para esto |

### Camino CAP (`BlacklistHitSavedEvent`)

- **Modelo de suscripción:** cada consumer implementa el marcador `ICapSubscribe` y expone un método público (por convención, `HandleAsync`) decorado con `[CapSubscribe("<topic>")]`, recibiendo el mensaje deserializado directo como parámetro.
- **Topics:** CAP enruta por nombre de topic (`string`), no por tipo .NET. Las constantes viven en `Core.Contracts/EventTopics.cs` — usar siempre esas constantes, tanto al publicar como al suscribir.
- **Publish:** se resuelve `ICapPublisher` (inyectado por DI) y se llama `PublishAsync(topic, evt)`.
- **`DefaultGroupName` por servicio:** `Service.Inference` y `Api.Web` suscriben ambos a `BlacklistHitSaved` y necesitan cada uno su propia copia del mensaje (uno persiste, el otro empuja por SignalR). Cada servicio configura `x.DefaultGroupName = "..."` en su `AddCap(...)` (`"service-inference"` y `"api-web"` respectivamente) — si dos servicios comparten el mismo `DefaultGroupName` sobre el mismo topic, CAP los trata como competidores por el mismo mensaje, no como suscriptores independientes.
- **Storage:** `x.UseMySql(connectionString)` (tablas `cap.Published`/`cap.Received` en la base `SistemaLPR`) para el outbox, `x.UseRabbitMQ(o => {...})` para el transporte.
- **Registro en DI:** las clases con `[CapSubscribe]` deben registrarse explícitamente (`AddTransient<TConsumer>()`) para que CAP las descubra al arrancar.
- **Paquetes:** `DotNetCore.CAP`, `DotNetCore.CAP.RabbitMQ`, `DotNetCore.CAP.MySql`, todos `Version="8.*"`.

**Nota histórica:** hasta Fase 3.5 este mismo camino CAP también transportaba `BlacklistEntryAddedEvent`/`BlacklistEntryRemovedEvent`, publicados por el `BlacklistController` propio de este repo cada vez que alguien daba de alta/baja un vehículo. Ese subsistema se retiró por completo (ver §11) — RedLists reemplaza esa función y este repo ya no publica ni suscribe esos dos eventos.

### Camino directo (`PlateReadEvent`)

- `PlateReadEvent` se publica y consume con `RabbitMQ.Client` puro, sin pasar por CAP — cola durable `plate-read-event.raw` (constante en `Core.Contracts/RawQueues.cs`).
- **`RabbitMQ.Client` resuelve en su serie 7.x** (no tiene `PackageReference` explícita — llega transitivamente vía `DotNetCore.CAP.RabbitMQ`), cuya API es async-only: `IChannel` en vez de `IModel`, y los métodos de publish/consumo/ack tienen sufijo `Async` (`CreateConnectionAsync`, `CreateChannelAsync`, `QueueDeclareAsync`, `BasicPublishAsync`, `BasicConsumeAsync`, `BasicAckAsync`/`BasicNackAsync`, `BasicQosAsync`). `BasicProperties` se instancia directo (`new BasicProperties()`, ya no `channel.CreateBasicProperties()`), y el mensaje persistente se marca con `DeliveryMode = DeliveryModes.Persistent`.
- **Publish:** abrir una `IConnection`/`IChannel`, declarar la cola (`durable: true`), serializar el evento a JSON (`System.Text.Json`) y `BasicPublishAsync`. Ver `tools/VerifyFase2/Program.cs` y `tools/LoadSimulator/Program.cs` como referencia.
- **Consumo:** `Service.Inference/Consumers/PlateReadConsumer.cs` es un `BackgroundService` normal (no un `[CapSubscribe]`) que abre su propio canal, configura `BasicQosAsync(prefetchCount: 100)` (necesario para throughput — sin esto RabbitMQ entrega un mensaje a la vez y espera el ack), y consume con `AsyncEventingBasicConsumer` (evento `ReceivedAsync`): deserializa el body, ejecuta el lookup contra `redlist:active-plates` en Redis (ver el camino SignalR abajo — este set lo mantiene `RedListCacheService`), y hace `BasicAckAsync`/`BasicNackAsync(requeue: true)` según el resultado. Si hay match, publica `BlacklistHitSavedEvent` — ese publish sí va por CAP (camino de bajo volumen). El cierre de `IChannel`/`IConnection` (ambos `IAsyncDisposable`) se hace en `StopAsync`, no en `Dispose()`.
- **Registro en DI:** se registra como `AddHostedService<PlateReadConsumer>()` (no `AddTransient` — no es un suscriptor de CAP).
- **Threading:** un `IChannel` (canal) de RabbitMQ.Client no es seguro para uso concurrente entre threads; una `IConnection` compartida sí permite `CreateChannelAsync()` concurrente. Si necesitas publicar desde múltiples tareas concurrentes, comparte la conexión y da un canal propio a cada tarea.
- **Nota para un publisher en Python (pipeline Edge, Fase 3):** al no pasar por CAP, publicar `PlateReadEvent` desde Python es un publish AMQP estándar (JSON plano a la cola `plate-read-event.raw`, con cualquier cliente como `pika`) — no hace falta replicar ningún envelope propio de CAP.

### Camino SignalR (cliente, invalidación de caché desde RedLists)

Un tercer camino, distinto de CAP y de RabbitMQ.Client directo: `Service.Inference/RedListCacheService.cs` es un `BackgroundService` que se conecta como **cliente de SignalR** al hub `/hubs/vehicle-lists` de `VehicleListsService.Api` (RedLists, repo separado — `C:\Ric68\RedLists`), para mantener el set de Redis `redlist:active-plates` sincronizado sin que el camino caliente de `PlateReadConsumer` tenga que tocar MySQL en cada lectura.

- **Doble mecanismo:** carga completa al arrancar + suscripción en vivo a los eventos `VehicleAdded`/`VehicleRemoved`/`VehicleRecovered` del hub, MÁS un refresco delta completo cada 5 minutos como respaldo (por si se pierde algún evento durante una caída momentánea de este servicio). Al reconectar tras una caída del hub, dispara un refresco completo (`_hubConnection.Reconnected += _ => RefreshAllAsync()`) en vez de confiar en que no se perdió ningún evento mientras estuvo desconectado.
- **Reconciliación por consulta, no por interpretación del evento:** RedLists no deduplica una placa entre filas (`vehicles`/`list_vehicles` puede tener varias filas para la misma placa) — así que tanto el refresco completo como cada evento puntual disparan la MISMA consulta: "¿sigue esta placa activa en alguna lista `list_type = 'Vehicles'` con `recovered_by_org_id = 0`?", y el resultado decide `SADD`/`SREM` en Redis. Es idempotente e independiente del orden de llegada de los eventos.
- **Config:** `RedLists:VehicleListsHubUrl` en `appsettings.json`/user-secrets de `Service.Inference` (default `http://localhost:5247/hubs/vehicle-lists`, coincide con el `launchSettings.json` de desarrollo de `VehicleListsService.Api`) — ajustar si RedLists corre en otro host/puerto.
- **No es fatal si RedLists no está corriendo al arrancar:** el intento de conexión al hub falla con un log de error (`"No se pudo conectar al hub de SignalR de RedLists..."`), pero el refresco delta de 5 min sigue funcionando en cuanto RedLists vuelva a estar disponible — no hace falta reiniciar `Service.Inference`.
- **Log a buscar cuando el refresco funciona:** `"RedList cache refrescada: {Count} placas activas."` — ver §9 para el flujo completo de verificación con `tools/VerifyFase2`.
- **Paquete:** `Microsoft.AspNetCore.SignalR.Client`, agregado a `Service.Inference.csproj` para esto (Api.Web usa el paquete equivalente de servidor, `Microsoft.AspNetCore.SignalR.StackExchangeRedis`, para su propio `AlertHub` — son roles distintos, no el mismo paquete).

## 8. Notas operativas conocidas

- **`dotnet ef` falla con `Unable to retrieve project metadata. Ensure it's an SDK-style project.`** aunque el `.csproj` sea correcto: este mensaje es genérico y engañoso — `dotnet-ef` dispara internamente un build de diseño del proyecto para leer su metadata, y si ese build falla por cualquier motivo, lo reporta siempre con este mismo texto en vez del error real.
  - **Causa más común:** desalineación de versiones entre el SDK de .NET instalado y el tool global `dotnet-ef`. `dotnet tool install --global dotnet-ef` sin fijar versión toma la última publicada, que puede ser mayor que el SDK instalado — esa combinación no es soportada.
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
- **Keycloak devuelve `invalid_grant: Account is not fully set up`** en un `password grant`: el usuario tiene una "required action" pendiente (típicamente porque la contraseña quedó marcada `Temporary`). Entra a `http://localhost:8080/realms/sistema-lpr/account/` e inicia sesión con ese usuario — Keycloak muestra en pantalla la acción exacta que falta completar; complétala ahí y reintenta el `password grant`.
- **`MySqlException: Table 'SistemaLPR.casbin_rule' doesn't exist`** al arrancar `Api.Web`: `CasbinDbContext<int>.Database.EnsureCreated()` solo crea su propio esquema cuando la base de datos física tiene **cero** tablas. Como `CasbinDbContext<int>` comparte la base `SistemaLPR` con `LprDbContext`, `EnsureCreated()` debe correr **antes** de `LprDbContext.Database.Migrate()` en `Program.cs` (ya está así en el código actual). Si una base ya quedó bootstrapeada en el orden incorrecto — o si vienes de antes de Fase 3.5 (ver la nota en §4) —, hay que resetear el volumen de MySQL una vez (no afecta a Redis/RabbitMQ/Keycloak):
  ```powershell
  docker compose stop mysql
  docker compose rm -f mysql
  docker volume ls            # busca el volumen *_mysql_data
  docker volume rm <nombre_del_volumen_mysql_data>
  docker compose up -d mysql
  ```
  Después, `dotnet run --project src/Api.Web` reconstruye todo desde cero en el orden correcto (y `tools/setup-new-machine.ps1` vuelve a aplicar `db/redlists-schema.sql`, ver §11).
- Varios paquetes NuGet de Microsoft publican versiones que exigen `net10.0`; si `dotnet add package <algo-de-Microsoft>` falla con `NU1202`, buscar la última versión `9.0.x` explícita en vez de dejar que tome la última disponible (ver [TechnicalDocumentation.md §6](TechnicalDocumentation.md#6-paquetes-nuget-relevantes-y-notas-de-versión)).
- `RabbitMQ.Client` no tiene `PackageReference` explícita en ningún `.csproj` — se resuelve transitivamente vía `DotNetCore.CAP.RabbitMQ`, en su serie 7.x (API async-only, ver §7). Si al compilar aparecen errores de overload en `BasicPublishAsync`/`BasicConsumeAsync`/`CreateChannelAsync` (nombres de parámetro o cantidad de argumentos), es la firma exacta de la versión de paquete resuelta — ajustar contra lo que sugiera el compilador/IntelliSense.

## 9. Herramientas de prueba: `tools/VerifyFase2` y `tools/LoadSimulator`

Ninguna de las dos está registrada en `SistemaLPR.sln` (convención del repo para herramientas desechables) — se compilan y corren desde su propia carpeta. Desde Fase 3.5, ninguna de las dos usa EF Core/`LprDbContext` para sembrar la placa de prueba — ambas siembran/limpian directo contra el esquema de RedLists con Dapper/MySqlConnector (que llegan transitivamente vía `Api.Web.csproj`, sin paquete nuevo), con el mismo patrón `EnsureRedListActiveAsync`/`RemoveFromRedListAsync` en las dos.

### `tools/VerifyFase2`

Verifica a mano el camino completo de Fase 2 (no hay todavía ningún publisher real de `PlateReadEvent` fuera de este tool, `tools/LoadSimulator` y el pipeline Edge; tampoco hay todavía un flujo real de alta/baja de vehículos en RedLists desde este repo — RedLists es un repo separado, este tool siembra directo contra su esquema SQL).

```powershell
cd C:\Ric68\SistemaMunicipaLPR\tools\VerifyFase2
dotnet build
```

Con `docker compose up -d`, `dotnet run --project src\Api.Web` y `dotnet run --project src\Service.Inference` corriendo en sus propias ventanas:

1. `dotnet run -- seed` — crea la `Camara` (`Codigo = CAM-TEST-01`) y activa la placa de prueba (`PlateText = TEST1234`) como miembro de una lista tipo `Vehicles` en RedLists (`vehicle_lists`/`vehicles`/`list_vehicles`, creando la lista `"VerifyFase2 test list"` si no existe). Idempotente — reutiliza la lista/vehículo si ya existen y reactiva la membresía si estaba marcada como recuperada por una corrida anterior.
2. Confirma en el log de `Service.Inference` que `RedListCacheService` ya cargó la placa (`"RedList cache refrescada: {Count} placas activas."` con `Count >= 1`) — si `Service.Inference` ya estaba corriendo antes del `seed`, reinícialo para forzar la carga inicial, espera hasta 5 min al próximo refresco delta, o confirma en el log que se conectó al hub de SignalR de RedLists (el `VehicleAdded` de este seed llega en vivo, ver §7).
3. `dotnet run -- publish` — publica un `PlateReadEvent` con la placa de prueba (debe dar match) directo a la cola `plate-read-event.raw`. Verifica en orden:
   - Log de `PlateReadConsumer` (`Service.Inference`) reportando el match.
   - Log de `BlacklistHitPersistenceConsumer` (`Service.Inference`, nombre de clase conservado — ver §11) y una fila nueva en `LecturasHistoricas`/`Alertas` en SQL (`Alertas.RedListVehicleId` apuntando al `vehicles.id` de RedLists).
   - Log de `AlertNotificationConsumer` (`Api.Web`) y, si tienes un cliente SignalR conectado a `/hubs/alerts` con un token válido, el mensaje `"AlertaRedList"`.
4. `dotnet run -- publish-nomatch` — publica un `PlateReadEvent` con una placa que no está activa en RedLists (`NOMATCH99`). Con `PlateReadLogging:OnlyLogMatches = true` (default), no debe generarse ninguna fila nueva.
5. `dotnet run -- cleanup` — borra la `Camara`, la membresía/vehículo de prueba en RedLists (borrado físico — correcto aquí porque es una placa de prueba, no un vehículo real; una baja real en RedLists solo marca `recovered_by_org_id`, nunca borra la fila), y cualquier `LecturaHistorica`/`Alerta` generada por las pruebas.

### `tools/LoadSimulator`

Genera carga sintética contra el mismo RabbitMQ/CAP que usan `Api.Web`/`Service.Inference`, para medir el camino caliente real (Redis lookup → persistencia → SignalR) sin depender del pipeline Edge de Python.

```powershell
cd C:\Ric68\SistemaMunicipaLPR\tools\LoadSimulator
dotnet build
```

- `dotnet run -- seed [--cameras N]` — siembra `N` cámaras (`CAM-SIM-0001`..`CAM-SIM-NNNN`, default 50) y activa la placa `SIMHIT001` en RedLists (lista `"LoadSimulator test list"`), mismo mecanismo idempotente que `tools/VerifyFase2`.
- `dotnet run -- run [--cameras N] [--rate R] [--duration S] [--match-ratio P] [--drain-seconds S]` — publica `PlateReadEvent` real directo a RabbitMQ, con `N` "cámaras" concurrentes publicando a `R` lecturas/seg cada una (defaults: `N=50`, `R=10` → ~500 eventos/seg agregados). Una fracción `P` (default `0.01`) de las lecturas de cada cámara usa la placa `SIMHIT001` para generar match real; el resto usa placas aleatorias `SIM<hex>` sin match. Sin `--duration` corre hasta Ctrl+C. Al detenerse, espera `--drain-seconds` (default `15`) con su suscriptor de latencia todavía activo antes de imprimir el resumen final, para no dejar hits en tránsito sin medir.
- `dotnet run -- cleanup [--cameras N]` — borra las cámaras sembradas, la membresía/vehículo de prueba `SIMHIT001` en RedLists (borrado físico, mismo criterio que `tools/VerifyFase2`), y cualquier `LecturaHistorica`/`Alerta` cuya `PlateText` empiece con `SIM`.

**Cómo mide la latencia cámara→alerta:** el tool se suscribe a `BlacklistHitSavedEvent` vía CAP (`HitLatencyConsumer`, `DefaultGroupName = "load-simulator"` — no compite con `service-inference` ni `api-web`, cada uno recibe su copia). Al publicar una lectura con la placa caliente, guarda `EventId → hora de publish`; cuando llega el `BlacklistHitSavedEvent` correspondiente, calcula la diferencia — ese número es el presupuesto de <300ms de Fase 3, medido end-to-end. Este mecanismo no cambió en Fase 3.5 — `BlacklistHitSavedEvent` es uno de los eventos que se conservó (ver §7).

**Por qué solo se mide latencia del 1% con match:** con `PlateReadLogging:OnlyLogMatches=true` (default), el resto de las lecturas nunca tocan SQL — para esas, lo relevante es que RabbitMQ no acumule backlog (visible en `http://localhost:15672`), una métrica de throughput distinta que hay que revisar a mano durante el `run`.

**Nota sobre el outbox de CAP bajo carga sintética:** este tool sigue usando CAP (con `UseMySql`) para su propio suscriptor de `BlacklistHitSavedEvent` — a la escala de eventos de blacklist (1% del tráfico) esto no genera carga significativa en `cap.Published`/`cap.Received`. Si se aumenta mucho `--match-ratio`, considerar limpiar esas tablas manualmente de vez en cuando (`cleanup` no las toca).

## 10. Pipeline Edge (Python) — `edge/`

Proceso Python independiente del backend .NET — corre en el nodo Edge junto a cada cámara física (un Jetson Orin Nano u otro PC por instalación, ver [ArchitectureGuide.md §6](ArchitectureGuide.md#6-stack-tecnológico)). Un proceso = una cámara. Estructura:

```
edge/
  requirements.txt
  config.example.yaml      # copiar a config.yaml y ajustar
  models/                  # colocar aquí el .pt del detector de placas (ver nota abajo)
  src/
    config.py              # carga y valida config.yaml
    events.py               # PlateReadEvent — espejo exacto del record de Core.Contracts
    capture.py               # OpenCV: lectura de frames + ráfaga/selección por nitidez
    detector.py               # YOLO (ultralytics): detección de la placa en el frame
    ocr.py                     # PaddleOCR: texto + confianza a partir del recorte de la placa
    buffer.py                   # cola local SQLite para tolerancia a cortes de red
    publisher.py                 # publish AMQP directo (pika) a la cola cruda de RabbitMQ
    main.py                       # orquesta el loop completo
```

**Instalación y ejecución:**

Requiere **Python 3.11** específicamente para el entorno virtual de `edge/` — `paddlepaddle` (dependencia de `paddleocr`) no publica wheels para versiones de Python muy recientes (confirmado: falla con `(from versions: none)` en Python 3.14), y 3.11 es la versión con mejor soporte actual en todo el stack (`ultralytics`, `paddleocr`/`paddlepaddle`, `opencv-python`). En Windows, si tu Python por default es otra versión, usa el lanzador `py` para crear el venv con la versión correcta:

```powershell
cd edge
py -3.11 -m venv venv
venv\Scripts\Activate.ps1
cp config.example.yaml config.yaml   # ajustar camera.id, camera.source, credenciales de RabbitMQ, etc.
pip install -r requirements.txt
python -m src.main
```

**Flujo por frame:** se lee un frame de la cámara (con `frame_skip` para no correr el detector en cada frame); si YOLO detecta una placa por encima de `detector.min_confidence`, se capturan `burst_size` frames adicionales en rápida sucesión y se elige el más nítido (mayor varianza del Laplaciano) antes de recortar (con un margen de `detector.crop_padding` píxeles alrededor del bbox, para no cortar el borde de algún carácter) — mitigación de motion blur a velocidades altas, el riesgo específico que señala [ArchitectureGuide.md §1](ArchitectureGuide.md#1-visión-general) para avenidas de hasta 160 km/h. Sobre ese recorte nítido corre PaddleOCR (con corrección de ángulo, `use_angle_cls=True` — útil para placas fotografiadas en ángulo desde el arco/avenida); si la confianza del texto reconocido supera `ocr.min_confidence` (y, opcionalmente, si coincide con `ocr.plate_pattern` cuando ese filtro está activo), se arma el `PlateReadEvent` y se intenta publicar. Un cooldown por placa (`plate_cooldown_seconds`) evita publicar el mismo vehículo varias veces mientras cruza el campo de visión de la cámara.

**Por qué PaddleOCR y no EasyOCR:** mismo alfabeto latino A-Z0-9 para placas mexicanas — no hay diferencia real de "idioma" entre ambos motores para este caso. Se eligió PaddleOCR por su tolerancia reportada a ruido/blur en video de vigilancia. Costo real a tener en cuenta: el proceso Edge ahora carga dos frameworks de ML distintos en memoria (PyTorch vía `ultralytics` para el detector, PaddlePaddle vía `paddleocr` para el texto) — más huella de RAM/disco en el Jetson que si ambas etapas compartieran framework. Si eso resulta un problema en hardware real, `ocr.py` es la única pieza que habría que volver a cambiar (misma interfaz pública `PlateOcr.read()`, así que el resto del pipeline no se ve afectado). `requirements.txt` fija `paddleocr<3` a propósito — la serie 3.x reescribió buena parte de la API pública (`.predict()` en vez de `.ocr()`), y este código está escrito contra la clásica 2.x, sin poder verificar contra el paquete real instalado (sin acceso a PyPI desde donde se escribió).

**Modelo de detección de placas:** se usa un YOLOv8 público ya entrenado para detección de placas (una sola clase), copiado a `edge/models/plate_detector.pt` — probado visualmente contra fotos reales de placas mexicanas y confirmado que detecta bien, sin necesidad de afinarlo. Detectar dónde está una placa depende poco del país (es un rectángulo con cierta proporción); lo que sí es específico de México (colores/diseños por estado, formato de dos líneas en motos) solo importaría si más adelante aparecen casos donde este modelo falle y haga falta afinarlo con datos propios.

**Publish:** igual que el lado .NET, `PlateReadEvent` viaja como JSON plano (AMQP puro, vía `pika`) a la cola durable `plate-read-event.raw` — sin ningún envelope de CAP que replicar (esa es la simplificación que dejó el cambio de diseño de Fase 3, ver [ArchitectureGuide.md §3](ArchitectureGuide.md#3-arquitectura-de-eventos-y-mensajería)). Los nombres de campo del JSON (`events.py`) deben coincidir EXACTO en casing con el record de C# — `System.Text.Json` deserializa sin `PropertyNameCaseInsensitive`.

**Tolerancia a cortes de red:** si el publish directo falla, el evento se guarda en un buffer local SQLite (`buffer.db`) en vez de perderse; un hilo de fondo lo drena hacia RabbitMQ en cuanto la conexión vuelve, en el mismo orden en que se generaron los eventos.

**Probar sin cámara IP física:** ver [vlcTests.md](vlcTests.md) — VLC puede emitir un video de prueba como stream RTSP, que `camera.source` consume igual que una cámara real, sin necesidad de esperar a un despliegue de campo.

**Gaps conocidos, sin resolver todavía:**
- **No incluye un modelo de detección de placas.** Un YOLO preentrenado en COCO no tiene clase de "placa" — hace falta conseguir un modelo público ya entrenado para esto o entrenar uno propio, y colocarlo en `edge/models/`. Sin ese archivo, `main.py` se niega a arrancar (falla rápido con un mensaje explícito en vez de correr sin detectar nada).
- **Sin uploader de imágenes a un storage central.** Los recortes de placa se guardan solo en disco local del nodo Edge (`images.save_dir`); `ImageReference` apunta a esa ruta local. No hay todavía ninguna pieza que suba esas imágenes a un storage accesible desde el backend/dashboard — hace falta diseñarla y construirla antes de que `ImageReference` sea útil fuera del propio nodo Edge.
- **Despliegue en Jetson:** `pip install ultralytics` en un entorno genérico trae wheels de PyTorch que no aprovechan la GPU del Jetson — en Jetson real hace falta instalar primero el PyTorch específico de NVIDIA para la versión de JetPack instalada (ver comentario en `requirements.txt`).
- **Sin verificar contra una cámara IP física ni un Jetson real todavía.** Ya está verificado end-to-end contra un video de prueba y, más recientemente, contra un stream RTSP simulado con VLC (ver [vlcTests.md](vlcTests.md)) — ambos corriendo en una máquina de desarrollo normal, sin GPU dedicada ni el hardware Jetson objetivo. Falta la verificación final contra una cámara IP física y el nodo Jetson desplegado en campo.

## 11. Integración con RedLists (Fase 3.5)

Hasta Fase 3.5, este repo tenía su propio subsistema de "lista negra" (`VehiculoRobado`, `BlacklistController`, importación por API externa/Excel/`.txt` en `Api.Web/Services/Blacklist/`). Ese subsistema se **retiró por completo**: RedLists (`VehicleListsService` + `ExternalVehicleFeedService`, repo separado en `C:\Ric68\RedLists`) hace ese mismo trabajo y pasa a ser la única fuente de verdad de vehículos reportados — ver la discusión de alcance y la decisión de terminología ("blacklist"/"lista negra" → "RedList"/"lista roja" en documentación y nombres nuevos) archivada en `docs/fases.md`, sección Fase 3.5.

### Qué queda en este repo

- `Alertas.RedListVehicleId` (`long`, **sin FK real**) reemplaza a `Alertas.VehiculoRobadoId` — apunta a `vehicles.id` de RedLists, un esquema que este `DbContext` no administra ni migra (ver el comentario XML en `Core.Domain/Alerta.cs` y el comentario en `LprDbContext.OnModelCreating`).
- `Service.Inference/RedListCacheService.cs` mantiene la caché de Redis sincronizada con RedLists (ver §7 — camino SignalR).
- `Service.Inference/Consumers/BlacklistHitPersistenceConsumer.cs` (nombre de clase conservado a propósito, sigue siendo la persistencia de un "hit de blacklist" — ver §7) resuelve el `vehicles.id` de RedLists por placa al registrar cada `Alerta`.
- Las políticas Casbin del recurso `blacklist` se quitaron del seeder — no queda ningún endpoint que las use.

### Cómo corre RedLists localmente

RedLists **no** se clona ni se referencia como proyecto desde este repo — es un repo hermano independiente, sin `ProjectReference` cruzado. Ambos sistemas se integran únicamente por:
1. Una base de datos MySQL física compartida (`SistemaLPR`).
2. El hub de SignalR de `VehicleListsService.Api` (`/hubs/vehicle-lists`), al que `Service.Inference` se conecta como cliente (§7).

`tools/setup-new-machine.ps1` de este repo automatiza la parte que le corresponde a SistemaMunicipaLPR:
- Aplica `db/redlists-schema.sql` (SQL directo — RedLists no usa migraciones de EF Core para su propio esquema: `vehicle_lists`/`vehicles`/`list_vehicles`/`adapters`/`sync_runs`/`imported_vehicles_log`) contra la base `SistemaLPR`, con `IF NOT EXISTS`/`ON DUPLICATE KEY` — idempotente, seguro de re-correr.
- Si el repo RedLists está clonado como carpeta hermana (`..\RedLists`, mismo layout que en la máquina original), configura los `user-secrets` de `VehicleListsService.Api` y `ExternalVehicleFeedService.Api` para que apunten a `SistemaLPR` en vez de a la base `redlists` separada que usaban antes de esta fase. Si RedLists todavía no está clonado ahí, este paso se omite con un aviso — vuelve a correr el script una vez que lo clones.

Para correr `VehicleListsService.Api` (necesario para que `Service.Inference` tenga algo a lo que conectarse por SignalR) y todo lo demás específico de RedLists — su propio setup, su WPF client, `ExternalVehicleFeedService` — ver `docs/Guia_Implementador_RedLists.md` **dentro del repo RedLists**, no duplicado aquí.

### Cómo dar de alta/baja un vehículo de prueba sin la UI de RedLists

Como este repo ya no tiene un `BlacklistController` propio para altas/bajas manuales, `tools/VerifyFase2` y `tools/LoadSimulator` (§9) siembran directo contra el esquema de RedLists con Dapper/MySqlConnector — el mismo patrón sirve para cualquier prueba manual:

```sql
-- Alta (o reactivación) de una placa en una lista tipo 'Vehicles' (RedList):
INSERT INTO vehicle_lists (global_id, name, list_type, creating_user, last_modifying_user)
VALUES (UUID(), 'Mi lista de prueba', 'Vehicles', 0, 0);   -- omitir si la lista ya existe

INSERT INTO vehicles (plate_number) VALUES ('ABC1234');     -- omitir si la placa ya existe

INSERT INTO list_vehicles (list_id, vehicle_id, recovered_by_org_id)
VALUES (@listId, @vehicleId, 0)
ON DUPLICATE KEY UPDATE recovered_by_org_id = 0, recovered_date = NULL, recovered_by_text = NULL;

-- Baja real (RedLists nunca borra la fila, solo marca recuperado):
UPDATE list_vehicles SET recovered_by_org_id = @orgId, recovered_date = NOW()
WHERE list_id = @listId AND vehicle_id = @vehicleId;
```

Ver `EnsureRedListActiveAsync`/`RemoveFromRedListAsync` en `tools/VerifyFase2/Program.cs` para la versión completa con Dapper (incluye el lookup idempotente de lista/vehículo existentes). El borrado físico (`DELETE`, en vez del `UPDATE` de baja real de arriba) solo es correcto para datos de prueba sintéticos — así es como `cleanup` de ambos tools limpian después de correr.
