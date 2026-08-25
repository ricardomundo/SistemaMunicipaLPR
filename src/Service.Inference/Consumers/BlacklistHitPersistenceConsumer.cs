using System.Threading.Tasks;
using Core.Contracts;
using Dapper;
using DotNetCore.CAP;
using MySqlConnector;
using Microsoft.Extensions.Logging;
using Service.Inference.Data;

namespace Service.Inference.Consumers;

/// <summary>
/// Persiste el rastro de auditoría (LecturaHistorica + Alerta) de cada match contra RedLists.
/// Corre como suscriptor separado de PlateReadConsumer para no meter esta escritura síncrona en
/// el camino caliente de matching -- a esta altura ya se publicó BlacklistHitSavedEvent, y
/// AlertNotificationConsumer (en Api.Web) ya puede empujar la alerta por SignalR en paralelo,
/// sin esperar a que esta escritura a SQL termine (cada servicio tiene su propio "Group" de CAP
/// sobre el mismo topic, así que ambos reciben su propia copia del mensaje).
/// </summary>
public class BlacklistHitPersistenceConsumer : ICapSubscribe
{
    private readonly SqlConnectionFactory _sqlConnectionFactory;
    private readonly ILogger<BlacklistHitPersistenceConsumer> _logger;

    public BlacklistHitPersistenceConsumer(SqlConnectionFactory sqlConnectionFactory, ILogger<BlacklistHitPersistenceConsumer> logger)
    {
        _sqlConnectionFactory = sqlConnectionFactory;
        _logger = logger;
    }

    [CapSubscribe(EventTopics.BlacklistHitSaved)]
    public async Task HandleAsync(BlacklistHitSavedEvent message)
    {
        var reading = message.Reading;
        using var connection = _sqlConnectionFactory.Create();

        var camaraId = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT Id FROM Camaras WHERE Codigo = @Codigo", new { Codigo = reading.CameraId });
        if (camaraId is null)
        {
            _logger.LogWarning(
                "BlacklistHitSavedEvent para {EventId} referencia la cámara '{CameraId}', que no existe en Camaras — no se puede registrar la Alerta.",
                reading.EventId, reading.CameraId);
            return;
        }

        // RedLists no deduplica por placa (una placa puede tener varias filas en vehicles/
        // list_vehicles) -- toma la membresía activa más reciente en una lista tipo 'Vehicles'
        // (RedList) para esta placa. Ver Fase 3.5 en docs/fases.md.
        var redListVehicleId = await connection.QuerySingleOrDefaultAsync<long?>(
            @"SELECT v.id
              FROM vehicles v
              JOIN list_vehicles lv ON lv.vehicle_id = v.id
              JOIN vehicle_lists vl ON vl.id = lv.list_id
              WHERE v.plate_number = @PlateText
                AND lv.recovered_by_org_id = 0
                AND vl.list_type = 'Vehicles'
              ORDER BY lv.added_at DESC
              LIMIT 1",
            new { reading.PlateText });
        if (redListVehicleId is null)
        {
            _logger.LogWarning(
                "BlacklistHitSavedEvent para placa '{PlateText}' no encontró una membresía activa en RedLists " +
                "(¿se dio de baja/recuperó entre el lookup en Redis y este consumer?) — no se registra la Alerta.",
                reading.PlateText);
            return;
        }

        long lecturaHistoricaId;
        try
        {
            lecturaHistoricaId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO LecturasHistoricas (EventId, PlateText, CamaraId, TimestampUtc, Confidence, ImageReference, EsCoincidenciaBlacklist)
                  VALUES (@EventId, @PlateText, @CamaraId, @TimestampUtc, @Confidence, @ImageReference, 1);
                  SELECT LAST_INSERT_ID();",
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
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            // Ya se había procesado este EventId antes (redelivery, código 1062 = duplicate
            // entry) — evita duplicar la Alerta.
            _logger.LogInformation("BlacklistHitSavedEvent {EventId} ya estaba registrado (dedupe por EventId).", reading.EventId);
            return;
        }

        await connection.ExecuteAsync(
            @"INSERT INTO Alertas (LecturaHistoricaId, RedListVehicleId, TimestampUtc, Estado)
              VALUES (@LecturaHistoricaId, @RedListVehicleId, @TimestampUtc, 'Pendiente');",
            new
            {
                LecturaHistoricaId = lecturaHistoricaId,
                RedListVehicleId = redListVehicleId.Value,
                TimestampUtc = message.MatchedAtUtc
            });

        _logger.LogInformation(
            "Alerta registrada: LecturaHistoricaId={LecturaHistoricaId}, RedListVehicleId={RedListVehicleId}, Placa={PlateText}.",
            lecturaHistoricaId, redListVehicleId, reading.PlateText);
    }
}
