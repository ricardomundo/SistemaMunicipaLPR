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
  Core.Domain/         Entidades del dominio (Camara, LecturaHistorica, Alerta) — POCOs sin dependencia de EF Core, usados por Api.Web (EF Core) y Service.Inference (Dapper); Alerta.RedListVehicleId referencia vehicles.id de RedLists (repo separado, ver Fase 3.5 en docs/fases.md)
  Service.Inference/  Worker Service: consume eventos de placas, cruza contra Redis, publica alertas
  Api.Web/            Web API + SignalR Hub (AlertHub) para el Dashboard C4 y apps de patrulla
```

## Infraestructura local (Fase 0)

```bash
docker compose up -d
```

Levanta:
- **MySQL 8.0** — `localhost:3306` (root / `Lpr#Dev_2026!`)
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

**Autorización (Casbin.NET):** `Api.Web` evalúa qué puede hacer cada rol usando un enforcer de Casbin (`src/Api.Web/Authorization/rbac_model.conf`) con las políticas persistidas en MySQL (tabla creada automáticamente al arrancar). Casbin es la única fuente de verdad de **qué puede hacer cada rol** — no gestiona usuarios ni roles, solo permisos.

Para proteger un endpoint:
```csharp
[Authorize(Policy = "Casbin")]
[CasbinResource("camaras", "write")]
public IActionResult Post() => ...
```
La matriz de permisos inicial por rol vive en [`Authorization/CasbinPolicySeeder.cs`](src/Api.Web/Authorization/CasbinPolicySeeder.cs); se re-siembra en cada arranque de forma idempotente (no pisa cambios hechos luego a mano en la tabla de políticas).

> Nota: este sandbox no tiene Docker disponible, así que la integración con Keycloak/MySQL no se pudo ejecutar de punta a punta aquí — verificar localmente con `docker compose up -d` seguido de `dotnet run --project src/Api.Web`.

## Modelo de datos (Fase 1)

Entidades en `Core.Domain`, mapeadas por `Api.Web/Data/LprDbContext.cs`, todas en la misma base `SistemaLPR`:

- **Camaras** — incluye `Latitude`/`Longitude` (decimal, WGS84 — mismo sistema de coordenadas que ESRI/Google Maps), tipo de instalación (arco de seguridad / avenida de alta velocidad) y velocidad máxima.
- La lista de vehículos reportados (RedList) ya no vive en este repo -- la administra [RedLists](../../RedLists) (`vehicle_lists`/`vehicles`/`list_vehicles`, misma base `SistemaLPR`, mismo servidor MySQL) -- ver Fase 3.5 en [`docs/fases.md`](docs/fases.md). `Alertas.RedListVehicleId` referencia esas tablas sin FK real: RedLists las administra con su propio esquema SQL, fuera del historial de migraciones de este DbContext. Redis solo cachea `PlateText` (`redlist:active-plates`) para el lookup O(1) del camino caliente.
- **LecturasHistoricas** — log append-only de cada lectura (match o no), con `EventId` único para deduplicar reintentos del buffer de borde, e índice compuesto (`TimestampUtc`, `PlateText`) para las consultas analíticas/forenses sobre volúmenes grandes.
- **Alertas** — registro auditado de cada coincidencia disparada hacia C4/patrullas.

Genera la migración inicial y aplícala contra el MySQL de `docker compose`:
```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialLprSchema --project src/Api.Web
dotnet ef database update --project src/Api.Web
```
(`Program.cs` también la aplica automáticamente al arrancar la API vía `Database.Migrate()`.)

### Contratos de eventos (`Core.Contracts`)

- `PlateReadEvent` — **sin** imagen embebida en base64 (a diferencia del blueprint original): incluirla en el mensaje caliente de RabbitMQ arriesgaba el presupuesto de latencia de <300ms. La imagen se sube de forma asíncrona y se referencia por `ImageReference`.
- `BlacklistHitSavedEvent` — publicado tras un match en Redis, para que un consumer aparte persista `LecturaHistorica`/`Alerta` sin bloquear el push de SignalR.
- La invalidación de la caché de Redis en segundos ya no usa eventos CAP propios de este repo (retirados en Fase 3.5) — `Service.Inference` se suscribe directo al hub de SignalR de RedLists (`VehicleAdded`/`VehicleRemoved`/`VehicleRecovered` en `/hubs/vehicle-lists`) en vez de esperar el refresco delta de 5 minutos.
