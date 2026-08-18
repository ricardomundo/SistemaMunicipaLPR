using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Service.Inference.Data;
using Service.Inference.Redis;
using StackExchange.Redis;

namespace Service.Inference;

/// <summary>
/// Mantiene el set de Redis "blacklist:active-plates" sincronizado con VehiculosRobados, para
/// que el lookup del camino caliente en PlateReadConsumer nunca tenga que tocar SQL Server.
/// Carga la lista completa de placas activas al arrancar, y la vuelve a cargar cada 5 minutos
/// como respaldo por si algún BlacklistEntryAddedEvent/RemovedEvent se perdiera (p. ej. una
/// caída momentánea de este servicio) — las actualizaciones al segundo, mientras tanto, las
/// maneja BlacklistEntryAddedConsumer/RemovedConsumer de forma event-driven.
/// </summary>
public class BlacklistCacheService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly IConnectionMultiplexer _redis;
    private readonly SqlConnectionFactory _sqlConnectionFactory;
    private readonly ILogger<BlacklistCacheService> _logger;

    public BlacklistCacheService(
        IConnectionMultiplexer redis,
        SqlConnectionFactory sqlConnectionFactory,
        ILogger<BlacklistCacheService> logger)
    {
        _redis = redis;
        _sqlConnectionFactory = sqlConnectionFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync();

        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var connection = _sqlConnectionFactory.Create();
            var activePlates = (await connection.QueryAsync<string>(
                "SELECT PlateText FROM VehiculosRobados WHERE Estado = 'Activo'")).ToArray();

            var db = _redis.GetDatabase();
            var key = (RedisKey)BlacklistRedisKeys.ActivePlates;

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

            _logger.LogInformation("Blacklist cache refrescada: {Count} placas activas.", activePlates.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al refrescar la blacklist cache en Redis; se mantiene el set anterior.");
        }
    }
}
