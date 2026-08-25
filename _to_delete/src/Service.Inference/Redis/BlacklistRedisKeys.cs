namespace Service.Inference.Redis;

/// <summary>
/// Nombres de llaves de Redis para la cache de blacklist, centralizados aquí para que el
/// loader (BlacklistCacheService) y el lookup del camino caliente (PlateReadConsumer) nunca
/// se desincronicen por un typo.
/// </summary>
public static class BlacklistRedisKeys
{
    /// <summary>Set de Redis con el PlateText de cada VehiculoRobado con Estado = Activo — lookup O(1) vía SISMEMBER.</summary>
    public const string ActivePlates = "blacklist:active-plates";
}
