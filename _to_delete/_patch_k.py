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

# README.md -- ejemplo de endpoint protegido con Casbin usaba "blacklist" como resource de
# ejemplo; ese resource ya no existe en CasbinPolicySeeder.cs (el subsistema de blacklist
# propio se retiro en Fase 3.5, ver docs/fases.md). "camaras" si sigue siendo un resource
# real sembrado para varios roles.
edit(
    "README.md",
    '''```csharp
[Authorize(Policy = "Casbin")]
[CasbinResource("blacklist", "write")]
public IActionResult Post() => ...
```''',
    '''```csharp
[Authorize(Policy = "Casbin")]
[CasbinResource("camaras", "write")]
public IActionResult Post() => ...
```''',
)

print("=== FASE K completa ===")
