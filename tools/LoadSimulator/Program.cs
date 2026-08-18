using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Api.Web.Data;
using Core.Contracts;
using Core.Domain;
using DotNetCore.CAP;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using RabbitMQ.Client;

// Simulador de carga de Fase 3: genera tráfico sintético de PlateReadEvent contra el mismo
// RabbitMQ que usa el pipeline real (Service.Inference + Api.Web), para validar el presupuesto
// de latencia de <300ms bajo carga (cámara → match en Redis → persistencia → alerta por
// SignalR) antes de que exista el pipeline Edge real (Python/YOLO, el resto de Fase 3). No
// reemplaza ese pipeline — solo prueba el camino caliente que ya existe.
//
// Uso (desde la raíz del repo, con el stack de docker compose y Api.Web + Service.Inference
// corriendo):
//   cd tools\LoadSimulator
//   dotnet run -- seed    [--cameras N]
//   dotnet run -- run     [--cameras N] [--rate R] [--duration S] [--match-ratio P] [--drain-seconds S]
//   dotnet run -- cleanup [--cameras N]
//
// "run" mantiene N cámaras concurrentes (default 50) publicando a R lecturas/seg cada una
// (default 10 -> 500 eventos/seg agregados). Una fracción P (default 0.01 = 1%) de las
// lecturas de cada cámara usa la placa ya sembrada en la blacklist, para poder medir la
// latencia real cámara→alerta bajo esa misma carga de fondo, en vez de solo throughput de
// publish. Sin --duration corre hasta Ctrl+C, imprimiendo throughput y latencia cada 5s.
//
// IMPORTANTE (2026-08-18): PlateReadEvent se publica DIRECTO a RabbitMQ (RabbitMQ.Client), NO
// vía DotNetCore.CAP. El primer diseño de este tool sí usaba CAP para publicarlo, y una carga
// real (50x10/seg sostenida varios minutos) confirmó que el outbox de CAP con storage de SQL
// Server no sostiene ese volumen: la cola "service-inference.v1" acumuló backlog creciente,
// con latencias medidas de hasta 5+ minutos (n=71, min=2948ms, p95=296081ms, max=321984ms) y
// seguía con mensajes pendientes minutos después de terminado el run. Causa: CAP escribe una
// fila en cap.Received por cada mensaje consumido (además de cap.Published por cada publish),
// y esa escritura transaccional por mensaje —en ambos lados del pipeline, multiplicada por
// varios procesos hablando con el mismo SQL Server— no escala a ~500/seg en este entorno.
// PlateReadEvent no necesita esa garantía (ver Core.Contracts/RawQueues.cs), así que se sacó
// del outbox de CAP por completo — tanto aquí como en Service.Inference (el consumer real) y
// tools/VerifyFase2. CAP se queda SOLO para la suscripción propia de este tool a
// BlacklistHitSavedEvent (bajo volumen — ~1% de las lecturas), usada para medir latencia.
// Detalle completo del hallazgo en ImplementersGuide.md §9.
//
// Al detenerse (Ctrl+C o --duration), NO se desconecta de inmediato: espera --drain-seconds
// (default 15) con su propio suscriptor de CAP todavía activo, porque el broker→entrega de los
// hits en tránsito no es instantáneo — desconectarse de inmediato deja hits ya publicados sin
// consumir, huérfanos en la cola "load-simulator.v1" de RabbitMQ para siempre. Recién después
// del drain se imprime el resumen final.

const string connectionString = "Server=localhost,1433;Database=SistemaLPR;User Id=sa;Password=Lpr#Dev_2026!;TrustServerCertificate=True";
const string hotPlate = "SIMHIT001"; // placa sembrada como VehiculoRobado Activo — genera match a propósito

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var options = ParseOptions(args);

var dbOptions = new DbContextOptionsBuilder<LprDbContext>()
    .UseSqlServer(connectionString, sql => sql.UseNetTopologySuite())
    .Options;

switch (mode)
{
    case "seed":
        await SeedAsync(options.Cameras);
        break;
    case "run":
        await RunAsync(options.Cameras, options.RatePerSecond, options.DurationSeconds, options.MatchRatio, options.DrainSeconds);
        break;
    case "cleanup":
        await CleanupAsync(options.Cameras);
        break;
    default:
        PrintHelp();
        break;
}

