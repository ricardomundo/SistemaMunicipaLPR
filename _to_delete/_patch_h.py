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

edit(
    "src/Service.Inference/Service.Inference.csproj",
    '''    <!-- Cliente de SignalR para suscribirse al hub de RedLists (VehicleListsService,
         "/hubs/vehicle-lists") y recibir VehicleAdded/VehicleRemoved/VehicleRecovered en vivo,
         en vez de los eventos CAP propios que este proyecto usaba antes de Fase 3.5 (ver
         docs/fases.md). Version sin verificar contra NuGet real (sin acceso a internet desde
         donde se escribio esto) -- si "dotnet restore" no la encuentra, ajustar a la ultima
         9.0.x publicada. -->''',
    '''    <!-- Cliente de SignalR para suscribirse al hub de RedLists (VehicleListsService,
         "/hubs/vehicle-lists") y recibir VehicleAdded/VehicleRemoved/VehicleRecovered en vivo,
         en vez de los eventos CAP propios que este proyecto usaba antes de Fase 3.5 (ver
         docs/fases.md). Version sin verificar contra NuGet real (sin acceso a internet desde
         donde se escribio esto); si "dotnet restore" no la encuentra, ajustar a la ultima
         9.0.x publicada. -->''',
)

print("=== FASE H completa ===")
