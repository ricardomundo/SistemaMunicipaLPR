namespace Core.Contracts;

/// <summary>
/// Nombres de topic usados por DotNetCore.CAP para publicar/suscribirse a cada evento. CAP
/// enruta por nombre de topic (string), no por tipo de mensaje .NET como hacía MassTransit,
/// así que cada evento necesita una constante aquí en vez de inferirse del nombre de la clase.
///
/// <see cref="PlateRead"/> NO está aquí a propósito: PlateReadEvent se saca del outbox de CAP
/// por volumen — ver <see cref="RawQueues.PlateRead"/> y ImplementersGuide.md §9 para el
/// hallazgo completo y el razonamiento.
///
/// La invalidación de la caché de RedLists en Redis (Service.Inference) ya NO usa eventos CAP
/// propios — BlacklistEntryAddedEvent/RemovedEvent se retiraron en Fase 3.5 (ver
/// docs/fases.md): Service.Inference se suscribe directo al hub de SignalR de RedLists
/// (VehicleListsService, "/hubs/vehicle-lists") en vez de a un evento propio de este repo.
/// </summary>
public static class EventTopics
{
    public const string BlacklistHitSaved = "blacklist-hit-saved-event";
}