static string CameraCodigo(int index) => $"CAM-SIM-{index:0000}";

async Task SeedAsync(int cameraCount)
{
    using var db = new LprDbContext(dbOptions);
    var factory = new GeometryFactory(new PrecisionModel(), 4326);

    var created = 0;
    for (var i = 1; i <= cameraCount; i++)
    {
        var codigo = CameraCodigo(i);
        if (await db.Camaras.AnyAsync(c => c.Codigo == codigo))
        {
            continue;
        }

        db.Camaras.Add(new Camara
        {
            Codigo = codigo,
            Nombre = $"Cámara simulada #{i} (carga Fase 3)",
            Ubicacion = factory.CreatePoint(new Coordinate(-100.3161, 25.6866)),
            TipoInstalacion = TipoInstalacionCamara.ArcoSeguridad,
            VelocidadMaximaKmh = 80,
            Activa = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        created++;
    }

    if (!await db.VehiculosRobados.AnyAsync(v => v.PlateText == hotPlate && v.Estado == EstadoVehiculoRobado.Activo))
    {
        db.VehiculosRobados.Add(new VehiculoRobado
        {
            PlateText = hotPlate,
            NumeroReporte = "REPORTE-SIM-LOADTEST",
            Estado = EstadoVehiculoRobado.Activo,
            FechaReporteUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });
        Console.WriteLine($"VehiculoRobado '{hotPlate}' creado (Activo) — es la placa que generará matches durante 'run'.");
    }
    else
    {
        Console.WriteLine($"VehiculoRobado '{hotPlate}' ya existía como Activo, no se duplica.");
    }

    await db.SaveChangesAsync();

    Console.WriteLine($"Sembradas {created} cámaras nuevas ({cameraCount} en total esperadas: {CameraCodigo(1)}..{CameraCodigo(cameraCount)}).");
    Console.WriteLine();
    Console.WriteLine("IMPORTANTE: antes de 'run', confirma en el log de Service.Inference que BlacklistCacheService");
    Console.WriteLine($"ya cargó '{hotPlate}' a Redis (reinicia Service.Inference si ya estaba corriendo desde antes de este seed,");
    Console.WriteLine("o espera hasta 5 min al próximo refresh delta).");
}

async Task RunAsync(int cameraCount, double ratePerSecond, int? durationSeconds, double matchRatio, int drainSeconds)
{
    var builder = Host.CreateApplicationBuilder();

    // El logging por default de CAP/RabbitMQ a nivel Information, a cientos de eventos/seg,
    // ahoga por completo las líneas de estadísticas propias de este tool en la consola.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddFilter("DotNetCore.CAP", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

    builder.Services.AddSingleton<LatencyTracker>();
    builder.Services.AddTransient<HitLatencyConsumer>();

    builder.Services.AddCap(x =>
    {
        // CAP aquí ya SOLO sirve para suscribirse a BlacklistHitSavedEvent (bajo volumen, ~1%
        // de las lecturas) — el publish de PlateReadEvent en sí ya no pasa por CAP (ver la nota
        // grande al inicio del archivo). Sigue usando SQL Server como storage porque
        // "UseInMemoryStorage()" no existe en este paquete de CAP 8.x (confirmado 2026-08-18,
        // CS1061 al compilar), pero a este volumen reducido no debería ser un problema.
        x.UseSqlServer(connectionString);

        x.UseRabbitMQ(o =>
        {
            o.HostName = "localhost";
            o.VirtualHost = "/";
            o.UserName = "guest";
            o.Password = "guest";
        });

        // Group propio: el simulador se suscribe a BlacklistHitSavedEvent solo para medir su
        // propia latencia cámara→alerta, en paralelo e independiente de los grupos
        // "service-inference" y "api-web" — no compite por el mensaje con ninguno de los dos.
        x.DefaultGroupName = "load-simulator";
    });

    using var host = builder.Build();
    await host.StartAsync();

    var tracker = host.Services.GetRequiredService<LatencyTracker>();

    // Conexión cruda de RabbitMQ.Client para publicar PlateReadEvent directo, sin pasar por
    // CAP. Un IChannel/canal no es seguro para publicar concurrentemente desde varios hilos, así
    // que cada "cámara" (Task.Run de abajo) crea y reusa su propio canal sobre esta misma
    // conexión compartida (crear canales concurrentemente sobre una conexión sí es seguro).
    // API async-only de RabbitMQ.Client 7.x (IChannel, métodos *Async).
    var rawFactory = new ConnectionFactory
    {
        HostName = "localhost",
        VirtualHost = "/",
        UserName = "guest",
        Password = "guest"
    };
    await using var rawConnection = await rawFactory.CreateConnectionAsync("load-simulator.plate-read-raw");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    if (durationSeconds is { } seconds)
    {
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
    }

    Console.WriteLine($"Publicando con {cameraCount} cámaras x {ratePerSecond.ToString(CultureInfo.InvariantCulture)}/seg " +
                       $"(~{cameraCount * ratePerSecond:0} eventos/seg objetivo), match-ratio={matchRatio:P1}.");
    Console.WriteLine(durationSeconds is { } d ? $"Corriendo {d}s..." : "Ctrl+C para detener y ver el resumen.");
    Console.WriteLine();

    long published = 0;
    long errors = 0;

    var cameraTasks = Enumerable.Range(1, cameraCount).Select(i => Task.Run(async () =>
    {
        var camaraId = CameraCodigo(i);
        var interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
        var random = new Random(i);
        using var timer = new PeriodicTimer(interval);

        await using var channel = await rawConnection.CreateChannelAsync();
        await channel.QueueDeclareAsync(RawQueues.PlateRead, durable: true, exclusive: false, autoDelete: false);
        var properties = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };

        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                var isHot = random.NextDouble() < matchRatio;
                var plateText = isHot ? hotPlate : $"SIM{random.Next(0, 0xFFFFFF):X6}";
                var evt = new PlateReadEvent
                {
                    EventId = Guid.NewGuid(),
                    PlateText = plateText,
                    CameraId = camaraId,
                    TimestampUtc = DateTime.UtcNow,
                    Confidence = 0.9 + random.NextDouble() * 0.1,
                    ImageReference = null
                };

                if (isHot)
                {
                    tracker.TrackPublish(evt.EventId, evt.TimestampUtc);
                }

                try
                {
                    var body = JsonSerializer.SerializeToUtf8Bytes(evt);
                    await channel.BasicPublishAsync(
                        exchange: "",
                        routingKey: RawQueues.PlateRead,
                        mandatory: false,
                        basicProperties: properties,
                        body: body);
                    Interlocked.Increment(ref published);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // esperado al cancelar con Ctrl+C o al llegar a --duration
        }
    })).ToArray();

    var statsTask = Task.Run(async () =>
    {
        var lastPublished = 0L;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                var current = Interlocked.Read(ref published);
                Console.WriteLine($"[{DateTime.Now:T}] publicados={current} (+{current - lastPublished} en 5s) " +
                                   $"errores={Interlocked.Read(ref errors)} | {tracker.Summary()}");
                lastPublished = current;
            }
        }
        catch (OperationCanceledException)
        {
            // esperado al cancelar
        }
    });

    await Task.WhenAll(cameraTasks);
    await Task.WhenAny(statsTask, Task.Delay(TimeSpan.FromSeconds(1)));

    Console.WriteLine();
    Console.WriteLine($"Publish terminado ({Interlocked.Read(ref published)} eventos). Drenando {drainSeconds}s más —");
    Console.WriteLine("el suscriptor de este tool sigue activo para que los hits ya publicados pero todavía en tránsito");
    Console.WriteLine("(RabbitMQ -> este proceso) alcancen a llegar antes de desconectarse. Si se corta la conexión antes");
    Console.WriteLine("de tiempo, esos hits quedan huérfanos en la cola 'load-simulator.v1' — nadie más los consume.");

    var drainDeadline = DateTime.UtcNow.AddSeconds(drainSeconds);
    while (DateTime.UtcNow < drainDeadline)
    {
        var wait = drainDeadline - DateTime.UtcNow;
        await Task.Delay(wait < TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5));
        Console.WriteLine($"[{DateTime.Now:T}] (drenando) {tracker.Summary()}");
    }

    Console.WriteLine();
    Console.WriteLine("=== Resumen ===");
    Console.WriteLine($"Eventos publicados: {Interlocked.Read(ref published)}");
    Console.WriteLine($"Errores de publish: {Interlocked.Read(ref errors)}");
    Console.WriteLine(tracker.Summary());
    Console.WriteLine();
    Console.WriteLine("Nota: esto mide el throughput de publish del simulador y la latencia cámara→alerta solo para");
    Console.WriteLine($"las lecturas con la placa '{hotPlate}' (las que sí generan match). No mide la latencia del");
    Console.WriteLine("99% restante que nunca toca SQL (por diseño, con PlateReadLogging:OnlyLogMatches=true) — para");
    Console.WriteLine("ese tramo, el indicador real es que Redis/PlateReadConsumer sostengan el throughput sin acumular");
    Console.WriteLine("backlog en la cola de RabbitMQ (revisar en http://localhost:15672 — colas 'api-web.v1' y");
    Console.WriteLine("'service-inference.v1'). Si al terminar este resumen la cola 'load-simulator.v1' de RabbitMQ");
    Console.WriteLine("todavía tiene mensajes Ready > 0, --drain-seconds fue insuficiente para esta carga — corre de");
    Console.WriteLine("nuevo con un valor más alto, y de paso purga esa cola a mano desde la UI de RabbitMQ.");

    await host.StopAsync();
}

