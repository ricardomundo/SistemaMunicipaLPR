using Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.Data;

public class LprDbContext(DbContextOptions<LprDbContext> options) : DbContext(options)
{
    public DbSet<Camara> Camaras => Set<Camara>();
    public DbSet<VehiculoRobado> VehiculosRobados => Set<VehiculoRobado>();
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
            entity.Property(c => c.Ubicacion).HasColumnType("geography").IsRequired();
            entity.Property(c => c.TipoInstalacion).HasConversion<string>().HasMaxLength(30);
        });

        modelBuilder.Entity<VehiculoRobado>(entity =>
        {
            entity.ToTable("VehiculosRobados");
            entity.HasKey(v => v.Id);
            entity.Property(v => v.PlateText).HasMaxLength(20).IsRequired();
            entity.HasIndex(v => v.PlateText);
            entity.Property(v => v.NumeroReporte).HasMaxLength(50).IsRequired();
            entity.Property(v => v.Estado).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<LecturaHistorica>(entity =>
        {
            entity.ToTable("LecturasHistoricas");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.PlateText).HasMaxLength(20).IsRequired();
            entity.HasIndex(l => l.EventId).IsUnique();
            entity.HasIndex(l => l.TimestampUtc);
            entity.HasOne<Camara>().WithMany().HasForeignKey(l => l.CamaraId);
        });

        modelBuilder.Entity<Alerta>(entity =>
        {
            entity.ToTable("Alertas");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Estado).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<LecturaHistorica>().WithMany().HasForeignKey(a => a.LecturaHistoricaId);
            entity.HasOne<VehiculoRobado>().WithMany().HasForeignKey(a => a.VehiculoRobadoId);
        });
    }
}
