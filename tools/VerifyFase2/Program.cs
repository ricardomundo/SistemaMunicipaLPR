using System.Text.Json;
using Api.Web.Data;
using Core.Contracts;
using Core.Domain;
using Dapper;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using RabbitMQ.Client;

// Herramienta desechable para probar el camino completo de Fase 2 a mano, ya que todavía no
// existe ningún publisher real de PlateReadEvent (eso lo trae el simulador de Fase 3) ni un
// flujo real de alta/baja de vehículos en RedLists desde este repo (RedLists es un repo
// separado, ver Fase 3.5 en docs/fases.md; este tool siembra directo contra su esquema SQL).
//
// Uso (desde la raíz del repo, con el stack de docker compose y Api.Web + Service.Inference
// corriendo):
//   cd tools\VerifyFase2
//   dotnet run -- seed             (crea la Camara y la placa de prueba activa en RedLists)
//   dotnet run -- publish          (publica un PlateReadEvent que SÍ debe dar match)
//   dotnet run -- publish-nomatch  (publica uno que NO debe dar match)
//   dotnet run -- cleanup          (borra todos los datos de prueba)

const string connectionString = "Server=localhost;Port=3306;Database=SistemaLPR;User=root;Password=Lpr#Dev_2026!;";
const string testCamaraCodigo = "CAM-TEST-01";
const string testPlateMatch = "TEST1234";
const string testPlateNoMatch = "NOMATCH99";
const string testRedListName = "VerifyFase2 test list";

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

var dbOptions = new DbContextOptionsBuilder<LprDbContext>()
    .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
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

    if (!await db.Camaras.AnyAsync(c => c.Codigo == testCamaraCodigo))
    {
        db.Camaras.Add(new Camara
        {
            Codigo = testCamaraCodigo,
            Nombre = "Cámara de prueba (verificación Fase 2)",
            Latitude = 25.6866,
            Longitude = -100.3161,
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

    await db.SaveChangesAsync();

    await EnsureRedListActiveAsync(testPlateMatch, testRedListName);

    Console.WriteLine();
    Console.WriteLine("Listo. IMPORTANTE: antes de 'publish', confirma en el log de Service.Inference que");
    Console.WriteLine("RedListCacheService ya cargó esta placa a Redis (\"RedList cache refrescada: N placas activas.\"");
    Console.WriteLine("con N >= 1) — si Service.Inference ya estaba corriendo desde antes de este seed, reinícialo");
    Console.WriteLine("para forzar la carga inicial, espera hasta 5 min al próximo refresh delta, o confirma en el");
    Console.WriteLine("log que se conectó al hub de SignalR de RedLists (el VehicleAdded de este seed llega en vivo).");
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
    Console.WriteLine("     /hubs/alerts (con un token válido) debería recibir el mensaje \"AlertaRedList\".");
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

    var camara = await db.Camaras.FirstOrDefaultAsync(c => c.Codigo == testCamaraCodigo);
    if (camara is not null)
    {
        db.Camaras.Remove(camara);
    }

    await db.SaveChangesAsync();

    await RemoveFromRedListAsync(testPlateMatch);

    Console.WriteLine("Datos de prueba de Fase 2 eliminados (Camara, placa de prueba en RedLists, LecturasHistoricas y Alertas asociadas).");
}

void PrintHelp()
{
    Console.WriteLine("Uso: dotnet run -- <modo>");
    Console.WriteLine("  seed             Crea la Camara y activa la placa de prueba en RedLists (idempotente).");
    Console.WriteLine("  publish          Publica un PlateReadEvent con la placa de prueba (debe dar match).");
    Console.WriteLine("  publish-nomatch  Publica un PlateReadEvent con una placa que no está en RedLists (no debe dar match).");
    Console.WriteLine("  cleanup          Borra todos los datos de prueba (Camara, placa en RedLists, LecturasHistoricas, Alertas).");
}

/// <summary>
/// Da de alta (o reactiva) <paramref name="plateNumber"/> como miembro activo de una lista tipo
/// 'Vehicles' (RedList) en el esquema de RedLists -- vehicle_lists/vehicles/list_vehicles, misma
/// base SistemaLPR (ver Fase 3.5 en docs/fases.md). Este tool ya no usa VehiculoRobado/
/// LprDbContext para esto desde que RedLists reemplazó el subsistema de blacklist propio; Dapper/
/// MySqlConnector llegan transitivamente vía Api.Web.csproj, sin paquete nuevo aquí. Idempotente:
/// reutiliza la lista/vehículo si ya existen, y reactiva la membresía si estaba marcada como
/// recuperada por una corrida anterior.
/// </summary>
async Task EnsureRedListActiveAsync(string plateNumber, string listName)
{
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    var listId = await connection.QuerySingleOrDefaultAsync<long?>(
        "SELECT id FROM vehicle_lists WHERE name = @listName AND list_type = 'Vehicles' LIMIT 1",
        new { listName });
    if (listId is null)
    {
        listId = await connection.ExecuteScalarAsync<long>(
            @"INSERT INTO vehicle_lists (global_id, name, list_type, creating_user, last_modifying_user)
              VALUES (UUID(), @listName, 'Vehicles', 0, 0);
              SELECT LAST_INSERT_ID();",
            new { listName });
    }

    var vehicleId = await connection.QuerySingleOrDefaultAsync<long?>(
        "SELECT id FROM vehicles WHERE plate_number = @plateNumber LIMIT 1", new { plateNumber });
    if (vehicleId is null)
    {
        vehicleId = await connection.ExecuteScalarAsync<long>(
            @"INSERT INTO vehicles (plate_number) VALUES (@plateNumber);
              SELECT LAST_INSERT_ID();",
            new { plateNumber });
    }

    var alreadyActive = await connection.ExecuteScalarAsync<long>(
        "SELECT COUNT(*) FROM list_vehicles WHERE list_id = @listId AND vehicle_id = @vehicleId AND recovered_by_org_id = 0",
        new { listId, vehicleId });
    if (alreadyActive > 0)
    {
        Console.WriteLine($"'{plateNumber}' ya estaba activa en RedLists (lista '{listName}'), no se duplica.");
        return;
    }

    await connection.ExecuteAsync(
        @"INSERT INTO list_vehicles (list_id, vehicle_id, recovered_by_org_id)
          VALUES (@listId, @vehicleId, 0)
          ON DUPLICATE KEY UPDATE recovered_by_org_id = 0, recovered_date = NULL, recovered_by_text = NULL;",
        new { listId, vehicleId });

    Console.WriteLine($"'{plateNumber}' agregada como activa en RedLists (lista '{listName}').");
}

/// <summary>Borrado físico de la membresía y el vehículo sintético de prueba -- correcto aquí
/// porque son placas de prueba (TEST*/NOMATCH*), no vehículos reales (una baja real en RedLists
/// solo marca recovered_by_org_id, nunca borra la fila).</summary>
async Task RemoveFromRedListAsync(string plateNumber)
{
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    var vehicleId = await connection.QuerySingleOrDefaultAsync<long?>(
        "SELECT id FROM vehicles WHERE plate_number = @plateNumber LIMIT 1", new { plateNumber });
    if (vehicleId is null)
    {
        return;
    }

    await connection.ExecuteAsync("DELETE FROM list_vehicles WHERE vehicle_id = @vehicleId", new { vehicleId });
    await connection.ExecuteAsync("DELETE FROM vehicles WHERE id = @vehicleId", new { vehicleId });
}
