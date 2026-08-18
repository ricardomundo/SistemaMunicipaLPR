using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Api.Web.Hubs;

/// <summary>
/// Empuja alertas de blacklist a los clientes conectados (dashboard C4, apps de patrulla) —
/// ver AlertNotificationConsumer, que es quien invoca los envíos. Este Hub no expone métodos
/// invocables por el cliente todavía; solo requiere un JWT válido para conectarse (mismo
/// esquema Bearer que el resto de la API, vía "access_token" en query string para el handshake
/// de WebSocket — ver el OnMessageReceived en Program.cs).
/// </summary>
[Authorize]
public class AlertHub : Hub
{
}
