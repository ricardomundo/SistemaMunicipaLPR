# Sistema Municipal LPR/ANPR

Sistema de reconocimiento de matrículas (LPR/ANPR) y cruzamiento en tiempo real contra la Lista Negra de Vehículos con Reporte de Robo.

## Documentación

- [Guía de Arquitectura](docs/ArchitectureGuide.md) — por qué se tomó cada decisión de diseño
- [Documentación Técnica](docs/TechnicalDocumentation.md) — esquemas, contratos y configuración exactos
- [Guía del Implementador](docs/ImplementersGuide.md) — cómo levantar el entorno y seguir construyendo
- [Guía de Usuario](docs/UsersGuide.md) — cómo se usa el sistema (roles, alertas)
- [Fases del Proyecto](docs/fases.md) — estado de avance por fase (fuente única de verdad)

## Estructura de la solución

```
SistemaLPR.sln
src/
  Core.Contracts/     Contratos de eventos compartidos (PlateReadEvent, BlacklistHitSavedEvent, ...)
  Core.Domain/         Entidades del dominio (Camara, VehiculoRobado, LecturaHistorica, Alerta) — POCOs sin dependencia de EF Core, usados por Api.Web (EF Core) y Service.Inference (Dapper)
  Service.Inference/  Worker Service: consume eventos de placas, cruza contra Redis, publica alertas
  Api.Web/            Web API + SignalR Hub (AlertHub) para el Dashboard C4 y apps de patrulla
```

## Infraestructura local (Fase 0)

```bash
docker compose up -d
```

Levanta:
- **SQL Server 2022** — `localhost:1433` (sa / `Lpr#Dev_2026!`)
- **Redis** — `localhost:6379`
- **RabbitMQ** — `localhost:5672` (AMQP), consola de administración en `localhost:15672` (guest/guest)
- **Keycloak** — `localhost:8080` (admin / `Lpr#Dev_2026!`), modo `start-dev`

## Build

```bash
dotnet build
```

## Autenticación y autorización

**Autenticación (Keycloak):** `Api.Web` valida JWTs emitidos por Keycloak (JWT Bearer), no gestiona contraseñas. Antes de poder ejecutar la API contra Keycloak hace falta, una sola vez, desde la consola de administración (`http://localhost:8080`):
1. Crear el realm `sistema-lpr` (coincide con `Keycloak:Authority` en `appsettings.json`).
2. Crear el cliente `api-web` (coincide con `Keycloak:Audience`), tipo confidential o public según el flujo elegido.
3. Crear los realm roles: `SuperAdmin`, `SupervisorC4`, `OperadorC4`, `PatrullaMovil`, `AuditorForense`.
4. Crear usuarios de prueba y asignarles esos roles.

Keycloak es la única fuente de verdad de **qué rol tiene cada usuario**.

**Autorización (Casbin.NET):** `Api.Web` evalúa qué puede hacer cada rol usando un enforcer de Casbin (`src/Api.Web/Authorization/rbac_model.conf`) con las políticas persistidas en SQL Server (tabla creada automáticamente al arrancar). Casbin es la única fuente de verdad de **qué puede hacer cada rol** — no gestiona usuarios ni roles, solo permisos.

Para proteger un endpoint:
```csharp
[Authorize(Policy = "Casbin")]
[CasbinResource("blacklist", "write")]
public IActionResult Post() => ...
```
Ver [`Controllers/BlacklistController.cs`](src/Api.Web/Controllers/BlacklistController.cs) como referencia completa. La matriz de permisos inicial por rol vive en [`Authorization/CasbinPolicySeeder.cs`](src/Api.Web/Authorization/CasbinPolicySeeder.cs); se re-siembra en cada arranque de forma idempotente (no pisa cambios hechos luego a mano en la tabla de políticas).

> Nota: este sandbox no tiene Docker disponible, así que la integración con Keycloak/SQL Server no se pudo ejecutar de punta a punta aquí — verificar localmente con `docker compose up -d` seguido de `dotnet run --project src/Api.Web`.

## Modelo de datos (Fase 1)

Entidades en `Core.Domain`, mapeadas por `Api.Web/Data/LprDbContext.cs`, todas en la misma base `SistemaLPR`:

- **Camaras** — incluye `Ubicacion` (`NetTopologySuite.Geometries.Point` mapeado a `geography` de SQL Server, SRID 4326/WGS84 — mismo sistema de coordenadas que ESRI/Google Maps), tipo de instalación (arco de seguridad / avenida de alta velocidad) y velocidad máxima.
- **VehiculosRobados** — la Lista Negra auditada; Redis solo cachea `PlateText` para el lookup O(1) del camino caliente, esta tabla es la fuente de verdad.
- **LecturasHistoricas** — log append-only de cada lectura (match o no), con `EventId` único para deduplicar reintentos del buffer de borde, e índice **columnstore no clusterizado** para las consultas analíticas/forenses sobre volúmenes grandes.
- **Alertas** — registro auditado de cada coincidencia disparada hacia C4/patrullas.

Migración inicial ya generada (`src/Api.Web/Migrations/`). Para aplicarla contra el SQL Server de `docker compose`:
```bash
dotnet tool install --global dotnet-ef
dotnet ef database update --project src/Api.Web
```
(`Program.cs` también la aplica automáticamente al arrancar la API vía `Database.Migrate()`.)

### Contratos de eventos (`Core.Contracts`)

- `PlateReadEvent` — **sin** imagen embebida en base64 (a diferencia del blueprint original): incluirla en el mensaje caliente de RabbitMQ arriesgaba el presupuesto de latencia de <300ms. La imagen se sube de forma asíncrona y se referencia por `ImageReference`.
- `BlacklistHitSavedEvent` — publicado tras un match en Redis, para que un consumer aparte persista `LecturaHistorica`/`Alerta` sin bloquear el push de SignalR.
- `BlacklistEntryAddedEvent` / `BlacklistEntryRemovedEvent` — para invalidar/actualizar Redis en segundos cuando se da de alta o de baja una placa en `VehiculosRobados`, en vez de esperar el refresco delta de 5 minutos.
