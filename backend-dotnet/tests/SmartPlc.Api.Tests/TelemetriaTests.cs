using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmartPlc.Api.Tests;

public class TelemetriaTests
{
    [Fact]
    public async Task Payload_valido_atualiza_estado_arredonda_e_notifica()
    {
        using var app = new TestApp();
        var r = await app.PostTelemetria(TestApp.TelemetriaOk);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var s = await app.Status();
        Assert.Equal(55.57, s.GetProperty("telemetria").GetProperty("nivel").GetDouble());
        Assert.Equal(60, s.GetProperty("controle").GetProperty("setpoint").GetDouble());
        Assert.Equal(2, s.GetProperty("controle").GetProperty("kp").GetDouble());
        Assert.Single(app.Notifier.Enviados);
    }

    [Fact]
    public async Task Status_tem_o_mesmo_formato_do_Express_mais_campos_aditivos()
    {
        using var app = new TestApp();
        await app.PostTelemetria(TestApp.TelemetriaOk);
        var s = await app.Status();

        Assert.Equal(["controle", "telemetria"], Nomes(s));
        // Express: nivel, valvula, leds, timestamp. Aditivos: plcOnline, idadeSegundos.
        Assert.Equal(["idadeSegundos", "leds", "nivel", "plcOnline", "timestamp", "valvula"], Nomes(s.GetProperty("telemetria")));
        Assert.Equal(["high", "low", "normal"], Nomes(s.GetProperty("telemetria").GetProperty("leds")));
        Assert.Equal(["kp", "modoManual", "setpoint", "td", "ti", "valvulaManual"], Nomes(s.GetProperty("controle")));
    }

    [Theory]
    [InlineData(9.99, "low")]
    [InlineData(10, "normal")]
    [InlineData(90, "normal")]
    [InlineData(90.01, "high")]
    public async Task Leds_respeitam_os_limites_10_e_90(double nivel, string esperado)
    {
        using var app = new TestApp();
        await app.PostTelemetria($$"""{"nivel":{{nivel.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"valvula":0}""");
        var leds = (await app.Status()).GetProperty("telemetria").GetProperty("leds");
        Assert.True(leds.GetProperty(esperado).GetBoolean());
    }

    // ---- Defeitos do Express que aqui devem estar corrigidos ----

    [Theory]
    [InlineData("""{"valvula":10}""", "nivel")]                                  // ausente
    [InlineData("""{"nivel":50}""", "valvula")]                                  // ausente
    [InlineData("""{"nivel":null,"valvula":1}""", "nivel")]                      // nulo
    [InlineData("""{"nivel":"abc","valvula":1}""", "nivel")]                     // texto
    [InlineData("""{"nivel":"","valvula":1}""", "nivel")]                        // vazio (Express: 0)
    [InlineData("""{"nivel":"NaN","valvula":1}""", "nivel")]                     // não finito
    [InlineData("""{"nivel":"Infinity","valvula":1}""", "nivel")]                // não finito
    [InlineData("""{"nivel":"-Infinity","valvula":1}""", "nivel")]
    [InlineData("""{"nivel":true,"valvula":1}""", "nivel")]                      // booleano no lugar de número
    [InlineData("""{"nivel":[50],"valvula":1}""", "nivel")]                      // tipo errado
    [InlineData("""{"nivel":101,"valvula":1}""", "nivel")]                       // fora da faixa
    [InlineData("""{"nivel":-0.1,"valvula":1}""", "nivel")]
    [InlineData("""{"nivel":500,"valvula":1}""", "nivel")]
    [InlineData("""{"nivel":50,"valvula":100.5}""", "valvula")]
    [InlineData("""{"nivel":50,"valvula":-20}""", "valvula")]
    [InlineData("""{"nivel":50,"valvula":1,"setpoint":9999}""", "setpoint")]
    [InlineData("""{"nivel":50,"valvula":1,"setpoint":"abc"}""", "setpoint")]    // Express: NaN -> null
    [InlineData("""{"nivel":50,"valvula":1,"valvulaManual":101}""", "valvulaManual")]
    [InlineData("""{"nivel":50,"valvula":1,"kp":-1}""", "kp")]
    [InlineData("""{"nivel":50,"valvula":1,"kp":1001}""", "kp")]
    [InlineData("""{"nivel":50,"valvula":1,"ti":100001}""", "ti")]
    [InlineData("""{"nivel":50,"valvula":1,"td":-0.5}""", "td")]
    [InlineData("""{"nivel":50,"valvula":1,"modoManual":"talvez"}""", "modoManual")]
    [InlineData("""{"nivel":50,"valvula":1,"modoManual":2}""", "modoManual")]
    [InlineData("""{"nivel":50,"valvula":1,"modoManual":{}}""", "modoManual")]
    public async Task Payload_invalido_devolve_400_com_o_campo_e_nao_altera_o_estado(string json, string campo)
    {
        using var app = new TestApp();
        await app.PostTelemetria(TestApp.TelemetriaOk); // estado conhecido
        var antes = (await app.Status()).GetRawText();
        app.Notifier.Enviados.Clear();

        var r = await app.PostTelemetria(json);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var corpo = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("erro", corpo.GetProperty("status").GetString());
        Assert.Contains(corpo.GetProperty("erros").EnumerateArray(), e => e.GetProperty("campo").GetString() == campo);
        Assert.Equal(antes, (await app.Status()).GetRawText());
        Assert.Empty(app.Notifier.Enviados);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("42")]
    [InlineData("{nivel:50")]
    public async Task Corpo_que_nao_e_um_objeto_json_devolve_400(string corpo)
    {
        using var app = new TestApp();
        var r = await app.PostTelemetria(corpo);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(app.Notifier.Enviados);
    }

