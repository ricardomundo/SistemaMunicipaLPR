namespace Api.Web.Services.Blacklist;

/// <summary>
/// Configuración de <see cref="HttpExternalBlacklistSource"/>. <see cref="BaseUrl"/> se llena en
/// <c>appsettings.json</c> (no es secreto). <see cref="BearerToken"/> SÍ es secreto — el cliente
/// ya lo entregó como token fijo — y NUNCA debe ponerse en <c>appsettings.json</c> ni en ningún
/// archivo que se suba a git. Configurarlo vía <c>dotnet user-secrets</c> en desarrollo, o vía
/// variable de entorno (<c>ExternalBlacklist__BearerToken</c>) en otros ambientes. Ver el
/// incidente ya resuelto del client_secret de Keycloak expuesto en <c>br.bat</c>
/// (ImplementersGuide.md §8) — la misma regla aplica aquí.
/// </summary>
public class ExternalBlacklistApiOptions
{
    public const string SectionName = "ExternalBlacklist";

    /// <summary>URL completa del endpoint GET que regresa el arreglo JSON de reportes de robo.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Token fijo provisto por el cliente — se manda como "Authorization: Bearer {token}".</summary>
    public string? BearerToken { get; set; }
}
