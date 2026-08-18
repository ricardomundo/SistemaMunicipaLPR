using System;
using System.Threading.Tasks;
using Core.Contracts;
using Dapper;
using DotNetCore.CAP;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Service.Inference.Data;
using Service.Inference.Redis;
using StackExchange.Redis;

namespace Service.Inference.Consumers;

/// <summary>
/// Suscriptor de CAP del camino caliente: por cada PlateReadEvent hace un solo lookup O(1)
/// contra el set de Redis "blacklist:active-plates". Si hay match, publica
/// BlacklistHitSavedEvent — el push a SignalR (en Api.Web) y la persistencia de
/// LecturaHistorica/Alerta (BlacklistHitPersistenceConsumer, en este mismo servicio) corren
/// como suscriptores independientes de ese topic, fuera de este camino caliente.
///
/// Por default, si NO hay match, no se escribe nada a SQL — evita meter una escritura síncrona
/// por cada lectura sin importancia en el camino caliente. Esto es configurable vía
/// "PlateReadLogging:OnlyLogMatches" en appsettings.json (default: true = solo se registran
/// los matches) para quien necesite el log forense completo de "toda lectura, match o no".
/// </summary>
public class PlateReadConsumer : ICapSubscribe
{
    private readonly IConnectionMultiplexer _redis;
    private readonly SqlConnectionFactory _sqlConnectionFactory;
    private readonly IConfiguration _configuration;
    private readonly ICapPublisher _capPublisher;
    private readonly ILogger<PlateReadConsumer> _logger;

    public PlateReadConsumer(
        IConnectionMultiplexer redis,
        SqlConnectionFactory sqlConnectionFactory,
        IConfiguration configuration,
        ICapPublisher capPublisher,
        ILogger<PlateReadConsumer> logger)
    {
        _redis = redis;
        _sqlConnectionFactory = sqlConnectionFactory;
        _configuration = configuration;
        _capPublisher = capPublisher;
        _logger = logger;
    }

    [CapSubscribe(EventTopics.PlateRead)]
    public async Task HandleAsync(PlateReadEvent reading)
    {
        var db = _redis.GetDatabase();
        var isMatch = await db.SetContainsAsync(BlacklistRedisKeys.ActivePlates, reading.PlateText);

        if (isMatch)
        {
            await _capPublisher.PublishAsync(EventTopics.BlacklistHitSaved, new BlacklistHitSavedEvent
            {
                Reading = reading,
                MatchedAtUtc = DateTime.UtcNow
            });
            _logger.LogInformation("Match de blacklist: {PlateText} (cámara {CameraId}).", reading.PlateText, reading.CameraId);
            return;
        }

        var onlyLogMatches = _configuration.GetValue("PlateReadLogging:OnlyLogMatches", true);
        if (onlyLogMatches)
        {
            return;
        }

        await LogNonMatchAsync(reading);
    }

    private async Task LogNonMatchAsync(PlateReadEvent reading)
    {
        using var connection = _sqlConnectionFactory.Create();

        var camaraId = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT Id FROM Camaras WHERE Codigo = @Codigo", new { Codigo = reading.CameraId });

        if (camaraId is null)
        {
            _logger.LogWarning(
                "PlateReadEvent {EventId} referencia la cámara '{CameraId}', que no existe en Camaras — no se registra la lectura sin match.",
                reading.EventId, reading.CameraId);
            return;
        }

        try
        {
            await connection.ExecuteAsync(
                @"INSERT INTO LecturasHistoricas (EventId, PlateText, CamaraId, TimestampUtc, Confidence, ImageReference, EsCoincidenciaBlacklist)
                  VALUES (@EventId, @PlateText, @CamaraId, @TimestampUtc, @Confidence, @ImageReference, 0);",
                new
                {
                    reading.EventId,
                    reading.PlateText,
                    CamaraId = camaraId.Value,
                    reading.TimestampUtc,
                    reading.Confidence,
                    reading.ImageReference
                });
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Ya se había insertado esta lectura antes (reintento/redelivery) — el índice
            // único de EventId lo bloqueó, tal como se espera. No es un error real.
            _logger.LogInformation("PlateReadEvent {EventId} ya estaba registrado (dedupe por EventId).", reading.EventId);
        }
    }
}