    [Fact]
    public async Task Corpo_maior_que_o_limite_e_rejeitado()
    {
        using var app = new TestApp();
        var grande = $$"""{"nivel":50,"valvula":1,"lixo":"{{new string('x', 20_000)}}"}""";
        var r = await app.PostTelemetria(grande);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Empty(app.Notifier.Enviados);
    }

    // ---- Booleanos e números: o que continua aceito, para não quebrar o fluxo do Node-RED ----

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("\"false\"", false)]   // Express: Boolean("false") === true  (DEFEITO corrigido)
    [InlineData("\"true\"", true)]
    [InlineData("\"FALSE\"", false)]
    public async Task ModoManual_aceita_apenas_formas_inequivocas(string valor, bool esperado)
    {
        using var app = new TestApp();
        var r = await app.PostTelemetria($$"""{"nivel":50,"valvula":1,"modoManual":{{valor}}}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(esperado, (await app.Status()).GetProperty("controle").GetProperty("modoManual").GetBoolean());
    }

    [Fact]
    public async Task Numeros_em_texto_inequivoco_sao_aceitos_como_no_express()
    {
        using var app = new TestApp();
        var r = await app.PostTelemetria("""{"nivel":"42.5","valvula":" 10 ","setpoint":"55"}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = await app.Status();
        Assert.Equal(42.5, s.GetProperty("telemetria").GetProperty("nivel").GetDouble());
        Assert.Equal(55, s.GetProperty("controle").GetProperty("setpoint").GetDouble());
    }

    [Fact]
    public async Task Campos_extras_na_telemetria_sao_ignorados()
    {
        using var app = new TestApp();
        var r = await app.PostTelemetria("""{"nivel":50,"valvula":1,"_msgid":"abc","topic":"x"}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Campos_de_controle_ausentes_preservam_o_valor_anterior()
    {
        using var app = new TestApp();
        await app.PostTelemetria(TestApp.TelemetriaOk);
        await app.PostTelemetria("""{"nivel":70,"valvula":5}""");
        var c = (await app.Status()).GetProperty("controle");
        Assert.Equal(60, c.GetProperty("setpoint").GetDouble());
        Assert.Equal(2, c.GetProperty("kp").GetDouble());
    }

    [Fact]
    public async Task Nomes_de_campo_ignoram_maiusculas()
    {
        using var app = new TestApp();
        var r = await app.PostTelemetria("""{"Nivel":50,"VALVULA":1,"ModoManual":true}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await app.Status()).GetProperty("controle").GetProperty("modoManual").GetBoolean());
    }

    // ---- Mensagens repetidas / fora de ordem (comportamento atual, documentado) ----

    [Fact]
    public async Task Telemetria_duplicada_e_idempotente_no_estado()
    {
        using var app = new TestApp();
        await app.PostTelemetria(TestApp.TelemetriaOk);
        var a = (await app.Status()).GetProperty("controle").GetRawText();
        await app.PostTelemetria(TestApp.TelemetriaOk);
        Assert.Equal(a, (await app.Status()).GetProperty("controle").GetRawText());
    }

    // ---- Chave de API do Node-RED ----

    [Fact]
    public async Task Com_chave_configurada_requisicao_sem_chave_ou_errada_e_401()
    {
        using var app = new TestApp(new() { ["Ingest:ApiKey"] = "segredo-de-teste" });
        var c = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostTelemetria(TestApp.TelemetriaOk, c)).StatusCode);

        c.DefaultRequestHeaders.Add("X-Api-Key", "errada");
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostTelemetria(TestApp.TelemetriaOk, c)).StatusCode);
        Assert.Empty(app.Notifier.Enviados);

        c.DefaultRequestHeaders.Remove("X-Api-Key");
        c.DefaultRequestHeaders.Add("X-Api-Key", "segredo-de-teste");
        Assert.Equal(HttpStatusCode.OK, (await app.PostTelemetria(TestApp.TelemetriaOk, c)).StatusCode);
    }

    [Fact]
    public async Task Leitura_do_status_nao_exige_chave()
    {
        using var app = new TestApp(new() { ["Ingest:ApiKey"] = "segredo-de-teste" });
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/api/status")).StatusCode);
    }

    private static string[] Nomes(JsonElement e) => e.EnumerateObject().Select(p => p.Name).Order().ToArray();
}
