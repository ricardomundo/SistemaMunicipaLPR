namespace Core.Domain;

/// <summary>
/// Registro auditado de cada coincidencia contra la lista negra que se disparó hacia C4/patrullas.
/// Append-only por diseño: el estado avanza (Pendiente -> Atendida/Descartada) pero el
/// registro y su timestamp original nunca se sobrescriben.
/// </summary>
public class Alerta
{
    public long Id { get; set; }
    public long LecturaHistoricaId { get; set; }
    public int VehiculoRobadoId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public EstadoAlerta Estado { get; set; } = EstadoAlerta.Pendiente;

    /// <summary>Identificador del operador (subject/sub de Keycloak) que atendió o descartó la alerta.</summary>
    public string? AtendidaPor { get; set; }
    public DateTime? AtendidaAtUtc { get; set; }
}
