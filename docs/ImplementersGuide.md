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

Verifica que los 4 contenedores estén sanos (`docker ps` — SQL Server, Redis, RabbitMQ y Keycloak tienen healthcheck configurado).

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

## 5. Correr la API

```bash
dotnet run --project src/Api.Web
dotnet run --project src/Service.Inference
```

Prueba rápida sin token (debe dar 401): `GET https://localhost:{puerto}/api/blacklist`.
Con un token válido de Keycloak (`Authorization: Bearer <token>`) y un usuario con rol `OperadorC4` o superior, debe dar 200.

## 6. Cómo extender el sistema

### Agregar un nuevo evento
1. Definir el `record` en `Core.Contracts` (records inmutables, sin lógica).
2. Decidir el camino de transporte (ver §7): eventos de bajo volumen van por DotNetCore.CAP (agregar su topic a `EventTopics.cs`); eventos de muy alto volumen y sin necesidad de durabilidad/retry van directo por RabbitMQ.Client (agregar su cola a `RawQueues.cs`).
3. Si el evento va en el camino caliente (< 300 ms), **no** incluir payloads grandes (imágenes, blobs) — usar una referencia y subir el contenido de forma asíncrona, como se hizo con `PlateReadEvent.ImageReference`.

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

## 7. Arquitectura de mensajería: DotNetCore.CAP + RabbitMQ.Client

El sistema usa dos caminos de transporte de eventos, según el volumen y las garantías que necesita cada uno:

| | DotNetCore.CAP | RabbitMQ.Client directo |
|---|---|---|
| **Eventos** | `BlacklistHitSavedEvent`, `BlacklistEntryAddedEvent`, `BlacklistEntryRemovedEvent` | `PlateReadEvent` |
| **Volumen** | Bajo | Alto (~500/seg agregado) |
| **Garantías** | Outbox transaccional (`cap.Published`/`cap.Received` en SQL Server), reintentos automáticos, idempotencia | Ninguna más allá de lo que da RabbitMQ (cola durable + ack manual) |
| **Por qué** | El costo por mensaje del outbox transaccional no es un problema a este volumen, y sí aporta durabilidad/reintentos reales | `PlateReadEvent` es una señal efímera de altísimo volumen, sin escritura local que necesite atomicidad con el publish — el costo del outbox de CAP no se justifica y no lo sostiene a este volumen |

### Camino CAP (`BlacklistHitSavedEvent` y eventos de blacklist)

- **Modelo de suscripción:** cada consumer implementa el marcador `ICapSubscribe` y expone un método público (por convención, `HandleAsync`) decorado con `[CapSubscribe("<topic>")]`, recibiendo el mensaje deserializado directo como parámetro.
- **Topics:** CAP enruta por nombre de topic (`string`), no por tipo .NET. Las constantes viven en `Core.Contracts/EventTopics.cs` — usar siempre esas constantes, tanto al publicar como al suscribir.
- **Publish:** se resuelve `ICapPublisher` (inyectado por DI) y se llama `PublishAsync(topic, evt)`.
- **`DefaultGroupName` por servicio:** `Service.Inference` y `Api.Web` suscriben ambos a `BlacklistHitSaved` y necesitan cada uno su propia copia del mensaje (uno persiste, el otro empuja por SignalR). Cada servicio configura `x.DefaultGroupName = "..."` en su `AddCap(...)` (`"service-inference"` y `"api-web"` respectivamente) — si dos servicios comparten el mismo `DefaultGroupName` sobre el mismo topic, CAP los trata como competidores por el mismo mensaje, no como suscriptores independientes.
- **Storage:** `x.UseSqlServer(connectionString)` (tablas `cap.Published`/`cap.Received` en la base `SistemaLPR`) para el outbox, `x.UseRabbitMQ(o => {...})` para el transporte.
- **Registro en DI:** las clases con `[CapSubscribe]` deben registrarse explícitamente (`AddTransient<TConsumer>()`) para que CAP las descubra al arrancar.
- **Paquetes:** `DotNetCore.CAP`, `DotNetCore.CAP.RabbitMQ`, `DotNetCore.CAP.SqlServer`, todos `Version="8.*"`.

