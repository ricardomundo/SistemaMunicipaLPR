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
# 1. Retirar BlacklistCacheService.cs / Redis/BlacklistRedisKeys.cs
# =============================================================================
rm("src/Service.Inference/BlacklistCacheService.cs")
rm("src/Service.Inference/Redis/BlacklistRedisKeys.cs")

# =============================================================================
# 2. Nuevo Redis/RedListRedisKeys.cs
# =============================================================================
w("src/Service.Inference/Redis/RedListRedisKeys.cs", '''namespace Service.Inference.Redis;

/// <summary>
/// Nombre de la llave de Redis para la caché de placas activas en RedLists, centralizado aquí
/// para que el loader (RedListCacheService) y el lookup del camino caliente (PlateReadConsumer)
/// nunca se desincronicen por un typo.
/// </summary>
public static class RedListRedisKeys
{
    /// <summary>Set de Redis con el PlateText de cada placa con membresía activa
    /// (recovered_by_org_id = 0) en una lista de RedLists de tipo Vehicles (RedList, no
    /// WhiteList) -- lookup O(1) vía SISMEMBER.</summary>
    public const string ActivePlates = "redlist:active-plates";
}
''')

# =============================================================================
# 3. Nuevo RedListCacheService.cs (reemplaza BlacklistCacheService.cs)
# =============================================================================
w("src/Service.Inference/RedListCacheService.cs", '''using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Service.Inference.Data;
using Service.Inference.Redis;
using StackExchange.Redis;

namespace Service.Inference;

/// <summary>
/// Mantiene el set de Redis "redlist:active-plates" sincronizado con RedLists
/// (VehicleListsService -- vehicle_lists/vehicles/list_vehicles, misma base SistemaLPR, ver
/// Fase 3.5 en docs/fases.md), para que el lookup del camino caliente en PlateReadConsumer
/// nunca tenga que tocar MySQL. Reemplaza a BlacklistCacheService (que leía VehiculosRobados,
/// retirado en Fase 3.5) y a los consumers BlacklistEntryAdded/RemovedConsumer (que dependían
/// de eventos CAP propios, también retirados).
///
/// Dos mecanismos:
///   1. Carga completa al arrancar + refresco delta cada 5 minutos como respaldo, por si se
///      pierde algún evento de SignalR (p. ej. una caída momentánea de este servicio).
///   2. Suscripción en vivo al hub de SignalR de RedLists ("/hubs/vehicle-lists") para
///      actualizaciones al segundo -- VehicleAdded/VehicleRemoved/VehicleRecovered.
///
/// RedLists no deduplica por placa (una placa puede tener varias filas en vehicles/
/// list_vehicles), así que tanto el refresco completo como cada actualización puntual
/// reconsultan "¿sigue esta placa activa en ALGUNA lista tipo Vehicles (RedList)?" en vez de
/// confiar en que el evento por sí solo refleje el estado final -- ver la decisión de
/// deduplicación "en la consulta hacia Redis" en Fase 3.5 de docs/fases.md.
/// </summary>
public class RedListCacheService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private const string VehicleAddedEvent = "VehicleAdded";
    private const string VehicleRemovedEvent = "VehicleRemoved";
    private const string VehicleRecoveredEvent = "VehicleRecovered";

    private readonly IConnectionMultiplexer _redis;
    private readonly SqlConnectionFactory _sqlConnectionFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RedListCacheService> _logger;

    private HubConnection? _hubConnection;

    public RedListCacheService(
        IConnectionMultiplexer redis,
        SqlConnectionFactory sqlConnectionFactory,
        IConfiguration configuration,
        ILogger<RedListCacheService> logger)
    {
        _redis = redis;
        _sqlConnectionFactory = sqlConnectionFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAllAsync();
        await StartHubConnectionAsync(stoppingToken);

        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAllAsync();
        }
    }

    private async Task StartHubConnectionAsync(CancellationToken stoppingToken)
    {
        // Default apunta al puerto HTTP de desarrollo de VehicleListsService.Api tal como está
        // en RedLists/src/VehicleListsService/src/VehicleListsService.Api/Properties/
        // launchSettings.json -- ajustar RedLists:VehicleListsHubUrl en appsettings.json/
        // user-secrets si VehicleListsService corre en otro host/puerto en tu ambiente.
        var hubUrl = _configuration["RedLists:VehicleListsHubUrl"] ?? "http://localhost:5247/hubs/vehicle-lists";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // VehicleAdded trae el objeto Vehicle completo (con PlateNumber) -- no hace falta una
        // consulta extra a MySQL para saber la placa. VehicleRemoved/VehicleRecovered solo traen
        // el vehicleId -- ReconcileVehicleIdAsync resuelve la placa y decide alta/baja
        // reconsultando el estado real, en vez de confiar en interpretar cada tipo de evento por
        // separado (ver el razonamiento de deduplicación en el comentario de la clase).
        _hubConnection.On<long, VehicleDto>(VehicleAddedEvent, async (_, vehicle) =>
            await ReconcilePlateAsync(vehicle.PlateNumber));

        _hubConnection.On<long, long>(VehicleRemovedEvent, async (_, vehicleId) =>
            await ReconcileVehicleIdAsync(vehicleId));

        _hubConnection.On<long, long, int>(VehicleRecoveredEvent, async (_, vehicleId, _) =>
            await ReconcileVehicleIdAsync(vehicleId));

        // Reconectar puede haber dejado pasar eventos -- un refresco completo tras cada
        // reconexión converge la caché sin tener que confiar en que no se perdió nada mientras
        // la conexión estaba caída.
        _hubConnection.Reconnected += _ => RefreshAllAsync();

        try
        {
            await _hubConnection.StartAsync(stoppingToken);
            _logger.LogInformation("Conectado al hub de SignalR de RedLists en {HubUrl}.", hubUrl);
        }
        catch (Exception ex)
        {
            // No es fatal: el refresco delta de 5 min sigue funcionando como respaldo aunque el
            // hub de RedLists no esté disponible al arrancar.
            _logger.LogError(ex, "No se pudo conectar al hub de SignalR de RedLists en {HubUrl} -- la caché seguirá refrescándose cada 5 min como respaldo.", hubUrl);
        }
    }

    private async Task ReconcileVehicleIdAsync(long vehicleId)
    {
        try
        {
            using var connection = _sqlConnectionFactory.Create();
            var plateNumber = await connection.QuerySingleOrDefaultAsync<string?>(
                "SELECT plate_number FROM vehicles WHERE id = @VehicleId", new { VehicleId = vehicleId });
            if (plateNumber is null)
            {
                _logger.LogWarning("Evento de RedLists para vehicleId={VehicleId} no encontró la placa correspondiente -- se ignora.", vehicleId);
                return;
            }

            await ReconcilePlateAsync(plateNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al reconciliar vehicleId={VehicleId} desde un evento de RedLists.", vehicleId);
        }
    }

    private async Task ReconcilePlateAsync(string plateNumber)
    {
        try
        {
            using var connection = _sqlConnectionFactory.Create();
            var count = await connection.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                  FROM vehicles v
                  JOIN list_vehicles lv ON lv.vehicle_id = v.id
                  JOIN vehicle_lists vl ON vl.id = lv.list_id
                  WHERE v.plate_number = @PlateNumber
                    AND lv.recovered_by_org_id = 0
                    AND vl.list_type = 'Vehicles'",
                new { PlateNumber = plateNumber });

            var db = _redis.GetDatabase();
            if (count > 0)
            {
                await db.SetAddAsync(RedListRedisKeys.ActivePlates, plateNumber);
                _logger.LogInformation("Placa {PlateText} agregada/confirmada en la RedList cache.", plateNumber);
            }
            else
            {
                await db.SetRemoveAsync(RedListRedisKeys.ActivePlates, plateNumber);
                _logger.LogInformation("Placa {PlateText} quitada de la RedList cache.", plateNumber);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al reconciliar la placa {PlateText} en la RedList cache.", plateNumber);
        }
    }

    private async Task RefreshAllAsync()
    {
        try
        {
            using var connection = _sqlConnectionFactory.Create();
            var activePlates = (await connection.QueryAsync<string>(
                @"SELECT DISTINCT v.plate_number
                  FROM vehicles v
                  JOIN list_vehicles lv ON lv.vehicle_id = v.id
                  JOIN vehicle_lists vl ON vl.id = lv.list_id
                  WHERE lv.recovered_by_org_id = 0
                    AND vl.list_type = 'Vehicles'")).ToArray();

            var db = _redis.GetDatabase();
            var key = (RedisKey)RedListRedisKeys.ActivePlates;

            // DEL + SADD en una sola transacción de Redis para no dejar la cache vacía a medio
            // refresh si el proceso se cae justo entre las dos operaciones.
            var transaction = db.CreateTransaction();
            _ = transaction.KeyDeleteAsync(key);
            if (activePlates.Length > 0)
            {
                var values = Array.ConvertAll(activePlates, p => (RedisValue)p);
                _ = transaction.SetAddAsync(key, values);
            }
            await transaction.ExecuteAsync();

            _logger.LogInformation("RedList cache refrescada: {Count} placas activas.", activePlates.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al refrescar la RedList cache en Redis; se mantiene el set anterior.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
        }
    }

    /// <summary>Subconjunto de VehicleListsService.Domain.Vehicle que nos interesa del payload
    /// de VehicleAdded -- solo PlateNumber hace falta aquí. No se referencia el ensamblado de
    /// RedLists directamente (es un repo/deploy separado), así que System.Text.Json solo
    /// deserializa los campos que este tipo declara e ignora el resto.</summary>
    private sealed class VehicleDto
    {
        public string PlateNumber { get; set; } = string.Empty;
    }
}
''')

