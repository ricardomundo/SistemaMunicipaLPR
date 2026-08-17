namespace Core.Contracts;

/// <summary>
/// Published when a plate is cleared from VehiculosRobados (vehicle recovered / report
/// closed), so BlacklistCacheService evicts it from Redis immediately rather than leaving a
/// stale hit in the hot-path cache until the next delta refresh.
/// </summary>
public sealed record BlacklistEntryRemovedEvent
{
    public string PlateText { get; init; } = default!;
    public DateTime RemovedAtUtc { get; init; }
}
