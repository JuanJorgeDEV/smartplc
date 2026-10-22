using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SmartPlc.Api.History;
using SmartPlc.Api.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartPlc.Api.Tests;

/// <summary>
/// Prepara um PostgreSQL de TESTE: recria o banco do zero com as migrations.
/// DESTRUTIVO: recusa-se a rodar se o nome do banco não contiver "test".
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public string? Raw { get; private set; }
    public DbContextOptions<SmartPlcDbContext>? Options { get; private set; }

    public async Task InitializeAsync()
    {
        Raw = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variavel);
        if (string.IsNullOrWhiteSpace(Raw)) return;

        var banco = new NpgsqlConnectionStringBuilder(Raw).Database ?? "";
        if (!banco.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Recusado: {PostgresFactAttribute.Variavel} aponta para o banco '{banco}'. Os testes apagam e recriam o banco; use um nome com 'test'.");

        Options = new DbContextOptionsBuilder<SmartPlcDbContext>().UseNpgsql(DbConnection.Normalize(Raw)).Options;
        await using var db = new SmartPlcDbContext(Options);
        await db.Database.EnsureDeletedAsync();
        await db.GetService<IRelationalDatabaseCreator>().CreateAsync();

        // Imita o Supabase: papéis da Data API existem e recebem privilégios padrão sobre tabelas novas do schema public.
        await db.Database.ExecuteSqlRawAsync(@"
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN CREATE ROLE anon NOLOGIN; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN CREATE ROLE authenticated NOLOGIN; END IF;
END $$;
GRANT USAGE ON SCHEMA public TO anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO anon, authenticated;");

        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public SmartPlcDbContext NovoContexto() => new(Options!);

    public EfHistoryStore NovaLoja() => new(new Fabrica(Options!), NullLogger<EfHistoryStore>.Instance);

    public async Task LimparAsync()
    {
        await using var db = NovoContexto();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE telemetry_samples, process_events, control_commands RESTART IDENTITY");
    }

    public sealed class Fabrica(DbContextOptions<SmartPlcDbContext> o) : IDbContextFactory<SmartPlcDbContext>
    {
        public SmartPlcDbContext CreateDbContext() => new(o);
    }
}

[CollectionDefinition("pg")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>
/// Usam PostgreSQL REAL (comportamento de timestamptz, índices, identity, violações de unicidade).
/// Rodam só com SMARTPLC_TEST_DB definida; sem ela aparecem como "ignorados" (ver docs/testes.md).
/// </summary>
[Collection("pg")]
public class PostgresIntegrationTests(PostgresFixture fx)
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

    private static TelemetrySample Amostra(int segundos, double nivel = 50, DateTimeOffset? aq = null, SampleReason m = SampleReason.Periodic) =>
        new() { RecebidoEm = T0.AddSeconds(segundos), AdquiridoEm = aq, Nivel = nivel, Valvula = 40, Setpoint = 60, ModoManual = false, MotivoGravacao = m };

    // ---------- Migrations ----------

    [PostgresFact]
    public async Task Migrations_ligam_RLS_e_retiram_privilegios_da_Data_API()
    {
        await using var db = fx.NovoContexto();
        var tabelas = new[] { "telemetry_samples", "process_events", "control_commands", "experiment_sessions", "__EFMigrationsHistory", "app_users", "data_protection_keys" };

        foreach (var t in tabelas)
        {
            var rls = await db.Database
                .SqlQuery<bool>($"SELECT relrowsecurity AS \"Value\" FROM pg_class WHERE oid = ('public.\"' || {t} || '\"')::regclass")
                .SingleAsync();
            Assert.True(rls, $"RLS desligado em {t}");

            foreach (var papel in new[] { "anon", "authenticated" })
            {
                var acesso = await db.Database
                    .SqlQuery<bool>($"SELECT has_table_privilege({papel}, ('public.\"' || {t} || '\"')::regclass, 'SELECT') AS \"Value\"")
                    .SingleAsync();
                Assert.False(acesso, $"{papel} ainda lê {t}");
            }
        }
    }

    [PostgresFact]
    public async Task Migration_inicial_aplicada_cria_tabelas_e_indices_esperados()
    {
        await using var db = fx.NovoContexto();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains("20261008120000_InitialCreate", await db.Database.GetAppliedMigrationsAsync());

        var indices = await db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'")
            .ToListAsync();
        foreach (var esperado in new[]
        {
            "IX_telemetry_samples_RecebidoEm", "IX_telemetry_samples_AdquiridoEm",
            "IX_process_events_OcorridoEm", "IX_control_commands_SolicitadoEm"
        })
            Assert.Contains(esperado, indices);
    }

    [PostgresFact]
    public async Task Indice_unico_de_aquisicao_e_parcial()
    {
        await using var db = fx.NovoContexto();
        var def = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_telemetry_samples_AdquiridoEm'")
            .SingleAsync();
        Assert.Contains("UNIQUE", def);
        Assert.Contains("IS NOT NULL", def);
    }

    // ---------- Gravação ----------

    [PostgresFact]
    public async Task Grava_e_le_amostra_preservando_o_instante_em_UTC()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        var aq = DateTimeOffset.Parse("2026-01-01T11:59:57.123456+00:00");

        var r = await loja.SaveAsync([Amostra(0, 55.5678, aq, SampleReason.Initial)], [], default);

        Assert.Equal(new SaveResult(1, 0, 0), r);
        var pagina = await loja.QuerySamplesAsync(T0.AddMinutes(-1), T0.AddMinutes(1), 10, null, default);
        var s = Assert.Single(pagina.Items);
        Assert.Equal(T0, s.RecebidoEm);
        Assert.Equal(aq, s.AdquiridoEm);
        Assert.Equal(TimeSpan.Zero, s.RecebidoEm.Offset);
        Assert.Equal(55.5678, s.Nivel);
        Assert.Equal(SampleReason.Initial, s.MotivoGravacao);
        Assert.True(s.Id > 0);
    }

    [PostgresFact]
    public async Task Grava_evento_com_tipo_legivel_no_banco()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        await loja.SaveAsync([], [new ProcessEvent { OcorridoEm = T0, Tipo = ProcessEventType.SetpointAlterado, Detalhe = "x", ValorAnterior = 60, ValorNovo = 70 }], default);

        await using var db = fx.NovoContexto();
        Assert.Equal("SetpointAlterado", await db.Database.SqlQuery<string>($"SELECT \"Tipo\" AS \"Value\" FROM process_events").SingleAsync());
        var e = Assert.Single((await loja.QueryEventsAsync(T0.AddHours(-1), T0.AddHours(1), 10, null, default)).Items);
        Assert.Equal(70, e.ValorNovo);
    }

    [PostgresFact]
    public async Task Gravacao_de_amostra_e_eventos_e_atomica()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        var ev = new ProcessEvent { OcorridoEm = T0, Tipo = ProcessEventType.NivelAlto, Detalhe = new string('x', 600) }; // excede varchar(500)

        await Assert.ThrowsAnyAsync<Exception>(() => loja.SaveAsync([Amostra(0)], [ev], default));

        await using var db = fx.NovoContexto();
        Assert.Equal(0, await db.TelemetrySamples.CountAsync());   // nada ficou gravado pela metade
    }

    [PostgresFact]
    public async Task Varias_amostras_sem_hora_de_aquisicao_nao_conflitam()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        await loja.SaveAsync([Amostra(0), Amostra(1), Amostra(2)], [], default);
        await using var db = fx.NovoContexto();
        Assert.Equal(3, await db.TelemetrySamples.CountAsync());
    }

    [PostgresFact]
    public async Task Hora_de_aquisicao_repetida_e_descartada_e_o_evento_junto_e_preservado()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        var aq = T0.AddSeconds(-2);
        await loja.SaveAsync([Amostra(0, aq: aq)], [], default);

        var r = await loja.SaveAsync(
            [Amostra(1, aq: aq)],
            [new ProcessEvent { OcorridoEm = T0.AddSeconds(1), Tipo = ProcessEventType.ModoAlterado }], default);

        Assert.Equal(new SaveResult(0, 1, 1), r);
        await using var db = fx.NovoContexto();
        Assert.Equal(1, await db.TelemetrySamples.CountAsync());
        Assert.Equal(1, await db.ProcessEvents.CountAsync());
    }

    // ---------- Consulta ----------

    [PostgresFact]
    public async Task Consulta_por_periodo_ordena_pela_hora_mesmo_com_insercao_fora_de_ordem()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        foreach (var seg in new[] { 30, 10, 50, 20, 40 }) await loja.SaveAsync([Amostra(seg, nivel: seg)], [], default);

        var p = await loja.QuerySamplesAsync(T0, T0.AddSeconds(100), 10, null, default);

        Assert.Equal([10.0, 20, 30, 40, 50], p.Items.Select(s => s.Nivel));
    }

    [PostgresFact]
    public async Task Periodo_e_inclusivo_nos_extremos_e_exclui_o_resto()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        await loja.SaveAsync(Enumerable.Range(0, 10).Select(i => Amostra(i * 10, nivel: i)).ToList(), [], default);

        var p = await loja.QuerySamplesAsync(T0.AddSeconds(20), T0.AddSeconds(50), 100, null, default);

        Assert.Equal([2.0, 3, 4, 5], p.Items.Select(s => s.Nivel));
    }

    [PostgresFact]
    public async Task Paginacao_por_cursor_cobre_tudo_inclusive_com_horas_repetidas()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        var todas = new List<TelemetrySample>();
        for (var i = 0; i < 23; i++) todas.Add(Amostra(i / 3 * 10, nivel: i));   // trios com a mesma hora
        await loja.SaveAsync(todas, [], default);

        var vistos = new List<double>();
        Cursor? cursor = null;
        for (var guarda = 0; guarda < 20; guarda++)
        {
            var p = await loja.QuerySamplesAsync(T0.AddMinutes(-1), T0.AddMinutes(10), 5, cursor, default);
            vistos.AddRange(p.Items.Select(s => s.Nivel));
            if (!p.HasMore) break;
            cursor = new Cursor(p.Items[^1].RecebidoEm, p.Items[^1].Id);
        }

        Assert.Equal(Enumerable.Range(0, 23).Select(i => (double)i), vistos);
    }

    [PostgresFact]
    public async Task Consulta_sem_dados_devolve_pagina_vazia()
    {
        await fx.LimparAsync();
        var p = await fx.NovaLoja().QuerySamplesAsync(T0, T0.AddHours(1), 10, null, default);
        Assert.Empty(p.Items);
        Assert.False(p.HasMore);
    }

    [PostgresFact]
    public async Task Indice_de_hora_e_utilizavel_pela_consulta_paginada()
    {
        // Com poucas linhas o planejador prefere varrer a tabela; desligar o seqscan nesta sessão prova que o índice
        // existe e serve ao formato da consulta (WHERE por período + ORDER BY hora, id + LIMIT).
        await fx.LimparAsync();
        await fx.NovaLoja().SaveAsync(Enumerable.Range(0, 500).Select(i => Amostra(i)).ToList(), [], default);
        await using var db = fx.NovoContexto();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("ANALYZE telemetry_samples");
        await db.Database.ExecuteSqlRawAsync("SET enable_seqscan = off");

        var plano = await db.Database.SqlQuery<string>(
            $"EXPLAIN SELECT * FROM telemetry_samples WHERE \"RecebidoEm\" >= {T0.AddSeconds(10)} AND \"RecebidoEm\" <= {T0.AddSeconds(20)} ORDER BY \"RecebidoEm\", \"Id\" LIMIT 11")
            .ToListAsync();

        Assert.Contains(plano, l => l.Contains("IX_telemetry_samples_RecebidoEm"));
    }

    // ---------- Retenção ----------

    [PostgresFact]
    public async Task Retencao_em_lotes_apaga_so_o_expirado_e_preserva_eventos_dentro_do_prazo()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        await loja.SaveAsync(
            Enumerable.Range(0, 12).Select(i => Amostra(-(20 * 86400) + i, nivel: i)).Concat(
            Enumerable.Range(0, 3).Select(i => Amostra(-(86400) + i, nivel: 100 + i))).ToList(),
            [
                new ProcessEvent { OcorridoEm = T0.AddDays(-20), Tipo = ProcessEventType.NivelAlto },     // evento antigo, retenção maior: fica
                new ProcessEvent { OcorridoEm = T0.AddDays(-400), Tipo = ProcessEventType.NivelBaixo }    // expirado
            ], default);

        var (amostras, eventos) = await loja.DeleteExpiredAsync(T0.AddDays(-14), T0.AddDays(-365), batchSize: 5, default);

        Assert.Equal(12, amostras);
        Assert.Equal(1, eventos);
        await using var db = fx.NovoContexto();
        Assert.Equal([100.0, 101, 102], await db.TelemetrySamples.OrderBy(s => s.Nivel).Select(s => s.Nivel).ToListAsync());
        Assert.Equal(ProcessEventType.NivelAlto, (await db.ProcessEvents.SingleAsync()).Tipo);
    }

    [PostgresFact]
    public async Task Retencao_sem_nada_expirado_nao_apaga_nada()
    {
        await fx.LimparAsync();
        var loja = fx.NovaLoja();
        await loja.SaveAsync([Amostra(0)], [new ProcessEvent { OcorridoEm = T0, Tipo = ProcessEventType.NivelAlto }], default);

        Assert.Equal((0, 0), await loja.DeleteExpiredAsync(T0.AddDays(-14), T0.AddDays(-365), 100, default));
    }

    // ---------- Falhas de conexão e recuperação ----------

    [PostgresFact]
    public async Task Conexao_indisponivel_lanca_excecao_sem_travar()
    {
        var opcoes = new DbContextOptionsBuilder<SmartPlcDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=smartplc_test;Timeout=2;Pooling=false").Options;
        var loja = new EfHistoryStore(new PostgresFixture.Fabrica(opcoes), NullLogger<EfHistoryStore>.Instance);

        var inicio = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() => loja.SaveAsync([Amostra(0)], [], default));
        Assert.True((DateTime.UtcNow - inicio).TotalSeconds < 15);
    }

    // ---------- Ponta a ponta: API -> política -> fila -> PostgreSQL -> /api/historico ----------

    [PostgresFact]
    public async Task Ponta_a_ponta_telemetria_vira_historico_consultavel()
    {
        await fx.LimparAsync();
        using var app = new TestApp(new()
        {
            ["ConnectionStrings:SmartPlc"] = fx.Raw,
            ["History:RetryDelayMilliseconds"] = "0"
        });

        // 1) a API recebe e valida
        Assert.Equal(HttpStatusCode.OK, (await app.PostTelemetria("""{"nivel":55.5,"valvula":30,"setpoint":60,"modoManual":false}""")).StatusCode);
        // 2) o SignalR/estado em tempo real já foi atualizado
        Assert.Single(app.Notifier.Enviados);
        // 3) a política decidiu e a fila grava (aqui explicitamente, sem o serviço em segundo plano)
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await app.PostTelemetria("""{"nivel":55.5,"valvula":30,"setpoint":70,"modoManual":false}""");   // mudança de setpoint lida do CLP
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);
        Assert.Equal(2, app.HistStatus.AmostrasGravadas);
        Assert.Equal(0, app.HistStatus.Falhas);

        // 4) o histórico recupera
        var inicio = app.Clock.GetUtcNow().AddMinutes(-1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var j = await app.CreateClient().GetFromJsonAsync<JsonElement>($"/api/historico?de={inicio}");
        Assert.Equal(2, j.GetProperty("quantidade").GetInt32());
        Assert.Equal("Initial", j.GetProperty("amostras")[0].GetProperty("motivo").GetString());
        Assert.Equal("Event", j.GetProperty("amostras")[1].GetProperty("motivo").GetString());

        var ev = await app.CreateClient().GetFromJsonAsync<JsonElement>($"/api/eventos?de={inicio}");
        Assert.Equal("SetpointAlterado", ev.GetProperty("eventos")[0].GetProperty("tipo").GetString());

        // 5) a supervisão continua operacional
        Assert.True((await app.Status()).GetProperty("telemetria").GetProperty("plcOnline").GetBoolean());
    }

    [PostgresFact]
    public async Task Ponta_a_ponta_com_banco_fora_do_ar_o_tempo_real_segue_e_a_falha_e_declarada()
    {
        using var app = new TestApp(new()
        {
            ["ConnectionStrings:SmartPlc"] = "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=smartplc_test;Timeout=2;Pooling=false",
            ["History:RetryDelayMilliseconds"] = "0",
            ["History:MaxWriteRetries"] = "1"
        });

        Assert.Equal(HttpStatusCode.OK, (await app.PostTelemetria(TestApp.TelemetriaOk)).StatusCode);
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Equal(2, app.HistStatus.Falhas);
        Assert.Equal(1, app.HistStatus.PerdidosAposFalhas);
        Assert.True((await app.Status()).GetProperty("telemetria").GetProperty("plcOnline").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.CreateClient().GetAsync("/api/historico")).StatusCode);
    }
}
