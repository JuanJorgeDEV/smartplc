using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using SmartPlc.Api.History;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

public class SchemaMigratorTests
{
    private sealed class Fabrica(DbContextOptions<SmartPlcDbContext> o) : IDbContextFactory<SmartPlcDbContext>
    {
        public SmartPlcDbContext CreateDbContext() => new(o);
    }

    private static async Task Ate(Func<bool> condicao, int ms = 10_000)
    {
        var limite = Environment.TickCount64 + ms;
        while (!condicao() && Environment.TickCount64 < limite) await Task.Delay(20);
        Assert.True(condicao(), "condição não atendida a tempo");
    }

    [Fact]
    public async Task Banco_fora_do_ar_nao_derruba_o_servico_e_ele_tenta_de_novo_com_espera_crescente()
    {
        // Porta 1: conexão recusada na hora (equivale a um banco pausado/indisponível).
        var opcoes = new DbContextOptionsBuilder<SmartPlcDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2;Pooling=false").Options;
        var relogio = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var migrator = new SchemaMigrator(new Fabrica(opcoes), relogio, NullLogger<SchemaMigrator>.Instance);

        await migrator.StartAsync(default);
        await Ate(() => migrator.Tentativas >= 1 && migrator.UltimoErro is not null);
        Assert.False(migrator.Concluida);

        // Avança o relógio falso aos poucos até a quarta tentativa (evita depender de quando o temporizador é criado).
        await Ate(() => { relogio.Advance(TimeSpan.FromSeconds(1)); return migrator.Tentativas >= 4; }, 30_000);

        await migrator.StopAsync(default);                                   // para sem lançar
        Assert.False(migrator.Concluida);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], migrator.Esperas.Take(3));
    }

    [Fact]
    public void A_espera_nunca_passa_do_maximo()
    {
        var espera = SchemaMigrator.EsperaInicial;
        for (var i = 0; i < 20; i++) espera = TimeSpan.FromTicks(Math.Min(espera.Ticks * 2, SchemaMigrator.EsperaMaxima.Ticks));
        Assert.Equal(SchemaMigrator.EsperaMaxima, espera);
    }
}

[Collection("pg")]
public class SchemaMigratorPostgresTests(PostgresFixture fx)
{
    [PostgresFact]
    public async Task Aplica_todas_as_migrations_em_um_banco_vazio_e_conclui()
    {
        var b = new NpgsqlConnectionStringBuilder(fx.Raw!) { Database = "smartplc_migrator_test" };
        var opcoes = new DbContextOptionsBuilder<SmartPlcDbContext>().UseNpgsql(b.ConnectionString).Options;
        await using (var limpar = new SmartPlcDbContext(opcoes)) await limpar.Database.EnsureDeletedAsync();

        var migrator = new SchemaMigrator(new SchemaMigratorTests_Fabrica(opcoes), TimeProvider.System, NullLogger<SchemaMigrator>.Instance);
        await migrator.StartAsync(default);
        await Task.WhenAny(migrator.ExecuteTask ?? Task.CompletedTask, Task.Delay(30_000));

        Assert.True(migrator.Concluida, migrator.UltimoErro);
        Assert.Equal(1, migrator.Tentativas);
        await using var db = new SmartPlcDbContext(opcoes);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.TelemetrySamples.CountAsync() >= 0);
        await migrator.StopAsync(default);
        await db.Database.EnsureDeletedAsync();
    }
}

internal sealed class SchemaMigratorTests_Fabrica(DbContextOptions<SmartPlcDbContext> o) : IDbContextFactory<SmartPlcDbContext>
{
    public SmartPlcDbContext CreateDbContext() => new(o);
}
