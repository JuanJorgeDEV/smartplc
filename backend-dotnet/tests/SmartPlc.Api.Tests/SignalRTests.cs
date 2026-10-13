using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace SmartPlc.Api.Tests;

public class SignalRTests
{
    private static async Task<(HubConnection Hub, Channel<JsonElement> Mensagens)> Conectar(TestApp app)
    {
        var canal = Channel.CreateUnbounded<JsonElement>();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/telemetria"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling; // TestServer: long polling dispensa WebSocket
            })
            .Build();
        hub.On<JsonElement>("telemetria_update", m => canal.Writer.TryWrite(m));
        await hub.StartAsync();
        return (hub, canal);
    }

    private static async Task<JsonElement?> Receber(Channel<JsonElement> canal, int ms = 3000)
    {
        using var cts = new CancellationTokenSource(ms);
        try { return await canal.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
    }

    [Fact]
    public async Task Telemetria_valida_chega_ao_dashboard_no_evento_telemetria_update()
    {
        using var app = new TestApp(notifierReal: true);
        var (hub, canal) = await Conectar(app);
        await using var _ = hub;

        await app.PostTelemetria(TestApp.TelemetriaOk);

        var msg = await Receber(canal);
        Assert.NotNull(msg);
        // Mesmo formato do Express: { telemetria, controle } em camelCase.
        Assert.Equal(55.57, msg!.Value.GetProperty("telemetria").GetProperty("nivel").GetDouble());
        Assert.True(msg.Value.GetProperty("telemetria").GetProperty("plcOnline").GetBoolean());
        Assert.Equal(60, msg.Value.GetProperty("controle").GetProperty("setpoint").GetDouble());
        Assert.False(msg.Value.GetProperty("telemetria").GetProperty("leds").GetProperty("low").GetBoolean());
    }

    [Fact]
    public async Task Telemetria_invalida_nao_gera_evento()
    {
        using var app = new TestApp(notifierReal: true);
        var (hub, canal) = await Conectar(app);
        await using var _ = hub;

        await app.PostTelemetria("""{"nivel":"abc","valvula":1}""");

        Assert.Null(await Receber(canal, 500));
    }

    [Fact]
    public async Task Todos_os_clientes_conectados_recebem()
    {
        using var app = new TestApp(notifierReal: true);
        var (h1, c1) = await Conectar(app);
        var (h2, c2) = await Conectar(app);
        await using var _1 = h1; await using var _2 = h2;

        await app.PostTelemetria(TestApp.TelemetriaOk);

        Assert.NotNull(await Receber(c1));
        Assert.NotNull(await Receber(c2));
    }

    [Fact]
    public async Task Mudanca_de_estado_de_comando_e_difundida_no_evento_comando_update()
    {
        using var app = new TestApp(new() { ["Control:Enabled"] = "true" }, notifierReal: true);
        var canal = Channel.CreateUnbounded<JsonElement>();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/telemetria"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            }).Build();
        hub.On<JsonElement>("comando_update", m => canal.Writer.TryWrite(m));
        await hub.StartAsync();
        await using var _ = hub;

        await app.PostControle("""{"setpoint":33}""");

        var estados = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var msg = await Receber(canal);
            Assert.NotNull(msg);
            estados.Add(msg!.Value.GetProperty("estado").GetString()!);
            Assert.Equal(33, msg.Value.GetProperty("campos").GetProperty("setpoint").GetDouble());
        }
        Assert.Equal(["solicitado", "recebido"], estados);
    }

    [Fact]
    public async Task Comando_aceito_nao_difunde_um_novo_controle_otimista_em_telemetria_update()
    {
        using var app = new TestApp(new() { ["Control:Enabled"] = "true" }, notifierReal: true);
        var (hub, canal) = await Conectar(app);
        await using var _ = hub;

        await app.PostControle("""{"setpoint":33}""");

        Assert.Null(await Receber(canal, 500)); // só telemetria real gera telemetria_update
    }

    [Fact]
    public async Task Cliente_que_conecta_depois_recupera_o_estado_por_api_status()
    {
        // O hub não reenvia histórico: o dashboard chama GET /api/status ao conectar (igual ao Express).
        using var app = new TestApp(notifierReal: true);
        await app.PostTelemetria(TestApp.TelemetriaOk);
        var (hub, canal) = await Conectar(app);
        await using var _ = hub;

        Assert.Null(await Receber(canal, 300));
        Assert.Equal(55.57, (await app.Status()).GetProperty("telemetria").GetProperty("nivel").GetDouble());
    }
}
