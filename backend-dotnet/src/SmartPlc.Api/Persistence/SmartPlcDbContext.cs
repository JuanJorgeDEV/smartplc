using Microsoft.EntityFrameworkCore;

namespace SmartPlc.Api.Persistence;

public sealed class SmartPlcDbContext(DbContextOptions<SmartPlcDbContext> options) : DbContext(options)
{
    public DbSet<TelemetrySample> TelemetrySamples => Set<TelemetrySample>();
    public DbSet<ProcessEvent> ProcessEvents => Set<ProcessEvent>();
    public DbSet<ControlCommand> ControlCommands => Set<ControlCommand>();
    public DbSet<ExperimentSession> ExperimentSessions => Set<ExperimentSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TelemetrySample>(e =>
        {
            e.ToTable("telemetry_samples");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();

            // Consultas por período e limpeza por retenção usam este índice.
            e.HasIndex(x => x.RecebidoEm);

            // Deduplicação: só vale quando a origem informa a hora de aquisição.
            e.HasIndex(x => x.AdquiridoEm).IsUnique().HasFilter("\"AdquiridoEm\" IS NOT NULL");
        });

        b.Entity<ProcessEvent>(e =>
        {
            e.ToTable("process_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.Detalhe).HasMaxLength(500);
            e.HasIndex(x => x.OcorridoEm);
        });

        b.Entity<ControlCommand>(e =>
        {
            e.ToTable("control_commands");
            e.HasKey(x => x.Id);
            e.Property(x => x.Estado).HasMaxLength(30);
            e.Property(x => x.Campos).HasColumnType("jsonb");
            e.Property(x => x.Detalhe).HasMaxLength(500);
            e.HasIndex(x => x.SolicitadoEm);
        });

        b.Entity<ExperimentSession>(e =>
        {
            e.ToTable("experiment_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Nome).HasMaxLength(80);
            e.Property(x => x.Tipo).HasMaxLength(30);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Observacoes).HasMaxLength(500);
            e.Property(x => x.Fonte).HasMaxLength(20).HasDefaultValue("clp");
            e.Property(x => x.SerieSimulada).HasColumnType("jsonb");
            e.HasIndex(x => x.InicioEm);
            // Garante, no banco, no máximo UMA sessão ativa (também protege contra duas requisições simultâneas).
            e.HasIndex(x => x.Status).IsUnique().HasFilter("\"Status\" = 'ativa'");
        });
    }
}