### Camino directo (`PlateReadEvent`)

- `PlateReadEvent` se publica y consume con `RabbitMQ.Client` puro, sin pasar por CAP — cola durable `plate-read-event.raw` (constante en `Core.Contracts/RawQueues.cs`).
- **`RabbitMQ.Client` resuelve en su serie 7.x** (no tiene `PackageReference` explícita — llega transitivamente vía `DotNetCore.CAP.RabbitMQ`), cuya API es async-only: `IChannel` en vez de `IModel`, y los métodos de publish/consumo/ack tienen sufijo `Async` (`CreateConnectionAsync`, `CreateChannelAsync`, `QueueDeclareAsync`, `BasicPublishAsync`, `BasicConsumeAsync`, `BasicAckAsync`/`BasicNackAsync`, `BasicQosAsync`). `BasicProperties` se instancia directo (`new BasicProperties()`, ya no `channel.CreateBasicProperties()`), y el mensaje persistente se marca con `DeliveryMode = DeliveryModes.Persistent`.
- **Publish:** abrir una `IConnection`/`IChannel`, declarar la cola (`durable: true`), serializar el evento a JSON (`System.Text.Json`) y `BasicPublishAsync`. Ver `tools/VerifyFase2/Program.cs` y `tools/LoadSimulator/Program.cs` como referencia.
- **Consumo:** `Service.Inference/Consumers/PlateReadConsumer.cs` es un `BackgroundService` normal (no un `[CapSubscribe]`) que abre su propio canal, configura `BasicQosAsync(prefetchCount: 100)` (necesario para throughput — sin esto RabbitMQ entrega un mensaje a la vez y espera el ack), y consume con `AsyncEventingBasicConsumer` (evento `ReceivedAsync`): deserializa el body, ejecuta el lookup de blacklist en Redis, y hace `BasicAckAsync`/`BasicNackAsync(requeue: true)` según el resultado. Si hay match, publica `BlacklistHitSavedEvent` — ese publish sí va por CAP (camino de bajo volumen). El cierre de `IChannel`/`IConnection` (ambos `IAsyncDisposable`) se hace en `StopAsync`, no en `Dispose()`.
- **Registro en DI:** se registra como `AddHostedService<PlateReadConsumer>()` (no `AddTransient` — no es un suscriptor de CAP).
- **Threading:** un `IChannel` (canal) de RabbitMQ.Client no es seguro para uso concurrente entre threads; una `IConnection` compartida sí permite `CreateChannelAsync()` concurrente. Si necesitas publicar desde múltiples tareas concurrentes, comparte la conexión y da un canal propio a cada tarea.
- **Nota para un publisher en Python (pipeline Edge, Fase 3):** al no pasar por CAP, publicar `PlateReadEvent` desde Python es un publish AMQP estándar (JSON plano a la cola `plate-read-event.raw`, con cualquier cliente como `pika`) — no hace falta replicar ningún envelope propio de CAP.

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
- **`SqlException: Invalid object name 'casbin_rule'`** al arrancar `Api.Web`: `CasbinDbContext<int>.Database.EnsureCreated()` solo crea su propio esquema cuando la base de datos física tiene **cero** tablas. Como `CasbinDbContext<int>` comparte la base `SistemaLPR` con `LprDbContext`, `EnsureCreated()` debe correr **antes** de `LprDbContext.Database.Migrate()` en `Program.cs` (ya está así en el código actual). Si una base ya quedó bootstrapeada en el orden incorrecto, hay que resetear el volumen de SQL Server una vez (no afecta a Redis/RabbitMQ/Keycloak):
  ```powershell
  docker compose stop sqlserver
  docker compose rm -f sqlserver
  docker volume ls            # busca el volumen *_sqlserver_data
  docker volume rm <nombre_del_volumen_sqlserver_data>
  docker compose up -d sqlserver
  ```
  Después, `dotnet run --project src/Api.Web` reconstruye todo desde cero en el orden correcto.
