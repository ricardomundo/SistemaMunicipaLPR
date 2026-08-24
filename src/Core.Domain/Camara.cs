namespace Core.Domain;

public class Camara
{
    public int Id { get; set; }
    public string Codigo { get; set; } = default!;
    public string Nombre { get; set; } = default!;

    /// <summary>Coordenadas WGS84 (mismo sistema que ESRI/Google Maps) de la instalación.</summary>
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    public TipoInstalacionCamara TipoInstalacion { get; set; }
    public int? VelocidadMaximaKmh { get; set; }
    public bool Activa { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}
