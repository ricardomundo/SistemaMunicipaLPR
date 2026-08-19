namespace Api.Web.Services.Blacklist;

/// <summary>
/// Fuente externa de reportes de robo (la "API externa" de las tres fuentes que alimentan la
/// lista negra — ver ImplementersGuide.md §11). Todavía no existe el servicio real ni su
/// endpoint/autenticación/formato de respuesta (confirmado al diseñar esto) — esta interfaz deja
/// el punto de extensión listo: cuando exista, escribir una implementación nueva y registrarla en
/// Program.cs en lugar de <see cref="PlaceholderExternalBlacklistSource"/>. El resto del sistema
/// (reconciliación, invalidación de Redis) no necesita cambiar.
/// </summary>
public interface IExternalBlacklistSource
{
    Task<IReadOnlyList<BlacklistImportRecord>> FetchAsync(CancellationToken ct);
}

/// <summary>
/// Placeholder mientras no exista el servicio real: devuelve una lista vacía en vez de fallar,
/// para que <see cref="ExternalBlacklistSyncService"/> pueda quedar registrado y corriendo sin
/// romper el arranque de <c>Api.Web</c>.
/// </summary>
public class PlaceholderExternalBlacklistSource : IExternalBlacklistSource
{
    public Task<IReadOnlyList<BlacklistImportRecord>> FetchAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BlacklistImportRecord>>(Array.Empty<BlacklistImportRecord>());
}
