namespace Core.Contracts;

/// <summary>
/// Published right after a plate is inserted into VehiculosRobados, so
/// BlacklistCacheService can push it into Redis immediately instead of waiting for the
/// 5-minute delta refresh — an urgent theft report must be enforceable in seconds, not minutes.
/// </summary>
public sealed record BlacklistEntryAddedEvent
{
    public string PlateText { get; init; } = default!;
    public DateTime AddedAtUtc { get; init; }
}
