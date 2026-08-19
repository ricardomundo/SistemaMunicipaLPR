<#
.SYNOPSIS
  Bootstrap de una máquina nueva para SistemaMunicipaLPR, después de clonar el repo.

.DESCRIPTION
  Automatiza lo que SÍ viaja con git (levantar infraestructura, aplicar migraciones,
  restaurar paquetes, dar de alta el realm/cliente/roles de Keycloak) y avisa
  explícitamente sobre lo que NO viaja con git y no se puede automatizar de forma
  segura: secretos (user-secrets), el modelo YOLO entrenado (edge/models/*.pt) y el
  edge/config.yaml real de una cámara. Ver docs/fases.md y docs/ImplementersGuide.md
  para contexto completo del proyecto.

  Uso (desde la raíz del repo, después de "git clone"):
    .\tools\setup-new-machine.ps1

  Requiere en PATH: Docker Desktop corriendo, .NET 9 SDK, git. Python es opcional
  (solo para el pipeline edge/) — usa -SkipEdgeVenv si no lo vas a instalar en esta
  máquina.
#>

param(
    [switch]$SkipKeycloakBootstrap,
    [switch]$SkipEdgeVenv
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot  # tools/.. = raíz del repo
Set-Location $repoRoot

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "OK: $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "AVISO: $msg" -ForegroundColor Yellow }

# --- 0. Prerequisitos ---
Step "Verificando prerequisitos"
foreach ($cmd in @("docker", "dotnet", "git")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        throw "'$cmd' no está en PATH. Instálalo antes de continuar."
    }
}
Ok "docker, dotnet, git presentes"

if (-not $SkipEdgeVenv -and -not (Get-Command python -ErrorAction SilentlyContinue)) {
    Warn "python no está en PATH -- se omitirá el setup de edge/ (usa -SkipEdgeVenv para silenciar este aviso, o instala Python)."
    $SkipEdgeVenv = $true
}

# --- 1. Infraestructura (docker compose) ---
Step "Levantando SQL Server, Redis, RabbitMQ, Keycloak (docker compose up -d)"
docker compose up -d

Step "Esperando healthchecks..."
$services = @("lpr-sqlserver", "lpr-redis", "lpr-rabbitmq")
foreach ($svc in $services) {
    $tries = 0
    do {
        Start-Sleep -Seconds 3
        $health = docker inspect --format='{{.State.Health.Status}}' $svc 2>$null
        $tries++
        if ($tries -gt 40) { throw "$svc no quedó healthy a tiempo -- revisa 'docker compose logs $svc'." }
    } while ($health -ne "healthy")
    Ok "$svc healthy"
}
# Keycloak (start-dev) no tiene healthcheck definido en docker-compose.yml -- se valida más
# abajo reintentando el primer request contra su API admin.

# --- 2. Build ---
Step "dotnet build"
dotnet build "$repoRoot\SistemaLPR.sln"
Ok "build correcto"

# --- 3. Migraciones EF Core ---
# Program.cs ya aplica EnsureCreated()/Migrate() automáticamente al arrancar Api.Web (ver el
# comentario sobre el orden Casbin -> LprDbContext en Program.cs), pero lo hacemos explícito
# aquí para no depender de arrancar la API a mano la primera vez.
Step "Aplicando migraciones de base de datos"
if (-not (dotnet tool list --global | Select-String "dotnet-ef")) {
    dotnet tool install --global dotnet-ef
}
dotnet ef database update --project src/Api.Web
Ok "migraciones aplicadas"

# --- 4. user-secrets (NUNCA van en appsettings.json ni en git) ---
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

# --- 5. Keycloak: realm, cliente, roles, usuario de prueba ---
if ($SkipKeycloakBootstrap) {
    Warn "Bootstrap de Keycloak omitido (-SkipKeycloakBootstrap)"
}
else {
    Step "Configurando Keycloak (realm sistema-lpr, cliente api-web, roles, usuario de prueba)"

    $kcUrl = "http://localhost:8080"
    $adminUser = "admin"
    $adminPass = "Lpr#Dev_2026!"   # mismo valor que KEYCLOAK_ADMIN_PASSWORD en docker-compose.yml

    # Keycloak en start-dev tarda unos segundos más en aceptar requests que en pasar el
    # healthcheck TCP del contenedor -- reintenta el login del admin unas cuantas veces.
    $kcToken = $null
    for ($i = 0; $i -lt 15; $i++) {
        try {
            $tokenResp = Invoke-RestMethod -Method Post -Uri "$kcUrl/realms/master/protocol/openid-connect/token" -Body @{
                client_id  = "admin-cli"
                username   = $adminUser
                password   = $adminPass
                grant_type = "password"
            }
            $kcToken = $tokenResp.access_token
            break
        }
        catch {
            Start-Sleep -Seconds 4
        }
    }
    if (-not $kcToken) { throw "No se pudo autenticar contra Keycloak admin -- revisa 'docker compose logs keycloak'." }
    $kcHeaders = @{ Authorization = "Bearer $kcToken" }

    # Realm (idempotente: si ya existe, Keycloak responde 409 y se ignora)
    try {
        Invoke-RestMethod -Method Post -Uri "$kcUrl/admin/realms" -Headers $kcHeaders -ContentType "application/json" `
            -Body (@{ realm = "sistema-lpr"; enabled = $true } | ConvertTo-Json)
        Ok "realm 'sistema-lpr' creado"
    }
    catch { Ok "realm 'sistema-lpr' ya existía" }

    # Cliente api-web -- confidential, con Direct Access Grants (password grant) habilitado,
    # que es el flujo usado por las pruebas de este proyecto (usuario importer.test).
    $clientsUrl = "$kcUrl/admin/realms/sistema-lpr/clients"
    $existingClient = Invoke-RestMethod -Uri "$clientsUrl?clientId=api-web" -Headers $kcHeaders
    if ($existingClient.Count -eq 0) {
        Invoke-RestMethod -Method Post -Uri $clientsUrl -Headers $kcHeaders -ContentType "application/json" -Body (@{
                clientId                  = "api-web"
                publicClient              = $false
                directAccessGrantsEnabled = $true
                standardFlowEnabled       = $true
                serviceAccountsEnabled    = $false
            } | ConvertTo-Json)
        Ok "cliente 'api-web' creado"
        $existingClient = Invoke-RestMethod -Uri "$clientsUrl?clientId=api-web" -Headers $kcHeaders
    }
    else {
        Ok "cliente 'api-web' ya existía"
    }
    $clientUuid = $existingClient[0].id
    $secretResp = Invoke-RestMethod -Uri "$clientsUrl/$clientUuid/client-secret" -Headers $kcHeaders
    Write-Host "`nCLIENT SECRET de 'api-web' (guárdalo -- lo necesitas para probar el login):" -ForegroundColor Magenta
    Write-Host "  $($secretResp.value)`n" -ForegroundColor Magenta

    # Roles de realm
    $roles = @("SuperAdmin", "SupervisorC4", "OperadorC4", "PatrullaMovil", "AuditorForense")
    $rolesUrl = "$kcUrl/admin/realms/sistema-lpr/roles"
    foreach ($role in $roles) {
        try {
            Invoke-RestMethod -Method Post -Uri $rolesUrl -Headers $kcHeaders -ContentType "application/json" -Body (@{ name = $role } | ConvertTo-Json)
            Ok "rol '$role' creado"
        }
        catch { Ok "rol '$role' ya existía" }
    }

    # Usuario de prueba importer.test (el mismo usado en las pruebas de import de blacklist),
    # con rol OperadorC4.
    $usersUrl = "$kcUrl/admin/realms/sistema-lpr/users"
    $existingUser = Invoke-RestMethod -Uri "$usersUrl?username=importer.test" -Headers $kcHeaders
    if ($existingUser.Count -eq 0) {
        Invoke-RestMethod -Method Post -Uri $usersUrl -Headers $kcHeaders -ContentType "application/json" -Body (@{
                username = "importer.test"; enabled = $true
            } | ConvertTo-Json)
        $existingUser = Invoke-RestMethod -Uri "$usersUrl?username=importer.test" -Headers $kcHeaders
        $userId = $existingUser[0].id

        $testPassword = Read-Host "Password para el usuario de prueba 'importer.test' (Enter = 'Generico2026')"
        if ([string]::IsNullOrWhiteSpace($testPassword)) { $testPassword = "Generico2026" }
        Invoke-RestMethod -Method Put -Uri "$usersUrl/$userId/reset-password" -Headers $kcHeaders -ContentType "application/json" -Body (@{
                type = "password"; value = $testPassword; temporary = $false
            } | ConvertTo-Json)

        $roleRep = Invoke-RestMethod -Uri "$rolesUrl/OperadorC4" -Headers $kcHeaders
        Invoke-RestMethod -Method Post -Uri "$usersUrl/$userId/role-mappings/realm" -Headers $kcHeaders -ContentType "application/json" -Body (ConvertTo-Json @($roleRep))
        Ok "usuario 'importer.test' creado con rol OperadorC4 (password: $testPassword)"
    }
    else {
        Ok "usuario 'importer.test' ya existía"
    }
}

# --- 6. Pipeline Edge (Python) ---
if ($SkipEdgeVenv) {
    Warn "Setup de edge/ omitido"
}
else {
    Step "Creando venv e instalando dependencias de edge/"
    Push-Location edge
    if (-not (Test-Path venv)) { python -m venv venv }
    . .\venv\Scripts\Activate.ps1
    pip install -r requirements.txt
    deactivate
    Pop-Location
    Ok "venv de edge/ listo"

    if (-not (Test-Path edge\config.yaml)) {
        Warn "edge\config.yaml no existe (está en .gitignore a propósito -- config real, no código) -- cópialo desde la otra máquina o parte de edge\config.example.yaml"
    }
    if (-not (Get-ChildItem edge\models -Filter *.pt -ErrorAction SilentlyContinue)) {
        Warn "No hay ningún .pt en edge\models\ (los pesos del modelo NO viajan con git, ver .gitignore) -- cópialo desde la otra máquina o consíguelo de nuevo (ver docs/ImplementersGuide.md §10)"
    }
}

Step "Listo"
Write-Host "Pendiente manual (no automatizable de forma segura):"
Write-Host "  - ExternalBlacklist:BearerToken, si lo omitiste arriba."
Write-Host "  - Confirmar que edge\config.yaml y el modelo .pt están en su lugar."
Write-Host "`nPara arrancar la API: dotnet run --project src/Api.Web"
