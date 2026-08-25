using Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.Data;

public class LprDbContext(DbContextOptions<LprDbContext> options) : DbContext(options)
{
    public DbSet<Camara> Camaras => Set<Camara>();
    public DbSet<LecturaHistorica> LecturasHistoricas => Set<LecturaHistorica>();
    public DbSet<Alerta> Alertas => Set<Alerta>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Camara>(entity =>
        {
            entity.ToTable("Camaras");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Codigo).HasMaxLength(50).IsRequired();
            entity.HasIndex(c => c.Codigo).IsUnique();
            entity.Property(c => c.Nombre).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Latitude).HasColumnType("decimal(9,6)").IsRequired();
            entity.Property(c => c.Longitude).HasColumnType("decimal(9,6)").IsRequired();
            entity.Property(c => c.TipoInstalacion).HasConversion<string>().HasMaxLength(30);
        });

        modelBuilder.Entity<LecturaHistorica>(entity =>
        {
            entity.ToTable("LecturasHistoricas");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.PlateText).HasMaxLength(20).IsRequired();
            entity.HasIndex(l => l.EventId).IsUnique();
            entity.HasIndex(l => new { l.TimestampUtc, l.PlateText });
            entity.HasOne<Camara>().WithMany().HasForeignKey(l => l.CamaraId);
        });

        modelBuilder.Entity<Alerta>(entity =>
        {
            entity.ToTable("Alertas");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Estado).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<LecturaHistorica>().WithMany().HasForeignKey(a => a.LecturaHistoricaId);

            // RedListVehicleId apunta a `vehicles.id` en el esquema de RedLists (misma base
            // SistemaLPR, ver Fase 3.5 en docs/fases.md) -- sin FK real: esa tabla la administra
            // RedLists (VehicleListsService) con su propio esquema SQL, fuera del historial de
            // migraciones de este DbContext.
            entity.Property(a => a.RedListVehicleId).IsRequired();
        });
    }
}
