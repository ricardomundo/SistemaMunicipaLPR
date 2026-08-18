# Fases del Proyecto — Sistema Municipal LPR/ANPR

> Fuente única de verdad del avance por fase. Actualizar este archivo (no crear uno nuevo) cada vez que cambie el estado de una fase. Detalle de diseño en [ArchitectureGuide.md](ArchitectureGuide.md); detalle técnico exacto de lo construido en [TechnicalDocumentation.md](TechnicalDocumentation.md#7-estado-real-de-implementación-checklist-técnico).

## Resumen

| Fase | Nombre | Estado |
|---|---|---|
| Fase 0 | Infraestructura base | ✅ Completa |
| Pre-Fase 1 | Authentication & Authorization | ✅ Completa (verificada end-to-end) |
| Fase 1 | Contratos de eventos + modelo de datos | ✅ Completa (verificada end-to-end) |
| Fase 2 | Cache Redis + mensajería DotNetCore.CAP/RabbitMQ + SignalR | ✅ Completa (verificada end-to-end) |
| Fase 3 | Simulador de carga + módulo Edge (Python/YOLO) | ⏳ Pendiente |
| Fase 4 | Frontend C4 (dashboard web + mapa) | ⏳ Pendiente (no planificada en detalle aún) |

---

## Fase 0 — Infraestructura base ✅

**Objetivo:** esqueleto de la solución .NET y servicios de infraestructura local.

**Entregado:**
- `SistemaLPR.sln` con proyectos `Core.Contracts`, `Service.Inference`, `Api.Web`.
- `docker-compose.yml`: SQL Server 2022, Redis, RabbitMQ.
- `dotnet build` limpio.

---

## Pre-Fase 1 — Authentication & Authorization ✅

**Objetivo:** modelo de auth escalable antes de construir features protegidas por rol.

**Decisión:** Keycloak (autenticación, OIDC/JWT) + Casbin.NET (autorización, rol→permiso). Ver [ArchitectureGuide.md §4](ArchitectureGuide.md#4-modelo-de-autenticación-y-autorización).

**Entregado:**
- Keycloak agregado a `docker-compose.yml`.
- JWT Bearer configurado en `Api.Web` (`Program.cs`), con aplanado de `realm_access.roles` → `ClaimTypes.Role`.
- Casbin.NET + adapter EF Core, modelo RBAC simple (`rbac_model.conf`), `CasbinResourceAttribute`/`CasbinRequirement`/`CasbinAuthorizationHandler`, `CasbinPolicySeeder` con la matriz inicial de 5 roles.
- Controlador de referencia (`BlacklistController`).

**Corregido (2026-08-18):** bug real de orden de arranque encontrado en la verificación end-to-end — `Program.cs` llamaba `LprDbContext.Database.Migrate()` antes que `CasbinDbContext<int>.Database.EnsureCreated()`; como ambos comparten la base `SistemaLPR`, `EnsureCreated()` veía que la base "ya tenía tablas" (las de `LprDbContext`) y nunca creaba `casbin_rule`, tumbando la API al primer arranque con `SqlException: Invalid object name 'casbin_rule'`. Se corrigió invirtiendo el orden en `Program.cs`. Detalle completo en [ImplementersGuide.md §8](ImplementersGuide.md#8-notas-operativas-conocidas).

**Verificado (2026-08-18):** realm `sistema-lpr` y cliente `api-web` creados en Keycloak real (Client authentication On, Direct access grants On, mapper de Audience agregado en `api-web-dedicated` apuntando a `api-web`), los 5 realm roles creados, usuario de prueba `operador.test` con rol `OperadorC4`. Ciclo completo probado contra la API ya corriendo: `GET /api/blacklist` sin token → 401; con `access_token` de Keycloak (obtenido por password grant) → 200 con `[]`. El JWT trae `aud:["api-web","account"]` (el mapper de audience funciona) y `realm_access.roles` incluye `OperadorC4` (el aplanado a `ClaimTypes.Role` funciona), y Casbin autorizó correctamente según la matriz de permisos.

**Pre-Fase 1 queda completamente verificada de punta a punta.**

---

## Fase 1 — Contratos de eventos + modelo de datos ✅

**Objetivo:** definir los contratos de MassTransit y el esquema de SQL Server.

**Entregado:**
- Nuevo proyecto `Core.Domain` (POCOs): `Camara` (con `Ubicacion` geoespacial), `VehiculoRobado`, `LecturaHistorica`, `Alerta`.
- `Core.Contracts`: `PlateReadEvent` (sin imagen base64 — revisado por riesgo de latencia), `BlacklistHitSavedEvent`, `BlacklistEntryAddedEvent`/`BlacklistEntryRemovedEvent` (nuevos, para invalidación inmediata de Redis).
- `LprDbContext` + migración inicial (`InitialLprSchema`) con índice columnstore en `LecturasHistoricas`.
- `dotnet build` limpio en los 4 proyectos.

**Verificado (2026-08-18):**
- `dotnet ef database update --project src/Api.Web --context LprDbContext` aplicó la migración `InitialLprSchema` sin errores contra el SQL Server real de `docker compose` (ver nota de troubleshooting en [ImplementersGuide.md §8](ImplementersGuide.md#8-notas-operativas-conocidas) — el intento inicial falló por un `dotnet-ef` global en `10.x` desalineado del SDK `9.x`, se resolvió fijando el tool a `9.0.19`).
- El tipo `geography`/NetTopologySuite serializa y deserializa correctamente end-to-end: se insertó una `Camara` real vía `LprDbContext` con `Ubicacion` (`POINT (-100.3161 25.6866)`, SRID 4326), se limpió el `ChangeTracker` para forzar una lectura real desde SQL Server, y el punto leído coincidió exactamente en coordenadas y SRID. Registro de prueba eliminado después.

**Fase 1 queda completamente verificada de punta a punta.**

---

## Fase 2 — Cache Redis + mensajería + SignalR ✅

**Objetivo:** el camino caliente real: cámara → RabbitMQ → consumer → Redis lookup → alerta.

**Entregado (2026-08-18):**
1. `BlacklistCacheService` (`IHostedService`, en `Service.Inference`): carga inicial de `VehiculosRobados.Estado = Activo` al set de Redis `blacklist:active-plates` al arrancar, y lo vuelve a cargar completo cada 5 min como respaldo (reemplazo atómico vía transacción `DEL`+`SADD`).
2. `BlacklistEntryAddedConsumer`/`BlacklistEntryRemovedConsumer` (`Service.Inference`): invalidación inmediata del set de Redis al recibir `BlacklistEntryAddedEvent`/`RemovedEvent` (aún no hay nada que publique estos eventos — `BlacklistController` sigue siendo un stub; ver nota abajo).
3. `PlateReadConsumer` (DotNetCore.CAP, en `Service.Inference`): lookup O(1) contra el set de Redis; si hay match, publica `BlacklistHitSavedEvent`.
4. `BlacklistHitPersistenceConsumer` (`Service.Inference`): suscriptor separado de `BlacklistHitSavedEvent` — resuelve `CamaraId`/`VehiculoRobadoId` por lookup y inserta `LecturaHistorica` + `Alerta` vía Dapper, deduplicando por `EventId` (atrapa la violación del índice único).
5. `AlertNotificationConsumer` (`Api.Web`): **otro** suscriptor independiente de `BlacklistHitSavedEvent` (corre en el proceso de `Api.Web`, no en `Service.Inference`, porque ahí es donde vive el Hub) — empuja la alerta a `AlertHub` por SignalR. Cada servicio tiene su propio `DefaultGroupName` de CAP sobre el mismo topic, así que no compite ni depende del suscriptor de persistencia.
6. `AlertHub` (SignalR) en `Api.Web`, ruta `/hubs/alerts`, `[Authorize]`, con backplane de Redis (`AddStackExchangeRedis`).

**Decisión de diseño (a pedido del usuario, 2026-08-18):** `LecturaHistorica` **solo** se registra cuando hay match de blacklist (vía `BlacklistHitPersistenceConsumer`) — las lecturas sin coincidencia no se guardan, para no acumular información innecesaria. Esto es configurable: `PlateReadLogging:OnlyLogMatches` en `appsettings.json` de `Service.Inference` (default `true`); en `false`, `PlateReadConsumer` registra también las lecturas sin match. **Nota:** esto deja a `TechnicalDocumentation.md §4` ligeramente desactualizado, que describe `LecturasHistoricas` como "log de cada lectura, match o no" — pendiente de alinear esa descripción con el default real.

**Verificado (2026-08-18):** `dotnet build` de la solución completa compiló limpio a la primera (código escrito sin acceso a SDK/NuGet del lado del asistente — ver nota en ImplementersGuide.md §8 — pero sin errores de compilación).

**Corregido (2026-08-18) — de MassTransit a DotNetCore.CAP:** al correr `dotnet run --project src/Api.Web`, MassTransit truena con `ConfigurationException: License must be specified with SetLicense/SetLicenseLocation...` — `MassTransit.RabbitMQ 9.x` (versión que quedó pineada al construir Fase 2) ahora exige licencia para arrancar el bus, incluso en local. Se probó bajar a la serie `8.x` (sin ese requerimiento) como parche rápido, pero el usuario decidió reemplazar la librería de mensajería por completo: **DotNetCore.CAP** (MIT, sin licencia comercial). Se migraron `Api.Web`, `Service.Inference` y `tools/VerifyFase2` de `IConsumer<T>`/`IPublishEndpoint` (MassTransit) a `ICapSubscribe`/`ICapPublisher` (CAP), con topics de string en el nuevo `Core.Contracts/EventTopics.cs` en vez de enrutar por tipo .NET. Detalle completo, incluyendo el patrón de outbox transaccional de CAP, en [ImplementersGuide.md §8](ImplementersGuide.md#8-notas-operativas-conocidas).

**Verificado (2026-08-18):** `dotnet restore`/`dotnet build`/`dotnet run` con DotNetCore.CAP corrieron correctamente en la máquina real del implementador — `Api.Web` y `Service.Inference` arrancan sin el error de licencia de MassTransit y sin errores de `AddCap(...)`.

**Verificado end-to-end (2026-08-18), con `tools/VerifyFase2` contra el stack real completo (Docker Compose + `Api.Web` + `Service.Inference` corriendo):**
- `seed` creó la `Camara`/`VehiculoRobado` de prueba; tras reiniciar `Service.Inference`, `BlacklistCacheService` confirmó `"Blacklist cache refrescada: 1 placas activas."`.
- `publish` (placa `TEST1234`, debe dar match) confirmó la cadena completa: `PlateReadConsumer` detectó el match en Redis → `BlacklistHitPersistenceConsumer` registró `LecturaHistorica` + `Alerta` en SQL Server → `AlertNotificationConsumer` (en `Api.Web`) empujó la alerta por SignalR. Los tres pasos se vieron en logs, cada uno en el proceso que le corresponde (confirma que los `DefaultGroupName` distintos de CAP — `service-inference` y `api-web` — sí entregan cada uno su propia copia de `BlacklistHitSavedEvent`, y que el outbox de CAP despacha a RabbitMQ dentro de la ventana de ~5s del tool).
- `publish-nomatch` (placa `NOMATCH99`) se comportó como se esperaba con `PlateReadLogging:OnlyLogMatches = true` (default): no generó ninguna fila nueva en `LecturasHistoricas` ni `Alertas`.
- `cleanup` borró los datos de prueba sin errores.

**Fase 2 queda completamente verificada de punta a punta, incluyendo la migración a DotNetCore.CAP.**

**Pendiente (no bloqueante, limpieza menor):**
- `Worker.cs` en `Service.Inference` quedó sin usar (ya no está registrado en `Program.cs`, lo reemplazó `BlacklistCacheService`) — se puede borrar.

Detalle paso a paso en [ImplementersGuide.md §7](ImplementersGuide.md#7-próximos-pasos-fase-2--pendiente).

---

## Fase 3 — Simulador de carga + módulo Edge ⏳

**Objetivo:** validar el presupuesto de latencia de <300ms bajo carga, y construir el pipeline de inferencia real.

**Por construir:**
1. Simulador de 50 cámaras × 10 lecturas/seg (script C# o Python) publicando contra RabbitMQ.
2. Pipeline Edge en Python: OpenCV captura (con burst de frames para mitigar motion blur a 160 km/h) → YOLOv8/v11 detección de placa → EasyOCR → selección por confianza → publish.
3. Buffer SQLite local en el nodo Edge + uploader en background para tolerancia a cortes de red.

---

## Fase 4 — Frontend C4 ⏳

**Objetivo:** dashboard web (React o Angular, aún sin elegir) con mapa de cámaras/alertas (ESRI o Google Maps — la ubicación de cámara ya se modela como `geography` en Fase 1) y consumo del `AlertHub` de SignalR.

**Estado:** no planificada en detalle todavía — depende de que Fase 2 (el Hub) y Fase 3 (datos reales fluyendo) estén construidas primero.

---

## Notas transversales

- **Bloqueo de verificación — cerrado (2026-08-18):** Docker Desktop instalado y corriendo. Se levantó el stack completo (`docker compose up -d`: SQL Server, Redis, RabbitMQ, Keycloak) y se verificaron de punta a punta **Pre-Fase 1** (Keycloak + Casbin + JWT) y **Fase 1** (migración + `geography`). Con esto ya no hay pendientes de verificación bloqueando el arranque de Fase 2.
- **Fase 2 — verificada end-to-end (2026-08-18):** tras la migración a DotNetCore.CAP, `dotnet restore`/`build`/`run` de `Api.Web` y `Service.Inference` corrieron sin errores, y el flujo completo `seed`/`publish`/`publish-nomatch`/`cleanup` de `tools/VerifyFase2` confirmó el camino caliente real de punta a punta (ver Fase 2 arriba). Con esto, Pre-Fase 1, Fase 1 y Fase 2 quedan las tres verificadas; el siguiente trabajo real es Fase 3.
- **Nota de troubleshooting reusable (Keycloak "Account is not fully set up"):** si el `password grant` de Keycloak devuelve `invalid_grant: Account is not fully set up`, el usuario tiene una "required action" pendiente (típicamente porque la contraseña quedó marcada `Temporary`). Entra a `http://localhost:8080/realms/sistema-lpr/account/` e inicia sesión con ese usuario — Keycloak muestra en pantalla la acción exacta que falta completar; complétala ahí y reintenta el `password grant`.
- **Nota de troubleshooting reusable (dotnet-ef):** si `dotnet ef` falla con `Unable to retrieve project metadata. Ensure it's an SDK-style project.`, ver [ImplementersGuide.md §8](ImplementersGuide.md#8-notas-operativas-conocidas) — normalmente es el tool global `dotnet-ef` en una versión mayor que el SDK instalado, no un problema real del proyecto.
- **Nota de troubleshooting reusable (casbin_rule):** si `dotnet run` truena con `SqlException: Invalid object name 'casbin_rule'` en un checkout viejo (antes del fix del 2026-08-18 en `Program.cs`), o si tu base de datos ya quedó bootstrapeada con el orden viejo, ver el paso de reseteo del volumen de SQL Server en [ImplementersGuide.md §8](ImplementersGuide.md#8-notas-operativas-conocidas).
- Convención de este repo: los cambios de estado de fase se reflejan **en este archivo**, no en archivos nuevos.