# =============================================================================
# 4. PlateReadConsumer.cs -- referencia a la llave de Redis renombrada
# =============================================================================
P = "src/Service.Inference/Consumers/PlateReadConsumer.cs"
edit(P, '''/// Por cada PlateReadEvent hace un solo lookup O(1) contra el set de Redis
/// "blacklist:active-plates". Si hay match,''',
        '''/// Por cada PlateReadEvent hace un solo lookup O(1) contra el set de Redis
/// "redlist:active-plates". Si hay match,''')
edit(P, "var isMatch = await db.SetContainsAsync(BlacklistRedisKeys.ActivePlates, reading.PlateText);",
        "var isMatch = await db.SetContainsAsync(RedListRedisKeys.ActivePlates, reading.PlateText);")

# =============================================================================
# 5. BlacklistHitPersistenceConsumer.cs -- reescrito contra el esquema de RedLists
# =============================================================================
w("src/Service.Inference/Consumers/BlacklistHitPersistenceConsumer.cs", '''using System.Threading.Tasks;
using Core.Contracts;
using Dapper;
using DotNetCore.CAP;
using MySqlConnector;
using Microsoft.Extensions.Logging;
using Service.Inference.Data;

namespace Service.Inference.Consumers;

/// <summary>
/// Persiste el rastro de auditoría (LecturaHistorica + Alerta) de cada match contra RedLists.
/// Corre como suscriptor separado de PlateReadConsumer para no meter esta escritura síncrona en
/// el camino caliente de matching -- a esta altura ya se publicó BlacklistHitSavedEvent, y
/// AlertNotificationConsumer (en Api.Web) ya puede empujar la alerta por SignalR en paralelo,
/// sin esperar a que esta escritura a SQL termine (cada servicio tiene su propio "Group" de CAP
/// sobre el mismo topic, así que ambos reciben su propia copia del mensaje).
/// </summary>
public class BlacklistHitPersistenceConsumer : ICapSubscribe
{
    private readonly SqlConnectionFactory _sqlConnectionFactory;
    private readonly ILogger<BlacklistHitPersistenceConsumer> _logger;

    public BlacklistHitPersistenceConsumer(SqlConnectionFactory sqlConnectionFactory, ILogger<BlacklistHitPersistenceConsumer> logger)
    {
        _sqlConnectionFactory = sqlConnectionFactory;
        _logger = logger;
    }

    [CapSubscribe(EventTopics.BlacklistHitSaved)]
    public async Task HandleAsync(BlacklistHitSavedEvent message)
    {
        var reading = message.Reading;
        using var connection = _sqlConnectionFactory.Create();

        var camaraId = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT Id FROM Camaras WHERE Codigo = @Codigo", new { Codigo = reading.CameraId });
        if (camaraId is null)
        {
            _logger.LogWarning(
                "BlacklistHitSavedEvent para {EventId} referencia la cámara '{CameraId}', que no existe en Camaras — no se puede registrar la Alerta.",
                reading.EventId, reading.CameraId);
            return;
        }

        // RedLists no deduplica por placa (una placa puede tener varias filas en vehicles/
        // list_vehicles) -- toma la membresía activa más reciente en una lista tipo 'Vehicles'
        // (RedList) para esta placa. Ver Fase 3.5 en docs/fases.md.
        var redListVehicleId = await connection.QuerySingleOrDefaultAsync<long?>(
            @"SELECT v.id
              FROM vehicles v
              JOIN list_vehicles lv ON lv.vehicle_id = v.id
              JOIN vehicle_lists vl ON vl.id = lv.list_id
              WHERE v.plate_number = @PlateText
                AND lv.recovered_by_org_id = 0
                AND vl.list_type = 'Vehicles'
              ORDER BY lv.added_at DESC
              LIMIT 1",
            new { reading.PlateText });
        if (redListVehicleId is null)
        {
            _logger.LogWarning(
                "BlacklistHitSavedEvent para placa '{PlateText}' no encontró una membresía activa en RedLists " +
                "(¿se dio de baja/recuperó entre el lookup en Redis y este consumer?) — no se registra la Alerta.",
                reading.PlateText);
            return;
        }

        long lecturaHistoricaId;
        try
        {
            lecturaHistoricaId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO LecturasHistoricas (EventId, PlateText, CamaraId, TimestampUtc, Confidence, ImageReference, EsCoincidenciaBlacklist)
                  VALUES (@EventId, @PlateText, @CamaraId, @TimestampUtc, @Confidence, @ImageReference, 1);
                  SELECT LAST_INSERT_ID();",
                new
                {
                    reading.EventId,
                    reading.PlateText,
                    CamaraId = camaraId.Value,
                    reading.TimestampUtc,
                    reading.Confidence,
                    reading.ImageReference
                });
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            // Ya se había procesado este EventId antes (redelivery, código 1062 = duplicate
            // entry) — evita duplicar la Alerta.
            _logger.LogInformation("BlacklistHitSavedEvent {EventId} ya estaba registrado (dedupe por EventId).", reading.EventId);
            return;
        }

        await connection.ExecuteAsync(
            @"INSERT INTO Alertas (LecturaHistoricaId, RedListVehicleId, TimestampUtc, Estado)
              VALUES (@LecturaHistoricaId, @RedListVehicleId, @TimestampUtc, 'Pendiente');",
            new
            {
                LecturaHistoricaId = lecturaHistoricaId,
                RedListVehicleId = redListVehicleId.Value,
                TimestampUtc = message.MatchedAtUtc
            });

        _logger.LogInformation(
            "Alerta registrada: LecturaHistoricaId={LecturaHistoricaId}, RedListVehicleId={RedListVehicleId}, Placa={PlateText}.",
            lecturaHistoricaId, redListVehicleId, reading.PlateText);
    }
}
''')

