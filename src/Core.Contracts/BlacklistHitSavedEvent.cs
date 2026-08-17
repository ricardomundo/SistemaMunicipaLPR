namespace Core.Contracts;

/// <summary>
/// Published by the plate-read consumer after a Redis blacklist hit, so the persistence
/// consumer can write the audit trail (LecturaHistorica + Alerta) asynchronously — off the
/// SignalR-push hot path.
/// </summary>
public sealed record BlacklistHitSavedEvent
{
    public PlateReadEvent Reading { get; init; } = default!;
    public DateTime MatchedAtUtc { get; init; }
}
