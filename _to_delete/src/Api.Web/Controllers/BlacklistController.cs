using Api.Web.Authorization;
using Api.Web.Data;
using Api.Web.Services.Blacklist;
using Core.Contracts;
using Core.Domain;
using DotNetCore.CAP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.Controllers;

/// <summary>
/// Alta/baja real de <see cref="VehiculoRobado"/> — SQL Server (vía <see cref="LprDbContext"/>)
/// es la fuente de verdad auditada; Redis (en Service.Inference) solo mantiene una copia en
/// memoria del <c>PlateText</c> activo para el lookup O(1) del camino caliente. Cada alta/baja
/// aquí publica <see cref="BlacklistEntryAddedEvent"/>/<see cref="BlacklistEntryRemovedEvent"/>
/// vía DotNetCore.CAP para invalidar esa copia de Redis de inmediato — sin esto, un INSERT/UPDATE
/// hecho fuera de esta API (p. ej. directo en SQL) solo se refleja hasta el refresco delta de 5
/// minutos de <c>BlacklistCacheService</c>.
///
/// Además del alta/baja manual (un reporte a la vez), <see cref="Import"/> alimenta la lista
/// negra en lote desde un archivo Excel/.txt — ver ImplementersGuide.md §11 para el resto de las
/// fuentes (la sincronización con la API externa vive en <see cref="ExternalBlacklistSyncService"/>,
/// no en este controller).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "Casbin")]
public class BlacklistController(LprDbContext db, ICapPublisher capPublisher, IBlacklistImportService importService) : ControllerBase
{
    public sealed record CreateBlacklistEntryRequest(
        string PlateText,
        string NumeroReporte,
        DateTime? FechaReporteUtc,
        string? ImagenPath = null,
        string? Modelo = null,
        int? Anio = null,
        string? Marca = null,
        string? Color = null,
        string? Clase = null,
        string? MarcasUOtros = null);

    public sealed record BlacklistEntryResponse(
        int Id,
        string PlateText,
        string NumeroReporte,
        string Estado,
        DateTime FechaReporteUtc,
        DateTime CreatedAtUtc,
        DateTime? RecuperadoAtUtc,
        string? ImagenPath,
        string? Modelo,
        int? Anio,
        string? Marca,
        string? Color,
        string? Clase,
        string? MarcasUOtros);

    private static BlacklistEntryResponse ToResponse(VehiculoRobado v) => new(
        v.Id, v.PlateText, v.NumeroReporte, v.Estado.ToString(), v.FechaReporteUtc, v.CreatedAtUtc, v.RecuperadoAtUtc,
        v.ImagenPath, v.Modelo, v.Anio, v.Marca, v.Color, v.Clase, v.MarcasUOtros);

    /// <param name="soloActivas">
    /// Por default solo devuelve reportes con Estado=Activo (lo que hoy está enforced en el
    /// camino caliente vía Redis). Pásalo en false para ver también los recuperados/cerrados —
    /// útil para auditoría, no para el uso operativo del día a día.
    /// </param>
    [HttpGet]
    [CasbinResource("blacklist", "read")]
    public async Task<IActionResult> Get([FromQuery] bool soloActivas = true)
    {
        var query = db.VehiculosRobados.AsNoTracking().AsQueryable();
        if (soloActivas)
        {
            query = query.Where(v => v.Estado == EstadoVehiculoRobado.Activo);
        }

        var entities = await query.OrderByDescending(v => v.CreatedAtUtc).ToListAsync();
        return Ok(entities.Select(ToResponse));
    }