async Task CleanupAsync(int cameraCount)
{
    using var db = new LprDbContext(dbOptions);

    var codigos = Enumerable.Range(1, cameraCount).Select(CameraCodigo).ToArray();

    var alertas = await db.Alertas
        .Join(db.LecturasHistoricas, a => a.LecturaHistoricaId, l => l.Id, (a, l) => new { Alerta = a, Lectura = l })
        .Where(x => x.Lectura.PlateText.StartsWith("SIM"))
        .Select(x => x.Alerta)
        .ToListAsync();
    db.Alertas.RemoveRange(alertas);

    var lecturas = await db.LecturasHistoricas
        .Where(l => l.PlateText.StartsWith("SIM"))
        .ToListAsync();
    db.LecturasHistoricas.RemoveRange(lecturas);

    var vehiculo = await db.VehiculosRobados.FirstOrDefaultAsync(v => v.PlateText == hotPlate);
    if (vehiculo is not null)
    {
        db.VehiculosRobados.Remove(vehiculo);
    }

    var camaras = await db.Camaras.Where(c => codigos.Contains(c.Codigo)).ToListAsync();
    db.Camaras.RemoveRange(camaras);

    await db.SaveChangesAsync();
    Console.WriteLine($"Datos del simulador de carga eliminados: {camaras.Count} cámaras, VehiculoRobado '{hotPlate}' " +
                       $"(si existía), {lecturas.Count} LecturasHistoricas y {alertas.Count} Alertas asociadas.");
}

