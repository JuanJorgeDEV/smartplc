using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartPlc.Api.Lab;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

public class TankSimulatorTests
{
    private static void Rodar(TankSimulator s, double segundos)
    {
        for (var t = 0.0; t < segundos - 1e-9; t += SimulationService.PassoSegundos) s.Passo(SimulationService.PassoSegundos);
    }

    [Fact]
    public void Comeca_em_regime_e_fica_parado_sem_perturbacao()
    {
        var s = new TankSimulator(nivelInicial: 40, setpoint: 40);
        Rodar(s, 300);
        Assert.InRange(s.Nivel, 39.9, 40.1);
        Assert.InRange(s.Valvula, TankSimulator.Equilibrio(40) - 0.5, TankSimulator.Equilibrio(40) + 0.5);
    }

    [Fact]
    public void Automatico_leva_o_nivel_ao_setpoint_sem_erro_permanente()
    {
        var s = new TankSimulator(nivelInicial: 20, setpoint: 20) { Setpoint = 60 };
        Rodar(s, 600);
        Assert.InRange(s.Nivel, 59.5, 60.5);
    }

    [Fact]
    public void Sem_acao_integral_sobra_erro_permanente()
    {
        var s = new TankSimulator(nivelInicial: 20, setpoint: 20, kp: 2, ti: 0) { Setpoint = 60 };
        Rodar(s, 900);
        Assert.True(Math.Abs(s.Nivel - 60) > 2, $"P puro não deveria zerar o erro, mas o nível foi a {s.Nivel:0.0}");
    }

    [Fact]
    public void Manual_mantem_a_abertura_e_o_nivel_vai_ao_equilibrio_dessa_abertura()
    {
        var s = new TankSimulator(nivelInicial: 30, setpoint: 30);
        s.DefinirManual(true);
        s.ValvulaManual = 50;
        Rodar(s, 900);
        Assert.Equal(50, s.Valvula, 6);
        // h de equilíbrio = (0,036·u / 0,2547)² = 50 % quando u = 50 %
        Assert.InRange(s.Nivel, 49, 51);
    }

    [Fact]
    public void Volta_ao_automatico_sem_salto_na_valvula()
    {
        var s = new TankSimulator(nivelInicial: 30, setpoint: 30);
        s.DefinirManual(true);
        s.ValvulaManual = 70;
        Rodar(s, 20);
        var antes = s.Valvula;
        s.DefinirManual(false);
        s.Passo(SimulationService.PassoSegundos);
        Assert.InRange(Math.Abs(s.Valvula - antes), 0, 1.5);
    }

    [Fact]
    public void Saida_da_valvula_fica_sempre_entre_0_e_100_e_o_integrador_nao_dispara()
    {
        var s = new TankSimulator(nivelInicial: 10, setpoint: 10, kp: 50, ti: 5) { Setpoint = 100, Perturbacao = 100 };
        for (var i = 0; i < 3000; i++)
        {
            s.Passo(0.1);
            Assert.InRange(s.Valvula, 0, 100);
            Assert.InRange(s.Nivel, 0, 100);
        }
        // Depois de saturar por muito tempo, ao baixar o setpoint a válvula sai do limite em poucos segundos (anti-windup).
        s.Setpoint = 0; s.Perturbacao = 0;
        var segundos = 0.0;
        while (s.Valvula >= 99.9 && segundos < 60) { s.Passo(0.1); segundos += 0.1; }
        Assert.True(segundos < 30, $"o integrador acumulou demais ({segundos:0.0} s para sair da saturação)");
    }

    [Fact]
    public void Perturbacao_derruba_o_nivel_e_o_PID_com_integral_recupera()
    {
        var s = new TankSimulator(nivelInicial: 50, setpoint: 50);
        Rodar(s, 30);
        s.Perturbacao = 40;
        Rodar(s, 20);
        var minimo = s.Nivel;
        Assert.True(minimo < 50, "o dreno deveria baixar o nível");
        Rodar(s, 900);
        Assert.InRange(s.Nivel, 49.5, 50.5);
    }

    [Fact]
    public void E_deterministico()
    {
        double Curva()
        {
            var s = new TankSimulator(20, 20) { Setpoint = 70 };
            Rodar(s, 120);
            return s.Nivel;
        }
        Assert.Equal(Curva(), Curva());
    }
}

