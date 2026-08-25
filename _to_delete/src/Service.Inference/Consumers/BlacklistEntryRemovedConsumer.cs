using System.Threading.Tasks;
using Core.Contracts;
using DotNetCore.CAP;
using Microsoft.Extensions.Logging;
using Service.Inference.Redis;
using StackExchange.Redis;

namespace Service.Inference.Consumers;

/// <summary>
/// Invalidación inmediata: cuando se da de baja una placa (vehículo recuperado / reporte
/// cerrado), se quita del set de Redis al instante para no dejar un hit obsoleto en el camino
/// caliente hasta el próximo refresco delta de BlacklistCacheService.
/// </summary>
public class BlacklistEntryRemovedConsumer : ICapSubscribe
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<BlacklistEntryRemovedConsumer> _logger;

    public BlacklistEntryRemovedConsumer(IConnectionMultiplexer redis, ILogger<BlacklistEntryRemovedConsumer> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    [CapSubscribe(EventTopics.BlacklistEntryRemoved)]
    public async Task HandleAsync(BlacklistEntryRemovedEvent message)
    {
        var db = _redis.GetDatabase();
        await db.SetRemoveAsync(BlacklistRedisKeys.ActivePlates, message.PlateText);
        _logger.LogInformation("Placa {PlateText} quitada de la blacklist cache.", message.PlateText);
    }
}
