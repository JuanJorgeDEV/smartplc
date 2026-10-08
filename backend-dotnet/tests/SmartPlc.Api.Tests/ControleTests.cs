using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task Comando_aceito_e_encaminhado_e_so_entao_o_estado_muda()
    {
        using var app = TestApp.ComControle();
        var r = await app.PostControle("""{"setpoint":42.5}""");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var corpo = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", corpo.GetProperty("status").GetString());
        Assert.Equal("encaminhado", corpo.GetProperty("estadoComando").GetString());
        Assert.False(corpo.GetProperty("confirmadoPeloClp").GetBoolean()); // HTTP 200 != CLP executou
        Assert.Equal(42.5, corpo.GetProperty("controle").GetProperty("setpoint").GetDouble());

        Assert.Equal(42.5, Assert.Single(app.NodeRed.Recebidos).Setpoint);
        Assert.Equal(42.5, (await app.Status()).GetProperty("controle").GetProperty("setpoint").GetDouble());
        Assert.Single(app.Notifier.Enviados);
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
    public async Task Pid_e_modo_manual_com_valvula_sao_encaminhados_juntos()
    {
        using var app = TestApp.ComControle();
        var r = await app.PostControle("""{"modoManual":true,"valvulaManual":33,"kp":2.5,"ti":12,"td":1}""");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var enviado = Assert.Single(app.NodeRed.Recebidos);
        Assert.True(enviado.ModoManual);
        Assert.Equal(33, enviado.ValvulaManual);
        Assert.Equal(2.5, enviado.Kp);
        var c = (await app.Status()).GetProperty("controle");
        Assert.True(c.GetProperty("modoManual").GetBoolean());
        Assert.Equal(12, c.GetProperty("ti").GetDouble());
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
