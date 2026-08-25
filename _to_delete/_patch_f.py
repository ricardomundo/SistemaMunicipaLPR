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

P = "tools/setup-new-machine.ps1"

old_block = '''# --- 4. user-secrets (NUNCA van en appsettings.json ni en git) ---
Step "Configurando user-secrets de Api.Web"
Push-Location src/Api.Web
dotnet user-secrets init 2>$null | Out-Null
$existingSecrets = dotnet user-secrets list 2>$null
if ($existingSecrets -match "ExternalBlacklist:BearerToken") {
    Ok "ExternalBlacklist:BearerToken ya está configurado -- se deja sin tocar"
}
else {
    $secureToken = Read-Host "Bearer token de la API externa de blacklist (Enter para omitir por ahora)" -AsSecureString
    $plainToken = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken))
    if ([string]::IsNullOrWhiteSpace($plainToken)) {
        Warn "Sin bearer token -- ExternalBlacklistSyncService fallará (401) hasta que lo configures a mano:`n  dotnet user-secrets set `"ExternalBlacklist:BearerToken`" `"TOKEN`" --project src/Api.Web"
    }
    else {
        dotnet user-secrets set "ExternalBlacklist:BearerToken" "$plainToken" | Out-Null
        Ok "ExternalBlacklist:BearerToken configurado"
    }
}
Pop-Location

# --- 5. Keycloak: realm, cliente, roles, usuario de prueba ---'''

new_block = '''# --- 4. Keycloak: realm, cliente, roles, usuario de prueba ---
# (Api.Web ya no necesita ningun user-secret propio -- ExternalBlacklist:BearerToken se retiro
# junto con el subsistema de blacklist propio en Fase 3.5, ver docs/fases.md. Los user-secrets
# de RedLists se configuran arriba, en el paso 1c.)'''

edit(P, old_block, new_block)

edit(
    P,
    '''Step "Listo"
Write-Host "Pendiente manual (no automatizable de forma segura):"
Write-Host "  - ExternalBlacklist:BearerToken, si lo omitiste arriba."
Write-Host "  - Confirmar que edge\\config.yaml y el modelo .pt están en su lugar."''',
    '''Step "Listo"
Write-Host "Pendiente manual (no automatizable de forma segura):"
Write-Host "  - Confirmar que edge\\config.yaml y el modelo .pt están en su lugar."''',
)

edit(
    P,
    '# --- 6. Pipeline Edge (Python) ---',
    '# --- 5. Pipeline Edge (Python) ---',
)

print("=== FASE F completa ===")
