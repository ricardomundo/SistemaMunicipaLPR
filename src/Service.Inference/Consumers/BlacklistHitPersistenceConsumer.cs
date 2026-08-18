using System.Threading.Tasks;
using Core.Contracts;
using Dapper;
using DotNetCore.CAP;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Service.Inference.Data;

namespace Service.Inference.Consumers;

/// <summary>
/// Persiste el rastro de auditoría (LecturaHistorica + Alerta) de cada match de blacklist.
/// Corre como suscriptor separado de PlateReadConsumer para no meter esta escritura síncrona
/// en el camino caliente de matching — a esta altura ya se publicó BlacklistHitSavedEvent, y
/// AlertNotificationConsumer (en Api.Web) ya puede empujar la alerta por SignalR en paralelo,
/// sin esperar a que esta escritura a SQL termine (cada servicio tiene su propio "Group" de
/// CAP sobre el mismo topic, así que ambos reciben su propia copia del mensaje).
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

        var vehiculoRobadoId = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT TOP 1 Id FROM VehiculosRobados WHERE PlateText = @PlateText AND Estado = 'Activo'",
            new { reading.PlateText });
        if (vehiculoRobadoId is null)
        {
            _logger.LogWarning(
                "BlacklistHitSavedEvent para placa '{PlateText}' no encontró un VehiculoRobado Activo correspondiente " +
                "(¿se dio de baja entre el lookup en Redis y este consumer?) — no se registra la Alerta.",
                reading.PlateText);
            return;
        }

        long lecturaHistoricaId;
        try
        {
            lecturaHistoricaId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO LecturasHistoricas (EventId, PlateText, CamaraId, TimestampUtc, Confidence, ImageReference, EsCoincidenciaBlacklist)
                  VALUES (@EventId, @PlateText, @CamaraId, @TimestampUtc, @Confidence, @ImageReference, 1);
                  SELECT CAST(SCOPE_IDENTITY() AS bigint);",
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
            // Ya se había procesado este EventId antes (redelivery) — evita duplicar la Alerta.
            _logger.LogInformation("BlacklistHitSavedEvent {EventId} ya estaba registrado (dedupe por EventId).", reading.EventId);
            return;
        }

        await connection.ExecuteAsync(
            @"INSERT INTO Alertas (LecturaHistoricaId, VehiculoRobadoId, TimestampUtc, Estado)
              VALUES (@LecturaHistoricaId, @VehiculoRobadoId, @TimestampUtc, 'Pendiente');",
            new
            {
                LecturaHistoricaId = lecturaHistoricaId,
                VehiculoRobadoId = vehiculoRobadoId.Value,
                TimestampUtc = message.MatchedAtUtc
            });

        _logger.LogInformation(
            "Alerta registrada: LecturaHistoricaId={LecturaHistoricaId}, VehiculoRobadoId={VehiculoRobadoId}, Placa={PlateText}.",
            lecturaHistoricaId, vehiculoRobadoId, reading.PlateText);
    }
}