- Varios paquetes NuGet de Microsoft publican versiones que exigen `net10.0`; si `dotnet add package <algo-de-Microsoft>` falla con `NU1202`, buscar la última versión `9.0.x` explícita en vez de dejar que tome la última disponible (ver [TechnicalDocumentation.md §6](TechnicalDocumentation.md#6-paquetes-nuget-relevantes-y-notas-de-versión)).
- `RabbitMQ.Client` no tiene `PackageReference` explícita en ningún `.csproj` — se resuelve transitivamente vía `DotNetCore.CAP.RabbitMQ`, en su serie 7.x (API async-only, ver §7). Si al compilar aparecen errores de overload en `BasicPublishAsync`/`BasicConsumeAsync`/`CreateChannelAsync` (nombres de parámetro o cantidad de argumentos), es la firma exacta de la versión de paquete resuelta — ajustar contra lo que sugiera el compilador/IntelliSense.

## 9. Herramientas de prueba: `tools/VerifyFase2` y `tools/LoadSimulator`

Ninguna de las dos está registrada en `SistemaLPR.sln` (convención del repo para herramientas desechables) — se compilan y corren desde su propia carpeta.

### `tools/VerifyFase2`

Verifica a mano el camino completo de Fase 2 (no hay todavía ningún publisher real de `PlateReadEvent` fuera de este tool, `tools/LoadSimulator` y el pipeline Edge — el alta/baja real de `VehiculoRobado` sí está implementada, en `BlacklistController` (`POST`/`DELETE /api/blacklist`), ver [TechnicalDocumentation.md §5](TechnicalDocumentation.md#5-autenticación-y-autorización)).

```powershell
cd C:\Ric68\SistemaMunicipaLPR\tools\VerifyFase2
dotnet build
```

Con `docker compose up -d`, `dotnet run --project src\Api.Web` y `dotnet run --project src\Service.Inference` corriendo en sus propias ventanas:

1. `dotnet run -- seed` — crea la `Camara` (`Codigo = CAM-TEST-01`) y el `VehiculoRobado` de prueba (`PlateText = TEST1234`, `Estado = Activo`). Idempotente.
2. Confirma en el log de `Service.Inference` que `BlacklistCacheService` ya cargó la placa (`"Blacklist cache refrescada: N placas activas."` con `N >= 1`) — si `Service.Inference` ya estaba corriendo antes del `seed`, reinícialo para forzar la carga inicial.
3. `dotnet run -- publish` — publica un `PlateReadEvent` con la placa de prueba (debe dar match) directo a la cola `plate-read-event.raw`. Verifica en orden:
   - Log de `PlateReadConsumer` (`Service.Inference`) reportando el match.
   - Log de `BlacklistHitPersistenceConsumer` (`Service.Inference`) y una fila nueva en `LecturasHistoricas`/`Alertas` en SQL.
   - Log de `AlertNotificationConsumer` (`Api.Web`) y, si tienes un cliente SignalR conectado a `/hubs/alerts` con un token válido, el mensaje `"AlertaBlacklist"`.
4. `dotnet run -- publish-nomatch` — publica un `PlateReadEvent` con una placa que no está en la blacklist (`NOMATCH99`). Con `PlateReadLogging:OnlyLogMatches = true` (default), no debe generarse ninguna fila nueva.
5. `dotnet run -- cleanup` — borra la `Camara`, el `VehiculoRobado` y cualquier `LecturaHistorica`/`Alerta` generada por las pruebas.

### `tools/LoadSimulator`

Genera carga sintética contra el mismo RabbitMQ/CAP que usan `Api.Web`/`Service.Inference`, para medir el camino caliente real (Redis lookup → persistencia → SignalR) sin depender del pipeline Edge de Python.

```powershell
cd C:\Ric68\SistemaMunicipaLPR\tools\LoadSimulator
dotnet build
```

- `dotnet run -- seed [--cameras N]` — siembra `N` cámaras (`CAM-SIM-0001`..`CAM-SIM-NNNN`, default 50) y una placa `SIMHIT001` como `VehiculoRobado` `Activo`. Idempotente.
- `dotnet run -- run [--cameras N] [--rate R] [--duration S] [--match-ratio P] [--drain-seconds S]` — publica `PlateReadEvent` real directo a RabbitMQ, con `N` "cámaras" concurrentes publicando a `R` lecturas/seg cada una (defaults: `N=50`, `R=10` → ~500 eventos/seg agregados). Una fracción `P` (default `0.01`) de las lecturas de cada cámara usa la placa `SIMHIT001` para generar match real; el resto usa placas aleatorias `SIM<hex>` sin match. Sin `--duration` corre hasta Ctrl+C. Al detenerse, espera `--drain-seconds` (default `15`) con su suscriptor de latencia todavía activo antes de imprimir el resumen final, para no dejar hits en tránsito sin medir.
- `dotnet run -- cleanup [--cameras N]` — borra las cámaras sembradas, el `VehiculoRobado` `SIMHIT001`, y cualquier `LecturaHistorica`/`Alerta` cuya `PlateText` empiece con `SIM`.

**Cómo mide la latencia cámara→alerta:** el tool se suscribe a `BlacklistHitSavedEvent` vía CAP (`HitLatencyConsumer`, `DefaultGroupName = "load-simulator"` — no compite con `service-inference` ni `api-web`, cada uno recibe su copia). Al publicar una lectura con la placa caliente, guarda `EventId → hora de publish`; cuando llega el `BlacklistHitSavedEvent` correspondiente, calcula la diferencia — ese número es el presupuesto de <300ms de Fase 3, medido end-to-end.

**Por qué solo se mide latencia del 1% con match:** con `PlateReadLogging:OnlyLogMatches=true` (default), el resto de las lecturas nunca tocan SQL — para esas, lo relevante es que RabbitMQ no acumule backlog (visible en `http://localhost:15672`), una métrica de throughput distinta que hay que revisar a mano durante el `run`.

**Nota sobre el outbox de CAP bajo carga sintética:** este tool sigue usando CAP (con `UseSqlServer`) para su propio suscriptor de `BlacklistHitSavedEvent` — a la escala de eventos de blacklist (1% del tráfico) esto no genera carga significativa en `cap.Published`/`cap.Received`. Si se aumenta mucho `--match-ratio`, considerar limpiar esas tablas manualmente de vez en cuando (`cleanup` no las toca).

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

**Gaps conocidos, sin resolver todavía:**
- **No incluye un modelo de detección de placas.** Un YOLO preentrenado en COCO no tiene clase de "placa" — hace falta conseguir un modelo público ya entrenado para esto o entrenar uno propio, y colocarlo en `edge/models/`. Sin ese archivo, `main.py` se niega a arrancar (falla rápido con un mensaje explícito en vez de correr sin detectar nada).
- **Sin uploader de imágenes a un storage central.** Los recortes de placa se guardan solo en disco local del nodo Edge (`images.save_dir`); `ImageReference` apunta a esa ruta local. No hay todavía ninguna pieza que suba esas imágenes a un storage accesible desde el backend/dashboard — hace falta diseñarla y construirla antes de que `ImageReference` sea útil fuera del propio nodo Edge.
- **Despliegue en Jetson:** `pip install ultralytics` en un entorno genérico trae wheels de PyTorch que no aprovechan la GPU del Jetson — en Jetson real hace falta instalar primero el PyTorch específico de NVIDIA para la versión de JetPack instalada (ver comentario en `requirements.txt`).
- **Sin verificar contra hardware real:** este código se escribió y se probó de forma aislada (config, serialización del evento, buffer SQLite, manejo de fallo de conexión a RabbitMQ) en un entorno sin cámara ni GPU — los módulos de captura/detección/OCR (`capture.py`, `detector.py`, `ocr.py`) no se han ejecutado todavía contra una cámara, un stream RTSP real, ni un modelo de placas entrenado. Primer paso al retomar: conseguir un modelo de detección de placas, conectar una cámara de prueba, y correr `python -m src.main` de punta a punta contra el RabbitMQ real del `docker-compose.yml`.

## 11. Alimentación de la lista negra (`VehiculosRobados`)

Además del alta/baja manual de `BlacklistController` (uno a la vez, ver [TechnicalDocumentation.md §5](TechnicalDocumentation.md#5-autenticación-y-autorización)), la lista negra se alimenta de tres fuentes que traen los mismos datos: una API externa, archivos Excel y archivos `.txt`. Todas convergen en el mismo tipo canónico y la misma lógica de reconciliación, en `Api.Web/Services/Blacklist/`:

```
Api.Web/Services/Blacklist/
  PlateTextNormalizer.cs           # mismo criterio de normalización que edge/src/ocr.py
  BlacklistImportRecord.cs         # tipo canónico + resultado/resumen de importación
  IBlacklistImportService.cs
  BlacklistImportService.cs        # reconciliación por placa (alta/baja/actualización)
  TabularBlacklistFileParser.cs    # lee .xlsx (ClosedXML) o .txt/.csv delimitado
  IExternalBlacklistSource.cs      # + PlaceholderExternalBlacklistSource
  ExternalBlacklistSyncService.cs  # BackgroundService, sincroniza cada 15 min
```

**Campos del tipo canónico (`BlacklistImportRecord`):** `PlateText`, `NumeroReporte`, `FechaReporteUtc`, `BusquedaActiva` (bool — true = activo/alta, false = recuperado/baja), `ImagenPath`, `Modelo`, `Anio`, `Marca` (algunas fuentes lo llaman "Vendor"), `Color`, `Clase`, `MarcasUOtros`. Los últimos siete son opcionales — `VehiculoRobado` los guarda como columnas nullable en `VehiculosRobados`.

**Reconciliación por placa (`BlacklistImportService.UpsertAsync`):** a diferencia del `POST` manual (que rechaza con 409 si la placa ya está activa), el import es idempotente — pensado para poder re-subir el mismo archivo, o uno más reciente de la misma fuente, sin generar duplicados:
- `BusquedaActiva=true` y no hay reporte activo para esa placa → alta (publica `BlacklistEntryAddedEvent`).
- `BusquedaActiva=true` y ya hay uno activo → actualiza los campos descriptivos que vinieron distintos (sin volver a publicar el evento — Redis ya tiene la placa).
- `BusquedaActiva=false` y hay uno activo → baja (`Estado=Recuperado`, publica `BlacklistEntryRemovedEvent`).
- `BusquedaActiva=false` y no hay ninguno activo → no hace nada (no es un error).

**Excel/.txt — `POST /api/blacklist/import`** (multipart, campo `file`, protegido con `blacklist`/`write`): `TabularBlacklistFileParser` mapea columnas por NOMBRE de encabezado (no por posición), con una tabla de alias por campo (ej. `Anio` acepta las columnas `Anio`/`Ano`/`Año`; `Marca` acepta `Vendor`/`Marca`/`Fabricante`). El `.txt` detecta el delimitador (tab/`;`/`,`/`|`) contando cuál aparece más veces en el encabezado. **Escrito sin un archivo de muestra real de ninguna de las tres fuentes** — si al probar con un archivo real alguna columna no matchea ningún alias, agregarlo a `ColumnAliases` en ese archivo es el único cambio necesario. La respuesta es un resumen (`BlacklistImportSummary`) con conteos por tipo de resultado y el detalle fila por fila.

**API externa — `IExternalBlacklistSource`:** el servicio real todavía no existe (sin endpoint/autenticación/formato definidos). `PlaceholderExternalBlacklistSource` devuelve una lista vacía para que `ExternalBlacklistSyncService` (`BackgroundService`, corre cada 15 min, registrado en `Program.cs` de `Api.Web`) pueda quedar activo sin romper el arranque. En cuanto exista el spec real, la única pieza que hay que reemplazar es esa implementación — el resto (reconciliación, invalidación de Redis) no cambia.

**Pendiente de correr tras este cambio:** el esquema de `VehiculosRobados` creció (7 columnas nuevas nullable) — hace falta generar y aplicar la migración:
```bash
dotnet ef migrations add AddVehiculoRobadoDetalles --project src/Api.Web --context LprDbContext
dotnet ef database update --project src/Api.Web --context LprDbContext
```
También se agregó el paquete `ClosedXML` a `Api.Web.csproj` (lectura de `.xlsx`) — su versión no se pudo verificar contra NuGet real al escribir esto; si `dotnet restore`/`dotnet build` no la encuentra, ajustar al último `0.104.x` publicado.

