# Documentación Técnica — Sistema Municipal LPR/ANPR

> Audiencia: desarrolladores que necesitan el detalle exacto de lo construido (esquemas, contratos, configuración). Para el razonamiento detrás de cada decisión ver [ArchitectureGuide.md](ArchitectureGuide.md). Para pasos de instalación/ejecución ver [ImplementersGuide.md](ImplementersGuide.md).

## 1. Estructura de la solución

```
SistemaLPR.sln
src/
  Core.Contracts/     Contratos de eventos + EventTopics (CAP) + RawQueues (RabbitMQ directo), sin dependencias de infraestructura
  Core.Domain/         Entidades del dominio (POCOs), sin dependencia de EF Core
  Service.Inference/  Worker Service — consumers de CAP/RabbitMQ + cache de Redis
  Api.Web/            Web API + Auth + SignalR + AlertNotificationConsumer
edge/                 Pipeline Edge en Python (captura OpenCV + detección YOLO + OCR PaddleOCR + publish RabbitMQ) — proceso independiente, uno por cámara física
```

Referencias de proyecto: `Service.Inference` → `Core.Contracts`, `Core.Domain`. `Api.Web` → `Core.Contracts`, `Core.Domain`. `edge/` no depende del backend .NET — se comunica con él únicamente publicando `PlateReadEvent` como JSON plano por AMQP a la cola `plate-read-event.raw` (ver §3 e [ImplementersGuide.md §10](ImplementersGuide.md#10-pipeline-edge-python--edge)).

## 2. Infraestructura (`docker-compose.yml`)

| Servicio | Imagen | Puerto(s) | Credenciales dev |
|---|---|---|---|
| SQL Server 2022 | `mcr.microsoft.com/mssql/server:2022-latest` | 1433 | `sa` / `Lpr#Dev_2026!` |
| Redis | `redis:7-alpine` (AOF habilitado) | 6379 | — |
| RabbitMQ | `rabbitmq:3-management-alpine` | 5672 (AMQP), 15672 (UI) | `guest`/`guest` |
| Keycloak | `quay.io/keycloak/keycloak:26.0` (`start-dev`) | 8080 | `admin` / `Lpr#Dev_2026!` |

## 3. Contratos de eventos (`Core.Contracts`)

```csharp
public sealed record PlateReadEvent
{
    public Guid EventId { get; init; }
    public string PlateText { get; init; }
    public string CameraId { get; init; }
    public DateTime TimestampUtc { get; init; }
    public double Confidence { get; init; }
    public string? ImageReference { get; init; }   // NO base64 — ver ArchitectureGuide §3
}

public sealed record BlacklistHitSavedEvent
{
    public PlateReadEvent Reading { get; init; }
    public DateTime MatchedAtUtc { get; init; }
}

public sealed record BlacklistEntryAddedEvent
{
    public string PlateText { get; init; }
    public DateTime AddedAtUtc { get; init; }
}

public sealed record BlacklistEntryRemovedEvent
{
    public string PlateText { get; init; }
    public DateTime RemovedAtUtc { get; init; }
}
```

Los cuatro eventos no se enrutan todos por el mismo camino. `BlacklistHitSavedEvent`, `BlacklistEntryAddedEvent` y `BlacklistEntryRemovedEvent` se publican/consumen por nombre de topic vía DotNetCore.CAP (constantes en `Core.Contracts/EventTopics.cs`), con suscriptores `ICapSubscribe`. `PlateReadEvent` — el de mayor volumen, por mucho — se publica/consume directo por `RabbitMQ.Client` contra la cola `plate-read-event.raw` (nombre centralizado en `Core.Contracts/RawQueues.cs`), con ack manual, sin pasar por el outbox transaccional de CAP. Razonamiento completo de esta división en [ArchitectureGuide.md §3](ArchitectureGuide.md#3-arquitectura-de-eventos-y-mensajería); detalle de implementación en [ImplementersGuide.md §7](ImplementersGuide.md#7-arquitectura-de-mensajería-dotnetcorecap--rabbitmqclient).

## 4. Modelo de datos (`Core.Domain` + `Api.Web/Data/LprDbContext.cs`)

Base de datos única: `SistemaLPR` (connection string `SistemaLPR` en `appsettings.json`, compartida por `LprDbContext` y `CasbinDbContext<int>`).

| Tabla | Columnas clave | Notas |
|---|---|---|
| `Camaras` | `Id` (PK identity), `Codigo` (único), `Nombre`, `Ubicacion` (`geography`, SRID 4326), `TipoInstalacion` (string: `ArcoSeguridad`\|`AvenidaAltaVelocidad`), `VelocidadMaximaKmh`, `Activa`, `CreatedAtUtc` | `Ubicacion` mapea `NetTopologySuite.Geometries.Point` — mismo sistema de coordenadas que ESRI/Google Maps |
| `VehiculosRobados` | `Id`, `PlateText` (indexado), `NumeroReporte`, `Estado` (`Activo`\|`Recuperado`), `FechaReporteUtc`, `CreatedAtUtc`, `RecuperadoAtUtc`, `ImagenPath`, `Modelo`, `Anio`, `Marca`, `Color`, `Clase`, `MarcasUOtros` (los últimos 7, nullable) | Fuente de verdad de la Lista Negra; Redis solo cachea `PlateText`. Alimentada por alta/baja manual (`BlacklistController`) y por importación masiva desde API externa/Excel/.txt (`Api.Web/Services/Blacklist/`, ver [ImplementersGuide.md §11](ImplementersGuide.md#11-alimentación-de-la-lista-negra-vehiculosrobados)) |
| `LecturasHistoricas` | `Id` (bigint PK), `EventId` (único), `PlateText`, `CamaraId` (FK), `TimestampUtc` (indexado), `Confidence`, `ImageReference`, `EsCoincidenciaBlacklist` | Append-only. **Por default solo se registran lecturas que coinciden con la blacklist** (`EsCoincidenciaBlacklist = 1`) — no se guarda cada lectura, para no acumular información innecesaria. Configurable vía `PlateReadLogging:OnlyLogMatches` en `appsettings.json` de `Service.Inference` (default `true`); en `false`, también se registran las lecturas sin match (`EsCoincidenciaBlacklist = 0`). Ver `Service.Inference/Consumers/PlateReadConsumer.cs`. **Índice columnstore no clusterizado** `IX_LecturasHistoricas_Columnstore` sobre `(PlateText, CamaraId, TimestampUtc, Confidence, EsCoincidenciaBlacklist)` para consultas forenses/analíticas — agregado a mano vía `migrationBuilder.Sql()` en la migración `InitialLprSchema`, ya que EF Core no expone una API fluida nativa para columnstore |
| `Alertas` | `Id` (bigint PK), `LecturaHistoricaId` (FK), `VehiculoRobadoId` (FK), `TimestampUtc`, `Estado` (`Pendiente`\|`Atendida`\|`Descartada`), `AtendidaPor`, `AtendidaAtUtc` | El único campo mutable tras la creación es el flujo de atención (`Estado`/`AtendidaPor`/`AtendidaAtUtc`); el resto del registro es inmutable |

Migración: `src/Api.Web/Migrations/20260817195921_InitialLprSchema.cs`.

## 5. Autenticación y autorización

### Autenticación (Keycloak / JWT Bearer)

Configuración en `appsettings.json`:
```json
"Keycloak": {
  "Authority": "http://localhost:8080/realms/sistema-lpr",
  "Audience": "api-web",
  "RequireHttpsMetadata": false
}
```

`Program.cs` registra `AddJwtBearer` con dos eventos:
- `OnMessageReceived` — permite autenticar conexiones SignalR vía `?access_token=` en rutas bajo `/hubs`.
- `OnTokenValidated` — aplana el claim anidado `realm_access.roles` de Keycloak a claims `ClaimTypes.Role` individuales (Keycloak no los expone así por defecto).

### Autorización (Casbin.NET)

- Modelo: `src/Api.Web/Authorization/rbac_model.conf` — matcher de **igualdad literal**, sin `g` (Keycloak ya asigna roles a usuarios; Casbin solo resuelve rol→permiso) y sin comodines `"*"` (el matcher no los interpreta; por eso `CasbinPolicySeeder` enumera cada permiso explícitamente).
- Persistencia: `CasbinDbContext<int>` vía `Casbin.NET.Adapter.EFCore`, tabla creada con `Database.EnsureCreated()` al arrancar (Casbin no distribuye migraciones EF propias).
- `CasbinResourceAttribute(object, action)` — metadata en el endpoint.
- `CasbinRequirement` + `CasbinAuthorizationHandler` — política `"Casbin"`; el handler junta los roles del usuario autenticado y aprueba si **alguno** satisface `enforcer.Enforce(rol, obj, act)`.
- `CasbinPolicySeeder.SeedDefaultPolicies()` — matriz inicial (idempotente, no pisa cambios posteriores hechos a mano en la tabla de políticas):

| Rol | Permisos |
|---|---|
| `SuperAdmin` | `alertas`/`camaras`/`blacklist`/`usuarios` (read+write), `lecturas-historicas` (read) |
| `SupervisorC4` | `alertas`/`camaras`/`blacklist` (read+write), `usuarios` (read) |
| `OperadorC4` | `alertas`/`camaras`/`blacklist` (read) |
| `PatrullaMovil` | `alertas` (read) |
| `AuditorForense` | `alertas`/`camaras`/`blacklist`/`lecturas-historicas` (read) |

Ejemplo de endpoint protegido: `src/Api.Web/Controllers/BlacklistController.cs` — alta/baja real de `VehiculoRobado` (`POST`/`DELETE /api/blacklist`), cada uno protegido con el permiso `blacklist`/`write`:
```csharp
[Authorize(Policy = "Casbin")]
[CasbinResource("blacklist", "write")]
public async Task<IActionResult> Post([FromBody] CreateBlacklistEntryRequest request) => ...
```

Tanto el alta como la baja publican `BlacklistEntryAddedEvent`/`BlacklistEntryRemovedEvent` vía `ICapPublisher` justo después de escribir en SQL, para que `BlacklistCacheService`/`BlacklistEntryAddedConsumer`/`RemovedConsumer` (en `Service.Inference`) invaliden el set de Redis de inmediato — cualquier cambio hecho fuera de esta API (p. ej. un `INSERT`/`UPDATE` directo en SQL) solo se refleja hasta el refresco delta de 5 minutos, no al instante.

## 6. Paquetes NuGet relevantes y notas de versión

Todos los proyectos apuntan a `net9.0`. Varios paquetes de Microsoft (`EntityFrameworkCore.SqlServer`, `EntityFrameworkCore.Design`, `AspNetCore.SignalR.StackExchangeRedis`, `AspNetCore.Authentication.JwtBearer`) tenían, al momento de escribir esto, su **última versión publicada en NuGet exigiendo `net10.0`** — se fijaron explícitamente en `9.0.19` (última compatible con `net9.0`) en lugar de dejar que `dotnet add package` tomara la última disponible.

| Proyecto | Paquetes clave |
|---|---|
| `Core.Domain` | `NetTopologySuite` |
| `Service.Inference` | `DotNetCore.CAP`, `DotNetCore.CAP.RabbitMQ`, `DotNetCore.CAP.SqlServer`, `StackExchange.Redis`, `Dapper`, `Microsoft.Data.SqlClient` |
| `Api.Web` | `DotNetCore.CAP`, `DotNetCore.CAP.RabbitMQ`, `DotNetCore.CAP.SqlServer`, `StackExchange.Redis`, `Dapper`, `Microsoft.EntityFrameworkCore.SqlServer` (9.0.19), `Microsoft.EntityFrameworkCore.SqlServer.NetTopologySuite` (9.0.19), `Microsoft.EntityFrameworkCore.Design`/`.Tools` (9.0.19), `Microsoft.AspNetCore.SignalR.StackExchangeRedis` (9.0.19), `Microsoft.AspNetCore.Authentication.JwtBearer` (9.0.19), `Casbin.NET`, `Casbin.NET.Adapter.EFCore`, `ClosedXML` (lectura de `.xlsx` para importar la lista negra, ver [ImplementersGuide.md §11](ImplementersGuide.md#11-alimentación-de-la-lista-negra-vehiculosrobados)) |

Los paquetes `DotNetCore.CAP*` están fijados a `Version="8.*"`. `RabbitMQ.Client` (usado directamente por `Service.Inference` para `PlateReadEvent`, ver [ArchitectureGuide.md §3](ArchitectureGuide.md#3-arquitectura-de-eventos-y-mensajería)) no tiene `PackageReference` explícita — se resuelve transitivamente vía `DotNetCore.CAP.RabbitMQ`.

## 7. Estado real de implementación (checklist técnico)

- [x] Solución .NET + `docker-compose.yml` (Fase 0)
- [x] Autenticación JWT/Keycloak + autorización Casbin.NET, con seeding de políticas
- [x] Contratos de eventos (`Core.Contracts`)
- [x] Entidades + `LprDbContext` + migración inicial con índice columnstore (`Core.Domain`, Fase 1)
- [x] `BlacklistCacheService` (carga inicial + invalidación event-driven + refresco delta) — Fase 2
- [x] `BlacklistController` (alta/baja real de `VehiculoRobado`, con invalidación inmediata de Redis vía CAP) — Fase 3
- [x] Alimentación masiva de la lista negra (`Api.Web/Services/Blacklist/`: import Excel/.txt vía `POST /api/blacklist/import` verificado con archivo real, reconciliación por placa) — Fase 3. Sincronización con API externa (`HttpExternalBlacklistSource` + `ExternalBlacklistSyncService`, GET + bearer token) verificada end-to-end contra el endpoint real del cliente (ver [ImplementersGuide.md §11](ImplementersGuide.md#11-alimentación-de-la-lista-negra-vehiculosrobados))
- [x] Consumers de `PlateReadEvent` (RabbitMQ.Client directo), `BlacklistHitPersistenceConsumer` (Dapper, vía CAP), `AlertNotificationConsumer` (vía CAP) — Fase 2
- [x] `AlertHub` (SignalR) — Fase 2
- [x] Simulador de carga (`tools/LoadSimulator`, 50 cámaras × 10 lecturas/seg) — Fase 3, latencia cámara→alerta verificada bajo carga: `p95=83ms max=175ms` (objetivo <300ms cumplido con margen)
- [x] Pipeline Edge Python (YOLO + OpenCV + PaddleOCR + buffer SQLite) — Fase 3, verificado end-to-end contra un video de prueba real (detección → OCR → publish → persistencia → alerta); sin uploader de imágenes a storage central todavía (ver [ImplementersGuide.md §10](ImplementersGuide.md#10-pipeline-edge-python--edge))
- [ ] Frontend C4 (React/Angular + mapa ESRI/Google Maps) — no iniciado

> Fase 0, Pre-Fase 1, Fase 1 y Fase 2 están verificadas de punta a punta contra servicios reales (ver `fases.md`). El siguiente trabajo real es completar Fase 3.
