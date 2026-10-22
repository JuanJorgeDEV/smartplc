using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SmartPlc.Api.Persistence;

public sealed class SmartPlcDbContext(DbContextOptions<SmartPlcDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<TelemetrySample> TelemetrySamples => Set<TelemetrySample>();
    public DbSet<ProcessEvent> ProcessEvents => Set<ProcessEvent>();
    public DbSet<ControlCommand> ControlCommands => Set<ControlCommand>();
    public DbSet<ExperimentSession> ExperimentSessions => Set<ExperimentSession>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();

    /// <summary>Chaves do cookie de sessão. No Render o disco some a cada reinício; guardadas aqui, as sessões sobrevivem a ele.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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

        b.Entity<AppUser>(e =>
        {
            e.ToTable("app_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Nome).HasMaxLength(80);
            e.Property(x => x.SenhaHash).HasMaxLength(300);
            e.Property(x => x.Papel).HasMaxLength(20);
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<DataProtectionKey>().ToTable("data_protection_keys");

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
