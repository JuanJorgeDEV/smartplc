using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartPlc.Api.Lab;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

public class ExperimentEndpointTests
{
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Tel(double nivel, double valvula = 40, double sp = 50, bool manual = false) =>
        $$"""{"nivel":{{F(nivel)}},"valvula":{{F(valvula)}},"setpoint":{{F(sp)}},"modoManual":{{(manual ? "true" : "false")}},"valvulaManual":0,"kp":1,"ti":20,"td":0}""";

    private static (TestApp App, InMemoryHistoryStore Hist, InMemoryExperimentStore Exp) Criar(Dictionary<string, string?>? extra = null)
    {
        var hist = new InMemoryHistoryStore();
        var exp = new InMemoryExperimentStore();
        return (TestApp.ComLaboratorio(hist, exp, extra), hist, exp);
    }

    private static Task<HttpResponseMessage> Iniciar(TestApp app, string json) => app.CreateClient().PostAsync("/api/experimentos", TestApp.Json(json));
    private static Task<HttpResponseMessage> Acao(TestApp app, string id, string acao, string? json = null) =>
        app.CreateClient().PostAsync($"/api/experimentos/{id}/{acao}", json is null ? new StringContent("") : TestApp.Json(json));

    private static async Task<JsonElement> Corpo(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> IdDe(HttpResponseMessage r) => (await Corpo(r)).GetProperty("sessao").GetProperty("id").GetString()!;

    // ---------- Iniciar ----------

    [Fact]
    public async Task Sem_telemetria_valida_nao_inicia_e_orienta_a_conexao()
    {
        var (app, _, exp) = Criar();
        using var lixo = app;
        var r = await Iniciar(app, """{"tipo":"respostaSetpoint"}""");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("telemetria", (await Corpo(r)).GetProperty("mensagem").GetString());
        Assert.Empty(exp.Sessoes);
    }

    [Fact]
    public async Task Com_telemetria_inicia_e_guarda_os_valores_de_partida_vindos_do_servidor()
    {
        var (app, _, exp) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(42.5, valvula: 33, sp: 60, manual: false));

        var r = await Iniciar(app, """{"tipo":"respostaSetpoint","nome":"Meu primeiro","observacoes":"teste"}""");

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var s = (await Corpo(r)).GetProperty("sessao");
        Assert.Equal("ativa", s.GetProperty("status").GetString());
        Assert.Equal("Meu primeiro", s.GetProperty("nome").GetString());
        Assert.Equal("Resposta ao setpoint", s.GetProperty("tipoRotulo").GetString());
        Assert.Equal(42.5, s.GetProperty("nivelInicial").GetDouble());
        Assert.Equal(60, s.GetProperty("setpointInicial").GetDouble());
        Assert.Equal(33, s.GetProperty("valvulaInicial").GetDouble());
        Assert.False(s.GetProperty("modoManualInicial").GetBoolean());
        Assert.True(s.GetProperty("plcOnlineInicial").GetBoolean());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("fimEm").ValueKind);
        Assert.Equal(app.Clock.GetUtcNow(), s.GetProperty("inicioEm").GetDateTimeOffset());
        Assert.Single(exp.Sessoes);
    }

    [Fact]
    public async Task O_cliente_nao_consegue_impor_valores_iniciais()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(42.5));
        var r = await Iniciar(app, """{"tipo":"respostaSetpoint","nivelInicial":99}""");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);    // campo desconhecido é rejeitado
    }

    [Fact]
    public async Task Nome_padrao_e_gerado_quando_nao_informado()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(50));
        var s = (await Corpo(await Iniciar(app, """{"tipo":"automaticoManual"}"""))).GetProperty("sessao");
        Assert.StartsWith("Automático versus manual 2026-01-01 12:00", s.GetProperty("nome").GetString());
    }

    [Theory]
    [InlineData("""{}""", "tipo")]
    [InlineData("""{"tipo":"outro"}""", "tipo")]
    [InlineData("""{"tipo":""}""", "tipo")]
    [InlineData("""{"tipo":"respostaSetpoint","nome":"XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"}""", "nome")]
    public async Task Entradas_invalidas_devolvem_400_com_o_campo(string json, string campo)
    {
        var (app, _, exp) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(50));
        var r = await Iniciar(app, json);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains((await Corpo(r)).GetProperty("erros").EnumerateArray(), e => e.GetProperty("campo").GetString() == campo);
        Assert.Empty(exp.Sessoes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{tipo:")]
    public async Task Corpo_malformado_devolve_400(string json)
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(50));
        Assert.Equal(HttpStatusCode.BadRequest, (await Iniciar(app, json)).StatusCode);
    }

    [Fact]
    public async Task Aceita_o_tipo_ignorando_maiusculas()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(50));
        var r = await Iniciar(app, """{"tipo":"RESPOSTASETPOINT"}""");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("respostaSetpoint", (await Corpo(r)).GetProperty("sessao").GetProperty("tipo").GetString());
    }

    [Fact]
    public async Task Uma_unica_sessao_ativa_por_vez()
    {
        var (app, _, exp) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(50));
        var id = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint"}"""));

        var segunda = await Iniciar(app, """{"tipo":"automaticoManual"}""");

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Equal(id, (await Corpo(segunda)).GetProperty("sessaoAtiva").GetString());
        Assert.Single(exp.Sessoes);
    }

    [Fact]
    public async Task Telemetria_velha_nao_serve_para_iniciar()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(50));
        app.Clock.Advance(TimeSpan.FromSeconds(30));   // CLP parou de responder
        Assert.Equal(HttpStatusCode.Conflict, (await Iniciar(app, """{"tipo":"respostaSetpoint"}""")).StatusCode);
    }

    // ---------- Encerrar / cancelar ----------

    [Fact]
    public async Task Encerrar_guarda_os_valores_finais_e_a_hora()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(40, sp: 40));
        var id = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint"}"""));
        app.Clock.Advance(TimeSpan.FromSeconds(60));
        await app.PostTelemetria(Tel(58, valvula: 12, sp: 60));

        var r = await Acao(app, id, "encerrar", """{"observacoes":"ok"}""");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = (await Corpo(r)).GetProperty("sessao");
        Assert.Equal("encerrada", s.GetProperty("status").GetString());
        Assert.Equal(58, s.GetProperty("nivelFinal").GetDouble());
        Assert.Equal(60, s.GetProperty("setpointFinal").GetDouble());
        Assert.Equal(12, s.GetProperty("valvulaFinal").GetDouble());
        Assert.Equal("ok", s.GetProperty("observacoes").GetString());
        Assert.Equal(app.Clock.GetUtcNow(), s.GetProperty("fimEm").GetDateTimeOffset());
    }

    [Fact]
    public async Task Encerrar_sem_comunicacao_nao_repete_o_ultimo_valor_como_final()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(40));
        var id = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint"}"""));
        app.Clock.Advance(TimeSpan.FromSeconds(60));          // CLP ficou mudo

        var s = (await Corpo(await Acao(app, id, "encerrar"))).GetProperty("sessao");

        Assert.Equal("encerrada", s.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("nivelFinal").ValueKind);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("setpointFinal").ValueKind);
    }

    [Fact]
    public async Task Encerrar_duas_vezes_ou_um_id_inexistente_devolve_erro()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(40));
        var id = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint"}"""));
        await Acao(app, id, "encerrar");

        Assert.Equal(HttpStatusCode.Conflict, (await Acao(app, id, "encerrar")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Acao(app, id, "cancelar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Acao(app, Guid.NewGuid().ToString(), "encerrar")).StatusCode);
    }

    [Fact]
    public async Task Cancelar_libera_a_vaga_para_uma_nova_sessao()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(40));
        var id = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint"}"""));

        var r = await Acao(app, id, "cancelar");

        Assert.Equal("cancelada", (await Corpo(r)).GetProperty("sessao").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Created, (await Iniciar(app, """{"tipo":"automaticoManual"}""")).StatusCode);
    }

    [Fact]
    public async Task Observacoes_longas_no_encerramento_sao_rejeitadas()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(40));
        var id = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint"}"""));
        var r = await Acao(app, id, "encerrar", $$"""{"observacoes":"{{new string('x', 501)}}"}""");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---------- Listar e recuperar após reinício ----------

    [Fact]
    public async Task Lista_filtra_por_status_e_ordena_da_mais_nova_para_a_mais_antiga()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(40));
        var a = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint","nome":"A"}"""));
        await Acao(app, a, "encerrar");
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await app.PostTelemetria(Tel(40));
        await Iniciar(app, """{"tipo":"automaticoManual","nome":"B"}""");

        var todas = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/experimentos");
        Assert.Equal(["B", "A"], todas.GetProperty("sessoes").EnumerateArray().Select(s => s.GetProperty("nome").GetString()));

        var ativas = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/experimentos?status=ativa");
        Assert.Equal(["B"], ativas.GetProperty("sessoes").EnumerateArray().Select(s => s.GetProperty("nome").GetString()));
    }

    [Fact]
    public async Task Sessao_ativa_sobrevive_a_reinicio_da_aplicacao_porque_vive_no_banco()
    {
        var hist = new InMemoryHistoryStore();
        var exp = new InMemoryExperimentStore();

        string id;
        using (var antes = TestApp.ComLaboratorio(hist, exp))
        {
            await antes.PostTelemetria(Tel(40));
            id = await IdDe(await Iniciar(antes, """{"tipo":"respostaSetpoint"}"""));
        }

        using var depois = TestApp.ComLaboratorio(hist, exp);   // "novo processo", mesmo banco
        var ativas = await depois.CreateClient().GetFromJsonAsync<JsonElement>("/api/experimentos?status=ativa");
        Assert.Equal(id, ativas.GetProperty("sessoes")[0].GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.OK, (await depois.CreateClient().GetAsync($"/api/experimentos/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("status=pendente")]
    [InlineData("limite=0")]
    [InlineData("limite=101")]
    [InlineData("limite=abc")]
    public async Task Parametros_invalidos_da_lista_devolvem_400(string query)
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        Assert.Equal(HttpStatusCode.BadRequest, (await app.CreateClient().GetAsync("/api/experimentos?" + query)).StatusCode);
    }

    // ---------- Detalhe e resumo a partir do histórico persistido ----------

    [Fact]
    public async Task Detalhe_de_sessao_inexistente_devolve_404()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/api/experimentos/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Periodo_sem_telemetria_devolve_resumo_vazio_sem_inventar_valores()
    {
        var (app, _, _) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(40));
        app.Clock.Advance(TimeSpan.FromSeconds(1));   // a amostra inicial fica ANTES do início da sessão
        var id = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint"}"""));
        app.Clock.Advance(TimeSpan.FromSeconds(20));

        var j = await app.CreateClient().GetFromJsonAsync<JsonElement>($"/api/experimentos/{id}");

        Assert.Equal(0, j.GetProperty("resumo").GetProperty("amostras").GetInt32());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("resumo").GetProperty("pv").GetProperty("max").ValueKind);
        Assert.False(j.GetProperty("resumo").GetProperty("resposta").GetProperty("calculada").GetBoolean());
        Assert.Empty(j.GetProperty("serie").EnumerateArray());
        Assert.True(j.GetProperty("janela").GetProperty("emAndamento").GetBoolean());
    }

    [Fact]
    public async Task Ponta_a_ponta_resposta_de_primeira_ordem_gravada_pela_politica_real_e_analisada()
    {
        var (app, hist, _) = Criar();
        using var lixo = app;

        // 1) CLP estável em 30 %, sessão inicia
        await app.PostTelemetria(Tel(30, sp: 30));
        var id = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint"}"""));

        // 2) a cada segundo o CLP reporta a resposta de um sistema de 1ª ordem (τ = 20 s) a um degrau 30 -> 60
        for (var t = 1; t <= 150; t++)
        {
            app.Clock.Advance(TimeSpan.FromSeconds(1));
            var degrauAtivo = t >= 5;
            var pv = degrauAtivo ? 30 + 30 * (1 - Math.Exp(-(t - 5) / 20.0)) : 30;
            await app.PostTelemetria(Tel(Math.Round(pv, 2), sp: degrauAtivo ? 60 : 30));
        }
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);
        await Acao(app, id, "encerrar");

        // 3) o resumo vem do que a política REALMENTE gravou (não de todas as 151 mensagens)
        Assert.InRange(hist.Samples.Count, 20, 120);
        var j = await app.CreateClient().GetFromJsonAsync<JsonElement>($"/api/experimentos/{id}");
        var r = j.GetProperty("resumo");
        var resp = r.GetProperty("resposta");

        Assert.Equal(hist.Samples.Count, r.GetProperty("amostras").GetInt32());
        Assert.True(resp.GetProperty("calculada").GetBoolean(), resp.GetProperty("motivo").GetString());
        Assert.InRange(resp.GetProperty("segundosAteT63").GetDouble(), 17, 24);          // τ = 20 s
        Assert.InRange(resp.GetProperty("sobreelevacaoPercentual").GetDouble(), 0, 1);
        Assert.Equal(30, r.GetProperty("pv").GetProperty("inicial").GetDouble());
        Assert.InRange(r.GetProperty("pv").GetProperty("max").GetDouble(), 59, 60.01);
        Assert.Equal(30, r.GetProperty("sp").GetProperty("inicial").GetDouble());
        Assert.Equal(60, r.GetProperty("sp").GetProperty("final").GetDouble());
        Assert.Equal("SetpointAlterado", j.GetProperty("eventos").EnumerateArray().First(e => e.GetProperty("tipo").GetString() == "SetpointAlterado").GetProperty("tipo").GetString());
        Assert.False(j.GetProperty("janela").GetProperty("emAndamento").GetBoolean());
        Assert.True(j.GetProperty("serie").GetArrayLength() <= 600);
    }

    // ---------- Disponibilidade e acesso ----------

    [Fact]
    public async Task Sem_banco_o_laboratorio_responde_503_explicando()
    {
        using var app = new TestApp();    // histórico e laboratório desligados
        foreach (var rota in new[] { "/api/experimentos", $"/api/experimentos/{Guid.NewGuid()}" })
        {
            var r = await app.CreateClient().GetAsync(rota);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Contains("Laboratório indisponível", (await Corpo(r)).GetProperty("mensagem").GetString());
        }
        await app.PostTelemetria(Tel(40));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Iniciar(app, """{"tipo":"respostaSetpoint"}""")).StatusCode);
    }

    [Fact]
    public async Task Laboratorio_pode_ser_desligado_por_configuracao()
    {
        var (app, _, _) = Criar(new() { ["Lab:Enabled"] = "false" });
        using var lixo = app;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.CreateClient().GetAsync("/api/experimentos")).StatusCode);
    }

    [Fact]
    public async Task Escrita_desligada_permite_consultar_mas_nao_iniciar_encerrar_nem_cancelar()
    {
        var hist = new InMemoryHistoryStore();
        var exp = new InMemoryExperimentStore();
        var id = Guid.NewGuid();
        exp.Sessoes.Add(new ExperimentSession { Id = id, Nome = "x", Tipo = ExperimentTypes.RespostaSetpoint, Status = ExperimentStatuses.Ativa, InicioEm = DateTimeOffset.Parse("2026-01-01T11:59:00Z") });
        using var app = TestApp.ComLaboratorio(hist, exp, new() { ["Lab:WriteEnabled"] = "false" });
        await app.PostTelemetria(Tel(40));

        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/api/experimentos")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync($"/api/experimentos/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Iniciar(app, """{"tipo":"respostaSetpoint"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Acao(app, id.ToString(), "encerrar")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Acao(app, id.ToString(), "cancelar")).StatusCode);
        Assert.Equal(ExperimentStatuses.Ativa, exp.Sessoes[0].Status);
    }

    [Fact]
    public async Task Falha_do_banco_devolve_503_sem_vazar_detalhes_e_nao_derruba_a_supervisao()
    {
        var (app, _, exp) = Criar();
        using var lixo = app;
        await app.PostTelemetria(Tel(40));
        exp.FalharSempre = true;

        var r = await Iniciar(app, """{"tipo":"respostaSetpoint"}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var texto = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("simulado", texto);
        Assert.Equal(HttpStatusCode.OK, (await app.PostTelemetria(Tel(41))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.CreateClient().GetAsync("/api/experimentos")).StatusCode);
    }

    [Fact]
    public async Task Experimento_nao_cria_comandos_nem_toca_o_CLP()
    {
        var (app, _, _) = Criar(new() { ["Control:Enabled"] = "true" });
        using var lixo = app;
        await app.PostTelemetria(Tel(40));
        var id = await IdDe(await Iniciar(app, """{"tipo":"respostaSetpoint"}"""));
        await Acao(app, id, "encerrar");

        Assert.Empty(app.NodeRed.Recebidos);
        Assert.Empty(app.Notifier.Comandos);
    }
}
