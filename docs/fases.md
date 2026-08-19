# Fases del Proyecto — Sistema Municipal LPR/ANPR

> Fuente única de verdad del avance por fase. Actualizar este archivo (no crear uno nuevo) cada vez que cambie el estado de una fase. Detalle de diseño en [ArchitectureGuide.md](ArchitectureGuide.md); detalle técnico exacto de lo construido en [TechnicalDocumentation.md](TechnicalDocumentation.md#7-estado-real-de-implementación-checklist-técnico).

## Resumen

| Fase | Nombre | Estado |
|---|---|---|
| Fase 0 | Infraestructura base | ✅ Completa |
| Pre-Fase 1 | Authentication & Authorization | ✅ Completa (verificada end-to-end) |
| Fase 1 | Contratos de eventos + modelo de datos | ✅ Completa (verificada end-to-end) |
| Fase 2 | Cache Redis + mensajería (DotNetCore.CAP + RabbitMQ.Client) + SignalR | ✅ Completa (verificada end-to-end) |
| Fase 3 | Simulador de carga + módulo Edge (Python/YOLO) | 🔶 En progreso |
| Fase 4 | Frontend C4 (dashboard web + mapa) | ⏳ Pendiente |

---

## Fase 0 — Infraestructura base ✅

**Objetivo:** esqueleto de la solución .NET y servicios de infraestructura local.

**Entregado:**
- `SistemaLPR.sln` con proyectos `Core.Contracts`, `Core.Domain`, `Service.Inference`, `Api.Web`.
- `docker-compose.yml`: SQL Server 2022, Redis, RabbitMQ, Keycloak.

---

## Pre-Fase 1 — Authentication & Authorization ✅

**Objetivo:** modelo de auth escalable antes de construir features protegidas por rol.

**Decisión:** Keycloak (autenticación, OIDC/JWT) + Casbin.NET (autorización, rol→permiso). Ver [ArchitectureGuide.md §4](ArchitectureGuide.md#4-modelo-de-autenticación-y-autorización).

**Entregado y verificado end-to-end:**
- JWT Bearer configurado en `Api.Web`, con aplanado de `realm_access.roles` de Keycloak a `ClaimTypes.Role`.
- Casbin.NET + adapter EF Core, modelo RBAC simple (`rbac_model.conf`), `CasbinResourceAttribute`/`CasbinRequirement`/`CasbinAuthorizationHandler`, `CasbinPolicySeeder` con la matriz inicial de 5 roles.
- Realm `sistema-lpr` y cliente `api-web` en Keycloak, con mapper de Audience; los 5 realm roles creados.
- Ciclo completo probado contra la API real: sin token → 401; con token válido y rol adecuado → 200, autorizado correctamente por Casbin.

---

## Fase 1 — Contratos de eventos + modelo de datos ✅

**Objetivo:** definir los contratos de eventos y el esquema de SQL Server.

**Entregado y verificado end-to-end:**
- `Core.Domain` (POCOs): `Camara` (con `Ubicacion` geoespacial), `VehiculoRobado`, `LecturaHistorica`, `Alerta`.
- `Core.Contracts`: `PlateReadEvent` (sin imagen base64 — ver [ArchitectureGuide.md §3](ArchitectureGuide.md#3-arquitectura-de-eventos-y-mensajería)), `BlacklistHitSavedEvent`, `BlacklistEntryAddedEvent`/`BlacklistEntryRemovedEvent`.
- `LprDbContext` + migración inicial (`InitialLprSchema`) con índice columnstore en `LecturasHistoricas`.
- El tipo `geography`/NetTopologySuite serializa y deserializa correctamente end-to-end contra SQL Server real.

---

## Fase 2 — Cache Redis + mensajería + SignalR ✅

**Objetivo:** el camino caliente real: cámara → mensajería → consumer → Redis lookup → alerta.

**Entregado y verificado end-to-end:**
1. `BlacklistCacheService` (`IHostedService`, en `Service.Inference`): carga inicial de `VehiculosRobados.Estado = Activo` al set de Redis `blacklist:active-plates` al arrancar, y refresco delta completo cada 5 min como respaldo.
2. `BlacklistEntryAddedConsumer`/`BlacklistEntryRemovedConsumer` (`Service.Inference`): invalidación inmediata del set de Redis al recibir `BlacklistEntryAddedEvent`/`RemovedEvent`. (El alta/baja real de `VehiculoRobado` que dispara estos eventos —`BlacklistController` en `Api.Web`— se implementó en Fase 3, ver abajo.)
3. `PlateReadConsumer` (`Service.Inference`): consume `PlateReadEvent` directo de RabbitMQ (fuera del outbox de CAP, por volumen — ver [ArchitectureGuide.md §3](ArchitectureGuide.md#3-arquitectura-de-eventos-y-mensajería)), hace un lookup O(1) contra Redis y, si hay match, publica `BlacklistHitSavedEvent` vía DotNetCore.CAP.
4. `BlacklistHitPersistenceConsumer` (`Service.Inference`): suscriptor de CAP de `BlacklistHitSavedEvent` — resuelve `CamaraId`/`VehiculoRobadoId` e inserta `LecturaHistorica` + `Alerta` vía Dapper, deduplicando por `EventId`.
5. `AlertNotificationConsumer` (`Api.Web`): otro suscriptor de CAP, independiente, del mismo `BlacklistHitSavedEvent` — empuja la alerta a `AlertHub` por SignalR.
6. `AlertHub` (SignalR) en `Api.Web`, ruta `/hubs/alerts`, `[Authorize]`, con backplane de Redis.

**Decisión de diseño:** `LecturaHistorica` solo se registra cuando hay match de blacklist — las lecturas sin coincidencia no se guardan, para no acumular información innecesaria. Configurable vía `PlateReadLogging:OnlyLogMatches` en `appsettings.json` de `Service.Inference` (default `true`).

**Verificado end-to-end** con `tools/VerifyFase2` contra el stack real completo (Docker Compose + `Api.Web` + `Service.Inference`): `seed` → `publish` (con match) confirma la cadena completa (Redis → persistencia SQL → SignalR); `publish-nomatch` confirma que no se genera ningún registro; `cleanup` limpia los datos de prueba.

---

## Fase 3 — Simulador de carga + módulo Edge 🔶

**Objetivo:** validar el presupuesto de latencia de <300ms bajo carga (50 cámaras × 10 lecturas/seg), y construir el pipeline de inferencia real.

**Entregado — simulador de carga, `tools/LoadSimulator`:**
- Proyecto consola C# con tres modos: `seed` (siembra N cámaras y una placa de prueba activa en la blacklist), `run` (publica carga sintética configurable, default 50 cámaras × 10 lecturas/seg) y `cleanup`.
- Mide throughput de publish y latencia real cámara→alerta sobre una fracción configurable de lecturas que generan match, correlacionando por `EventId`.
- Diseño y uso detallados en [ImplementersGuide.md §9](ImplementersGuide.md#9-herramientas-de-prueba-toolsverifyfase2-y-toolsloadsimulator).

**Entregado — pipeline Edge, `edge/` (Python):**
- Captura con OpenCV (con ráfaga + selección por nitidez para mitigar motion blur a 160 km/h), detección de placa con YOLO (ultralytics), OCR con PaddleOCR (con corrección de ángulo y un margen configurable alrededor del bbox antes de leer el texto), selección por confianza (con un filtro opcional por formato de placa vía regex, apagado por default), y publish de `PlateReadEvent` directo a RabbitMQ (cola `plate-read-event.raw`) — sin pasar por CAP, igual que el lado .NET.
- Buffer local en SQLite + hilo de drenado en background para tolerancia a cortes de red.
- Diseño y uso detallados en [ImplementersGuide.md §10](ImplementersGuide.md#10-pipeline-edge-python--edge).

**En progreso — modelo de detección de placas:** el pipeline Edge no incluye un modelo todavía. Se evaluaron varios candidatos locales además del dataset de Roboflow:

- `Car-License-Detection/best.pt` — YOLOv8, clase única de placa, entrenado sobre un dataset genérico de Kaggle (433 imágenes, no específico de México). Carga directa con `ultralytics.YOLO(path)`, sin dependencias ni arquitectura especial — compatible tal cual con `edge/src/detector.py`. Candidato más rápido de probar.
- `License-Plate-Detector/` — descartado: es un fork chino (zeusees) de YOLOv5 con cabeza de salida custom (landmarks/esquinas, estilo yolov5-face) para clasificar tipos de placa chinos por color (azul, amarilla, verde de nuevas energías, etc.). Incompatible tanto en arquitectura (no es un `ultralytics.YOLO` estándar) como en dominio (placas chinas, no mexicanas).
- `CarRecognition/` — descartado: es una app Android (Gradle), sin relación con detección de placas.
- `Custom Workflow Object Detection.v2i.yolov8/` — dataset YOLOv8 ya extraído (no zip), del workspace Roboflow `placasmexicanas`, 101 imágenes (train/valid/test) etiquetadas específicamente sobre placas mexicanas. Dos clases (`placa`, `placa1` — pendiente confirmar en Roboflow qué distingue una de otra). Por su tamaño (101 imágenes) sirve mejor para *fine-tuning* de un modelo ya entrenado que para entrenar uno desde cero.
- `training/Full-Plate-Dataset-1/roboflow.zip` — descarga previa desde Roboflow (`cv-0zohq`/`full-plate-dataset`), quedó sin extraer correctamente; sigue pendiente si se decide no usar las alternativas de arriba.

**Decisión:** `Car-License-Detection/best.pt` se probó contra fotos reales de placas mexicanas (prueba visual con `yolo predict`, fuera del pipeline) y detecta bien — se adopta como modelo de detección, sin necesidad de fine-tuning ni de retomar `full-plate-dataset`/`placasmexicanas` por ahora. Esos dos datasets quedan como opción de respaldo si más adelante aparecen casos donde falle (ángulos, distancia, placas dobles, etc.).

**Verificado end-to-end:** primera corrida completa del pipeline Edge contra un video de prueba real — captura (OpenCV) → detección (`Car-License-Detection/best.pt` copiado a `edge/models/plate_detector.pt`) → OCR (PaddleOCR) → publish a `plate-read-event.raw` → `PlateReadConsumer` → lookup en Redis → `BlacklistHitSavedEvent` (CAP) → `BlacklistHitPersistenceConsumer` → alerta registrada en SQL. Requiere Python 3.11 para el venv de `edge/` (ver [ImplementersGuide.md §10](ImplementersGuide.md#10-pipeline-edge-python--edge)).

**Entregado — `BlacklistController` (`Api.Web`):** alta (`POST /api/blacklist`) y baja (`DELETE /api/blacklist/{plateText}`) real de `VehiculoRobado`, protegidos con el permiso `blacklist`/`write` (Casbin). Cada alta/baja publica `BlacklistEntryAddedEvent`/`BlacklistEntryRemovedEvent` vía CAP, invalidando el caché de Redis de inmediato en vez de esperar el refresco delta de 5 min. El texto de placa se normaliza (mayúsculas, sin guiones/espacios) con el mismo criterio que usa el OCR del pipeline Edge, para garantizar match exacto.

**Confirmado:** `dotnet build` de la solución completa compila limpio — el fix de la API async de RabbitMQ.Client 7.x y el nuevo `BlacklistController` ya están verificados contra el proyecto real.

**Entregado — alimentación de la lista negra (`Api.Web/Services/Blacklist/`):** la lista negra se alimenta de tres fuentes que traen los mismos datos (confirmado con el negocio) — una API externa, archivos Excel y archivos `.txt` — reconciliadas por placa en un solo servicio compartido (`BlacklistImportService`). `VehiculoRobado` creció con 7 columnas descriptivas nullable (`ImagenPath`, `Modelo`, `Anio`, `Marca`, `Color`, `Clase`, `MarcasUOtros`). El import de Excel/.txt es un endpoint nuevo (`POST /api/blacklist/import`); la sincronización con la API externa quedó lista como worker periódico (`ExternalBlacklistSyncService`) pero con un placeholder, porque ese servicio externo todavía no existe. Detalle completo en [ImplementersGuide.md §11](ImplementersGuide.md#11-alimentación-de-la-lista-negra-vehiculosrobados).

**Confirmado:** migración de EF Core generada/aplicada y `dotnet build` limpio con las columnas nuevas de `VehiculosRobados` y el paquete `ClosedXML`.

**Para retomar:**
1. Probar `POST /api/blacklist/import` con un archivo real (Excel o .txt) de alguna de las tres fuentes — se escribió sin tener un archivo de muestra a la mano; si alguna columna no matchea, ajustar `ColumnAliases` en `TabularBlacklistFileParser.cs`.
2. Definir el spec real de la API externa (endpoint, autenticación, formato) para reemplazar `PlaceholderExternalBlacklistSource` por la implementación real.
3. Correr `tools/LoadSimulator` para validar por fin la latencia cámara→alerta bajo carga (<300ms) — el objetivo original de esta mitad de Fase 3, todavía sin confirmar.
4. Diseñar el uploader de imágenes de placa hacia un storage central (hoy solo se guardan en disco local del nodo Edge).
5. Rotar la API key de Roboflow usada para descargar el dataset (quedó expuesta en texto plano durante esta sesión de trabajo).

---

## Fase 4 — Frontend C4 ⏳

**Objetivo:** dashboard web (React o Angular, aún sin elegir) con mapa de cámaras/alertas (ESRI o Google Maps — la ubicación de cámara ya se modela como `geography` en Fase 1) y consumo del `AlertHub` de SignalR.

**Estado:** no planificada en detalle todavía — depende de que Fase 3 (datos reales fluyendo) esté completa.

---

## Notas

Convención de este repo: los cambios de estado de fase se reflejan **en este archivo**, no en archivos nuevos. Notas operativas y troubleshooting reusable viven en [ImplementersGuide.md §8](ImplementersGuide.md#8-notas-operativas-conocidas).
