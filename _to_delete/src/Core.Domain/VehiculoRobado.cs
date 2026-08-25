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

    // Datos descriptivos del vehículo — alimentados por las fuentes que reportan robos (API
    // externa / Excel / .txt, ver ImplementersGuide.md §11). Todos opcionales: el alta manual
    // vía BlacklistController no los requiere, y no todas las fuentes traen todos los campos.
    public string? ImagenPath { get; set; }
    public string? Modelo { get; set; }
    public int? Anio { get; set; }
    /// <summary>Fabricante del vehículo (algunas fuentes lo llaman "Vendor").</summary>
    public string? Marca { get; set; }
    public string? Color { get; set; }
    /// <summary>Tipo de vehículo (Carro, Motocicleta, Camioneta, etc.) — texto libre a propósito,
    /// no un enum: las fuentes externas pueden traer valores nuevos que no anticipamos aquí.</summary>
    public string? Clase { get; set; }
    /// <summary>Campo abierto para datos adicionales que no encajan en ninguna columna anterior.</summary>
    public string? MarcasUOtros { get; set; }
}
