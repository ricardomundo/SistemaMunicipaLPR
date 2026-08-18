using Api.Web.Data;
using Core.Contracts;
using Core.Domain;
using DotNetCore.CAP;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

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
    // CAP no publica directo a RabbitMQ como hacía MassTransit — primero escribe el mensaje en
    // su tabla de outbox transaccional (cap.Published, en esta misma base "SistemaLPR") y un
    // dispatcher en background lo envía al broker poco después. Por eso este host se queda
    // arriba unos segundos tras el publish antes de detenerse: si se cierra de inmediato, el
    // dispatcher no alcanza a correr y el mensaje se queda pendiente en la tabla hasta el
    // próximo arranque de un proceso con CAP configurado.
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddCap(x =>
    {
        x.UseSqlServer(connectionString);
        x.UseRabbitMQ(o =>
        {
            o.HostName = "localhost";
            o.VirtualHost = "/";
            o.UserName = "guest";
            o.Password = "guest";
        });
        x.DefaultGroupName = "verify-fase2-tool";
    });

    using var host = builder.Build();
    await host.StartAsync();
    try
    {
        var capPublisher = host.Services.GetRequiredService<ICapPublisher>();

        var evt = new PlateReadEvent
        {
            EventId = Guid.NewGuid(),
            PlateText = plateText,
            CameraId = testCamaraCodigo,
            TimestampUtc = DateTime.UtcNow,
            Confidence = 0.95,
            ImageReference = null
        };

        await capPublisher.PublishAsync(EventTopics.PlateRead, evt);

        Console.WriteLine($"PlateReadEvent publicado ({descripcion}): EventId={evt.EventId}, PlateText={evt.PlateText}, CameraId={evt.CameraId}.");
        Console.WriteLine("Esperando ~5s a que el dispatcher de CAP lo envíe a RabbitMQ...");
        await Task.Delay(TimeSpan.FromSeconds(5));

        Console.WriteLine();
        Console.WriteLine("Revisa ahora:");
        Console.WriteLine("  1. Log de Service.Inference (PlateReadConsumer) — debe loguear el match o simplemente consumir sin logs si no hay match.");
        Console.WriteLine("  2. Si hubo match: log de BlacklistHitPersistenceConsumer (\"Alerta registrada...\") y una fila nueva en LecturasHistoricas + Alertas en SQL.");
        Console.WriteLine("  3. Si hubo match: log de AlertNotificationConsumer en Api.Web (\"Alerta empujada por SignalR...\"), y un cliente SignalR conectado a");
        Console.WriteLine("     /hubs/alerts (con un token válido) debería recibir el mensaje \"AlertaBlacklist\".");
    }
    finally
    {
        await host.StopAsync();
    }
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