# =============================================================================
# 6. Program.cs (Service.Inference)
# =============================================================================
w("src/Service.Inference/Program.cs", '''using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Service.Inference;
using Service.Inference.Consumers;
using Service.Inference.Data;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<SqlConnectionFactory>();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379"));

// Los suscriptores de CAP (clases con [CapSubscribe]) deben estar registrados en el
// contenedor de DI para que CAP los descubra al arrancar.
builder.Services.AddTransient<BlacklistHitPersistenceConsumer>();

// PlateReadConsumer YA NO es un suscriptor de CAP (2026-08-18) — se registra como
// BackgroundService normal porque habla RabbitMQ.Client directo, fuera del outbox de CAP. Ver
// el comentario en Consumers/PlateReadConsumer.cs e ImplementersGuide.md §9 para el porqué.
builder.Services.AddHostedService<PlateReadConsumer>();

builder.Services.AddCap(x =>
{
    var sqlConnectionString = builder.Configuration.GetConnectionString("SistemaLPR");
    x.UseMySql(sqlConnectionString!);

    var rabbitMq = builder.Configuration.GetSection("RabbitMq");
    x.UseRabbitMQ(o =>
    {
        o.HostName = rabbitMq["Host"] ?? "localhost";
        o.VirtualHost = rabbitMq["VirtualHost"] ?? "/";
        o.UserName = rabbitMq["Username"] ?? "guest";
        o.Password = rabbitMq["Password"] ?? "guest";
    });

    // "Group" distinto por servicio: Service.Inference y Api.Web suscriben ambos a
    // blacklist-hit-saved-event, y necesitan cada uno su propia copia del mensaje (uno
    // persiste, el otro empuja por SignalR) en vez de competir por el mismo mensaje.
    x.DefaultGroupName = "service-inference";
});

// RedListCacheService mantiene el set de Redis "redlist:active-plates" sincronizado con
// RedLists (VehicleListsService, misma base SistemaLPR -- ver Fase 3.5 en docs/fases.md): carga
// inicial + refresco delta cada 5 min como respaldo, más una suscripción en vivo al hub de
// SignalR de RedLists para actualizaciones al segundo. Reemplaza a BlacklistCacheService y a
// los consumers BlacklistEntryAdded/RemovedConsumer que este proyecto usaba antes de Fase 3.5
// (dependían de VehiculosRobados + eventos CAP propios, ambos retirados).
builder.Services.AddHostedService<RedListCacheService>();

var host = builder.Build();
host.Run();
''')

