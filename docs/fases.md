# Fases del Proyecto — Sistema Municipal LPR/ANPR

> Fuente única de verdad del avance por fase. Actualizar este archivo (no crear uno nuevo) cada vez que cambie el estado de una fase. Detalle de diseño en [ArchitectureGuide.md](ArchitectureGuide.md); detalle técnico exacto de lo construido en [TechnicalDocumentation.md](TechnicalDocumentation.md#7-estado-real-de-implementación-checklist-técnico).

## Resumen

| Fase | Nombre | Estado |
|---|---|---|
| Fase 0 | Infraestructura base | ✅ Completa |
| Pre-Fase 1 | Authentication & Authorization | ✅ Completa |
| Fase 1 | Contratos de eventos + modelo de datos | ✅ Completa (pendiente de verificación end-to-end con Docker) |
| Fase 2 | Cache Redis + mensajería MassTransit/RabbitMQ + SignalR | ⏳ Pendiente |
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

**Pendiente:** crear manualmente el realm `sistema-lpr` y el cliente `api-web` en una instancia real de Keycloak (documentado en [ImplementersGuide.md §3](ImplementersGuide.md#3-configurar-keycloak-una-sola-vez)) — no verificado end-to-end por falta de Docker en el entorno de desarrollo usado.

---

## Fase 1 — Contratos de eventos + modelo de datos ✅

**Objetivo:** definir los contratos de MassTransit y el esquema de SQL Server.

**Entregado:**
- Nuevo proyecto `Core.Domain` (POCOs): `Camara` (con `Ubicacion` geoespacial), `VehiculoRobado`, `LecturaHistorica`, `Alerta`.
- `Core.Contracts`: `PlateReadEvent` (sin imagen base64 — revisado por riesgo de latencia), `BlacklistHitSavedEvent`, `BlacklistEntryAddedEvent`/`BlacklistEntryRemovedEvent` (nuevos, para invalidación inmediata de Redis).
- `LprDbContext` + migración inicial (`InitialLprSchema`) con índice columnstore en `LecturasHistoricas`.
- `dotnet build` limpio en los 4 proyectos.

**Pendiente:** aplicar la migración contra un SQL Server real y confirmar que el tipo `geography`/NetTopologySuite serializa correctamente (no probado end-to-end, mismo motivo que Pre-Fase 1).

---

## Fase 2 — Cache Redis + mensajería + SignalR ⏳

**Objetivo:** el camino caliente real: cámara → RabbitMQ → consumer → Redis lookup → alerta.

**Por construir:**
1. `BlacklistCacheService` (`IHostedService`): carga inicial de `VehiculosRobados` activos a Redis + suscripción a `BlacklistEntryAddedEvent`/`RemovedEvent` (invalidación inmediata) + refresco delta de 5 min como respaldo.
2. `PlateReadConsumer` (MassTransit, en `Service.Inference`): lookup Redis, publica `BlacklistHitSavedEvent` si hay match, deduplica por `EventId`.
3. Consumer de persistencia async (Dapper) para `BlacklistHitSavedEvent` → inserta `LecturaHistorica` + `Alerta`.
4. `AlertHub` (SignalR) en `Api.Web`, ruta `/hubs/alerts`, con backplane de Redis para escalar a múltiples instancias.

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

- **Bloqueo de verificación:** ninguna fase construida hasta ahora (Fase 0, Pre-Fase 1, Fase 1) se probó de punta a punta contra los servicios reales — el entorno de desarrollo usado no tenía Docker disponible. El usuario instalará Docker Desktop en una laptop (la VM actual podría no soportar virtualización). Verificar esto es la primera tarea antes de retomar.
- Convención de este repo: los cambios de estado de fase se reflejan **en este archivo**, no en archivos nuevos.
