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

# =============================================================================
# README.md
# =============================================================================
edit(
    "README.md",
    '''Ver [`Controllers/BlacklistController.cs`](src/Api.Web/Controllers/BlacklistController.cs) como referencia completa. La matriz de permisos inicial por rol vive en [`Authorization/CasbinPolicySeeder.cs`](src/Api.Web/Authorization/CasbinPolicySeeder.cs); se re-siembra en cada arranque de forma idempotente (no pisa cambios hechos luego a mano en la tabla de políticas).''',
    '''La matriz de permisos inicial por rol vive en [`Authorization/CasbinPolicySeeder.cs`](src/Api.Web/Authorization/CasbinPolicySeeder.cs); se re-siembra en cada arranque de forma idempotente (no pisa cambios hechos luego a mano en la tabla de políticas).''',
)

edit(
    "README.md",
    '''- **VehiculosRobados** — la Lista Negra auditada; Redis solo cachea `PlateText` para el lookup O(1) del camino caliente, esta tabla es la fuente de verdad.
''',
    '''- La lista de vehículos reportados (RedList) ya no vive en este repo -- la administra [RedLists](../../RedLists) (`vehicle_lists`/`vehicles`/`list_vehicles`, misma base `SistemaLPR`, mismo servidor MySQL) -- ver Fase 3.5 en [`docs/fases.md`](docs/fases.md). `Alertas.RedListVehicleId` referencia esas tablas sin FK real: RedLists las administra con su propio esquema SQL, fuera del historial de migraciones de este DbContext. Redis solo cachea `PlateText` (`redlist:active-plates`) para el lookup O(1) del camino caliente.
''',
)

edit(
    "README.md",
    '''- `BlacklistEntryAddedEvent` / `BlacklistEntryRemovedEvent` — para invalidar/actualizar Redis en segundos cuando se da de alta o de baja una placa en `VehiculosRobados`, en vez de esperar el refresco delta de 5 minutos.''',
    '''- La invalidación de la caché de Redis en segundos ya no usa eventos CAP propios de este repo (retirados en Fase 3.5) — `Service.Inference` se suscribe directo al hub de SignalR de RedLists (`VehicleAdded`/`VehicleRemoved`/`VehicleRecovered` en `/hubs/vehicle-lists`) en vez de esperar el refresco delta de 5 minutos.''',
)

print("=== FASE E completa ===")
