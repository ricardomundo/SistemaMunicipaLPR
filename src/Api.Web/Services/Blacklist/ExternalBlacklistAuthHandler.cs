using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Api.Web.Services.Blacklist;

/// <summary>
/// Agrega "Authorization: Bearer {token}" a cada request hacia la API externa, leyendo el token
/// de <see cref="ExternalBlacklistApiOptions"/> vía <see cref="IOptionsMonitor{TOptions}"/> en
/// cada llamada (no una sola vez al arrancar) — así, si el token se actualiza en
/// appsettings.json/user-secrets y la configuración se recarga, no hace falta reiniciar el
/// proceso para que tome efecto.
/// </summary>
public class ExternalBlacklistAuthHandler(IOptionsMonitor<ExternalBlacklistApiOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = options.CurrentValue.BearerToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
