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

P = "src/Api.Web/Program.cs"

edit(P, "using Api.Web.Data;\nusing Api.Web.Hubs;\nusing Api.Web.Services.Blacklist;\nusing Casbin;",
        "using Api.Web.Data;\nusing Api.Web.Hubs;\nusing Casbin;")

edit(P, "// --- Domain data: Camaras, VehiculosRobados, LecturasHistoricas, Alertas ---",
        "// --- Domain data: Camaras, LecturasHistoricas, Alertas (la lista de vehículos\n// reportados vive en RedLists -- VehicleListsService -- sobre la misma base SistemaLPR,\n// ver Fase 3.5 en docs/fases.md) ---")

old_block = '''// --- Fase 3: alimentación de la lista negra (VehiculosRobados) desde múltiples fuentes que
// traen los mismos datos (API externa, Excel, .txt) — ver ImplementersGuide.md §11.
// IBlacklistImportService concentra la reconciliación por placa (alta/baja/actualización) que
// usan tanto el import manual de archivos (BlacklistController.Import) como la sincronización
// periódica con la fuente externa (ExternalBlacklistSyncService).
builder.Services.AddScoped<IBlacklistImportService, BlacklistImportService>();

// HttpExternalBlacklistSource: GET simple con bearer token estático (ver
// ExternalBlacklistApiOptions — BaseUrl en appsettings.json, BearerToken vía user-secrets/env,
// NUNCA en appsettings.json). AddHttpClient<TInterface, TImplementation> registra un HttpClient
// tipado, con el auth handler inyectando el header en cada request.
builder.Services.Configure<ExternalBlacklistApiOptions>(builder.Configuration.GetSection(ExternalBlacklistApiOptions.SectionName));
builder.Services.AddTransient<ExternalBlacklistAuthHandler>();
builder.Services.AddHttpClient<IExternalBlacklistSource, HttpExternalBlacklistSource>(client =>
    {
        // Timeout corto a propósito: si la red/VPN hacia la API del cliente falla, queremos un
        // error claro y rápido en el log en vez de esperar el default de HttpClient (~100s) en
        // silencio antes de que aparezca cualquier mensaje.
        client.Timeout = TimeSpan.FromSeconds(20);
    })
    .AddHttpMessageHandler<ExternalBlacklistAuthHandler>();
builder.Services.AddHostedService<ExternalBlacklistSyncService>();

'''
new_block = '''// --- Fase 3.5: la lista de vehículos reportados (antes "blacklist" propia, alimentada por
// IBlacklistImportService/ExternalBlacklistSyncService) la administra ahora RedLists
// (VehicleListsService/ExternalVehicleFeedService, repo separado) -- ver docs/fases.md. Api.Web
// no tiene aquí ningún registro correspondiente; Service.Inference lee el esquema de RedLists
// directo (ver Service.Inference/RedListCacheService.cs).

'''
edit(P, old_block, new_block)

print("=== FASE C completa ===")
