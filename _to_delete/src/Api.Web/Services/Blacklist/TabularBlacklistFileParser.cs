using System.Globalization;
using ClosedXML.Excel;

namespace Api.Web.Services.Blacklist;

/// <summary>
/// Lee un archivo Excel (.xlsx) o texto delimitado (.txt/.csv) y lo convierte a
/// <see cref="BlacklistImportRecord"/>, mapeando columnas por NOMBRE de encabezado (no por
/// posición) — así el orden de columnas puede variar entre archivos sin romper el import.
///
/// Formato genérico a propósito: al escribir esto no había todavía un archivo de muestra real de
/// ninguna de las tres fuentes (ver fases.md, Fase 3) — solo la lista de campos que trae cada una
/// (Placa, NumeroReporte, ImagenCarro, BusquedaActiva, Modelo, Año, Vendor, Color, Clase,
/// MarcasUotros). Si al probar con un archivo real el nombre de alguna columna no coincide con
/// ninguno de los alias de <see cref="ColumnAliases"/>, agregar el alias real ahí — es el único
/// lugar que necesita cambiar.
/// </summary>
public static class TabularBlacklistFileParser
{
    private static readonly Dictionary<string, string[]> ColumnAliases = new()
    {
        ["PlateText"] = ["Placa", "PlateText", "Matricula", "Matrícula"],
        ["NumeroReporte"] = ["NumeroReporte", "NumeroDeReporte", "NoReporte", "Reporte"],
        ["FechaReporteUtc"] = ["FechaReporte", "FechaReporteUtc", "Fecha"],
        ["BusquedaActiva"] = ["BusquedaActiva", "Activa", "Activo"],
        ["ImagenPath"] = ["ImagenCarro", "Imagen", "Foto"],
        ["Modelo"] = ["Modelo"],
        ["Anio"] = ["Anio", "Ano", "Año"],
        ["Marca"] = ["Vendor", "Marca", "Fabricante"],
        ["Color"] = ["Color"],
        ["Clase"] = ["Clase", "Tipo", "TipoVehiculo"],
        ["MarcasUOtros"] = ["MarcasUotros", "MarcasUOtros", "Otros", "Notas"],
    };

    private static readonly char[] DelimiterCandidates = ['\t', ';', ',', '|'];

    public static IReadOnlyList<BlacklistImportRecord> Parse(Stream content, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var rows = extension is ".xlsx" or ".xls"
            ? ParseExcel(content)
            : ParseDelimited(content);

        return rows.Select(MapRow).ToList();
    }

    private static IEnumerable<Dictionary<string, string>> ParseExcel(Stream content)
    {
        using var workbook = new XLWorkbook(content);
        var sheet = workbook.Worksheets.First();
        var headerRow = sheet.FirstRowUsed();
        var lastUsedCell = headerRow?.LastCellUsed();
        if (headerRow is null || lastUsedCell is null)
        {
            yield break;
        }

        var lastColumn = lastUsedCell.Address.ColumnNumber;
        var headers = new string?[lastColumn + 1]; // 1-indexado; índice 0 sin usar
        for (var col = 1; col <= lastColumn; col++)
        {
            headers[col] = headerRow.Cell(col).GetValue<string>();
        }

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var col = 1; col <= lastColumn; col++)
            {
                var header = headers[col];
                if (!string.IsNullOrWhiteSpace(header))
                {
                    dict[header] = row.Cell(col).GetValue<string>();
                }
            }

            yield return dict;
        }
    }

    private static IEnumerable<Dictionary<string, string>> ParseDelimited(Stream content)
    {
        using var reader = new StreamReader(content);
        var headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            yield break;
        }

        var delimiter = DetectDelimiter(headerLine);
        var headers = headerLine.Split(delimiter);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = line.Split(delimiter);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length && i < values.Length; i++)
            {
                dict[headers[i].Trim()] = values[i].Trim();
            }

            yield return dict;
        }
    }

    /// <summary>Sin un archivo de muestra real todavía, se detecta por el delimitador más
    /// frecuente en el encabezado entre los candidatos usuales — ajustar si el .txt real usa
    /// otro esquema (ej. ancho fijo, que esto no soporta).</summary>
    private static char DetectDelimiter(string headerLine)
    {
        var best = DelimiterCandidates[0];
        var bestCount = -1;
        foreach (var candidate in DelimiterCandidates)
        {
            var count = headerLine.Count(ch => ch == candidate);
            if (count > bestCount)
            {
                bestCount = count;
                best = candidate;
            }
        }

        return best;
    }

    private static BlacklistImportRecord MapRow(Dictionary<string, string> row)
    {
        string? Get(string canonical)
        {
            foreach (var alias in ColumnAliases[canonical])
            {
                if (row.TryGetValue(alias, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        return new BlacklistImportRecord
        {
            PlateText = Get("PlateText") ?? string.Empty,
            NumeroReporte = Get("NumeroReporte") ?? string.Empty,
            FechaReporteUtc = ParseFecha(Get("FechaReporteUtc")),
            BusquedaActiva = ParseBool(Get("BusquedaActiva")),
            ImagenPath = Get("ImagenPath"),
            Modelo = Get("Modelo"),
            Anio = ParseAnio(Get("Anio")),
            Marca = Get("Marca"),
            Color = Get("Color"),
            Clase = Get("Clase"),
            MarcasUOtros = Get("MarcasUOtros"),
        };
    }

    private static DateTime? ParseFecha(string? raw) =>
        raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    private static int? ParseAnio(string? raw) =>
        raw is not null && double.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? (int)value
            : null;

    /// <summary>Acepta "true"/"false", "1"/"0", y "verdadero"/"falso" (Excel en español) — la
    /// fuente real describe este campo justo como "Falso o verdadero".</summary>
    private static bool ParseBool(string? raw)
    {
        if (raw is null)
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "verdadero" or "si" or "sí" or "activo" => true,
            _ => false,
        };
    }
}
