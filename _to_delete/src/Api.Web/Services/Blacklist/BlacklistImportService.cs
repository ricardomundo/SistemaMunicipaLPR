using Api.Web.Data;
using Core.Contracts;
using Core.Domain;
using DotNetCore.CAP;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.Services.Blacklist;

public class BlacklistImportService(LprDbContext db, ICapPublisher capPublisher) : IBlacklistImportService
{
    public async Task<BlacklistImportRowResult> UpsertAsync(BlacklistImportRecord record, int filaIndex, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(record.PlateText))
        {
            return new BlacklistImportRowResult(filaIndex, null, BlacklistImportAction.Error, "Fila sin placa — se descarta.");
        }

        var plateText = PlateTextNormalizer.Normalize(record.PlateText);
        if (plateText.Length == 0)
        {
            return new BlacklistImportRowResult(filaIndex, record.PlateText, BlacklistImportAction.Error, "Placa inválida: no quedan caracteres alfanuméricos tras normalizar.");
        }

        if (string.IsNullOrWhiteSpace(record.NumeroReporte))
        {
            return new BlacklistImportRowResult(filaIndex, plateText, BlacklistImportAction.Error, "NumeroReporte requerido.");
        }

        var now = DateTime.UtcNow;

        // Reconciliación por placa: se busca el reporte ACTIVO más reciente para esta placa, sin
        // importar de qué NumeroReporte se trate — así una misma placa que reaparece en corridas
        // sucesivas (de la misma fuente u otra) actualiza el mismo registro en vez de duplicarlo.
        var existente = await db.VehiculosRobados
            .Where(v => v.PlateText == plateText && v.Estado == EstadoVehiculoRobado.Activo)
            .OrderByDescending(v => v.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (record.BusquedaActiva)
        {
            if (existente is null)
            {
                var entity = new VehiculoRobado
                {
                    PlateText = plateText,
                    NumeroReporte = record.NumeroReporte,
                    Estado = EstadoVehiculoRobado.Activo,
                    FechaReporteUtc = record.FechaReporteUtc ?? now,
                    CreatedAtUtc = now,
                    ImagenPath = record.ImagenPath,
                    Modelo = record.Modelo,
                    Anio = record.Anio,
                    Marca = record.Marca,
                    Color = record.Color,
                    Clase = record.Clase,
                    MarcasUOtros = record.MarcasUOtros,
                };

                db.VehiculosRobados.Add(entity);
                await db.SaveChangesAsync(ct);

                // Invalidación inmediata de Redis — no espera el refresco delta de 5 min de
                // BlacklistCacheService.
                await capPublisher.PublishAsync(EventTopics.BlacklistEntryAdded, new BlacklistEntryAddedEvent
                {
                    PlateText = plateText,
                    AddedAtUtc = now,
                });

                return new BlacklistImportRowResult(filaIndex, plateText, BlacklistImportAction.Alta);
            }

            // Ya estaba activa: se actualizan los datos descriptivos por si vinieron
            // corregidos/ampliados, pero no se vuelve a publicar el evento de alta — Redis ya
            // tiene la placa.
            if (ActualizarDatosDescriptivos(existente, record))
            {
                await db.SaveChangesAsync(ct);
                return new BlacklistImportRowResult(filaIndex, plateText, BlacklistImportAction.Actualizado);
            }

            return new BlacklistImportRowResult(filaIndex, plateText, BlacklistImportAction.SinCambios);
        }

        // BusquedaActiva = false: baja / recuperación.
        if (existente is null)
        {
            // No hay nada activo que dar de baja — no es un error, simplemente no hay cambio.
            return new BlacklistImportRowResult(filaIndex, plateText, BlacklistImportAction.SinCambios, "No había un reporte activo para dar de baja.");
        }

        existente.Estado = EstadoVehiculoRobado.Recuperado;
        existente.RecuperadoAtUtc = now;
        await db.SaveChangesAsync(ct);

        await capPublisher.PublishAsync(EventTopics.BlacklistEntryRemoved, new BlacklistEntryRemovedEvent
        {
            PlateText = plateText,
            RemovedAtUtc = now,
        });

        return new BlacklistImportRowResult(filaIndex, plateText, BlacklistImportAction.Baja);
    }

    /// <summary>Solo pisa un campo si el registro entrante trae un valor no vacío y distinto al
    /// que ya había — así una fuente que no manda cierta columna (ej. un .txt sin MarcasUOtros)
    /// no borra lo que ya se sabía por otra fuente.</summary>
    private static bool ActualizarDatosDescriptivos(VehiculoRobado entity, BlacklistImportRecord record)
    {
        var cambio = false;

        cambio |= AsignarSiCambia(record.ImagenPath, entity.ImagenPath, v => entity.ImagenPath = v);
        cambio |= AsignarSiCambia(record.Modelo, entity.Modelo, v => entity.Modelo = v);
        cambio |= AsignarSiCambia(record.Marca, entity.Marca, v => entity.Marca = v);
        cambio |= AsignarSiCambia(record.Color, entity.Color, v => entity.Color = v);
        cambio |= AsignarSiCambia(record.Clase, entity.Clase, v => entity.Clase = v);
        cambio |= AsignarSiCambia(record.MarcasUOtros, entity.MarcasUOtros, v => entity.MarcasUOtros = v);

        if (record.Anio is not null && record.Anio != entity.Anio)
        {
            entity.Anio = record.Anio;
            cambio = true;
        }

        return cambio;
    }

    private static bool AsignarSiCambia(string? nuevo, string? actual, Action<string?> asignar)
    {
        if (string.IsNullOrWhiteSpace(nuevo) || nuevo == actual)
        {
            return false;
        }

        asignar(nuevo);
        return true;
    }
}
