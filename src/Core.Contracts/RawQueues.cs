namespace Core.Contracts;

/// <summary>
/// Nombres de cola de RabbitMQ para eventos que se publican/consumen DIRECTO por
/// RabbitMQ.Client, sin pasar por el outbox transaccional de DotNetCore.CAP. Hoy, solo
/// <see cref="PlateRead"/>.
///
/// Decisión tomada 2026-08-18, tras confirmar con carga sintética (tools/LoadSimulator) que
/// DotNetCore.CAP con storage de SQL Server no sostiene ~500 PlateReadEvent/seg sin acumular
/// backlog creciente (confirmado: la cola "service-inference.v1" seguía con mensajes
/// pendientes varios minutos después de terminado el run). Causa: CAP escribe una fila en
/// cap.Received por cada mensaje CONSUMIDO (además de cap.Published por cada publish) como
/// parte de su garantía de entrega/idempotencia — a alto volumen, eso es una escritura
/// transaccional a SQL Server por mensaje en ambos lados del pipeline, y ese es justo el costo
/// que PlateReadEvent no puede pagar ni necesita: es una señal efímera y de altísimo volumen,
/// sin ninguna escritura local que necesite atomicidad transaccional con el publish (a
/// diferencia de, por ejemplo, un consumer que persiste algo en SQL y necesita garantizar que
/// el publish del evento resultante no se pierda si el proceso truena a medio camino — ahí sí
/// vale la pena pagar el costo del outbox).
///
/// El resto de los eventos (BlacklistHitSavedEvent, BlacklistEntryAddedEvent/RemovedEvent —
/// ver <see cref="EventTopics"/>) son de bajo volumen y SÍ se quedan en CAP: se benefician del
/// outbox transaccional, reintentos automáticos, y la convención de "Group" por servicio, sin
/// que su costo por mensaje sea un problema a ese volumen.
///
/// Detalle completo del hallazgo y el diseño en ImplementersGuide.md §9.
/// </summary>
public static class RawQueues
{
    public const string PlateRead = "plate-read-event.raw";
}