    /// <summary>
    /// Alta de un reporte de robo. Si ya existe un reporte ACTIVO para la misma placa (tras
    /// normalizar), se rechaza con 409 en vez de crear un duplicado — evita dos filas activas
    /// para el mismo vehículo, que confundiría a qué reporte corresponde una futura baja.
    /// </summary>
    [HttpPost]
    [CasbinResource("blacklist", "write")]
    public async Task<IActionResult> Post([FromBody] CreateBlacklistEntryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PlateText) || string.IsNullOrWhiteSpace(request.NumeroReporte))
        {
            return BadRequest("PlateText y NumeroReporte son requeridos.");
        }

        var plateText = PlateTextNormalizer.Normalize(request.PlateText);
        if (plateText.Length == 0)
        {
            return BadRequest("PlateText inválido: no quedan caracteres alfanuméricos tras normalizar.");
        }

        var yaActiva = await db.VehiculosRobados
            .AnyAsync(v => v.PlateText == plateText && v.Estado == EstadoVehiculoRobado.Activo);
        if (yaActiva)
        {
            return Conflict($"Ya existe un reporte activo para la placa '{plateText}'.");
        }

        var now = DateTime.UtcNow;
        var entity = new VehiculoRobado
        {
            PlateText = plateText,
            NumeroReporte = request.NumeroReporte,
            Estado = EstadoVehiculoRobado.Activo,
            FechaReporteUtc = request.FechaReporteUtc ?? now,
            CreatedAtUtc = now,
            ImagenPath = request.ImagenPath,
            Modelo = request.Modelo,
            Anio = request.Anio,
            Marca = request.Marca,
            Color = request.Color,
            Clase = request.Clase,
            MarcasUOtros = request.MarcasUOtros,
        };

        db.VehiculosRobados.Add(entity);
        await db.SaveChangesAsync();

        // Invalidación inmediata de Redis — sin esperar el refresco delta de 5 min de
        // BlacklistCacheService, un reporte de robo urgente debe ser exigible en segundos.
        await capPublisher.PublishAsync(EventTopics.BlacklistEntryAdded, new BlacklistEntryAddedEvent
        {
            PlateText = plateText,
            AddedAtUtc = now,
        });

        return StatusCode(StatusCodes.Status201Created, ToResponse(entity));
    }

    /// <summary>
    /// Baja (vehículo recuperado / reporte cerrado) del reporte ACTIVO para <paramref
    /// name="plateText"/>. No borra la fila — la marca Estado=Recuperado y registra
    /// RecuperadoAtUtc, preservando el historial de altas/bajas que <see cref="VehiculoRobado"/>
    /// está pensado para guardar.
    /// </summary>
    [HttpDelete("{plateText}")]
    [CasbinResource("blacklist", "write")]
    public async Task<IActionResult> Delete(string plateText)
    {
        var normalized = PlateTextNormalizer.Normalize(plateText);

        var entity = await db.VehiculosRobados
            .FirstOrDefaultAsync(v => v.PlateText == normalized && v.Estado == EstadoVehiculoRobado.Activo);
        if (entity is null)
        {
            return NotFound($"No hay un reporte activo para la placa '{normalized}'.");
        }

        var now = DateTime.UtcNow;
        entity.Estado = EstadoVehiculoRobado.Recuperado;
        entity.RecuperadoAtUtc = now;
        await db.SaveChangesAsync();

        await capPublisher.PublishAsync(EventTopics.BlacklistEntryRemoved, new BlacklistEntryRemovedEvent
        {
            PlateText = normalized,
            RemovedAtUtc = now,
        });

        return NoContent();
    }

    /// <summary>
    /// Importación masiva desde un archivo Excel (.xlsx) o texto delimitado (.txt/.csv) — una de
    /// las tres fuentes que alimentan la lista negra (ver ImplementersGuide.md §11). A diferencia
    /// de <see cref="Post"/> (que rechaza duplicados con 409), aquí cada fila se RECONCILIA por
    /// placa: alta si no existía, actualización de datos si ya estaba activa, o baja si la fila
    /// trae BusquedaActiva=false — pensado para poder re-subir el mismo archivo (o uno más
    /// reciente de la misma fuente) sin generar duplicados ni tener que calcular a mano qué
    /// cambió.
    /// </summary>
    [HttpPost("import")]
    [CasbinResource("blacklist", "write")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest("Sube un archivo (.xlsx, .txt o .csv).");
        }

        List<BlacklistImportRecord> registros;
        try
        {
            await using var stream = file.OpenReadStream();
            registros = TabularBlacklistFileParser.Parse(stream, file.FileName).ToList();
        }
        catch (Exception ex)
        {
            return BadRequest($"No se pudo leer el archivo: {ex.Message}");
        }

        var detalle = new List<BlacklistImportRowResult>();
        for (var i = 0; i < registros.Count; i++)
        {
            // Fila 1 = encabezado, así que el primer registro de datos es la fila 2 — coincide
            // con lo que vería alguien abriendo el archivo en Excel.
            var resultado = await importService.UpsertAsync(registros[i], filaIndex: i + 2, ct);
            detalle.Add(resultado);
        }

        var resumen = new BlacklistImportSummary(
            TotalFilas: detalle.Count,
            Altas: detalle.Count(d => d.Accion == BlacklistImportAction.Alta),
            Bajas: detalle.Count(d => d.Accion == BlacklistImportAction.Baja),
            Actualizados: detalle.Count(d => d.Accion == BlacklistImportAction.Actualizado),
            SinCambios: detalle.Count(d => d.Accion == BlacklistImportAction.SinCambios),
            Errores: detalle.Count(d => d.Accion == BlacklistImportAction.Error),
            Detalle: detalle);

        return Ok(resumen);
    }
}
