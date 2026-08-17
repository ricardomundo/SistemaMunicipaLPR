namespace Core.Domain;

/// <summary>
/// Fila de la Lista Negra en SQL Server (fuente de verdad auditada). Redis mantiene una
/// copia en memoria solo de <see cref="PlateText"/> para el lookup O(1) del camino caliente;
/// esta tabla es la que respalda esa copia y guarda el historial de altas/bajas.
/// </summary>
public class VehiculoRobado
{
    public int Id { get; set; }
    public string PlateText { get; set; } = default!;
    public string NumeroReporte { get; set; } = default!;
    public EstadoVehiculoRobado Estado { get; set; } = EstadoVehiculoRobado.Activo;
    public DateTime FechaReporteUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RecuperadoAtUtc { get; set; }
}
