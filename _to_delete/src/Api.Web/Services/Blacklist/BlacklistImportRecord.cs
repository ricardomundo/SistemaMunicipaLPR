namespace Api.Web.Services.Blacklist;

/// <summary>
/// Forma canónica de un reporte de robo, sin importar si vino de un archivo Excel, un .txt, o la
/// API externa (todavía no construida, ver <see cref="IExternalBlacklistSource"/>) — las tres
/// fuentes traen los mismos datos, así que un solo tipo basta para las tres
/// (ver ImplementersGuide.md §11).
/// </summary>
public sealed record BlacklistImportRecord
{
    public required string PlateText { get; init; }
    public required string NumeroReporte { get; init; }
    public DateTime? FechaReporteUtc { get; init; }

    /// <summary>true = robo activo (alta / mantener en la lista negra); false = recuperado (baja).</summary>
    public required bool BusquedaActiva { get; init; }

    public string? ImagenPath { get; init; }
    public string? Modelo { get; init; }
    public int? Anio { get; init; }
    public string? Marca { get; init; }
    public string? Color { get; init; }
    public string? Clase { get; init; }
    public string? MarcasUOtros { get; init; }
}

public enum BlacklistImportAction
{
    Alta,
    Baja,
    Actualizado,
    SinCambios,
    Error,
}

/// <summary>Resultado de reconciliar un único registro. <paramref name="Fila"/> es 0 cuando el
/// registro no vino de un archivo (ej. la sincronización con la API externa).</summary>
public sealed record BlacklistImportRowResult(int Fila, string? PlateText, BlacklistImportAction Accion, string? Mensaje = null);

public sealed record BlacklistImportSummary(
    int TotalFilas,
    int Altas,
    int Bajas,
    int Actualizados,
    int SinCambios,
    int Errores,
    IReadOnlyList<BlacklistImportRowResult> Detalle);
