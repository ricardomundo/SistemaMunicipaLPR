using System.Threading.Tasks;
using Api.Web.Hubs;
using Core.Contracts;
using DotNetCore.CAP;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Api.Web.Consumers;

/// <summary>
/// Empuja cada match de blacklist a todos los clientes conectados al AlertHub. Corre como
/// suscriptor independiente de BlacklistHitPersistenceConsumer (en Service.Inference) — ambos
/// se suscriben al mismo topic "blacklist-hit-saved-event", pero cada servicio tiene su propio
/// "Group" de CAP (ver Program.cs), así que cada uno recibe su propia copia del mensaje y una
/// escritura lenta a SQL nunca retrasa este push en tiempo real.
/// </summary>
public class AlertNotificationConsumer : ICapSubscribe
{
    private readonly IHubContext<AlertHub> _hubContext;
    private readonly ILogger<AlertNotificationConsumer> _logger;

    public AlertNotificationConsumer(IHubContext<AlertHub> hubContext, ILogger<AlertNotificationConsumer> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    [CapSubscribe(EventTopics.BlacklistHitSaved)]
    public async Task HandleAsync(BlacklistHitSavedEvent message)
    {
        var reading = message.Reading;

        await _hubContext.Clients.All.SendAsync("AlertaBlacklist", new
        {
            reading.EventId,
            reading.PlateText,
            reading.CameraId,
            reading.TimestampUtc,
            reading.Confidence,
            message.MatchedAtUtc
        });

        _logger.LogInformation("Alerta empujada por SignalR: {PlateText} (cámara {CameraId}).", reading.PlateText, reading.CameraId);
    }
}
