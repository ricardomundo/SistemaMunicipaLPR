using System.Text.RegularExpressions;

namespace Api.Web.Services.Blacklist;

/// <summary>
/// Un solo lugar para el criterio de normalización de placas, usado tanto por
/// <c>BlacklistController</c> (alta/baja manual) como por <c>BlacklistImportService</c>
/// (importación masiva). Mismo criterio que <c>edge/src/ocr.py</c> (<c>normalize_plate_text</c>):
/// mayúsculas, sin espacios/guiones/ruido — necesario para que el lookup en Redis (por igualdad
/// de string) siempre haga match, sin importar de qué fuente vino la placa.
/// </summary>
public static class PlateTextNormalizer
{
    private static readonly Regex Pattern = new("[^A-Z0-9]", RegexOptions.Compiled);

    public static string Normalize(string raw) => Pattern.Replace(raw.ToUpperInvariant(), "");
}
