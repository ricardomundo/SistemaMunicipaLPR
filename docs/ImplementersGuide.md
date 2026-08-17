# Guía del Implementador — Sistema Municipal LPR/ANPR

> Audiencia: desarrolladores que van a levantar el entorno o construir las siguientes fases. Para el detalle de esquemas/contratos ver [TechnicalDocumentation.md](TechnicalDocumentation.md); para el razonamiento de diseño ver [ArchitectureGuide.md](ArchitectureGuide.md).

## 1. Requisitos previos

- .NET 9 SDK
- Docker Desktop (con WSL2/virtualización habilitada en Windows)
- `dotnet-ef` como herramienta global: `dotnet tool install --global dotnet-ef`

## 2. Levantar el entorno local

```bash
cd F:\source\SistMunicipal
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

## 7. Próximos pasos (Fase 2 — pendiente)

1. **`BlacklistCacheService`** (`IHostedService` en `Api.Web` o `Service.Inference`): carga inicial de `VehiculosRobados.Estado == Activo` a Redis al arrancar; se suscribe a `BlacklistEntryAddedEvent`/`BlacklistEntryRemovedEvent` para invalidación inmediata; refresco delta cada 5 min como respaldo.
2. **`PlateReadConsumer`** en `Service.Inference` (MassTransit `IConsumer<PlateReadEvent>`): lookup en Redis, si hay match publica `BlacklistHitSavedEvent` y notifica por SignalR; **debe deduplicar por `EventId`** antes de insertar en `LecturasHistoricas` (índice único ya existe, pero atrapar la excepción de violación de índice o chequear existencia antes de insertar).
3. **`AlertHub`** (SignalR) en `Api.Web`, bajo la ruta `/hubs/alerts` (el middleware JWT ya soporta `access_token` en query string para rutas `/hubs/*` — ver `Program.cs`). Configurar el backplane de Redis (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`, ya instalado, falta llamar `AddStackExchangeRedis(...)`).
4. Consumer de persistencia async para `BlacklistHitSavedEvent` → inserta `LecturaHistorica` + `Alerta` vía Dapper (alto rendimiento, camino separado del hot-path de matching).

## 8. Notas operativas conocidas

- Este entorno de desarrollo (sandbox) no tuvo Docker disponible durante la construcción de Fase 0/1 — el `docker compose up -d` y la configuración de Keycloak **no se han probado de punta a punta todavía**. Verificarlo es el primer paso antes de seguir construyendo Fase 2.
- Varios paquetes NuGet de Microsoft empezaron a publicar versiones que exigen `net10.0`; si `dotnet add package <algo-de-Microsoft>` falla con `NU1202`, buscar la última versión `9.0.x` explícita en vez de dejar que tome la última disponible (ver [TechnicalDocumentation.md §6](TechnicalDocumentation.md#6-paquetes-nuget-relevantes-y-notas-de-versión)).
