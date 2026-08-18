using Core.Domain;
using Api.Web.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

var connectionString = "Server=localhost,1433;Database=SistemaLPR;User Id=sa;Password=Lpr#Dev_2026!;TrustServerCertificate=True";

var options = new DbContextOptionsBuilder<LprDbContext>()
    .UseSqlServer(connectionString, sql => sql.UseNetTopologySuite())
    .Options;

var factory = new GeometryFactory(new PrecisionModel(), 4326);

using var db = new LprDbContext(options);

var camara = new Camara
{
    Codigo = "TEST-GEO-001",
    Nombre = "Cámara de prueba (verificación geography)",
    Ubicacion = factory.CreatePoint(new Coordinate(-100.3161, 25.6866)),
    TipoInstalacion = TipoInstalacionCamara.ArcoSeguridad,
    VelocidadMaximaKmh = 80,
    Activa = true,
    CreatedAtUtc = DateTime.UtcNow
};

db.Camaras.Add(camara);
db.SaveChanges();
Console.WriteLine($"Insertada Id={camara.Id}");

db.ChangeTracker.Clear(); // fuerza leer de SQL Server, no del cache en memoria

var fetched = db.Camaras.Single(c => c.Codigo == "TEST-GEO-001");
Console.WriteLine($"Leída: {fetched.Ubicacion} SRID={fetched.Ubicacion.SRID}");

var ok = Math.Abs(fetched.Ubicacion.X - camara.Ubicacion.X) < 0.0001
      && Math.Abs(fetched.Ubicacion.Y - camara.Ubicacion.Y) < 0.0001
      && fetched.Ubicacion.SRID == 4326;
Console.WriteLine(ok ? "OK: geography serializa correctamente." : "FALLO: los valores no coinciden.");

db.Camaras.Remove(fetched);
db.SaveChanges();
Console.WriteLine("Registro de prueba eliminado.");