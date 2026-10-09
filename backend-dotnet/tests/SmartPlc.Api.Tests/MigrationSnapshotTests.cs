using Microsoft.EntityFrameworkCore;
using SmartPlc.Api.History;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

/// <summary>
/// A migration e o snapshot foram escritos à mão (o `dotnet ef` não rodou na máquina de origem).
/// Este teste falha se o modelo (entidades + DbContext) divergir do que as migrations descrevem,
/// o equivalente a `dotnet ef migrations has-pending-model-changes`. Não abre conexão.
/// </summary>
public class MigrationSnapshotTests
{
    [Fact]
    public void Modelo_e_migrations_estao_em_sincronia()
    {
        var opcoes = new DbContextOptionsBuilder<SmartPlcDbContext>()
            .UseNpgsql("Host=localhost;Database=nunca_conecta;Username=x;Password=x").Options;
        using var db = new SmartPlcDbContext(opcoes);

        Assert.False(db.Database.HasPendingModelChanges(),
            "Há diferença entre o modelo e o snapshot. Regere com: dotnet ef migrations add <Nome> --output-dir Persistence/Migrations");
    }

    [Fact]
    public void Existe_ao_menos_uma_migration()
    {
        var opcoes = new DbContextOptionsBuilder<SmartPlcDbContext>()
            .UseNpgsql("Host=localhost;Database=nunca_conecta;Username=x;Password=x").Options;
        using var db = new SmartPlcDbContext(opcoes);
        Assert.Contains("20261008120000_InitialCreate", db.Database.GetMigrations());
    }
}