public class SimulationServiceTests
{
    private static readonly DateTimeOffset Inicio = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
    private static SimulationService Novo() => new(TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimulationService>.Instance);
    private static SimulationParameters Padrao => new(30, 30, 2, 30, 0, 1);

    [Fact]
    public void Registra_uma_amostra_por_segundo_simulado_e_o_ponto_inicial()
    {
        var svc = Novo();
        var id = Guid.NewGuid();
        svc.Iniciar(id, Inicio, Padrao);
        svc.Avancar(10);
        var snap = svc.Snapshot(id)!;
        Assert.Equal(11, snap.Amostras.Count);                 // t=0 mais 10 amostras
        Assert.Equal(SampleReason.Initial, snap.Amostras[0].MotivoGravacao);
        Assert.Equal(Inicio.AddSeconds(5), snap.Amostras[5].RecebidoEm);
        Assert.Equal(10, snap.Estado.Decorrido, 6);
    }

    [Fact]
    public void Comando_registra_evento_e_amostra_imediata_no_instante_da_mudanca()
    {
        var svc = Novo();
        var id = Guid.NewGuid();
        svc.Iniciar(id, Inicio, Padrao);
        svc.Avancar(5.5);
        svc.Comandar(id, new SimulationCommand(Setpoint: 60));
        var snap = svc.Snapshot(id)!;

        var ev = Assert.Single(snap.Eventos);
        Assert.Equal(ProcessEventType.SetpointAlterado, ev.Tipo);
        Assert.Equal(30, ev.ValorAnterior);
        Assert.Equal(60, ev.ValorNovo);
        Assert.Equal(Inicio.AddSeconds(5.5), ev.OcorridoEm, TimeSpan.FromMilliseconds(1));
        var ultima = snap.Amostras[^1];
        Assert.Equal(SampleReason.Event, ultima.MotivoGravacao);
        Assert.Equal(60, ultima.Setpoint);
    }

    [Fact]
    public void Comando_sem_mudanca_real_nao_gera_evento()
    {
        var svc = Novo();
        var id = Guid.NewGuid();
        svc.Iniciar(id, Inicio, Padrao);
        svc.Comandar(id, new SimulationCommand(Setpoint: 30, Kp: 2, Ti: 30));
        Assert.Empty(svc.Snapshot(id)!.Eventos);
    }

    [Fact]
    public void Modo_e_PID_e_perturbacao_geram_eventos_com_o_texto_que_o_analisador_espera()
    {
        var svc = Novo();
        var id = Guid.NewGuid();
        svc.Iniciar(id, Inicio, Padrao);
        svc.Comandar(id, new SimulationCommand(ModoManual: true));
        svc.Comandar(id, new SimulationCommand(Kp: 3));
        svc.Comandar(id, new SimulationCommand(Perturbacao: 20));
        var ev = svc.Snapshot(id)!.Eventos;
        Assert.Equal([ProcessEventType.ModoAlterado, ProcessEventType.PidAlterado, ProcessEventType.PerturbacaoSimulada], ev.Select(e => e.Tipo));
        Assert.EndsWith("-> manual", ev[0].Detalhe);
    }

    [Fact]
    public void So_existe_uma_simulacao_por_vez()
    {
        var svc = Novo();
        svc.Iniciar(Guid.NewGuid(), Inicio, Padrao);
        Assert.Throws<InvalidOperationException>(() => svc.Iniciar(Guid.NewGuid(), Inicio, Padrao));
    }

    [Fact]
    public void Encerrar_devolve_tudo_e_libera_para_uma_nova()
    {
        var svc = Novo();
        var id = Guid.NewGuid();
        svc.Iniciar(id, Inicio, Padrao);
        svc.Avancar(3);
        var final = svc.Encerrar(id)!;
        Assert.Equal(3, final.Estado.Decorrido, 6);
        Assert.False(svc.Ativa(id));
        Assert.Null(svc.Snapshot(id));
        svc.Iniciar(Guid.NewGuid(), Inicio, Padrao);   // não lança
    }

    [Fact]
    public void Limita_o_numero_de_amostras_e_marca_como_truncado()
    {
        var svc = Novo();
        var id = Guid.NewGuid();
        svc.Iniciar(id, Inicio, Padrao);
        svc.Avancar(SimulationService.MaxAmostras + 100);
        var snap = svc.Snapshot(id)!;
        Assert.Equal(SimulationService.MaxAmostras, snap.Amostras.Count);
        Assert.True(snap.Estado.Truncado);
    }

