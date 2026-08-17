namespace Core.Domain;

/// <summary>
/// Registro append-only de cada lectura de placa procesada (coincida o no con la lista
/// negra). Es el log forense/estadístico — respaldado por un índice columnstore para
/// consultas analíticas sobre volúmenes grandes. Insertado por Service.Inference vía Dapper
/// en el camino caliente; nunca actualizado ni borrado por la aplicación.
/// </summary>
public class LecturaHistorica
{
    public long Id { get; set; }

    /// <summary>Id del PlateReadEvent original — deduplica reintentos/reprocesos del buffer de borde.</summary>
    public Guid EventId { get; set; }

    public string PlateText { get; set; } = default!;
    public int CamaraId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public double Confidence { get; set; }
    public string? ImageReference { get; set; }
    public bool EsCoincidenciaBlacklist { get; set; }
}
