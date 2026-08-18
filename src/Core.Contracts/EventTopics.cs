namespace Core.Contracts;

/// <summary>
/// Nombres de topic usados por DotNetCore.CAP para publicar/suscribirse a cada evento de
/// Fase 2. CAP enruta por nombre de topic (string), no por tipo de mensaje .NET como hacía
/// MassTransit, así que cada evento necesita una constante aquí en vez de inferirse del
/// nombre de la clase.
///
/// <see cref="PlateRead"/> NO está aquí a propósito (2026-08-18): PlateReadEvent se saca del
/// outbox de CAP por volumen — ver <see cref="RawQueues.PlateRead"/> y
/// ImplementersGuide.md §9 para el hallazgo completo y el razonamiento.
/// </summary>
public static class EventTopics
{
    public const string BlacklistHitSaved = "blacklist-hit-saved-event";
    public const string BlacklistEntryAdded = "blacklist-entry-added-event";
    public const string BlacklistEntryRemoved = "blacklist-entry-removed-event";
}
