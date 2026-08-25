import os
import shutil

ROOT = "SistemaMunicipaLPR"
TRASH = os.path.join(ROOT, "_to_delete")

def w(path, content):
    full = os.path.join(ROOT, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w", encoding="utf-8") as f:
        f.write(content)
    print(f"WRITE: {path}")

def rm(path):
    full = os.path.join(ROOT, path)
    if os.path.exists(full):
        dest = os.path.join(TRASH, path)
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        shutil.move(full, dest)
        print(f"MOVED TO _to_delete/: {path}")
    else:
        print(f"SKIP (no existe): {path}")

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
# 4. Core.Contracts/EventTopics.cs -- quitar BlacklistEntryAdded/Removed
# =============================================================================
w("src/Core.Contracts/EventTopics.cs", '''namespace Core.Contracts;

/// <summary>
/// Nombres de topic usados por DotNetCore.CAP para publicar/suscribirse a cada evento. CAP
/// enruta por nombre de topic (string), no por tipo de mensaje .NET como hacía MassTransit,
/// así que cada evento necesita una constante aquí en vez de inferirse del nombre de la clase.
///
/// <see cref="PlateRead"/> NO está aquí a propósito: PlateReadEvent se saca del outbox de CAP
/// por volumen — ver <see cref="RawQueues.PlateRead"/> y ImplementersGuide.md §9 para el
/// hallazgo completo y el razonamiento.
///
/// La invalidación de la caché de RedLists en Redis (Service.Inference) ya NO usa eventos CAP
/// propios — BlacklistEntryAddedEvent/RemovedEvent se retiraron en Fase 3.5 (ver
/// docs/fases.md): Service.Inference se suscribe directo al hub de SignalR de RedLists
/// (VehicleListsService, "/hubs/vehicle-lists") en vez de a un evento propio de este repo.
/// </summary>
public static class EventTopics
{
    public const string BlacklistHitSaved = "blacklist-hit-saved-event";
}
''')

# =============================================================================
# 5. Api.Web/Authorization/CasbinPolicySeeder.cs -- quitar filas del recurso "blacklist"
#    (ya no existe ningun endpoint de blacklist en Api.Web -- ver Fase 3.5 en docs/fases.md)
# =============================================================================
edit(
    "src/Api.Web/Authorization/CasbinPolicySeeder.cs",
    '        ("SuperAdmin", "blacklist", "read"),\n        ("SuperAdmin", "blacklist", "write"),\n',
    "",
)
edit(
    "src/Api.Web/Authorization/CasbinPolicySeeder.cs",
    '        ("SupervisorC4", "blacklist", "read"),\n        ("SupervisorC4", "blacklist", "write"),\n',
    "",
)
edit(
    "src/Api.Web/Authorization/CasbinPolicySeeder.cs",
    '        ("OperadorC4", "blacklist", "read"),\n',
    "",
)
edit(
    "src/Api.Web/Authorization/CasbinPolicySeeder.cs",
    '        ("AuditorForense", "blacklist", "read"),\n',
    "",
)

# =============================================================================
# 6. Api.Web/Api.Web.csproj -- quitar ClosedXML (solo lo usaba TabularBlacklistFileParser,
#    ya retirado)
# =============================================================================
edit(
    "src/Api.Web/Api.Web.csproj",
    '''    <!-- Lectura de archivos .xlsx para la importación masiva de la lista negra (ver
         ImplementersGuide.md §11). MIT license, sin dependencia de Excel/Interop instalado.
         Versión sin verificar contra NuGet real (sin acceso a internet desde donde se escribió
         esto) — si "dotnet restore" no la encuentra, ajustar al último 0.104.x publicado. -->
    <PackageReference Include="ClosedXML" Version="0.104.2" />
''',
    "",
)

print("=== FASE B completa ===")
