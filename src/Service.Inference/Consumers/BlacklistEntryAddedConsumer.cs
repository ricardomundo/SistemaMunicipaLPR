using System.Threading.Tasks;
using Core.Contracts;
using DotNetCore.CAP;
using Microsoft.Extensions.Logging;
using Service.Inference.Redis;
using StackExchange.Redis;

namespace Service.Inference.Consumers;

/// <summary>
/// Invalidación inmediata: cuando se da de alta una placa en la lista negra, se agrega al set
/// de Redis al instante en vez de esperar el refresco delta de BlacklistCacheService (que
/// corre cada 5 min) — un reporte de robo urgente debe ser exigible en segundos, no minutos.
/// </summary>
public class BlacklistEntryAddedConsumer : ICapSubscribe
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<BlacklistEntryAddedConsumer> _logger;

    public BlacklistEntryAddedConsumer(IConnectionMultiplexer redis, ILogger<BlacklistEntryAddedConsumer> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    [CapSubscribe(EventTopics.BlacklistEntryAdded)]
    public async Task HandleAsync(BlacklistEntryAddedEvent message)
    {
        var db = _redis.GetDatabase();
        await db.SetAddAsync(BlacklistRedisKeys.ActivePlates, message.PlateText);
        _logger.LogInformation("Placa {PlateText} agregada a la blacklist cache.", message.PlateText);
    }
}
