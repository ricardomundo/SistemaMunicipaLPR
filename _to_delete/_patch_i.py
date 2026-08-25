import os
import shutil
import glob

ROOT = "SistemaMunicipaLPR"
TRASH = os.path.join(ROOT, "_to_delete")

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

# RawQueues.cs -- el comentario menciona BlacklistEntryAddedEvent/RemovedEvent como parte del
# outbox de CAP; esos dos eventos ya no existen (retirados en Fase 3.5).
edit(
    "src/Core.Contracts/RawQueues.cs",
    '''/// El resto de los eventos (BlacklistHitSavedEvent, BlacklistEntryAddedEvent/RemovedEvent —
/// ver <see cref="EventTopics"/>) son de bajo volumen y SÍ se quedan en CAP: se benefician del
/// outbox transaccional, reintentos automáticos, y la convención de "Group" por servicio, sin
/// que su costo por mensaje sea un problema a ese volumen.''',
    '''/// El resto de los eventos (BlacklistHitSavedEvent — ver <see cref="EventTopics"/>) son de
/// bajo volumen y SÍ se quedan en CAP: se benefician del outbox transaccional, reintentos
/// automáticos, y la convención de "Group" por servicio, sin que su costo por mensaje sea un
/// problema a ese volumen. (BlacklistEntryAddedEvent/RemovedEvent, que también vivían aquí,
/// se retiraron en Fase 3.5 -- ver docs/fases.md -- Service.Inference se suscribe ahora
/// directo al hub de SignalR de RedLists en vez de a un evento propio de este repo.)''',
)

# Mover los scripts de parche (_patch_*.py) fuera del repo -- son herramientas de esta sesion,
# no parte del codigo fuente.
for p in glob.glob(os.path.join(ROOT, "_patch_*.py")):
    name = os.path.basename(p)
    dest = os.path.join(TRASH, name)
    os.makedirs(TRASH, exist_ok=True)
    shutil.move(p, dest)
    print(f"MOVED patch script a _to_delete/: {name}")

print("=== FASE I completa ===")