(int Cameras, double RatePerSecond, int? DurationSeconds, double MatchRatio, int DrainSeconds) ParseOptions(string[] rawArgs)
{
    var cameras = 50;
    var rate = 10.0;
    int? duration = null;
    var matchRatio = 0.01;
    var drainSeconds = 15;

    for (var i = 1; i < rawArgs.Length; i++)
    {
        switch (rawArgs[i])
        {
            case "--cameras" when i + 1 < rawArgs.Length:
                cameras = int.Parse(rawArgs[++i], CultureInfo.InvariantCulture);
                break;
            case "--rate" when i + 1 < rawArgs.Length:
                rate = double.Parse(rawArgs[++i], CultureInfo.InvariantCulture);
                break;
            case "--duration" when i + 1 < rawArgs.Length:
                duration = int.Parse(rawArgs[++i], CultureInfo.InvariantCulture);
                break;
            case "--match-ratio" when i + 1 < rawArgs.Length:
                matchRatio = double.Parse(rawArgs[++i], CultureInfo.InvariantCulture);
                break;
            case "--drain-seconds" when i + 1 < rawArgs.Length:
                drainSeconds = int.Parse(rawArgs[++i], CultureInfo.InvariantCulture);
                break;
        }
    }

    return (cameras, rate, duration, matchRatio, drainSeconds);
}

