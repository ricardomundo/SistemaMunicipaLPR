import os

ROOT = "SistemaMunicipaLPR"

def edit(path, old, new, count=1):
    full = os.path.join(ROOT, path)
    with open(full, "r", encoding="utf-8") as f:
        content = f.read()
    occurrences = content.count(old)
    assert occurrences >= count, f"EDIT FAILED (no encontrado x{count}, hay {occurrences}): {path}\n---OLD---\n{old[:300]}"
    content = content.replace(old, new, count)
    with open(full, "w", encoding="utf-8") as f:
        f.write(content)
    print(f"EDIT: {path}")

P = "docs/fases.md"

edit(
    P,
    "| Fase 3.5 | Integración con RedLists (lista roja de vehículos, reemplaza la blacklist propia) | ⏳ Base de datos consolidada; falta retirar blacklist propio y reconectar Service.Inference |",
    "| Fase 3.5 | Integración con RedLists (lista roja de vehículos, reemplaza la blacklist propia) | ⏳ Blacklist propio retirado y Service.Inference reconectado; falta decidir Api.Web↔VehicleListsService y actualizar los 3 docs de arquitectura |",
)

old_implementado = '''**Implementado:**
- Esquema de RedLists (`vehicle_lists`, `vehicles`, `list_vehicles`, `adapters`, `sync_runs`, `imported_vehicles_log`) consolidado en la base `SistemaLPR` — ver [`db/redlists-schema.sql`](../db/redlists-schema.sql) (mismas tablas/tipos que los scripts originales de RedLists, `GRANT` acotados a `SistemaLPR.*` en vez de `redlists.*`). `tools/setup-new-machine.ps1` lo aplica automáticamente contra el contenedor `lpr-mysql`.
- `VehicleListsService`/`ExternalVehicleFeedService` repuntados a `SistemaLPR`: `tools/setup-new-machine.ps1` configura su `ConnectionString` vía `dotnet user-secrets` (nunca en `appsettings.json`) cuando el repo RedLists está clonado como carpeta hermana (`..\\RedLists`).
- El `docker-compose.yml`/`db/README.md` propios de RedLists quedan marcados como obsoletos dentro de ese repo (solo referencia histórica) — la infraestructura vigente es la de este repo.

**Pendiente:**
1. Eliminar el subsistema de blacklist propio (código + migración) de este repo, una vez migrados los datos existentes que haga falta conservar.
2. Reescribir `BlacklistCacheService`/`Service.Inference` contra el esquema de RedLists (consulta agregada + suscripción a SignalR en vez de CAP).
3. Decidir si `Api.Web` necesita llamar a `VehicleListsService` por HTTP en algún flujo (hoy ninguno de los 4 componentes de RedLists tiene autenticación implementada — relevante si se expone algo más allá del acceso directo a MySQL desde `Service.Inference`).
4. Actualizar `ArchitectureGuide.md`/`TechnicalDocumentation.md`/`ImplementersGuide.md` para reflejar RedLists como arquitectura vigente, sin referencias a `VehiculosRobados`/`BlacklistController`.'''

new_implementado = '''**Implementado:**
- Esquema de RedLists (`vehicle_lists`, `vehicles`, `list_vehicles`, `adapters`, `sync_runs`, `imported_vehicles_log`) consolidado en la base `SistemaLPR` — ver [`db/redlists-schema.sql`](../db/redlists-schema.sql) (mismas tablas/tipos que los scripts originales de RedLists, `GRANT` acotados a `SistemaLPR.*` en vez de `redlists.*`). `tools/setup-new-machine.ps1` lo aplica automáticamente contra el contenedor `lpr-mysql`.
- `VehicleListsService`/`ExternalVehicleFeedService` repuntados a `SistemaLPR`: `tools/setup-new-machine.ps1` configura su `ConnectionString` vía `dotnet user-secrets` (nunca en `appsettings.json`) cuando el repo RedLists está clonado como carpeta hermana (`..\\RedLists`).
- El `docker-compose.yml`/`db/README.md` propios de RedLists quedan marcados como obsoletos dentro de ese repo (solo referencia histórica) — la infraestructura vigente es la de este repo.
- Subsistema de blacklist propio retirado de este repo: `VehiculoRobado`/`EstadoVehiculoRobado` (`Core.Domain`), `BlacklistController`, `BlacklistImportService` y el resto de `Api.Web/Services/Blacklist/` (incluida la importación por Excel/.txt/API externa), `BlacklistEntryAddedEvent`/`RemovedEvent` (`Core.Contracts`) y los consumers `BlacklistEntryAdded/RemovedConsumer` (`Service.Inference`). `Alertas.VehiculoRobadoId` (int, con FK real) se reemplazó por `Alertas.RedListVehicleId` (long, sin FK real — apunta a `vehicles.id` de RedLists, que este `DbContext` no administra). Las políticas Casbin del recurso `blacklist` se quitaron del seeder (ya no hay ningún endpoint que las use).
- `Service.Inference` reconectado a RedLists: `RedListCacheService` (reemplaza a `BlacklistCacheService`) mantiene `redlist:active-plates` en Redis con una consulta agregada por placa (`DISTINCT`, `recovered_by_org_id = 0`, `list_type = 'Vehicles'`) más una suscripción como cliente de SignalR al hub de RedLists (`VehicleAdded`/`VehicleRemoved`/`VehicleRecovered` en `/hubs/vehicle-lists`, con reconexión automática y resincronización completa al reconectar) — ya no depende de eventos CAP propios. `BlacklistHitPersistenceConsumer` ahora resuelve el `vehicles.id` de RedLists por placa al registrar cada `Alerta`.

**Pendiente:**
1. Decidir si `Api.Web` necesita llamar a `VehicleListsService` por HTTP en algún flujo (hoy ninguno de los 4 componentes de RedLists tiene autenticación implementada — relevante si se expone algo más allá del acceso directo a MySQL desde `Service.Inference`).
2. Actualizar `ArchitectureGuide.md`/`TechnicalDocumentation.md`/`ImplementersGuide.md` para reflejar RedLists como arquitectura vigente, sin referencias a `VehiculosRobados`/`BlacklistController`.

**Nota de migración:** el esquema de `LprDbContext` cambió (se eliminó `VehiculosRobados`, `Alertas.VehiculoRobadoId` pasó a `RedListVehicleId`) y la única migración existente (`InitialLprSchema`) se eliminó junto con el resto del subsistema retirado — `tools/setup-new-machine.ps1` regenera una migración `InitialLprSchema` limpia automáticamente si no encuentra ninguna. En una máquina que ya tenía la base `SistemaLPR` de antes de este cambio, hace falta reiniciar el volumen de MySQL (`docker compose down -v` seguido de `docker compose up -d` y volver a correr `tools/setup-new-machine.ps1`, o `docker compose exec -T mysql mysql ...` para dropear/recrear `SistemaLPR` a mano) antes de que la migración regenerada se pueda aplicar limpiamente.'''

edit(P, old_implementado, new_implementado)

print("=== FASE fases.md completa ===")
