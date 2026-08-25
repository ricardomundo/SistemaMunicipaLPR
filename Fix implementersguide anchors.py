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
    print(f"EDIT: {path} ({count}x)")
 
OLD_ANCHOR = "ImplementersGuide.md#11-alimentación-de-la-lista-negra-vehiculosrobados"
NEW_ANCHOR = "ImplementersGuide.md#11-integración-con-redlists-fase-35"
 
# Solo se repara el fragmento de ancla (la seccion #11 de ImplementersGuide.md se renombro al
# actualizar ese archivo para Fase 3.5) -- el resto del texto de estos tres docs sigue
# describiendo el subsistema de blacklist propio retirado, tarea explicitamente pendiente y
# separada (ver "Pendiente" en docs/fases.md, item 2: actualizar ArchitectureGuide.md/
# TechnicalDocumentation.md/ImplementersGuide.md). No se toca esa prosa aqui, solo se evita
# dejar un link roto como efecto colateral del rename.
edit("docs/ArchitectureGuide.md", OLD_ANCHOR, NEW_ANCHOR, count=1)
edit("docs/fases.md", OLD_ANCHOR, NEW_ANCHOR, count=1)
edit("docs/TechnicalDocumentation.md", OLD_ANCHOR, NEW_ANCHOR, count=3)
 
print("=== anclas de ImplementersGuide.md#11 reparadas ===")
 

