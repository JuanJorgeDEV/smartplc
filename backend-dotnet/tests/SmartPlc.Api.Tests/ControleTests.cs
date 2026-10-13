using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartPlc.Api.Services;

namespace SmartPlc.Api.Tests;

public class ControleTests
{
    [Fact]
    public async Task Controle_vem_desabilitado_por_padrao_e_nada_e_encaminhado()
    {
        using var app = new TestApp();
        var r = await app.PostControle("""{"setpoint":50}""");

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(app.NodeRed.Recebidos);
        Assert.Equal(0, (await app.Status()).GetProperty("controle").GetProperty("setpoint").GetDouble());
    }

    [Fact]
    public async Task Comando_aceito_pelo_Node_RED_fica_recebido_e_o_valor_no_CLP_nao_muda()
    {
        // Antes o backend mostrava o novo SP assim que o Node-RED respondia 200. Agora só a telemetria lida do CLP muda o controle.
        using var app = TestApp.ComControle();
        var r = await app.PostControle("""{"setpoint":42.5}""");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var corpo = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", corpo.GetProperty("status").GetString());
        Assert.Equal("recebido", corpo.GetProperty("estadoComando").GetString());
        Assert.False(corpo.GetProperty("confirmadoPeloClp").GetBoolean()); // HTTP 200 != CLP executou
        Assert.True(Guid.TryParse(corpo.GetProperty("comandoId").GetString(), out _));
        Assert.Equal(0, corpo.GetProperty("controle").GetProperty("setpoint").GetDouble()); // ainda o valor do CLP

        Assert.Equal(42.5, Assert.Single(app.NodeRed.Recebidos).Setpoint);
        Assert.Equal(0, (await app.Status()).GetProperty("controle").GetProperty("setpoint").GetDouble());
        Assert.Empty(app.Notifier.Enviados);                                  // nenhum "estado" otimista foi difundido
        Assert.Equal(["solicitado", "recebido"], app.Notifier.Comandos.Select(c => c.Estado));
    }

    [Fact]
    public async Task Comando_so_vira_confirmado_quando_a_telemetria_traz_o_valor_pedido()
    {
        using var app = TestApp.ComControle();
        await app.PostTelemetria("""{"nivel":50,"valvula":30,"setpoint":50,"modoManual":false,"valvulaManual":0,"kp":1,"ti":20,"td":0}""");
        var r = await app.PostControle("""{"setpoint":70}""");
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("comandoId").GetString();

        // CLP ainda reporta o valor antigo: continua "recebido"
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await app.PostTelemetria("""{"nivel":51,"valvula":35,"setpoint":50,"modoManual":false,"valvulaManual":0,"kp":1,"ti":20,"td":0}""");
        Assert.Equal("recebido", (await Comando(app, id!)).GetProperty("estado").GetString());

        // CLP reporta o novo valor: confirmado
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await app.PostTelemetria("""{"nivel":52,"valvula":40,"setpoint":70,"modoManual":false,"valvulaManual":0,"kp":1,"ti":20,"td":0}""");
        var c = await Comando(app, id!);
        Assert.Equal("confirmado", c.GetProperty("estado").GetString());
        Assert.True(c.GetProperty("confirmadoPeloClp").GetBoolean());
        Assert.Equal("confirmado", app.Notifier.Comandos[^1].Estado);
        Assert.Equal(70, (await app.Status()).GetProperty("controle").GetProperty("setpoint").GetDouble());
    }