void PrintHelp()
{
    Console.WriteLine("Uso: dotnet run -- <modo> [opciones]");
    Console.WriteLine();
    Console.WriteLine("  seed    [--cameras N]");
    Console.WriteLine("      Siembra N cámaras (CAM-SIM-0001..N, default 50) y la placa de prueba en blacklist.");
    Console.WriteLine();
    Console.WriteLine("  run     [--cameras N] [--rate R] [--duration S] [--match-ratio P] [--drain-seconds S]");
    Console.WriteLine("      Publica carga sintética: N cámaras (default 50) x R lecturas/seg cada una (default 10,");
    Console.WriteLine("      ~500/seg agregado). P es la fracción de lecturas con la placa en blacklist (default 0.01).");
    Console.WriteLine("      Sin --duration corre hasta Ctrl+C. Imprime throughput y latencia cámara→alerta cada 5s.");
    Console.WriteLine("      Al detenerse, espera --drain-seconds (default 15) con el suscriptor todavía activo antes");
    Console.WriteLine("      de imprimir el resumen final y desconectarse — evita dejar hits en tránsito huérfanos.");
    Console.WriteLine();
    Console.WriteLine("  cleanup [--cameras N]");
    Console.WriteLine("      Borra las cámaras, el VehiculoRobado, y toda LecturaHistorica/Alerta generada por esta prueba.");
}

/// <summary>
/// Correlaciona cada publish de la placa "caliente" con su BlacklistHitSavedEvent de vuelta,
/// usando el EventId (que viaja intacto dentro de PlateReadEvent.Reading) para calcular la
/// latencia real cámara→alerta, sin necesitar reloj sincronizado entre procesos porque todo
/// corre en esta misma máquina de pruebas.
/// </summary>
public class LatencyTracker
{
    private readonly ConcurrentDictionary<Guid, DateTime> _pending = new();
    private readonly ConcurrentBag<double> _samplesMs = new();
    private long _unmatchedReceived;

    public void TrackPublish(Guid eventId, DateTime publishedAtUtc) => _pending[eventId] = publishedAtUtc;

    public void RecordHit(Guid eventId, DateTime receivedAtUtc)
    {
        if (_pending.TryRemove(eventId, out var publishedAtUtc))
        {
            _samplesMs.Add((receivedAtUtc - publishedAtUtc).TotalMilliseconds);
        }
        else
        {
            // Probablemente un BlacklistHitSavedEvent de otra herramienta corriendo al mismo
            // tiempo (p. ej. tools/VerifyFase2) — no es un error de este simulador.
            Interlocked.Increment(ref _unmatchedReceived);
        }
    }

    public string Summary()
    {
        var samples = _samplesMs.ToArray();
        if (samples.Length == 0)
        {
            return "latencia: sin muestras todavía";
        }

        Array.Sort(samples);
        var p95Index = Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.95) - 1);
        return $"latencia cámara→alerta (ms): n={samples.Length} min={samples[0]:0} avg={samples.Average():0} " +
               $"p95={samples[p95Index]:0} max={samples[^1]:0}";
    }
}

/// <summary>
/// Suscriptor de CAP propio del simulador, con su propio DefaultGroupName ("load-simulator")
/// — recibe su propia copia de cada BlacklistHitSavedEvent, en paralelo e independiente de
/// BlacklistHitPersistenceConsumer (Service.Inference) y AlertNotificationConsumer (Api.Web).
/// </summary>
public class HitLatencyConsumer : ICapSubscribe
{
    private readonly LatencyTracker _tracker;

    public HitLatencyConsumer(LatencyTracker tracker)
    {
        _tracker = tracker;
    }

    [CapSubscribe(EventTopics.BlacklistHitSaved)]
    public Task HandleAsync(BlacklistHitSavedEvent message)
    {
        _tracker.RecordHit(message.Reading.EventId, DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
