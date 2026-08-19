namespace Api.Web.Services.Blacklist;

/// <summary>
/// Sincronización periódica desde <see cref="IExternalBlacklistSource"/> hacia
/// <c>VehiculosRobados</c>, reutilizando <see cref="IBlacklistImportService"/> — la misma
/// reconciliación (alta/baja/actualización por placa) que usa la importación manual de archivos,
/// para no duplicar esa lógica en dos lugares.
///
/// <see cref="IBlacklistImportService"/> y <c>LprDbContext</c> están registrados como Scoped, así
/// que este worker (Singleton, como todo <see cref="BackgroundService"/>) crea su propio scope en
/// cada ciclo — mismo patrón que ya usa <c>Program.cs</c> para las migraciones al arrancar.
/// </summary>
public class ExternalBlacklistSyncService(
    IServiceScopeFactory scopeFactory,
    IExternalBlacklistSource source,
    ILogger<ExternalBlacklistSyncService> logger) : BackgroundService
{
    // Sin un SLA real de la API externa todavía (no existe, ver IExternalBlacklistSource), 15
    // minutos es un punto de partida razonable para un feed de reportes de robo — no es el
    // camino caliente de <300ms, así que no hay presión de latencia aquí. Ajustar si la fuente
    // real expone algo tipo "cambios desde X" que permita sincronizar más seguido sin re-leer
    // todo el catálogo cada vez.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SyncOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncOnceAsync(stoppingToken);
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        try
        {
            var registros = await source.FetchAsync(ct);
            if (registros.Count == 0)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var importService = scope.ServiceProvider.GetRequiredService<IBlacklistImportService>();

            int altas = 0, bajas = 0, actualizados = 0, errores = 0;
            foreach (var registro in registros)
            {
                // filaIndex=0: estos registros no vienen de un archivo con encabezado, no aplica
                // un número de fila.
                var resultado = await importService.UpsertAsync(registro, filaIndex: 0, ct);
                switch (resultado.Accion)
                {
                    case BlacklistImportAction.Alta: altas++; break;
                    case BlacklistImportAction.Baja: bajas++; break;
                    case BlacklistImportAction.Actualizado: actualizados++; break;
                    case BlacklistImportAction.Error: errores++; break;
                }
            }

            logger.LogInformation(
                "Sincronización externa de blacklist: {Total} registro(s) — {Altas} alta(s), {Bajas} baja(s), {Actualizados} actualizado(s), {Errores} error(es).",
                registros.Count, altas, bajas, actualizados, errores);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fallo al sincronizar la blacklist desde la fuente externa; se reintenta en el siguiente ciclo.");
        }
    }
}
