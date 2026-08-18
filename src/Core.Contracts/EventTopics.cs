namespace Core.Contracts;

/// <summary>
/// Nombres de topic usados por DotNetCore.CAP para publicar/suscribirse a cada evento de
/// Fase 2. CAP enruta por nombre de topic (string), no por tipo de mensaje .NET como hacía
/// MassTransit, así que cada evento necesita una constante aquí en vez de inferirse del
/// nombre de la clase.
/// </summary>
public static class EventTopics
{
    public const string PlateRead = "plate-read-event";
    public const string BlacklistHitSaved = "blacklist-hit-saved-event";
    public const string BlacklistEntryAdded = "blacklist-entry-added-event";
    public const string BlacklistEntryRemoved = "blacklist-entry-removed-event";
}
