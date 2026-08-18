# Documentación Técnica — Sistema Municipal LPR/ANPR

> Audiencia: desarrolladores que necesitan el detalle exacto de lo construido (esquemas, contratos, configuración). Para el razonamiento detrás de cada decisión ver [ArchitectureGuide.md](ArchitectureGuide.md). Para pasos de instalación/ejecución ver [ImplementersGuide.md](ImplementersGuide.md).

## 1. Estructura de la solución

```
SistemaLPR.sln
src/
  Core.Contracts/     Contratos de eventos + EventTopics (DotNetCore.CAP), sin dependencias de infraestructura
  Core.Domain/         Entidades del dominio (POCOs), sin dependencia de EF Core
  Service.Inference/  Worker Service — suscriptores de CAP/RabbitMQ + cache de Redis (Fase 2, implementado y verificado)
  Api.Web/            Web API + Auth + SignalR + AlertNotificationConsumer (Fase 2, implementado y verificado)
```

Referencias de proyecto: `Service.Inference` → `Core.Contracts`, `Core.Domain`. `Api.Web` → `Core.Contracts`, `Core.Domain`.

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

Cada evento se publica/consume por nombre de topic vía DotNetCore.CAP, no por tipo .NET — las constantes de topic viven en `Core.Contracts/EventTopics.cs` (`PlateRead`, `BlacklistHitSaved`, `BlacklistEntryAdded`, `BlacklistEntryRemoved`). Los suscriptores (`ICapSubscribe`) están implementados y verificados end-to-end desde Fase 2 — ver §7 y [fases.md](fases.md).

## 4. Modelo de datos (`Core.Domain` + `Api.Web/Data/LprDbContext.cs`)

Base de datos única: `SistemaLPR` (connection string `SistemaLPR` en `appsettings.json`, compartida por `LprDbContext` y `CasbinDbContext<int>`).

| Tabla | Columnas clave | Notas |
|---|---|---|
| `Camaras` | `Id` (PK identity), `Codigo` (único), `Nombre`, `Ubicacion` (`geography`, SRID 4326), `TipoInstalacion` (string: `ArcoSeguridad`\|`AvenidaAltaVelocidad`), `VelocidadMaximaKmh`, `Activa`, `CreatedAtUtc` | `Ubicacion` mapea `NetTopologySuite.Geometries.Point` — mismo sistema de coordenadas que ESRI/Google Maps |
| `VehiculosRobados` | `Id`, `PlateText` (indexado), `NumeroReporte`, `Estado` (`Activo`\|`Recuperado`), `FechaReporteUtc`, `CreatedAtUtc`, `RecuperadoAtUtc` | Fuente de verdad de la Lista Negra; Redis solo cachea `PlateText` |
| `LecturasHistoricas` | `Id` (bigint PK), `EventId` (único), `PlateText`, `CamaraId` (FK), `TimestampUtc` (indexado), `Confidence`, `ImageReference`, `EsCoincidenciaBlacklist` | Append-only. **Por default solo se registran lecturas que coinciden con la blacklist** (`EsCoincidenciaBlacklist = 1`) — no se guarda cada lectura, para no acumular información innecesaria. Configurable vía `PlateReadLogging:OnlyLogMatches` en `appsettings.json` de `Service.Inference` (default `true`); en `false`, también se registran las lecturas sin match (`EsCoincidenciaBlacklist = 0`). Ver `Service.Inference/Consumers/PlateReadConsumer.cs` y [fases.md — Fase 2](fases.md). **Índice columnstore no clusterizado** `IX_LecturasHistoricas_Columnstore` sobre `(PlateText, CamaraId, TimestampUtc, Confidence, EsCoincidenciaBlacklist)` para consultas forenses/analíticas — agregado a mano vía `migrationBuilder.Sql()` en la migración `InitialLprSchema`, ya que EF Core no expone una API fluida nativa para columnstore |
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

