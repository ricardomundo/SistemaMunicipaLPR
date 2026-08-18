using System.Text.Json;
using Api.Web.Data;
using Core.Contracts;
using Core.Domain;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using RabbitMQ.Client;

// Herramienta desechable para probar el camino completo de Fase 2 a mano, ya que todavía no
// existe ningún publisher real de PlateReadEvent (eso lo trae el simulador de Fase 3) ni un
// flujo real de alta/baja de VehiculoRobado (BlacklistController sigue siendo un stub).
//
// Uso (desde la raíz del repo, con el stack de docker compose y Api.Web + Service.Inference
// corriendo):
//   cd tools\VerifyFase2
//   dotnet run -- seed             (crea la Camara y el VehiculoRobado de prueba)
//   dotnet run -- publish          (publica un PlateReadEvent que SÍ debe dar match)
//   dotnet run -- publish-nomatch  (publica uno que NO debe dar match)
//   dotnet run -- cleanup          (borra todos los datos de prueba)

const string connectionString = "Server=localhost,1433;Database=SistemaLPR;User Id=sa;Password=Lpr#Dev_2026!;TrustServerCertificate=True";
const string testCamaraCodigo = "CAM-TEST-01";
const string testPlateMatch = "TEST1234";
const string testPlateNoMatch = "NOMATCH99";

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

var dbOptions = new DbContextOptionsBuilder<LprDbContext>()
    .UseSqlServer(connectionString, sql => sql.UseNetTopologySuite())
    .Options;

switch (mode)
{
    case "seed":
        await SeedAsync();
        break;
    case "publish":
        await PublishAsync(testPlateMatch, "CON match esperado");
        break;
    case "publish-nomatch":
        await PublishAsync(testPlateNoMatch, "SIN match esperado");
        break;
    case "cleanup":
        await CleanupAsync();
        break;
    default:
        PrintHelp();
        break;
}