# =============================================================================
# 7. Service.Inference.csproj -- agregar Microsoft.AspNetCore.SignalR.Client
# =============================================================================
edit(
    "src/Service.Inference/Service.Inference.csproj",
    '    <PackageReference Include="Dapper" Version="2.1.79" />\n',
    '''    <PackageReference Include="Dapper" Version="2.1.79" />
    <!-- Cliente de SignalR para suscribirse al hub de RedLists (VehicleListsService,
         "/hubs/vehicle-lists") y recibir VehicleAdded/VehicleRemoved/VehicleRecovered en vivo,
         en vez de los eventos CAP propios que este proyecto usaba antes de Fase 3.5 (ver
         docs/fases.md). Version sin verificar contra NuGet real (sin acceso a internet desde
         donde se escribio esto) -- si "dotnet restore" no la encuentra, ajustar a la ultima
         9.0.x publicada. -->
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="9.0.19" />
''',
)

# =============================================================================
# 8. appsettings.json (Service.Inference) -- URL del hub de RedLists
# =============================================================================
edit(
    "src/Service.Inference/appsettings.json",
    '''  "PlateReadLogging": {
    "OnlyLogMatches": true
  }
}''',
    '''  "PlateReadLogging": {
    "OnlyLogMatches": true
  },
  "RedLists": {
    "VehicleListsHubUrl": "http://localhost:5247/hubs/vehicle-lists",
    "_comment": "Debe apuntar a donde corre VehicleListsService.Api (repo RedLists) en cada ambiente -- el default de arriba coincide con su launchSettings.json de desarrollo. Ver Fase 3.5 en docs/fases.md."
  }
}''',
)

print("=== FASE D completa ===")