Ejemplo de endpoint protegido: `src/Api.Web/Controllers/BlacklistController.cs`.
```csharp
[Authorize(Policy = "Casbin")]
[CasbinResource("blacklist", "write")]
public IActionResult Post() => ...
```

## 6. Paquetes NuGet relevantes y notas de versión

Todos los proyectos apuntan a `net9.0`. Varios paquetes de Microsoft (`EntityFrameworkCore.SqlServer`, `EntityFrameworkCore.Design`, `AspNetCore.SignalR.StackExchangeRedis`, `AspNetCore.Authentication.JwtBearer`) tenían, al momento de escribir esto, su **última versión publicada en NuGet exigiendo `net10.0`** — se fijaron explícitamente en `9.0.19` (última compatible con `net9.0`) en lugar de dejar que `dotnet add package` tomara la última disponible.

| Proyecto | Paquetes clave |
|---|---|
| `Core.Domain` | `NetTopologySuite` |
| `Service.Inference` | `DotNetCore.CAP`, `DotNetCore.CAP.RabbitMQ`, `DotNetCore.CAP.SqlServer`, `StackExchange.Redis`, `Dapper`, `Microsoft.Data.SqlClient` |
| `Api.Web` | `DotNetCore.CAP`, `DotNetCore.CAP.RabbitMQ`, `DotNetCore.CAP.SqlServer`, `StackExchange.Redis`, `Dapper`, `Microsoft.EntityFrameworkCore.SqlServer` (9.0.19), `Microsoft.EntityFrameworkCore.SqlServer.NetTopologySuite` (9.0.19), `Microsoft.EntityFrameworkCore.Design`/`.Tools` (9.0.19), `Microsoft.AspNetCore.SignalR.StackExchangeRedis` (9.0.19), `Microsoft.AspNetCore.Authentication.JwtBearer` (9.0.19), `Casbin.NET`, `Casbin.NET.Adapter.EFCore` |

> **Nota (2026-08-18):** Fase 2 se construyó originalmente sobre `MassTransit.RabbitMQ`, pero se reemplazó por `DotNetCore.CAP` porque MassTransit 9.x exige configurar una licencia (`SetLicense`/`MT_LICENSE`) incluso para uso local — ver [ImplementersGuide.md §8](ImplementersGuide.md#8-notas-operativas-conocidas) para el detalle completo de la migración. Los paquetes `DotNetCore.CAP*` están fijados a `Version="8.*"` (flotante, sin confirmar el patch exacto).

## 7. Estado real de implementación (checklist técnico)

- [x] Solución .NET + `docker-compose.yml` (Fase 0)
- [x] Autenticación JWT/Keycloak + autorización Casbin.NET, con seeding de políticas
- [x] Contratos de eventos (`Core.Contracts`)
- [x] Entidades + `LprDbContext` + migración inicial con índice columnstore (`Core.Domain`, Fase 1)
- [x] `BlacklistCacheService` (carga inicial + invalidación event-driven + refresco delta) — Fase 2, verificado end-to-end
- [x] Suscriptores de DotNetCore.CAP (`PlateReadConsumer`, `BlacklistHitPersistenceConsumer` vía Dapper, `AlertNotificationConsumer`) — Fase 2, verificado end-to-end (migrados de MassTransit el 2026-08-18)
- [x] `AlertHub` (SignalR) — Fase 2, verificado end-to-end
- [ ] Simulador de carga (50 cámaras × 10 lecturas/seg) — Fase 3
- [ ] Pipeline Edge Python (YOLO + OpenCV + EasyOCR + buffer SQLite) — Fase 3
- [ ] Frontend C4 (React/Angular + mapa ESRI/Google Maps) — no iniciado

> Fase 0, Pre-Fase 1, Fase 1 y Fase 2 ya están verificadas de punta a punta contra servicios reales (ver `fases.md`). El siguiente trabajo real es Fase 3.
