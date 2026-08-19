using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Api.Web.Services.Blacklist;

/// <summary>
/// Implementación real de <see cref="IExternalBlacklistSource"/>: un GET simple a
/// ExternalBlacklist:BaseUrl (appsettings.json), autenticado con un bearer token estático que el
/// cliente ya entregó (ver <see cref="ExternalBlacklistApiOptions"/> y
/// <see cref="ExternalBlacklistAuthHandler"/>).
///
/// El endpoint confirmado con el cliente regresa SIEMPRE el catálogo completo de reportes
/// (activos e inactivos, con "busquedaActiva" explícito por registro) — no expone un concepto de
/// "cambios desde X". Por eso no hace falta ninguna lógica de delta aquí: cada ciclo de
/// <see cref="ExternalBlacklistSyncService"/> reprocesa el arreglo completo por
/// <see cref="IBlacklistImportService.UpsertAsync"/>, que ya es idempotente (una placa sin
/// cambios reales queda como <see cref="BlacklistImportAction.SinCambios"/>).
/// </summary>
public class HttpExternalBlacklistSource(
    HttpClient httpClient,
    IOptionsMonitor<ExternalBlacklistApiOptions> options,
    ILogger<HttpExternalBlacklistSource> logger) : IExternalBlacklistSource
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<BlacklistImportRecord>> FetchAsync(CancellationToken ct)
    {
        var baseUrl = options.CurrentValue.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("TODO", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("ExternalBlacklist:BaseUrl no está configurado todavía (ver appsettings.json) — se omite este ciclo de sincronización.");
            return [];
        }

        logger.LogInformation("Consultando fuente externa de blacklist en {BaseUrl}...", baseUrl);

        using var response = await httpClient.GetAsync(baseUrl, ct);
        response.EnsureSuccessStatusCode();

        var registrosExternos = await response.Content.ReadFromJsonAsync<List<ExternalBlacklistApiRecord>>(JsonOptions, ct) ?? [];

        logger.LogInformation("Fuente externa de blacklist respondió con {Count} registro(s).", registrosExternos.Count);

        return registrosExternos.Select(Map).ToList();
    }

    private static BlacklistImportRecord Map(ExternalBlacklistApiRecord r) => new()
    {
        PlateText = r.Placa ?? string.Empty,
        NumeroReporte = r.NumeroReporte ?? string.Empty,
        FechaReporteUtc = ParseFecha(r.FechaReporte),
        BusquedaActiva = r.BusquedaActiva,
        ImagenPath = NullIfEmpty(r.ImagenCarro),
        Modelo = NullIfEmpty(r.Modelo),
        Anio = r.Anio,
        Marca = NullIfEmpty(r.Vendor),
        Color = NullIfEmpty(r.Color),
        Clase = NullIfEmpty(r.Clase),
        MarcasUOtros = NullIfEmpty(r.MarcasUotros),
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTime? ParseFecha(string? raw) =>
        raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    /// <summary>
    /// Forma exacta del JSON que regresa la API externa (confirmada con un ejemplo real del
    /// cliente): un arreglo plano de objetos camelCase. "vendor" mapea a "Marca" en el tipo
    /// canónico — mismo criterio que ya usa <c>TabularBlacklistFileParser</c> para Excel/.txt.
    /// </summary>
    private sealed record ExternalBlacklistApiRecord
    {
        public string? Placa { get; init; }
        public string? NumeroReporte { get; init; }
        public string? FechaReporte { get; init; }
        public bool BusquedaActiva { get; init; }
        public string? ImagenCarro { get; init; }
        public string? Modelo { get; init; }
        public int? Anio { get; init; }
        public string? Vendor { get; init; }
        public string? Color { get; init; }
        public string? Clase { get; init; }
        public string? MarcasUotros { get; init; }
    }
}
