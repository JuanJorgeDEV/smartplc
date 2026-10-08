using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartPlc.Api.Services;

namespace SmartPlc.Api.Tests;

public class StatusEFrescorTests
{
    [Fact]
    public async Task Antes_da_primeira_leitura_o_CLP_aparece_offline_e_sem_timestamp()
    {
        // Express: timestamp = hora de subida do servidor, o que parecia uma leitura recente.
        using var app = new TestApp();
        var t = (await app.Status()).GetProperty("telemetria");

        Assert.False(t.GetProperty("plcOnline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, t.GetProperty("timestamp").ValueKind);
        Assert.Equal(JsonValueKind.Null, t.GetProperty("idadeSegundos").ValueKind);
        Assert.Equal(0, t.GetProperty("nivel").GetDouble());
        var leds = t.GetProperty("leds");
        Assert.False(leds.GetProperty("low").GetBoolean());   // sem leitura não existe "nível baixo"
        Assert.False(leds.GetProperty("normal").GetBoolean());
        Assert.False(leds.GetProperty("high").GetBoolean());
    }

    [Fact]
    public async Task Estado_inicial_de_controle_igual_ao_do_express()
    {
        using var app = new TestApp();
        var c = (await app.Status()).GetProperty("controle");
        Assert.Equal(0, c.GetProperty("setpoint").GetDouble());
        Assert.False(c.GetProperty("modoManual").GetBoolean());
        Assert.Equal(1, c.GetProperty("kp").GetDouble());
        Assert.Equal(20, c.GetProperty("ti").GetDouble());
        Assert.Equal(0, c.GetProperty("td").GetDouble());
    }

    [Fact]
    public async Task Online_ate_o_limite_e_offline_depois_com_idade_crescente()
    {
        using var app = new TestApp();
        await app.PostTelemetria(TestApp.TelemetriaOk);

        app.Clock.Advance(TimeSpan.FromSeconds(5));
        var t = (await app.Status()).GetProperty("telemetria");
        Assert.True(t.GetProperty("plcOnline").GetBoolean());
        Assert.Equal(5, t.GetProperty("idadeSegundos").GetDouble());

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        t = (await app.Status()).GetProperty("telemetria");
        Assert.False(t.GetProperty("plcOnline").GetBoolean());
        Assert.Equal(6, t.GetProperty("idadeSegundos").GetDouble());
        Assert.Equal(55.57, t.GetProperty("nivel").GetDouble()); // último valor real é mantido, mas sinalizado como velho
    }

    [Fact]
    public async Task Nova_telemetria_volta_a_ficar_online()
    {
        using var app = new TestApp();
        await app.PostTelemetria(TestApp.TelemetriaOk);
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        await app.PostTelemetria(TestApp.TelemetriaOk);

        var t = (await app.Status()).GetProperty("telemetria");
        Assert.True(t.GetProperty("plcOnline").GetBoolean());
        Assert.Equal(0, t.GetProperty("idadeSegundos").GetDouble());
    }

    [Fact]
    public async Task Limiar_de_frescor_e_configuravel()
    {
        using var app = new TestApp(new() { ["Telemetry:StaleAfterSeconds"] = "20" });
        await app.PostTelemetria(TestApp.TelemetriaOk);
        app.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True((await app.Status()).GetProperty("telemetria").GetProperty("plcOnline").GetBoolean());
    }

    [Fact]
    public async Task Monitor_notifica_uma_unica_vez_na_passagem_para_offline()
    {
        using var app = new TestApp();
        var monitor = app.Services.GetRequiredService<StalenessMonitor>();
        await app.PostTelemetria(TestApp.TelemetriaOk);
        app.Notifier.Enviados.Clear();

        await monitor.VerificarAsync();
        Assert.Empty(app.Notifier.Enviados); // ainda online

        app.Clock.Advance(TimeSpan.FromSeconds(6));
        await monitor.VerificarAsync();
        await monitor.VerificarAsync();

        var enviado = Assert.Single(app.Notifier.Enviados);
        Assert.False(enviado.Telemetria.PlcOnline);
    }

    [Fact]
    public async Task Monitor_nao_notifica_se_nunca_houve_telemetria()
    {
        using var app = new TestApp();
        var monitor = app.Services.GetRequiredService<StalenessMonitor>();
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.VerificarAsync();
        Assert.Empty(app.Notifier.Enviados);
    }

    [Fact]
    public async Task Health_responde_ok()
    {
        using var app = new TestApp();
        var r = await app.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Cors_nao_libera_origens_externas_por_padrao()
    {
        using var app = new TestApp();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        req.Headers.Add("Origin", "https://site-malicioso.example");
        var r = await app.CreateClient().SendAsync(req);
        Assert.False(r.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_libera_somente_as_origens_configuradas()
    {
        using var app = new TestApp(new() { ["Cors:AllowedOrigins:0"] = "https://painel.example" });
        var c = app.CreateClient();

        var ok = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        ok.Headers.Add("Origin", "https://painel.example");
        Assert.Equal("https://painel.example", (await c.SendAsync(ok)).Headers.GetValues("Access-Control-Allow-Origin").Single());

        var ruim = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        ruim.Headers.Add("Origin", "https://outro.example");
        Assert.False((await c.SendAsync(ruim)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Frontend_estatico_e_servido_na_raiz()
    {
        using var app = new TestApp();
        var html = await app.CreateClient().GetStringAsync("/");
        Assert.Contains("Tanque TQ-101", html);
    }
}
