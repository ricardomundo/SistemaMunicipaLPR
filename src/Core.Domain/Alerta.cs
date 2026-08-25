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

    /// <summary>Id de <c>vehicles</c> en el esquema de RedLists (VehicleListsService, misma base
    /// SistemaLPR -- ver Fase 3.5 en docs/fases.md). Sin FK real en LprDbContext: esa tabla la
    /// administra RedLists con su propio esquema SQL, fuera del historial de migraciones de
    /// este DbContext.</summary>
    public long RedListVehicleId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public EstadoAlerta Estado { get; set; } = EstadoAlerta.Pendiente;

    /// <summary>Identificador del operador (subject/sub de Keycloak) que atendió o descartó la alerta.</summary>
    public string? AtendidaPor { get; set; }
    public DateTime? AtendidaAtUtc { get; set; }
}
