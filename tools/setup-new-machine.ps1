<#
.SYNOPSIS
  Bootstrap de una máquina nueva para la solución completa: SistemaMunicipaLPR + RedLists +
  AdminService.

.DESCRIPTION
  Automatiza lo que SÍ viaja con git (levantar infraestructura, aplicar esquemas/migraciones,
  restaurar/compilar los 3 proyectos, dar de alta realm/clientes/roles de Keycloak, y arrancar
  los 5 servicios backend) y avisa explícitamente sobre lo que NO viaja con git y no se puede
  automatizar de forma segura: secretos (user-secrets), el modelo YOLO entrenado
  (edge/models/*.pt) y el edge/config.yaml real de una cámara. Ver docs/fases.md y
  docs/ImplementersGuide.md para contexto completo del proyecto, y
  C:\Ric68\docs\ImplementationGuide.md para la guía consolidada de los 3 proyectos.

  Este script vive en el repo SistemaMunicipaLPR pero orquesta los 3 repos, que son
  independientes entre sí (sin ProjectReference cruzados) y se coordinan solo a través de una
  base de datos MySQL física compartida ("SistemaLPR") y un realm de Keycloak compartido
  ("sistema-lpr"). RedLists y AdminService son OPCIONALES: si no están clonados como carpetas
  hermanas de este repo (..\RedLists, ..\AdminService), sus pasos se omiten automáticamente con
  un aviso -- este script sigue siendo válido para levantar solo SistemaMunicipaLPR.

  Uso (desde la raíz de este repo, después de "git clone"):
    .\tools\setup-new-machine.ps1

  Requiere en PATH: Docker Desktop corriendo, .NET 9 SDK (SistemaMunicipaLPR/AdminService) y
  .NET 8 SDK (RedLists), git, curl.exe (incluido desde Windows 10 1803 / Windows 11 -- usado
  para el bootstrap de Keycloak, ver el paso 4). Python es opcional (solo para el pipeline
  edge/) -- usa -SkipEdgeVenv si no lo vas a instalar en esta máquina.

.PARAMETER SkipKeycloakBootstrap
  Omite el paso 4 (realm/clientes/roles de Keycloak). Útil si Keycloak ya está configurado o si
  no lo vas a usar todavía.

.PARAMETER SkipEdgeVenv
  Omite el paso 5 (venv de Python para el pipeline edge/).

.PARAMETER SkipStartServices
  Omite el paso 6 (arrancar los 5 servicios backend al final). Útil si solo querés dejar la
  infraestructura/DB/Keycloak listos y arrancar los servicios vos mismo, a mano o desde tu IDE.

.PARAMETER ResetDatabase
  Antes de levantar infraestructura, borra el volumen de Docker de MySQL (mysql_data) --
  TODOS los datos de la base "SistemaLPR" se pierden (SistemaMunicipaLPR + RedLists +
  AdminService, ya que comparten la misma base física). Se recrea vacía y este mismo script
  vuelve a aplicar esquema/migraciones desde cero en los pasos que siguen. Pedirá confirmación
  antes de borrar, salvo que también se pase -Force.

.PARAMETER ResetKeycloak
  Antes de levantar infraestructura, borra el volumen de Docker de Keycloak (keycloak_data) --
  el realm "sistema-lpr" y todos sus clientes/roles/usuarios se pierden. Se recrea vacío y el
  paso 4 (si no se omite con -SkipKeycloakBootstrap) vuelve a darlo de alta desde cero
  (incluidos client secrets NUEVOS -- vas a tener que reconfigurar user-secrets con los
  secrets nuevos que imprime el script, aunque este mismo script ya lo hace automáticamente
  para RedLists/AdminService cuando sus repos están clonados al lado). Pedirá confirmación
  antes de borrar, salvo que también se pase -Force.

.PARAMETER Force
  Salta la confirmación interactiva de -ResetDatabase / -ResetKeycloak. Pensado para uso no
  interactivo (CI, scripts). Sin efecto si ninguno de los dos flags de reset está presente.
#>

param(
    [switch]$SkipKeycloakBootstrap,
    [switch]$SkipEdgeVenv,
    [switch]$SkipStartServices,
    [switch]$ResetDatabase,
    [switch]$ResetKeycloak,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot  # tools/.. = raíz del repo
Set-Location $repoRoot

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "OK: $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "AVISO: $msg" -ForegroundColor Yellow }

# Rutas de los repos hermanos (RedLists y AdminService son opcionales -- ver docs/fases.md,
# Fase 3.5, y AdminService/README.md). Se resuelven una sola vez, al principio, porque varios
# pasos de este script las necesitan.
$redListsRoot     = Join-Path $repoRoot "..\RedLists"
$adminServiceRoot = Join-Path $repoRoot "..\AdminService"

# --- 0. Prerequisitos ---
Step "Verificando prerequisitos"
foreach ($cmd in @("docker", "dotnet", "git")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        throw "'$cmd' no está en PATH. Instálalo antes de continuar."
    }
}
Ok "docker, dotnet, git presentes"

if (-not $SkipKeycloakBootstrap -and -not (Get-Command curl -ErrorAction SilentlyContinue)) {
    throw "'curl' (curl.exe) no está en PATH -- lo necesita el paso 4 (bootstrap de Keycloak; ver el comentario ahí sobre por qué no se usa Invoke-RestMethod). Viene incluido desde Windows 10 1803 / Windows 11 -- si de verdad no está, instálalo o usa -SkipKeycloakBootstrap."
}

if (-not $SkipEdgeVenv -and -not (Get-Command python -ErrorAction SilentlyContinue)) {
    Warn "python no está en PATH -- se omitirá el setup de edge/ (usa -SkipEdgeVenv para silenciar este aviso, o instala Python)."
    $SkipEdgeVenv = $true
}

if (Test-Path $redListsRoot) { Ok "repo RedLists encontrado en '$redListsRoot'" }
else { Warn "repo RedLists no encontrado en '$redListsRoot' -- sus pasos se omiten. Clónalo ahí y vuelve a correr este script para incluirlo." }

if (Test-Path $adminServiceRoot) { Ok "repo AdminService encontrado en '$adminServiceRoot'" }
else { Warn "repo AdminService no encontrado en '$adminServiceRoot' -- sus pasos se omiten. Clónalo ahí y vuelve a correr este script para incluirlo." }

# --- 0b. Reset de datos (opcional, -ResetDatabase / -ResetKeycloak) ---
# Borra volúmenes de Docker con nombre completo, no todo con "docker compose down -v" (eso
# borraría también redis_data/rabbitmq_data sin que se haya pedido). El filtro de
# "docker volume ls" hace match por substring contra el nombre completo con prefijo de proyecto
# de Compose (p.ej. "sistemamunicipalpr_mysql_data"), así que no hace falta saber ese prefijo.
if ($ResetDatabase -or $ResetKeycloak) {
    Step "Reset de datos solicitado (ResetDatabase=$ResetDatabase, ResetKeycloak=$ResetKeycloak)"
    if ($ResetDatabase) {
        Warn "Esto borra TODA la base 'SistemaLPR' (SistemaMunicipaLPR + RedLists + AdminService, comparten la misma base física)."
    }
    if ($ResetKeycloak) {
        Warn "Esto borra TODO el realm 'sistema-lpr' (usuarios, clientes, roles, secrets)."
    }
    if (-not $Force) {
        $confirm = Read-Host "Escribí 'si' para confirmar el borrado (cualquier otra cosa cancela)"
        if ($confirm -ne "si") { throw "Reset cancelado por el usuario." }
    }

    docker compose down

    $volumeNameFilters = @()
    if ($ResetDatabase) { $volumeNameFilters += "mysql_data" }
    if ($ResetKeycloak) { $volumeNameFilters += "keycloak_data" }

    foreach ($filter in $volumeNameFilters) {
        $vols = docker volume ls -q --filter "name=$filter"
        foreach ($vol in $vols) {
            docker volume rm $vol | Out-Null
            Ok "volumen '$vol' eliminado"
        }
    }
    Ok "reset completo -- se recrea vacío en el paso 1 (infraestructura) y los pasos siguientes vuelven a aplicar esquema/migraciones/Keycloak desde cero"
}

# --- 1. Infraestructura (docker compose) ---
Step "Levantando MySQL, Redis, RabbitMQ, Keycloak (docker compose up -d)"
docker compose up -d

Step "Esperando healthchecks..."
$services = @("lpr-mysql", "lpr-redis", "lpr-rabbitmq")
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

# --- 1b. Esquema de RedLists (Fase 3.5 -- ver docs/fases.md) ---
# vehicle_lists/vehicles/list_vehicles/adapters/sync_runs/imported_vehicles_log se crean con
# SQL directo (RedLists no usa EF Core migrations para su propio esquema, ver db/redlists-schema.sql)
# contra la misma base SistemaLPR -- reemplaza la base "redlists" separada que RedLists usaba por su
# cuenta antes de esta fase. Es idempotente (IF NOT EXISTS / ON DUPLICATE KEY), seguro de re-correr.
Step "Aplicando esquema de RedLists (db/redlists-schema.sql) contra SistemaLPR"
Get-Content db\redlists-schema.sql | docker compose exec -T mysql mysql -uroot -p'Lpr#Dev_2026!' SistemaLPR
Ok "esquema de RedLists aplicado"

# --- 1c. Cadenas de conexión de RedLists (user-secrets, si el repo está clonado al lado) ---
# RedLists es un repo separado (ver docs/fases.md, Fase 3.5). Si está clonado como carpeta
# hermana de este repo (..\RedLists, mismo layout que en la máquina original), se configuran
# aquí sus user-secrets para que sus dos servicios apunten a SistemaLPR en vez de a su propia
# base "redlists" -- si no está clonado ahí todavía, este paso se omite (vuelve a correr el
# script una vez que lo clones).
if (Test-Path $redListsRoot) {
    Step "Configurando user-secrets de RedLists (VehicleListsService, ExternalVehicleFeedService)"

    $vehicleListsProject = Join-Path $redListsRoot "src\VehicleListsService\src\VehicleListsService.Api"
    if (Test-Path $vehicleListsProject) {
        Push-Location $vehicleListsProject
        dotnet user-secrets init 2>$null | Out-Null
        dotnet user-secrets set "VehicleLists:ConnectionString" "Server=localhost;Port=3306;Database=SistemaLPR;User=vehiclelists_svc;Password=change_me_vehiclelists_dev_only" | Out-Null
        Pop-Location
        Ok "VehicleListsService.Api: ConnectionString configurada (SistemaLPR, no la base 'redlists' separada)"
    }
    else {
        Warn "No se encontró VehicleListsService.Api en '$vehicleListsProject' -- revisa la estructura del repo RedLists."
    }

    $externalFeedProject = Join-Path $redListsRoot "src\ExternalVehicleFeedService\src\ExternalVehicleFeedService.Api"
    if (Test-Path $externalFeedProject) {
        Push-Location $externalFeedProject
        dotnet user-secrets init 2>$null | Out-Null
        dotnet user-secrets set "ExternalVehicleFeed:ConnectionString" "Server=localhost;Port=3306;Database=SistemaLPR;User=externalfeed_svc;Password=change_me_externalfeed_dev_only" | Out-Null
        Pop-Location
        Ok "ExternalVehicleFeedService.Api: ConnectionString configurada (SistemaLPR)"
    }
    else {
        Warn "No se encontró ExternalVehicleFeedService.Api en '$externalFeedProject' -- revisa la estructura del repo RedLists."
    }
}
else {
    Warn "No se encontró el repo RedLists en '$redListsRoot' -- omite este paso si aún no lo has clonado junto a este repo. Vuelve a correr este script después de clonarlo junto a este repo para configurar sus cadenas de conexión."
}

# --- 1d. Cadena de conexión de AdminService (user-secrets, si el repo está clonado al lado) ---
# AdminService/src/Admin.Api/appsettings.json trae "ConnectionStrings:SistemaLPR" con la
# contraseña de root EN TEXTO PLANO (deuda técnica heredada, ver AdminService/docs/fases.md) --
# se sobrescribe aquí vía user-secrets (que ASP.NET Core prioriza sobre appsettings.json en
# Development) para no depender de ese valor comiteado. Mismo criterio que RedLists en el paso
# 1c y que este mismo repo (ver el incidente documentado con el client_secret de Keycloak en
# br.bat, referenciado en AdminService/README.md).
if (Test-Path $adminServiceRoot) {
    Step "Configurando user-secrets de AdminService (ConnectionStrings:SistemaLPR)"

    $adminApiProject = Join-Path $adminServiceRoot "src\Admin.Api"
    if (Test-Path $adminApiProject) {
        Push-Location $adminApiProject
        dotnet user-secrets init 2>$null | Out-Null
        dotnet user-secrets set "ConnectionStrings:SistemaLPR" "Server=localhost;Port=3306;Database=SistemaLPR;User=root;Password=Lpr#Dev_2026!;" | Out-Null
        Pop-Location
        Ok "Admin.Api: ConnectionStrings:SistemaLPR configurada vía user-secrets"
    }
    else {
        Warn "No se encontró Admin.Api en '$adminApiProject' -- revisa la estructura del repo AdminService."
    }
}
else {
    Warn "No se encontró el repo AdminService en '$adminServiceRoot' -- omite este paso si aún no lo has clonado junto a este repo. Vuelve a correr este script después de clonarlo junto a este repo."
}

# --- 2. Build ---
Step "dotnet build (SistemaMunicipaLPR)"
dotnet build "$repoRoot\SistemaLPR.sln"
Ok "build de SistemaMunicipaLPR correcto"

if (Test-Path $redListsRoot) {
    Step "dotnet build (RedLists -- 4 soluciones independientes)"
    $redListsSlns = @(
        "src\VehicleListsService\VehicleListsService.sln",
        "src\ExternalVehicleFeedService\ExternalVehicleFeedService.sln",
        "src\RedList.Client\RedList.Client.sln",
        "src\RedList.Web\RedList.Web.sln"
    )
    foreach ($sln in $redListsSlns) {
        $slnPath = Join-Path $redListsRoot $sln
        if (Test-Path $slnPath) {
            dotnet build $slnPath
            Ok "build de '$sln' correcto"
        }
        else {
            Warn "No se encontró '$slnPath' -- se omite."
        }
    }
}

if (Test-Path $adminServiceRoot) {
    Step "dotnet build (AdminService)"
    dotnet build "$adminServiceRoot\AdminService.sln"
    Ok "build de AdminService correcto"
}

# --- 3. Migraciones EF Core ---
# Program.cs aplica el esquema completo al arrancar Api.Web: EnsureCreated() de
# CasbinDbContext<int> primero, Migrate() de LprDbContext después (ver el comentario sobre ese
# orden en Program.cs). A propósito NO corremos "dotnet ef database update" en este paso -- antes
# lo hacíamos acá para no depender de arrancar la API a mano la primera vez, pero eso aplica el
# esquema de LprDbContext contra una base todavía vacía ANTES de que Api.Web tenga la oportunidad
# de correr EnsureCreated(); cuando la API arranca después, CasbinDbContext<int>.EnsureCreated()
# encuentra una base que YA tiene tablas (las que este paso acaba de crear) y no hace nada --
# EnsureCreated() solo actúa si la base está completamente vacía. Resultado: "casbin_rule"
# nunca se crea, sin importar cuántas veces se reinicie el volumen de MySQL -- este fue
# exactamente el bug reproducido en la práctica ("Table 'SistemaLPR.casbin_rule' doesn't exist"
# al arrancar la API). Por eso acá solo generamos el archivo de migración (no toca la base); el
# esquema se aplica solo, en el orden correcto, la primera vez que corrés
# "dotnet run --project src\Api.Web". AdminService (más abajo) sigue exactamente el mismo
# patrón, por la misma razón (Admin.Api/Program.cs también hace EnsureCreated() antes de
# Migrate(), contra la MISMA base física).
#
# --context LprDbContext es obligatorio: Api.Web tiene DOS DbContext (LprDbContext y el
# CasbinDbContext<int> que usa Casbin.NET.Adapter.EFCore) -- sin especificar cuál, "dotnet ef"
# falla con "More than one DbContext was found". CasbinDbContext<int> no usa migraciones (usa
# EnsureCreated(), ver Program.cs), así que nunca hace falta generarle una migración a ese.
Step "Generando migración de base de datos (si hace falta)"
if (-not (dotnet tool list --global | Select-String "dotnet-ef")) {
    dotnet tool install --global dotnet-ef
}
if (-not (Test-Path src\Api.Web\Migrations)) {
    Ok "no hay ninguna migración todavía -- generando InitialLprSchema"
    dotnet ef migrations add InitialLprSchema --project src/Api.Web --context LprDbContext
}
Ok "migración de SistemaMunicipaLPR lista -- se aplica sola (Casbin primero, LprDbContext después) la primera vez que corras 'dotnet run --project src\Api.Web'"

if (Test-Path $adminServiceRoot) {
    $adminMigrationsPath = Join-Path $adminServiceRoot "src\Admin.Infrastructure\Migrations"
    if (-not (Test-Path $adminMigrationsPath)) {
        Ok "AdminService no tiene ninguna migración todavía -- generando InitialAdminSchema"
        Push-Location $adminServiceRoot
        # --context AdminDbContext es obligatorio, mismo motivo que --context LprDbContext en el
        # paso de arriba para Api.Web: Admin.Api tiene DOS DbContext (AdminDbContext y el
        # CasbinDbContext<int> que usa Casbin.NET.Adapter.EFCore) -- sin especificar cuál,
        # "dotnet ef" falla con "More than one DbContext was found". CasbinDbContext<int> no usa
        # migraciones (EnsureCreated(), ver Admin.Api/Program.cs), así que nunca hace falta
        # generarle una migración a ese.
        dotnet ef migrations add InitialAdminSchema --project src/Admin.Infrastructure --startup-project src/Admin.Api --context AdminDbContext
        Pop-Location
    }
    Ok "migración de AdminService lista -- se aplica sola (mismo orden Casbin-primero) la primera vez que corras 'dotnet run --project src\Admin.Api'"
}

# --- 4. Keycloak: realm, clientes, roles, usuario de prueba ---
# (Api.Web ya no necesita ningun user-secret propio -- ExternalBlacklist:BearerToken se retiro
# junto con el subsistema de blacklist propio en Fase 3.5, ver docs/fases.md. Los user-secrets
# de RedLists y AdminService se configuran arriba, en los pasos 1c/1d.)
if ($SkipKeycloakBootstrap) {
    Warn "Bootstrap de Keycloak omitido (-SkipKeycloakBootstrap)"
}
else {
    Step "Configurando Keycloak (realm sistema-lpr, clientes, roles, usuario de prueba)"

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

    # Invoke-RestMethod resulto NO ser confiable en esta maquina para las llamadas al admin REST
    # API de Keycloak: se probo exhaustivamente (ver historial de diagnostico de este bloque) que
    # tanto curl.exe como System.Net.Http.HttpClient usado directamente funcionan sin problema con
    # la MISMA url/headers/token que Invoke-RestMethod rechaza con un falso "UriFormatException" --
    # o sea el bug esta en el cmdlet de PowerShell en si (hay reportes conocidos de fallas
    # intermitentes de Invoke-RestMethod/Invoke-WebRequest relacionadas con su manejo interno de
    # sesion/conexion), no en la red, no en Keycloak, ni en este script. Para no depender de ese
    # cmdlet, todas las llamadas al admin API de Keycloak de aca en adelante pasan por curl.exe
    # (incluido de forma nativa desde Windows 10 1803 / Windows 11) en vez de Invoke-RestMethod.
    # El body de las llamadas POST/PUT se escribe a un archivo temporal en vez de pasarse como
    # argumento de linea de comandos -- Windows/PowerShell tiene problemas conocidos escapando
    # comillas dobles embebidas (como las de un JSON) al invocar ejecutables nativos, y pasar el
    # body por archivo (curl "-d @archivo") evita ese problema por completo.
    function Invoke-KcCurl {
        param(
            [Parameter(Mandatory)] [string]$Uri,
            [string]$Method = "Get",
            [Parameter(Mandatory)] [hashtable]$Headers,
            [string]$Body,
            [string]$ContentType
        )
        $curlArgs = @("-s", "-X", $Method.ToUpper(), "-w", "`n<<HTTP_STATUS:%{http_code}>>")
        foreach ($key in $Headers.Keys) { $curlArgs += @("-H", "${key}: $($Headers[$key])") }
        if ($ContentType) { $curlArgs += @("-H", "Content-Type: $ContentType") }
        $tempFile = $null
        if ($PSBoundParameters.ContainsKey('Body')) {
            $tempFile = [System.IO.Path]::GetTempFileName()
            [System.IO.File]::WriteAllText($tempFile, $Body, [System.Text.Encoding]::UTF8)
            $curlArgs += @("-d", "@$tempFile")
        }
        $curlArgs += $Uri
        try {
            $raw = (& curl.exe @curlArgs 2>$null) -join "`n"
        }
        finally {
            if ($tempFile) { Remove-Item $tempFile -ErrorAction SilentlyContinue }
        }
        if ($raw -notmatch "<<HTTP_STATUS:(\d+)>>\s*$") {
            throw "No se pudo invocar curl.exe contra $Method $Uri (¿está curl.exe en el PATH? Viene incluido desde Windows 10 1803 / Windows 11)."
        }
        [PSCustomObject]@{
            StatusCode = [int]$Matches[1]
            Body       = $raw.Substring(0, $raw.Length - $Matches[0].Length)
        }
    }

    # Envoltorio para el caso comun: falla si el status no es 2xx, y devuelve el body ya parseado
    # como JSON (o $null si viene vacío) -- mismo contrato que tenía el Invoke-KcRestMethod
    # anterior (basado en Invoke-RestMethod con reintentos), así que ningún call site de abajo
    # necesita cambiar más allá de esta función.
    function Invoke-KcRestMethod {
        param(
            [Parameter(Mandatory)] [string]$Uri,
            [string]$Method = "Get",
            [Parameter(Mandatory)] [hashtable]$Headers,
            [string]$Body,
            [string]$ContentType
        )
        $curlParams = @{ Uri = $Uri; Method = $Method; Headers = $Headers }
        if ($PSBoundParameters.ContainsKey('Body')) { $curlParams.Body = $Body }
        if ($ContentType) { $curlParams.ContentType = $ContentType }
        $result = Invoke-KcCurl @curlParams
        if ($result.StatusCode -ge 400) {
            throw "Keycloak admin API devolvió HTTP $($result.StatusCode) para $Method $Uri`: $($result.Body)"
        }
        if ([string]::IsNullOrWhiteSpace($result.Body)) { return $null }
        return $result.Body | ConvertFrom-Json
    }

    $clientsUrl = "$kcUrl/admin/realms/sistema-lpr/clients"
    $usersUrl   = "$kcUrl/admin/realms/sistema-lpr/users"

    # Realm (idempotente: si ya existe, Keycloak responde 409 y se ignora).
    $realmResult = Invoke-KcCurl -Method Post -Uri "$kcUrl/admin/realms" -Headers $kcHeaders -ContentType "application/json" `
        -Body (@{ realm = "sistema-lpr"; enabled = $true } | ConvertTo-Json)
    if ($realmResult.StatusCode -eq 201) {
        Ok "realm 'sistema-lpr' creado"
    }
    elseif ($realmResult.StatusCode -eq 409) {
        Ok "realm 'sistema-lpr' ya existía"
    }
    else {
        throw "Fallo al crear/verificar el realm 'sistema-lpr' en '$kcUrl' (status: $($realmResult.StatusCode)): $($realmResult.Body)"
    }

    # Cliente api-web -- confidential, con Direct Access Grants (password grant) habilitado,
    # que es el flujo usado por las pruebas de este proyecto (usuario importer.test).
    #
    # NOTA sobre @(...): Invoke-KcRestMethod devuelve el resultado de "ConvertFrom-Json" sobre un
    # array JSON -- cuando ese array tiene EXACTAMENTE un elemento, PowerShell lo "desenvuelve" a
    # un objeto escalar al pasar por una asignación/retorno, así que ".Count" deja de existir (o
    # da 1 sobre las propiedades del objeto, no sobre el array) y "-eq 0" nunca es cierto incluso
    # cuando el array real tenía cero elementos. Bug reproducido en la práctica: con el cliente
    # 'api-web' ya creado, este chequeo daba falso y el script intentaba crearlo de nuevo, y
    # Keycloak respondía "HTTP 409 Client api-web already exists". Envolver la llamada en @(...)
    # fuerza el resultado a ser siempre un array de PowerShell (0, 1 o N elementos), sin importar
    # cuántos elementos tenga el JSON de origen -- mismo fix aplicado más abajo a $existingUser.
    $existingClient = @(Invoke-KcRestMethod -Uri "$clientsUrl?clientId=api-web" -Headers $kcHeaders)
    if ($existingClient.Count -eq 0) {
        Invoke-KcRestMethod -Method Post -Uri $clientsUrl -Headers $kcHeaders -ContentType "application/json" -Body (@{
                clientId                  = "api-web"
                publicClient              = $false
                directAccessGrantsEnabled = $true
                standardFlowEnabled       = $true
                serviceAccountsEnabled    = $false
            } | ConvertTo-Json)
        Ok "cliente 'api-web' creado"
        $existingClient = @(Invoke-KcRestMethod -Uri "$clientsUrl?clientId=api-web" -Headers $kcHeaders)
    }
    else {
        Ok "cliente 'api-web' ya existía"
    }
    $clientUuid = $existingClient[0].id
    $secretResp = Invoke-KcRestMethod -Uri "$clientsUrl/$clientUuid/client-secret" -Headers $kcHeaders
    Write-Host "`nCLIENT SECRET de 'api-web' (guárdalo -- lo necesitas para probar el login):" -ForegroundColor Magenta
    Write-Host "  $($secretResp.value)`n" -ForegroundColor Magenta

    # Roles de realm
    $roles = @("SuperAdmin", "SupervisorC4", "OperadorC4", "PatrullaMovil", "AuditorForense")
    $rolesUrl = "$kcUrl/admin/realms/sistema-lpr/roles"
    foreach ($role in $roles) {
        $roleResult = Invoke-KcCurl -Method Post -Uri $rolesUrl -Headers $kcHeaders -ContentType "application/json" -Body (@{ name = $role } | ConvertTo-Json)
        if ($roleResult.StatusCode -eq 201) {
            Ok "rol '$role' creado"
        }
        else {
            Ok "rol '$role' ya existía"
        }
    }

    # --- Clientes de AdminService (ver AdminService/README.md, sección "Configuración en
    # Keycloak") -- no reutiliza 'api-web', necesita los suyos propios. ---

    # admin-api: valida los tokens de quien use la futura UI de administración (Fase C de
    # AdminService, todavía no existe -- el cliente se deja listo de todas formas). Mismo tipo
    # que api-web (confidential + Direct Access Grants, así se puede probar con curl/Postman
    # mientras no hay UI). Debe coincidir con "Keycloak:Audience" en
    # AdminService/src/Admin.Api/appsettings.json (ya viene en "admin-api" ahí).
    $adminApiClientId = "admin-api"
    $existingAdminApiClient = @(Invoke-KcRestMethod -Uri "$clientsUrl?clientId=$adminApiClientId" -Headers $kcHeaders)
    if ($existingAdminApiClient.Count -eq 0) {
        Invoke-KcRestMethod -Method Post -Uri $clientsUrl -Headers $kcHeaders -ContentType "application/json" -Body (@{
                clientId                  = $adminApiClientId
                publicClient              = $false
                directAccessGrantsEnabled = $true
                standardFlowEnabled       = $true
                serviceAccountsEnabled    = $false
            } | ConvertTo-Json)
        Ok "cliente '$adminApiClientId' creado"
    }
    else {
        Ok "cliente '$adminApiClientId' ya existía"
    }

    # admin-service: confidencial, Service Accounts habilitado -- AdminService lo usa para
    # llamar al Admin REST API de Keycloak (RF-1: crear/deshabilitar usuarios, resetear
    # contraseñas). Necesita los roles de cliente "manage-users"/"view-users" del cliente
    # "realm-management", asignados a SU PROPIA cuenta de servicio.
    $adminServiceClientId = "admin-service"
    $existingAdminServiceClient = @(Invoke-KcRestMethod -Uri "$clientsUrl?clientId=$adminServiceClientId" -Headers $kcHeaders)
    if ($existingAdminServiceClient.Count -eq 0) {
        Invoke-KcRestMethod -Method Post -Uri $clientsUrl -Headers $kcHeaders -ContentType "application/json" -Body (@{
                clientId                  = $adminServiceClientId
                publicClient              = $false
                standardFlowEnabled       = $false
                directAccessGrantsEnabled = $false
                serviceAccountsEnabled    = $true
            } | ConvertTo-Json)
        Ok "cliente '$adminServiceClientId' creado"
        $existingAdminServiceClient = @(Invoke-KcRestMethod -Uri "$clientsUrl?clientId=$adminServiceClientId" -Headers $kcHeaders)
    }
    else {
        Ok "cliente '$adminServiceClientId' ya existía"
    }
    $adminServiceClientUuid = $existingAdminServiceClient[0].id

    # Asignar manage-users/view-users de realm-management a la cuenta de servicio de
    # admin-service -- alcance acotado al realm "sistema-lpr", sin tocar el realm "master".
    # Idempotente: volver a asignar un rol ya asignado no falla en Keycloak.
    $realmMgmtClient = @(Invoke-KcRestMethod -Uri "$clientsUrl?clientId=realm-management" -Headers $kcHeaders)
    if ($realmMgmtClient.Count -eq 0) {
        Warn "No se encontró el cliente 'realm-management' en el realm -- no se pudieron asignar roles a la cuenta de servicio de '$adminServiceClientId'. Esto no debería pasar en un realm estándar de Keycloak; revísalo a mano."
    }
    else {
        $realmMgmtUuid = $realmMgmtClient[0].id
        $serviceAccountUser = Invoke-KcRestMethod -Uri "$clientsUrl/$adminServiceClientUuid/service-account-user" -Headers $kcHeaders
        $rolesToAssign = @("manage-users", "view-users")
        $roleReps = @(foreach ($roleName in $rolesToAssign) {
                Invoke-KcRestMethod -Uri "$clientsUrl/$realmMgmtUuid/roles/$roleName" -Headers $kcHeaders
            })
        Invoke-KcRestMethod -Method Post -Uri "$usersUrl/$($serviceAccountUser.id)/role-mappings/clients/$realmMgmtUuid" -Headers $kcHeaders -ContentType "application/json" -Body (ConvertTo-Json @($roleReps))
        Ok "roles 'manage-users'/'view-users' de 'realm-management' asignados a la cuenta de servicio de '$adminServiceClientId'"
    }

    $adminServiceSecretResp = Invoke-KcRestMethod -Uri "$clientsUrl/$adminServiceClientUuid/client-secret" -Headers $kcHeaders
    Write-Host "`nCLIENT SECRET de '$adminServiceClientId' (guárdalo -- lo necesitas para AdminService):" -ForegroundColor Magenta
    Write-Host "  $($adminServiceSecretResp.value)`n" -ForegroundColor Magenta

    if (Test-Path $adminServiceRoot) {
        $adminApiProjectForSecret = Join-Path $adminServiceRoot "src\Admin.Api"
        if (Test-Path $adminApiProjectForSecret) {
            Push-Location $adminApiProjectForSecret
            dotnet user-secrets set "Keycloak:AdminClientSecret" $adminServiceSecretResp.value | Out-Null
            Pop-Location
            Ok "Admin.Api: Keycloak:AdminClientSecret configurado automáticamente vía user-secrets"
        }
    }

    # Usuario de prueba importer.test (el mismo usado en las pruebas de import de blacklist),
    # con rol OperadorC4.
    $existingUser = @(Invoke-KcRestMethod -Uri "$usersUrl?username=importer.test" -Headers $kcHeaders)
    if ($existingUser.Count -eq 0) {
        Invoke-KcRestMethod -Method Post -Uri $usersUrl -Headers $kcHeaders -ContentType "application/json" -Body (@{
                username = "importer.test"; enabled = $true
            } | ConvertTo-Json)
        $existingUser = @(Invoke-KcRestMethod -Uri "$usersUrl?username=importer.test" -Headers $kcHeaders)
        $userId = $existingUser[0].id

        $testPassword = Read-Host "Password para el usuario de prueba 'importer.test' (Enter = 'Generico2026')"
        if ([string]::IsNullOrWhiteSpace($testPassword)) { $testPassword = "Generico2026" }
        Invoke-KcRestMethod -Method Put -Uri "$usersUrl/$userId/reset-password" -Headers $kcHeaders -ContentType "application/json" -Body (@{
                type = "password"; value = $testPassword; temporary = $false
            } | ConvertTo-Json)

        $roleRep = Invoke-KcRestMethod -Uri "$rolesUrl/OperadorC4" -Headers $kcHeaders
        Invoke-KcRestMethod -Method Post -Uri "$usersUrl/$userId/role-mappings/realm" -Headers $kcHeaders -ContentType "application/json" -Body (ConvertTo-Json @($roleRep))
        Ok "usuario 'importer.test' creado con rol OperadorC4 (password: $testPassword)"
    }
    else {
        Ok "usuario 'importer.test' ya existía"
    }
}

# --- 5. Pipeline Edge (Python) ---
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

# --- 6. Arrancar la solución completa ---
# Cada servicio backend se arranca en su propia ventana de PowerShell (Start-Process), no en
# background silencioso, para que sus logs queden visibles y cerrar la ventana alcance para
# detenerlo. Los 2 clientes de UI de RedLists (WPF y Blazor WebAssembly) son interactivos --
# se imprimen los comandos para arrancarlos a mano en vez de intentar automatizarlos.
function Start-DotnetService {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$WorkingDirectory,
        [Parameter(Mandatory)] [string]$ProjectPath
    )
    if (-not (Test-Path (Join-Path $WorkingDirectory $ProjectPath))) {
        Warn "$Name -- no se encontró '$ProjectPath' en '$WorkingDirectory', se omite."
        return
    }
    Start-Process powershell -ArgumentList @(
        "-NoExit", "-Command",
        "Set-Location '$WorkingDirectory'; Write-Host '=== $Name ===' -ForegroundColor Cyan; dotnet run --project '$ProjectPath'"
    )
    Ok "$Name -- arrancando en una nueva ventana de PowerShell"
}

if ($SkipStartServices) {
    Warn "Arranque de servicios omitido (-SkipStartServices). Para arrancar todo a mano, ver C:\Ric68\docs\ImplementationGuide.md."
}
else {
    Step "Arrancando la solución completa (SistemaMunicipaLPR + RedLists + AdminService)"
    Warn "Se abre una ventana de PowerShell nueva por cada servicio backend -- cerrarla lo detiene."

    Start-DotnetService -Name "Api.Web (SistemaMunicipaLPR)" -WorkingDirectory $repoRoot -ProjectPath "src\Api.Web"
    # Pausa breve: Api.Web es quien corre EnsureCreated()/Migrate() la primera vez -- darle
    # ventaja para que termine antes de que los demás servicios empiecen a golpear la misma base.
    Start-Sleep -Seconds 5
    Start-DotnetService -Name "Service.Inference (SistemaMunicipaLPR)" -WorkingDirectory $repoRoot -ProjectPath "src\Service.Inference"

    if (Test-Path $redListsRoot) {
        Start-DotnetService -Name "VehicleListsService.Api (RedLists)" -WorkingDirectory $redListsRoot -ProjectPath "src\VehicleListsService\src\VehicleListsService.Api"
        Start-DotnetService -Name "ExternalVehicleFeedService.Api (RedLists)" -WorkingDirectory $redListsRoot -ProjectPath "src\ExternalVehicleFeedService\src\ExternalVehicleFeedService.Api"
    }

    if (Test-Path $adminServiceRoot) {
        Start-DotnetService -Name "Admin.Api (AdminService)" -WorkingDirectory $adminServiceRoot -ProjectPath "src\Admin.Api"
    }

    Write-Host "`nClientes de UI de RedLists (interactivos, arrancar a mano cuando quieras usarlos):" -ForegroundColor Cyan
    if (Test-Path $redListsRoot) {
        Write-Host "  RedList.Client.Wpf (escritorio): cd '$redListsRoot'; dotnet run --project src\RedList.Client\src\RedList.Client.Wpf"
        Write-Host "  RedList.Web (navegador):         cd '$redListsRoot'; dotnet run --project src\RedList.Web\src\RedList.Web"
    }
    else {
        Write-Host "  (repo RedLists no encontrado -- no aplica)"
    }
}

Step "Listo"
Write-Host "Pendiente manual (no automatizable de forma segura):"
Write-Host "  - Confirmar que edge\config.yaml y el modelo .pt están en su lugar."
if ($SkipStartServices) {
    Write-Host "`nPara arrancar todo manualmente, ver C:\Ric68\docs\ImplementationGuide.md (o corré este script sin -SkipStartServices)."
}
else {
    Write-Host "`nServicios backend arrancando en sus propias ventanas de PowerShell. Ver C:\Ric68\docs\ImplementationGuide.md para el detalle de cada uno (puertos, dependencias, orden)."
}
