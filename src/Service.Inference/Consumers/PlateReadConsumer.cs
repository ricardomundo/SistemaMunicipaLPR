using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Contracts;
using Dapper;
using DotNetCore.CAP;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Service.Inference.Data;
using Service.Inference.Redis;
using StackExchange.Redis;

namespace Service.Inference.Consumers;

/// <summary>
/// Consumer del camino caliente de PlateReadEvent — habla RabbitMQ.Client directo (ack manual),
/// fuera del outbox de DotNetCore.CAP a propósito: es una señal efímera de alto volumen, sin
/// escritura local que necesite atomicidad transaccional con el publish. Detalle del
/// razonamiento en ArchitectureGuide.md §3 e ImplementersGuide.md §7.
///
/// Por cada PlateReadEvent hace un solo lookup O(1) contra el set de Redis
/// "blacklist:active-plates". Si hay match, publica BlacklistHitSavedEvent — ESE publish SÍ se
/// queda en CAP (ver <see cref="EventTopics.BlacklistHitSaved"/>): es de bajo volumen y se
/// beneficia del outbox transaccional/reintentos/grupos de CAP. El push a SignalR (en Api.Web)
/// y la persistencia de LecturaHistorica/Alerta (BlacklistHitPersistenceConsumer, en este mismo
/// servicio) corren como suscriptores de CAP independientes de ese evento, fuera de este
/// camino caliente.
///
/// Por default, si NO hay match, no se escribe nada a SQL — evita meter una escritura síncrona
/// por cada lectura sin importancia en el camino caliente. Esto es configurable vía
/// "PlateReadLogging:OnlyLogMatches" en appsettings.json (default: true = solo se registran
/// los matches) para quien necesite el log forense completo de "toda lectura, match o no".
/// </summary>
public class PlateReadConsumer : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly SqlConnectionFactory _sqlConnectionFactory;
    private readonly IConfiguration _configuration;
    private readonly ICapPublisher _capPublisher;
    private readonly ILogger<PlateReadConsumer> _logger;

    private IConnection? _connection;
    private IChannel? _channel;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // RabbitMQ.Client 7.x: API async-only (IChannel en vez de IModel, métodos *Async).
        // "DispatchConsumersAsync" ya no existe en ConnectionFactory — todo consumer es async
        // por diseño en esta versión.
        var rabbitMq = _configuration.GetSection("RabbitMq");
        var factory = new ConnectionFactory
        {
            HostName = rabbitMq["Host"] ?? "localhost",
            VirtualHost = rabbitMq["VirtualHost"] ?? "/",
            UserName = rabbitMq["Username"] ?? "guest",
            Password = rabbitMq["Password"] ?? "guest"
        };

        _connection = await factory.CreateConnectionAsync("service-inference.plate-read-raw", stoppingToken);
        var channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        _channel = channel;

        await channel.QueueDeclareAsync(RawQueues.PlateRead, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);

        // Prefetch > 1: sin esto, RabbitMQ entrega un mensaje a la vez y espera el ack antes de
        // mandar el siguiente — mata el throughput bajo carga. 100 es un valor de partida, no
        // afinado con medición real.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 100, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(ea.Body.Span);
                var reading = JsonSerializer.Deserialize<PlateReadEvent>(json);
                if (reading is not null)
                {
                    await HandleAsync(reading);
                }

                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando PlateReadEvent crudo — se reencola.");
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
            }
        };

        await channel.BasicConsumeAsync(
            queue: RawQueues.PlateRead,
            autoAck: false,
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Consumer crudo de PlateReadEvent conectado a la cola '{Queue}' (fuera del outbox de CAP — ver ImplementersGuide.md §7).",
            RawQueues.PlateRead);
    }

    private async Task HandleAsync(PlateReadEvent reading)
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // IChannel/IConnection en RabbitMQ.Client 7.x son IAsyncDisposable — el cierre/limpieza
        // se hace async en vez de en un Dispose() síncrono.
        if (_channel is not null)
        {
            await _channel.CloseAsync(cancellationToken: cancellationToken);
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync(cancellationToken: cancellationToken);
            await _connection.DisposeAsync();
        }
    }
}