    [Fact]
    public void Serie_guardada_volta_identica_ao_ler()
    {
        var svc = Novo();
        var id = Guid.NewGuid();
        svc.Iniciar(id, Inicio, Padrao);
        svc.Avancar(4);
        svc.Comandar(id, new SimulationCommand(Setpoint: 55, ModoManual: true));
        svc.Avancar(4);
        var snap = svc.Encerrar(id)!;

        var json = SimulatedSeries.Serializar(snap.Inicio, snap.Amostras, snap.Eventos, snap.Estado.Truncado);
        var (amostras, eventos, truncado) = SimulatedSeries.Ler(snap.Inicio, json);

        Assert.False(truncado);
        Assert.Equal(snap.Amostras.Count, amostras.Count);
        Assert.Equal(snap.Eventos.Count, eventos.Count);
        for (var i = 0; i < amostras.Count; i++)
        {
            Assert.Equal(snap.Amostras[i].RecebidoEm, amostras[i].RecebidoEm, TimeSpan.FromMilliseconds(1));
            Assert.Equal(snap.Amostras[i].Nivel, amostras[i].Nivel, 2);
            Assert.Equal(snap.Amostras[i].Setpoint, amostras[i].Setpoint);
            Assert.Equal(snap.Amostras[i].ModoManual, amostras[i].ModoManual);
        }
        Assert.Equal(snap.Eventos.Select(e => e.Tipo), eventos.Select(e => e.Tipo));
    }

    [Fact]
    public void Ler_serie_vazia_ou_nula_nao_quebra()
    {
        Assert.Empty(SimulatedSeries.Ler(Inicio, null).Amostras);
        Assert.Empty(SimulatedSeries.Ler(Inicio, "  ").Amostras);
    }
}

/// <summary>A simulação pela API: independe de telemetria do CLP e nunca toca o estado real do processo.</summary>
public class SimulationEndpointTests
{
    private static (TestApp App, InMemoryExperimentStore Exp, SimulationService Sim) Criar()
    {
        var app = TestApp.ComLaboratorio(new InMemoryHistoryStore(), new InMemoryExperimentStore());
        return (app, (InMemoryExperimentStore)app.Services.GetRequiredService<IExperimentStore>(), app.Services.GetRequiredService<SimulationService>());
    }

    private static Task<HttpResponseMessage> Post(TestApp app, string url, string json) => app.CreateClient().PostAsync(url, TestApp.Json(json));
    private static async Task<JsonElement> Corpo(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<string> IdDe(HttpResponseMessage r) => (await Corpo(r)).GetProperty("sessao").GetProperty("id").GetString()!;

    [Fact]
    public async Task Simulacao_inicia_sem_nenhuma_telemetria_do_CLP()
    {
        var (app, exp, _) = Criar();
        using var lixo = app;
        var r = await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint","fonte":"simulacao"}""");

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var s = (await Corpo(r)).GetProperty("sessao");
        Assert.Equal("simulacao", s.GetProperty("fonte").GetString());
        Assert.False(s.GetProperty("plcOnlineInicial").GetBoolean());
        Assert.Equal(30, s.GetProperty("nivelInicial").GetDouble());
        Assert.Single(exp.Sessoes);
    }