    [Fact]
    public async Task Comando_sem_confirmacao_no_prazo_expira_e_o_dashboard_e_avisado()
    {
        using var app = TestApp.ComControle();
        var monitor = app.Services.GetRequiredService<SmartPlc.Api.Services.StalenessMonitor>();
        await app.PostTelemetria("""{"nivel":50,"valvula":30,"setpoint":50}""");
        var id = (await (await app.PostControle("""{"setpoint":80}""")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("comandoId").GetString();

        app.Clock.Advance(TimeSpan.FromSeconds(9));
        await monitor.VerificarAsync();
        Assert.Equal("recebido", (await Comando(app, id!)).GetProperty("estado").GetString());

        app.Clock.Advance(TimeSpan.FromSeconds(2)); // passou de 10 s
        await monitor.VerificarAsync();
        var c = await Comando(app, id!);
        Assert.Equal("expirado", c.GetProperty("estado").GetString());
        Assert.False(c.GetProperty("confirmadoPeloClp").GetBoolean());
        Assert.Equal("expirado", app.Notifier.Comandos[^1].Estado);
    }

    private static async Task<JsonElement> Comando(TestApp app, string id) =>
        await app.CreateClient().GetFromJsonAsync<JsonElement>($"/api/comandos/{id}");

    [Fact]
    public async Task Lista_de_comandos_recentes_e_consulta_de_id_inexistente()
    {
        using var app = TestApp.ComControle();
        await app.PostControle("""{"setpoint":10}""");
        await app.PostControle("""{"kp":2}""");
        var lista = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/comandos?limite=5");
        Assert.Equal(2, lista.GetProperty("comandos").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/api/comandos/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.CreateClient().GetAsync("/api/comandos?limite=0")).StatusCode);
    }

    [Theory]
    [InlineData(ResultadoEnvio.Recusado, HttpStatusCode.BadGateway)]
    [InlineData(ResultadoEnvio.Inacessivel, HttpStatusCode.BadGateway)]
    [InlineData(ResultadoEnvio.Timeout, HttpStatusCode.GatewayTimeout)]
    public async Task Falha_no_Node_RED_nao_altera_estado_nem_notifica(ResultadoEnvio falha, HttpStatusCode esperado)
    {
        // Express: respondia 500 mas o SP já tinha sido alterado e emitido ao dashboard.
        using var app = TestApp.ComControle();
        app.NodeRed.Resultado = falha;

        var r = await app.PostControle("""{"setpoint":77}""");

        Assert.Equal(esperado, r.StatusCode);
        Assert.Equal("erro", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(0, (await app.Status()).GetProperty("controle").GetProperty("setpoint").GetDouble());
        Assert.Empty(app.Notifier.Enviados);
        Assert.Equal("falhou", app.Notifier.Comandos[^1].Estado);
    }

    [Theory]
    [InlineData("""{}""", "$")]
    [InlineData("""{"setpoint":"abc"}""", "setpoint")]
    [InlineData("""{"setpoint":""}""", "setpoint")]
    [InlineData("""{"setpoint":101}""", "setpoint")]
    [InlineData("""{"setpoint":-1}""", "setpoint")]
    [InlineData("""{"valvulaManual":100.1}""", "valvulaManual")]
    [InlineData("""{"modoManual":"talvez"}""", "modoManual")]
    [InlineData("""{"kp":"abc"}""", "kp")]                    // Express: kp virava null
    [InlineData("""{"kp":1001}""", "kp")]
    [InlineData("""{"ti":-1}""", "ti")]
    [InlineData("""{"td":100001}""", "td")]
    [InlineData("""{"setpoint":50,"comandoPerigoso":1}""", "comandoPerigoso")]  // Express repassava campos desconhecidos
    public async Task Comando_invalido_devolve_400_e_nunca_chega_ao_Node_RED(string json, string campo)
    {
        using var app = TestApp.ComControle();
        var r = await app.PostControle(json);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var erros = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("erros");
        Assert.Contains(erros.EnumerateArray(), e => e.GetProperty("campo").GetString() == campo);
        Assert.Empty(app.NodeRed.Recebidos);
        Assert.Empty(app.Notifier.Enviados);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("{setpoint:5")]
    public async Task Corpo_malformado_devolve_400(string corpo)
    {
        using var app = TestApp.ComControle();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostControle(corpo)).StatusCode);
        Assert.Empty(app.NodeRed.Recebidos);
    }

    [Fact]
    public async Task Pid_e_modo_manual_com_valvula_sao_encaminhados_juntos_e_confirmados_juntos()
    {
        using var app = TestApp.ComControle();
        var r = await app.PostControle("""{"modoManual":true,"valvulaManual":33,"kp":2.5,"ti":12,"td":1}""");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var enviado = Assert.Single(app.NodeRed.Recebidos);
        Assert.True(enviado.ModoManual);
        Assert.Equal(33, enviado.ValvulaManual);
        Assert.Equal(2.5, enviado.Kp);
        Assert.False((await app.Status()).GetProperty("controle").GetProperty("modoManual").GetBoolean()); // sem otimismo

        // Só parte dos campos chegou ao CLP: não confirma
        await app.PostTelemetria("""{"nivel":50,"valvula":33,"setpoint":50,"modoManual":true,"valvulaManual":33,"kp":1,"ti":20,"td":0}""");
        Assert.Equal("recebido", app.Notifier.Comandos[^1].Estado);

        // Todos chegaram
        await app.PostTelemetria("""{"nivel":50,"valvula":33,"setpoint":50,"modoManual":true,"valvulaManual":33,"kp":2.5,"ti":12,"td":1}""");
        Assert.Equal("confirmado", app.Notifier.Comandos[^1].Estado);
    }

    [Fact]
    public async Task Comando_nao_mexe_na_telemetria_nem_na_idade_da_leitura()
    {
        // Express: /api/controle reemitia a telemetria antiga, fazendo o dashboard crer que o CLP estava vivo.
        using var app = TestApp.ComControle();
        await app.PostTelemetria(TestApp.TelemetriaOk);
        app.Clock.Advance(TimeSpan.FromSeconds(30));

        await app.PostControle("""{"setpoint":10}""");

        var t = (await app.Status()).GetProperty("telemetria");
        Assert.False(t.GetProperty("plcOnline").GetBoolean());
        Assert.Equal(30, t.GetProperty("idadeSegundos").GetDouble());
    }

    // ---- Chave do operador (provisória até o login com papéis) ----

    [Fact]
    public async Task Com_chave_de_operador_configurada_exige_o_cabecalho()
    {
        using var app = TestApp.ComControle(new() { ["Control:OperatorApiKey"] = "operador-teste" });
        var c = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostControle("""{"setpoint":5}""", c)).StatusCode);
        Assert.Empty(app.NodeRed.Recebidos);

        c.DefaultRequestHeaders.Add("X-Operator-Key", "operador-teste");
        Assert.Equal(HttpStatusCode.OK, (await app.PostControle("""{"setpoint":5}""", c)).StatusCode);
    }

    [Fact]
    public async Task Chave_de_ingestao_do_Node_RED_nao_serve_como_chave_de_operador()
    {
        using var app = TestApp.ComControle(new()
        {
            ["Ingest:ApiKey"] = "chave-nodered",
            ["Control:OperatorApiKey"] = "operador-teste"
        });
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", "chave-nodered");
        c.DefaultRequestHeaders.Add("X-Operator-Key", "chave-nodered");
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostControle("""{"setpoint":5}""", c)).StatusCode);
    }
}
