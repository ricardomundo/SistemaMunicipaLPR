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
    "README.md",
    "  Core.Domain/         Entidades del dominio (Camara, VehiculoRobado, LecturaHistorica, Alerta) — POCOs sin dependencia de EF Core, usados por Api.Web (EF Core) y Service.Inference (Dapper)",
    "  Core.Domain/         Entidades del dominio (Camara, LecturaHistorica, Alerta) — POCOs sin dependencia de EF Core, usados por Api.Web (EF Core) y Service.Inference (Dapper); Alerta.RedListVehicleId referencia vehicles.id de RedLists (repo separado, ver Fase 3.5 en docs/fases.md)",
)

print("=== FASE J completa ===")
