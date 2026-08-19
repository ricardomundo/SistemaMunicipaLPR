namespace Api.Web.Services.Blacklist;

/// <summary>
/// Fuente externa de reportes de robo (la "API externa" de las tres fuentes que alimentan la
/// lista negra — ver ImplementersGuide.md §11). Implementación real: <see cref="HttpExternalBlacklistSource"/>
/// (GET + bearer token, ver <see cref="ExternalBlacklistApiOptions"/>). Esta interfaz mantiene el
/// punto de extensión desacoplado del resto del sistema (reconciliación, invalidación de Redis),
/// que no necesita cambiar si la fuente externa cambia de forma/transporte más adelante.
/// </summary>
public interface IExternalBlacklistSource
{
    Task<IReadOnlyList<BlacklistImportRecord>> FetchAsync(CancellationToken ct);
}