    [Fact]
    public async Task Sessao_com_CLP_continua_exigindo_telemetria_e_a_mensagem_oferece_a_simulacao()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        var r = await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint"}""");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("simulação", (await Corpo(r)).GetProperty("mensagem").GetString());
    }

    [Fact]
    public async Task Parametros_iniciais_sao_validados()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        foreach (var corpo in new[]
        {
            """{"tipo":"respostaSetpoint","fonte":"simulacao","simulacao":{"nivelInicial":120}}""",
            """{"tipo":"respostaSetpoint","fonte":"simulacao","simulacao":{"kp":-1}}""",
            """{"tipo":"respostaSetpoint","fonte":"simulacao","simulacao":{"velocidade":3}}""",
            """{"tipo":"respostaSetpoint","fonte":"outra"}""",
            """{"tipo":"respostaSetpoint","simulacao":{"kp":2}}"""
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(app, "/api/experimentos", corpo)).StatusCode);
    }

    [Fact]
    public async Task Parametros_informados_chegam_ao_simulador()
    {
        var (app, _, sim) = Criar();
        using var lixo = app;
        var id = await IdDe(await Post(app, "/api/experimentos",
            """{"tipo":"respostaSetpoint","fonte":"simulacao","simulacao":{"nivelInicial":20,"setpoint":50,"kp":4,"ti":10,"td":1,"velocidade":5}}"""));
        var e = sim.Snapshot(Guid.Parse(id))!.Estado;
        Assert.Equal(20, e.Nivel);
        Assert.Equal(50, e.Setpoint);
        Assert.Equal((4d, 10d, 1d, 5d), (e.Kp, e.Ti, e.Td, e.Velocidade));
    }

    [Fact]
    public async Task Simulacao_nao_mexe_no_estado_real_do_processo()
    {
        var (app, _, sim) = Criar();
        using var lixo = app;
        var id = await IdDe(await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint","fonte":"simulacao"}"""));
        await Post(app, $"/api/experimentos/{id}/simulacao", """{"setpoint":80}""");
        sim.Avancar(30);
        var status = await app.Status();
        Assert.False(status.GetProperty("telemetria").GetProperty("plcOnline").GetBoolean());
        Assert.NotEqual(80, status.GetProperty("controle").GetProperty("setpoint").GetDouble());
    }

    [Fact]
    public async Task Comando_altera_a_simulacao_e_aparece_no_detalhe_com_resumo_da_resposta()
    {
        var (app, _, sim) = Criar();
        using var lixo = app;
        var id = await IdDe(await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint","fonte":"simulacao"}"""));
        sim.Avancar(10);
        var r = await Post(app, $"/api/experimentos/{id}/simulacao", """{"setpoint":60}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(60, (await Corpo(r)).GetProperty("simulacao").GetProperty("setpoint").GetDouble());
        sim.Avancar(300);

        var d = await Corpo(await app.CreateClient().GetAsync($"/api/experimentos/{id}"));
        Assert.True(d.GetProperty("janela").GetProperty("emAndamento").GetBoolean());
        Assert.Equal(JsonValueKind.Object, d.GetProperty("simulacao").ValueKind);
        Assert.Equal("SetpointAlterado", d.GetProperty("eventos")[0].GetProperty("tipo").GetString());
        var resposta = d.GetProperty("resumo").GetProperty("resposta");
        Assert.True(resposta.GetProperty("calculada").GetBoolean(), resposta.GetRawText());
        Assert.InRange(resposta.GetProperty("segundosAteT63").GetDouble(), 5, 200);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"setpoint":101}""")]
    [InlineData("""{"valvulaManual":-1}""")]
    [InlineData("""{"perturbacao":150}""")]
    [InlineData("""{"velocidade":7}""")]
    [InlineData("""{"abrirValvula":true}""")]
    public async Task Comando_invalido_e_rejeitado_sem_alterar_a_simulacao(string corpo)
    {
        var (app, _, sim) = Criar();
        using var lixo = app;
        var id = await IdDe(await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint","fonte":"simulacao"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(app, $"/api/experimentos/{id}/simulacao", corpo)).StatusCode);
        Assert.Empty(sim.Snapshot(Guid.Parse(id))!.Eventos);
    }

    [Fact]
    public async Task Comando_so_vale_para_sessao_simulada_e_em_andamento()
    {
        var (app, exp, sim) = Criar();
        using var lixo = app;

        // sessão do CLP
        await app.PostTelemetria("""{"nivel":50,"valvula":40,"setpoint":50,"modoManual":false,"valvulaManual":0,"kp":1,"ti":20,"td":0}""");
        var idClp = await IdDe(await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint"}"""));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(app, $"/api/experimentos/{idClp}/simulacao", """{"setpoint":10}""")).StatusCode);
        await Post(app, $"/api/experimentos/{idClp}/cancelar", "{}");

        // sessão simulada encerrada
        var idSim = await IdDe(await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint","fonte":"simulacao"}"""));
        await Post(app, $"/api/experimentos/{idSim}/encerrar", "{}");
        Assert.Equal(HttpStatusCode.Conflict, (await Post(app, $"/api/experimentos/{idSim}/simulacao", """{"setpoint":10}""")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Post(app, $"/api/experimentos/{Guid.NewGuid()}/simulacao", """{"setpoint":10}""")).StatusCode);
        Assert.False(sim.Ativa(Guid.Parse(idSim)));
    }

    [Fact]
    public async Task Encerrar_guarda_a_serie_e_o_detalhe_passa_a_vir_do_que_foi_guardado()
    {
        var (app, exp, sim) = Criar();
        using var lixo = app;
        var id = await IdDe(await Post(app, "/api/experimentos", """{"tipo":"automaticoManual","fonte":"simulacao"}"""));
        sim.Avancar(20);
        await Post(app, $"/api/experimentos/{id}/simulacao", """{"modoManual":true,"valvulaManual":70}""");
        sim.Avancar(60);

        var r = await Post(app, $"/api/experimentos/{id}/encerrar", "{}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var sessao = exp.Sessoes.Single();
        Assert.Equal("encerrada", sessao.Status);
        Assert.NotNull(sessao.SerieSimulada);
        Assert.Equal(sessao.InicioEm.AddSeconds(80), sessao.FimEm!.Value, TimeSpan.FromMilliseconds(5));   // relógio simulado, não o de parede
        Assert.True(sessao.ModoManualFinal);
        Assert.False(sim.Ativa(sessao.Id));

        var d = await Corpo(await app.CreateClient().GetAsync($"/api/experimentos/{id}"));
        Assert.False(d.GetProperty("janela").GetProperty("emAndamento").GetBoolean());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("simulacao").ValueKind);
        var resumo = d.GetProperty("resumo");
        Assert.True(resumo.GetProperty("manual").GetProperty("segundos").GetDouble() > 50);
        Assert.True(resumo.GetProperty("automatico").GetProperty("segundos").GetDouble() > 15);
        Assert.True(d.GetProperty("serie").GetArrayLength() > 50);
    }

    [Fact]
    public async Task Cancelar_descarta_a_serie()
    {
        var (app, exp, sim) = Criar();
        using var lixo = app;
        var id = await IdDe(await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint","fonte":"simulacao"}"""));
        sim.Avancar(10);
        Assert.Equal(HttpStatusCode.OK, (await Post(app, $"/api/experimentos/{id}/cancelar", "{}")).StatusCode);
        Assert.Null(exp.Sessoes.Single().SerieSimulada);
        Assert.Equal("cancelada", exp.Sessoes.Single().Status);
        Assert.False(sim.Ativa(Guid.Parse(id)));
    }

    [Fact]
    public async Task Duas_sessoes_ao_mesmo_tempo_sao_recusadas()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        Assert.Equal(HttpStatusCode.Created, (await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint","fonte":"simulacao"}""")).StatusCode);
        var r = await Post(app, "/api/experimentos", """{"tipo":"automaticoManual","fonte":"simulacao"}""");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.True((await Corpo(r)).TryGetProperty("sessaoAtiva", out _));
    }

    [Fact]
    public async Task Sessao_simulada_orfa_apos_reinicio_e_cancelada_e_nao_bloqueia_a_proxima()
    {
        var (app, exp, sim) = Criar();
        using var lixo = app;
        // "reinício": existe no banco como ativa, mas a execução em memória não existe.
        exp.Sessoes.Add(new ExperimentSession
        {
            Id = Guid.NewGuid(), Nome = "antiga", Tipo = "respostaSetpoint", Status = "ativa", Fonte = "simulacao",
            InicioEm = app.Clock.GetUtcNow().AddHours(-1)
        });

        var lista = await Corpo(await app.CreateClient().GetAsync("/api/experimentos"));
        Assert.Equal("cancelada", lista.GetProperty("sessoes")[0].GetProperty("status").GetString());
        Assert.Contains("reinício", exp.Sessoes[0].Observacoes);

        var r = await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint","fonte":"simulacao"}""");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.True(sim.Ativa(Guid.Parse(await IdDe(r))));
    }

    [Fact]
    public async Task Sessao_do_CLP_ativa_nao_e_tocada_pela_limpeza_de_orfas()
    {
        var (app, exp, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria("""{"nivel":50,"valvula":40,"setpoint":50,"modoManual":false,"valvulaManual":0,"kp":1,"ti":20,"td":0}""");
        await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint"}""");
        await app.CreateClient().GetAsync("/api/experimentos");
        Assert.Equal("ativa", exp.Sessoes.Single().Status);
    }

    [Fact]
    public async Task Com_laboratorio_sem_escrita_a_simulacao_tambem_e_recusada()
    {
        var app = TestApp.ComLaboratorio(new InMemoryHistoryStore(), new InMemoryExperimentStore(), new() { ["Lab:WriteEnabled"] = "false" });
        using var lixo = app;
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(app, "/api/experimentos", """{"tipo":"respostaSetpoint","fonte":"simulacao"}""")).StatusCode);
    }
}
