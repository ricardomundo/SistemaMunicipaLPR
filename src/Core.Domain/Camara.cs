using NetTopologySuite.Geometries;

namespace Core.Domain;

public class Camara
{
    public int Id { get; set; }
    public string Codigo { get; set; } = default!;
    public string Nombre { get; set; } = default!;

    /// <summary>SQL Server <c>geography</c> point (SRID 4326 / WGS84) — igual sistema que ESRI/Google Maps.</summary>
    public Point Ubicacion { get; set; } = default!;

    public TipoInstalacionCamara TipoInstalacion { get; set; }
    public int? VelocidadMaximaKmh { get; set; }
    public bool Activa { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}
