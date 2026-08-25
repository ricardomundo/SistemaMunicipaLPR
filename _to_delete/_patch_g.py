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

# appsettings.json de Api.Web -- quitar la seccion ExternalBlacklist muerta (ExternalBlacklistApiOptions
# ya no existe, retirado junto con el subsistema de blacklist propio en Fase 3.5).
edit(
    "src/Api.Web/appsettings.json",
    ''',
 "ExternalBlacklist": {
  "BaseUrl": "http://192.168.10.123:5000/api/reportes"
  }
}''',
    '''
}''',
)

# AlertNotificationConsumer.cs -- nombre del metodo de SignalR, consistente con la terminologia
# RedList de Fase 3.5 (nadie mas lo consume todavia -- el frontend de Fase 4 no existe aun).
edit(
    "src/Api.Web/Consumers/AlertNotificationConsumer.cs",
    'await _hubContext.Clients.All.SendAsync("AlertaBlacklist", new',
    'await _hubContext.Clients.All.SendAsync("AlertaRedList", new',
)

print("=== FASE G completa ===")
