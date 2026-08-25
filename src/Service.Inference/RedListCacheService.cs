using System;
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
