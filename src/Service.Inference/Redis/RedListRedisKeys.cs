namespace Service.Inference.Redis;

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