async Task SeedAsync()
{
    using var db = new LprDbContext(dbOptions);
    var factory = new GeometryFactory(new PrecisionModel(), 4326);

    if (!await db.Camaras.AnyAsync(c => c.Codigo == testCamaraCodigo))
    {
        db.Camaras.Add(new Camara
        {
            Codigo = testCamaraCodigo,
            Nombre = "Cámara de prueba (verificación Fase 2)",
            Ubicacion = factory.CreatePoint(new Coordinate(-100.3161, 25.6866)),
            TipoInstalacion = TipoInstalacionCamara.ArcoSeguridad,
            VelocidadMaximaKmh = 80,
            Activa = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        Console.WriteLine($"Camara '{testCamaraCodigo}' creada.");
    }
    else
    {
        Console.WriteLine($"Camara '{testCamaraCodigo}' ya existía, no se duplica.");
    }

    if (!await db.VehiculosRobados.AnyAsync(v => v.PlateText == testPlateMatch && v.Estado == EstadoVehiculoRobado.Activo))
    {
        db.VehiculosRobados.Add(new VehiculoRobado
        {
            PlateText = testPlateMatch,
            NumeroReporte = "REPORTE-TEST-001",
            Estado = EstadoVehiculoRobado.Activo,
            FechaReporteUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });
        Console.WriteLine($"VehiculoRobado '{testPlateMatch}' creado (Activo).");
    }
    else
    {
        Console.WriteLine($"VehiculoRobado '{testPlateMatch}' ya existía como Activo, no se duplica.");
    }

    await db.SaveChangesAsync();

    Console.WriteLine();
    Console.WriteLine("Listo. IMPORTANTE: antes de 'publish', confirma en el log de Service.Inference que");
    Console.WriteLine("BlacklistCacheService ya cargó esta placa a Redis (\"Blacklist cache refrescada: N placas activas.\"");
    Console.WriteLine("con N >= 1) — si Service.Inference ya estaba corriendo desde antes de este seed, reinícialo");
    Console.WriteLine("para forzar la carga inicial, o espera hasta 5 min al próximo refresh delta.");
}

async Task PublishAsync(string plateText, string descripcion)
{
    // PlateReadEvent se publica directo a RabbitMQ con RabbitMQ.Client — no pasa por el outbox
    // de CAP (ver Core.Contracts/RawQueues.cs e ImplementersGuide.md §7). El paquete resuelto es
    // RabbitMQ.Client 7.x, cuya API es async-only (IChannel en vez de IModel, métodos *Async) —
    // no existe overload síncrono de CreateConnection/CreateModel en esta versión.
    var factory = new ConnectionFactory
    {
        HostName = "localhost",
        VirtualHost = "/",
        UserName = "guest",
        Password = "guest"
    };

    await using var connection = await factory.CreateConnectionAsync("verify-fase2.plate-read-raw");
    await using var channel = await connection.CreateChannelAsync();
    await channel.QueueDeclareAsync(RawQueues.PlateRead, durable: true, exclusive: false, autoDelete: false);

    var evt = new PlateReadEvent
    {
        EventId = Guid.NewGuid(),
        PlateText = plateText,
        CameraId = testCamaraCodigo,
        TimestampUtc = DateTime.UtcNow,
        Confidence = 0.95,
        ImageReference = null
    };

    // BasicProperties ya no se obtiene de channel.CreateBasicProperties() (ese método no existe
    // en 7.x) — se instancia directo. El booleano "Persistent" también se reemplazó por el enum
    // DeliveryMode; si esta línea no compila contra tu versión exacta del paquete, prueba
    // "Persistent = true" en su lugar (nombre usado en versiones anteriores de la librería).
    var properties = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
    var body = JsonSerializer.SerializeToUtf8Bytes(evt);
    await channel.BasicPublishAsync(
        exchange: "",
        routingKey: RawQueues.PlateRead,
        mandatory: false,
        basicProperties: properties,
        body: body);

    Console.WriteLine($"PlateReadEvent publicado ({descripcion}): EventId={evt.EventId}, PlateText={evt.PlateText}, CameraId={evt.CameraId}.");
    Console.WriteLine();
    Console.WriteLine("Revisa ahora:");
    Console.WriteLine("  1. Log de Service.Inference (PlateReadConsumer) — debe loguear el match o simplemente consumir sin logs si no hay match.");
    Console.WriteLine("  2. Si hubo match: log de BlacklistHitPersistenceConsumer (\"Alerta registrada...\") y una fila nueva en LecturasHistoricas + Alertas en SQL.");
    Console.WriteLine("  3. Si hubo match: log de AlertNotificationConsumer en Api.Web (\"Alerta empujada por SignalR...\"), y un cliente SignalR conectado a");
    Console.WriteLine("     /hubs/alerts (con un token válido) debería recibir el mensaje \"AlertaBlacklist\".");
}

async Task CleanupAsync()
{
    using var db = new LprDbContext(dbOptions);

    var alertas = await db.Alertas
        .Join(db.LecturasHistoricas, a => a.LecturaHistoricaId, l => l.Id, (a, l) => new { Alerta = a, Lectura = l })
        .Where(x => x.Lectura.PlateText == testPlateMatch || x.Lectura.PlateText == testPlateNoMatch)
        .Select(x => x.Alerta)
        .ToListAsync();
    db.Alertas.RemoveRange(alertas);

    var lecturas = await db.LecturasHistoricas
        .Where(l => l.PlateText == testPlateMatch || l.PlateText == testPlateNoMatch)
        .ToListAsync();
    db.LecturasHistoricas.RemoveRange(lecturas);

    var vehiculo = await db.VehiculosRobados.FirstOrDefaultAsync(v => v.PlateText == testPlateMatch);
    if (vehiculo is not null)
    {
        db.VehiculosRobados.Remove(vehiculo);
    }

    var camara = await db.Camaras.FirstOrDefaultAsync(c => c.Codigo == testCamaraCodigo);
    if (camara is not null)
    {
        db.Camaras.Remove(camara);
    }

    await db.SaveChangesAsync();
    Console.WriteLine("Datos de prueba de Fase 2 eliminados (Camara, VehiculoRobado, LecturasHistoricas y Alertas asociadas).");
}

void PrintHelp()
{
    Console.WriteLine("Uso: dotnet run -- <modo>");
    Console.WriteLine("  seed             Crea la Camara y el VehiculoRobado de prueba (idempotente).");
    Console.WriteLine("  publish          Publica un PlateReadEvent con la placa de prueba (debe dar match).");
    Console.WriteLine("  publish-nomatch  Publica un PlateReadEvent con una placa que no está en la blacklist (no debe dar match).");
    Console.WriteLine("  cleanup          Borra todos los datos de prueba (Camara, VehiculoRobado, LecturasHistoricas, Alertas).");
}
