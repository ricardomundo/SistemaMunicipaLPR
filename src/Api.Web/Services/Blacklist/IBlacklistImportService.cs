namespace Api.Web.Services.Blacklist;

/// <summary>
/// Reconciliación de UN registro de robo contra <c>VehiculosRobados</c> — usada tanto por la
/// importación manual de archivos (<c>BlacklistController.Import</c>) como por la sincronización
/// periódica con la API externa (<c>ExternalBlacklistSyncService</c>), para no duplicar la lógica
/// de alta/baja/actualización en dos lugares.
/// </summary>
public interface IBlacklistImportService
{
    /// <param name="filaIndex">Número de fila para reportar en <see cref="BlacklistImportRowResult"/>
    /// (1-based, contando el encabezado como fila 1). Usa 0 cuando el registro no viene de un
    /// archivo (ej. la fuente externa).</param>
    Task<BlacklistImportRowResult> UpsertAsync(BlacklistImportRecord record, int filaIndex, CancellationToken ct = default);
}
