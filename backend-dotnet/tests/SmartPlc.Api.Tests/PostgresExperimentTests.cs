using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPlc.Api.Lab;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

/// <summary>Sessões do Laboratório contra PostgreSQL REAL (mesmas regras de PostgresIntegrationTests: banco de teste, SMARTPLC_TEST_DB).</summary>
[Collection("pg")]
public class PostgresExperimentTests(PostgresFixture fx)
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

    private static ExperimentSession Nova(string status = ExperimentStatuses.Ativa, int segundos = 0, string tipo = ExperimentTypes.RespostaSetpoint) => new()
    {
        Id = Guid.NewGuid(), Nome = "sessão " + segundos, Tipo = tipo, Status = status, InicioEm = T0.AddSeconds(segundos),
        PlcOnlineInicial = true, NivelInicial = 40.25, SetpointInicial = 40, ValvulaInicial = 33.5, ModoManualInicial = false
    };

    private EfExperimentStore Loja() => new(new PostgresFixture.Fabrica(fx.Options!));

    private async Task Limpar()
    {
        await fx.LimparAsync();
        await using var db = fx.NovoContexto();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE experiment_sessions");
    }

    [PostgresFact]
    public async Task Migration_cria_a_tabela_e_o_indice_unico_parcial_da_sessao_ativa()
    {
        await using var db = fx.NovoContexto();
        Assert.Contains("AddExperimentSessions", string.Join(",", await db.Database.GetAppliedMigrationsAsync()));
        var def = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_experiment_sessions_Status'")
            .SingleAsync();
        Assert.Contains("UNIQUE", def);
        Assert.Contains("'ativa'", def);
    }

    [PostgresFact]
    public async Task Sessao_sem_fonte_informada_vale_clp_e_sessao_simulada_guarda_a_serie_em_jsonb()
    {
        await Limpar();
        var loja = Loja();
        var comum = Nova(ExperimentStatuses.Encerrada, 0);
        await loja.CriarAsync(comum, default);
        Assert.Equal("clp", (await loja.ObterAsync(comum.Id, default))!.Fonte);
        Assert.Null((await loja.ObterAsync(comum.Id, default))!.SerieSimulada);

        // Série de verdade, produzida pelo simulador e passada pelo mesmo caminho de gravação/leitura da API.
        var svc = new SimulationService(TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimulationService>.Instance);
        var id = Guid.NewGuid();
        svc.Iniciar(id, T0, new SimulationParameters(30, 30, 2, 30, 0, 1));
        svc.Avancar(5);
        svc.Comandar(id, new SimulationCommand(Setpoint: 60));
        svc.Avancar(5);
        var snap = svc.Encerrar(id)!;

        var sim = Nova(ExperimentStatuses.Encerrada, 100);
        sim.Fonte = ExperimentSources.Simulacao;
        sim.SerieSimulada = SimulatedSeries.Serializar(snap.Inicio, snap.Amostras, snap.Eventos, false);
        await loja.CriarAsync(sim, default);

        var lida = await loja.ObterAsync(sim.Id, default);
        Assert.Equal(ExperimentSources.Simulacao, lida!.Fonte);
        var (amostras, eventos, _) = SimulatedSeries.Ler(lida.InicioEm, lida.SerieSimulada);
        Assert.Equal(snap.Amostras.Count, amostras.Count);
        Assert.Single(eventos);

        // O banco entende o documento como jsonb de verdade (consultável), não como texto solto.
        await using var db = fx.NovoContexto();
        var n = await db.Database
            .SqlQuery<int>($"SELECT jsonb_array_length(\"SerieSimulada\"->'p') AS \"Value\" FROM experiment_sessions WHERE \"Id\" = {sim.Id}")
            .SingleAsync();
        Assert.Equal(snap.Amostras.Count, n);
    }

    [PostgresFact]
    public async Task Grava_e_le_a_sessao_com_todos_os_campos_e_instantes_em_UTC()
    {
        await Limpar();
        var loja = Loja();
        var s = Nova();
        s.Observacoes = "obs";
        await loja.CriarAsync(s, default);

        var lida = await loja.ObterAsync(s.Id, default);

        Assert.NotNull(lida);
        Assert.Equal(s.Nome, lida!.Nome);
        Assert.Equal(T0, lida.InicioEm);
        Assert.Equal(TimeSpan.Zero, lida.InicioEm.Offset);
        Assert.Equal(40.25, lida.NivelInicial);
        Assert.Equal(33.5, lida.ValvulaInicial);
        Assert.False(lida.ModoManualInicial);
        Assert.Null(lida.FimEm);
        Assert.Null(lida.NivelFinal);
        Assert.Equal("obs", lida.Observacoes);
    }

    [PostgresFact]
    public async Task Atualiza_ao_encerrar_e_libera_a_vaga_da_sessao_ativa()
    {
        await Limpar();
        var loja = Loja();
        var s = Nova();
        await loja.CriarAsync(s, default);

        s.Status = ExperimentStatuses.Encerrada;
        s.FimEm = T0.AddMinutes(2);
        s.NivelFinal = 59.9;
        await loja.AtualizarAsync(s, default);

        var lida = (await loja.ObterAsync(s.Id, default))!;
        Assert.Equal(ExperimentStatuses.Encerrada, lida.Status);
        Assert.Equal(59.9, lida.NivelFinal);
        Assert.Equal(T0.AddMinutes(2), lida.FimEm);
        Assert.Null(await loja.ObterAtivaAsync(default));

        await loja.CriarAsync(Nova(segundos: 300), default);               // agora cabe outra ativa
        Assert.NotNull(await loja.ObterAtivaAsync(default));
    }

    [PostgresFact]
    public async Task O_proprio_banco_impede_duas_sessoes_ativas()
    {
        await Limpar();
        var loja = Loja();
        var primeira = Nova();
        await loja.CriarAsync(primeira, default);

        var ex = await Assert.ThrowsAsync<ExperimentConflictException>(() => loja.CriarAsync(Nova(segundos: 10), default));

        Assert.Equal(primeira.Id, ex.SessaoAtiva);
        await using var db = fx.NovoContexto();
        Assert.Equal(1, await db.ExperimentSessions.CountAsync());
    }

    [PostgresFact]
    public async Task Duas_criacoes_simultaneas_resultam_em_exatamente_uma_sessao_ativa()
    {
        await Limpar();
        var loja = Loja();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try { await loja.CriarAsync(Nova(segundos: i), default); return true; }
            catch (ExperimentConflictException) { return false; }
        }));

        Assert.Equal(1, resultados.Count(r => r));
        await using var db = fx.NovoContexto();
        Assert.Equal(1, await db.ExperimentSessions.CountAsync(s => s.Status == ExperimentStatuses.Ativa));
    }

    [PostgresFact]
    public async Task Varias_sessoes_encerradas_ou_canceladas_convivem()
    {
        await Limpar();
        var loja = Loja();
        foreach (var (st, seg) in new[] { (ExperimentStatuses.Encerrada, 0), (ExperimentStatuses.Encerrada, 10), (ExperimentStatuses.Cancelada, 20) })
            await loja.CriarAsync(Nova(st, seg), default);
        await using var db = fx.NovoContexto();
        Assert.Equal(3, await db.ExperimentSessions.CountAsync());
    }

    [PostgresFact]
    public async Task Lista_ordena_da_mais_nova_para_a_mais_antiga_filtra_e_limita()
    {
        await Limpar();
        var loja = Loja();
        await loja.CriarAsync(Nova(ExperimentStatuses.Encerrada, 0), default);
        await loja.CriarAsync(Nova(ExperimentStatuses.Cancelada, 100), default);
        await loja.CriarAsync(Nova(ExperimentStatuses.Encerrada, 200), default);
        await loja.CriarAsync(Nova(ExperimentStatuses.Ativa, 300), default);

        Assert.Equal([300, 200, 100, 0], (await loja.ListarAsync(10, null, default)).Select(s => (int)(s.InicioEm - T0).TotalSeconds));
        Assert.Equal([200, 0], (await loja.ListarAsync(10, ExperimentStatuses.Encerrada, default)).Select(s => (int)(s.InicioEm - T0).TotalSeconds));
        Assert.Equal(2, (await loja.ListarAsync(2, null, default)).Count);
    }

    [PostgresFact]
    public async Task Sessao_inexistente_devolve_nulo()
    {
        await Limpar();
        Assert.Null(await Loja().ObterAsync(Guid.NewGuid(), default));
    }

    [PostgresFact]
    public async Task Nome_maior_que_o_limite_do_banco_falha_e_nao_grava()
    {
        await Limpar();
        var s = Nova();
        s.Nome = new string('x', 200);
        await Assert.ThrowsAnyAsync<Exception>(() => Loja().CriarAsync(s, default));
        await using var db = fx.NovoContexto();
        Assert.Equal(0, await db.ExperimentSessions.CountAsync());
    }

    // ---------- Ponta a ponta: API -> política -> PostgreSQL -> resumo do laboratório ----------

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string Tel(double pv, double sp) =>
        $$"""{"nivel":{{F(pv)}},"valvula":40,"setpoint":{{F(sp)}},"modoManual":false,"valvulaManual":0,"kp":1,"ti":20,"td":0}""";

    [PostgresFact]
    public async Task Ponta_a_ponta_laboratorio_com_historico_persistido_no_PostgreSQL()
    {
        await Limpar();
        using var app = new TestApp(new()
        {
            ["ConnectionStrings:SmartPlc"] = fx.Raw,
            ["History:RetryDelayMilliseconds"] = "0"
        });

        // sessão ativa no banco real, a partir de telemetria válida
        await app.PostTelemetria(Tel(30, 30));
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        var criada = await app.CreateClient().PostAsync("/api/experimentos", TestApp.Json("""{"tipo":"respostaSetpoint","nome":"Degrau 30 para 60"}"""));
        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);
        var id = (await criada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessao").GetProperty("id").GetString()!;

        // CLP reporta um degrau de setpoint e a resposta de 1ª ordem (τ = 15 s)
        for (var t = 1; t <= 120; t++)
        {
            app.Clock.Advance(TimeSpan.FromSeconds(1));
            var apos = t >= 4;
            var pv = apos ? 30 + 30 * (1 - Math.Exp(-(t - 4) / 15.0)) : 30;
            await app.PostTelemetria(Tel(Math.Round(pv, 2), apos ? 60 : 30));
        }
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);
        Assert.Equal(0, app.HistStatus.Falhas);

        var fim = await app.CreateClient().PostAsync($"/api/experimentos/{id}/encerrar", TestApp.Json("""{"observacoes":"fim"}"""));
        Assert.Equal(HttpStatusCode.OK, fim.StatusCode);

        // "novo processo": relê tudo do banco (nada vem da memória da aplicação anterior)
        var detalhe = await app.CreateClient().GetFromJsonAsync<JsonElement>($"/api/experimentos/{id}");
        var resposta = detalhe.GetProperty("resumo").GetProperty("resposta");
        Assert.True(resposta.GetProperty("calculada").GetBoolean(), resposta.GetProperty("motivo").GetString());
        Assert.InRange(resposta.GetProperty("segundosAteT63").GetDouble(), 12, 18);
        Assert.Equal("encerrada", detalhe.GetProperty("sessao").GetProperty("status").GetString());
        Assert.Equal(30, detalhe.GetProperty("sessao").GetProperty("nivelInicial").GetDouble());
        Assert.Equal(60, detalhe.GetProperty("sessao").GetProperty("setpointFinal").GetDouble());
        Assert.Contains(detalhe.GetProperty("eventos").EnumerateArray(), e => e.GetProperty("tipo").GetString() == "SetpointAlterado");
        Assert.True(detalhe.GetProperty("resumo").GetProperty("amostras").GetInt32() > 15);

        var lista = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/experimentos");
        Assert.Equal(id, lista.GetProperty("sessoes")[0].GetProperty("id").GetString());
    }
}
