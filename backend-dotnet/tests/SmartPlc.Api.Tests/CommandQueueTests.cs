using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartPlc.Api.Services;

namespace SmartPlc.Api.Tests;

/// <summary>
/// Entrega de comandos por FILA (Control:Delivery=queue): o servidor guarda o comando e o Node-RED o recebe na RESPOSTA da próxima
/// telemetria. É o único caminho que funciona com o servidor na nuvem, porque a conexão sempre parte do Node-RED.
/// </summary>
public class CommandQueueTests
{
    private static TestApp Criar(Dictionary<string, string?>? extra = null)
    {
        var cfg = new Dictionary<string, string?> { ["Control:Delivery"] = "queue" };
        foreach (var (k, v) in extra ?? []) cfg[k] = v;
        return TestApp.ComControle(cfg);
    }

    private static string Tel(double nivel = 50, double sp = 50, bool manual = false, double kp = 1, double ti = 20, double td = 0, double vm = 0) =>
        $$"""{"nivel":{{nivel}},"valvula":40,"setpoint":{{sp}},"modoManual":{{(manual ? "true" : "false")}},"valvulaManual":{{vm}},"kp":{{kp}},"ti":{{ti}},"td":{{td}}}""";

    private static async Task<JsonElement> Corpo(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Comando_na_fila_nao_chama_o_NodeRed_e_fica_solicitado()
    {
        using var app = Criar();
        await app.PostTelemetria(Tel());
        var r = await app.PostControle("""{"setpoint":70}""");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var c = await Corpo(r);
        Assert.Equal("solicitado", c.GetProperty("estadoComando").GetString());
        Assert.Equal("fila", c.GetProperty("entrega").GetString());
        Assert.False(c.GetProperty("confirmadoPeloClp").GetBoolean());
        Assert.Empty(app.NodeRed.Recebidos);                         // o servidor não chama ninguém
        Assert.Equal(50, (await app.Status()).GetProperty("controle").GetProperty("setpoint").GetDouble());   // nada de otimismo
    }

    [Fact]
    public async Task Comando_vai_na_resposta_da_proxima_telemetria_uma_unica_vez_e_vira_recebido()
    {
        using var app = Criar();
        await app.PostTelemetria(Tel());
        var id = (await Corpo(await app.PostControle("""{"setpoint":70,"kp":3}"""))).GetProperty("comandoId").GetString();

        var resp = await Corpo(await app.PostTelemetria(Tel()));
        var comandos = resp.GetProperty("comandos");
        Assert.Equal(1, comandos.GetArrayLength());
        Assert.Equal(id, comandos[0].GetProperty("id").GetString());
        Assert.Equal(70, comandos[0].GetProperty("campos").GetProperty("setpoint").GetDouble());
        Assert.Equal(3, comandos[0].GetProperty("campos").GetProperty("kp").GetDouble());

        var segunda = await Corpo(await app.PostTelemetria(Tel()));
        Assert.False(segunda.TryGetProperty("comandos", out _));     // não repete

        var tracker = app.Services.GetRequiredService<CommandTracker>();
        Assert.Equal("recebido", tracker.Obter(Guid.Parse(id!))!.Estado);
        Assert.Contains(app.Notifier.Comandos, c => c.Estado == "recebido");
    }

    [Fact]
    public async Task So_vira_confirmado_quando_a_telemetria_seguinte_traz_o_valor_pedido()
    {
        using var app = Criar();
        await app.PostTelemetria(Tel());
        var id = Guid.Parse((await Corpo(await app.PostControle("""{"setpoint":70}"""))).GetProperty("comandoId").GetString()!);
        var tracker = app.Services.GetRequiredService<CommandTracker>();

        app.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await app.PostTelemetria(Tel(sp: 50));                       // entrega, mas o CLP ainda reporta 50
        Assert.Equal("recebido", tracker.Obter(id)!.Estado);

        app.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await app.PostTelemetria(Tel(sp: 70));                       // agora o CLP reportou 70
        var final = tracker.Obter(id)!;
        Assert.Equal("confirmado", final.Estado);
        Assert.True(final.ConfirmadoPeloClp);
        Assert.Equal(["solicitado", "recebido", "confirmado"], app.Notifier.Comandos.Where(c => c.Id == id).Select(c => c.Estado).Distinct());
    }

    [Fact]
    public async Task Se_o_NodeRed_nao_busca_o_comando_ele_expira_sem_nunca_ter_sido_entregue()
    {
        using var app = Criar();
        await app.PostTelemetria(Tel());
        var id = Guid.Parse((await Corpo(await app.PostControle("""{"setpoint":70}"""))).GetProperty("comandoId").GetString()!);

        app.Clock.Advance(TimeSpan.FromSeconds(11));
        var tracker = app.Services.GetRequiredService<CommandTracker>();
        tracker.ExpirarVencidos();
        Assert.Equal("expirado", tracker.Obter(id)!.Estado);

        var resp = await Corpo(await app.PostTelemetria(Tel()));
        Assert.False(resp.TryGetProperty("comandos", out _), "comando vencido não pode ser entregue depois");
    }

    [Fact]
    public async Task Comando_substituido_nao_e_entregue_e_a_ordem_de_entrega_e_a_do_pedido()
    {
        using var app = Criar();
        await app.PostTelemetria(Tel());
        var antigo = (await Corpo(await app.PostControle("""{"setpoint":60}"""))).GetProperty("comandoId").GetString();
        app.Clock.Advance(TimeSpan.FromMilliseconds(100));
        var kp = (await Corpo(await app.PostControle("""{"kp":2}"""))).GetProperty("comandoId").GetString();
        app.Clock.Advance(TimeSpan.FromMilliseconds(100));
        var novo = (await Corpo(await app.PostControle("""{"setpoint":80}"""))).GetProperty("comandoId").GetString();

        var comandos = (await Corpo(await app.PostTelemetria(Tel()))).GetProperty("comandos");
        Assert.Equal([kp, novo], comandos.EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.DoesNotContain(antigo, comandos.EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.Equal("substituido", app.Services.GetRequiredService<CommandTracker>().Obter(Guid.Parse(antigo!))!.Estado);
    }

    [Fact]
    public async Task Sem_a_chave_de_ingestao_nada_e_entregue_nem_consumido()
    {
        using var app = Criar(new() { ["Ingest:ApiKey"] = "segredo" });
        var com = new HttpRequestMessage(HttpMethod.Post, "/api/telemetria") { Content = TestApp.Json(Tel()) };
        com.Headers.Add("X-Api-Key", "segredo");
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().SendAsync(com)).StatusCode);

        var op = new HttpRequestMessage(HttpMethod.Post, "/api/controle") { Content = TestApp.Json("""{"setpoint":70}""") };
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().SendAsync(op)).StatusCode);

        // tentativa sem chave: recusada, e o comando continua na fila
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostTelemetria(Tel())).StatusCode);
        var legitimo = new HttpRequestMessage(HttpMethod.Post, "/api/telemetria") { Content = TestApp.Json(Tel()) };
        legitimo.Headers.Add("X-Api-Key", "segredo");
        var resp = await Corpo(await app.CreateClient().SendAsync(legitimo));
        Assert.Equal(1, resp.GetProperty("comandos").GetArrayLength());
    }

    [Fact]
    public async Task Controle_desligado_ou_entrega_direta_nao_enviam_comandos_na_resposta()
    {
        using var desligado = new TestApp(new() { ["Control:Delivery"] = "queue" });
        await desligado.PostTelemetria(Tel());
        Assert.Equal(HttpStatusCode.Forbidden, (await desligado.PostControle("""{"setpoint":70}""")).StatusCode);
        Assert.False((await Corpo(await desligado.PostTelemetria(Tel()))).TryGetProperty("comandos", out _));

        using var direta = TestApp.ComControle();          // padrão: push
        await direta.PostTelemetria(Tel());
        var r = await direta.PostControle("""{"setpoint":70}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Single(direta.NodeRed.Recebidos);            // o servidor chamou o Node-RED, como sempre
        Assert.False((await Corpo(await direta.PostTelemetria(Tel()))).TryGetProperty("comandos", out _));
    }

    [Fact]
    public async Task Config_informa_como_o_comando_e_entregue()
    {
        using var fila = Criar();
        Assert.Equal("fila", (await Corpo(await fila.CreateClient().GetAsync("/api/config"))).GetProperty("controle").GetProperty("entrega").GetString());
        using var direta = TestApp.ComControle();
        Assert.Equal("direta", (await Corpo(await direta.CreateClient().GetAsync("/api/config"))).GetProperty("controle").GetProperty("entrega").GetString());
    }

    [Fact]
    public async Task Comando_invalido_continua_sendo_recusado_antes_de_entrar_na_fila()
    {
        using var app = Criar();
        await app.PostTelemetria(Tel());
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostControle("""{"setpoint":150}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostControle("""{}""")).StatusCode);
        Assert.False((await Corpo(await app.PostTelemetria(Tel()))).TryGetProperty("comandos", out _));
    }

    [Fact]
    public async Task Com_login_so_operador_enfileira_e_a_fila_funciona_igual()
    {
        var users = new InMemoryUserStore();
        using var app = TestApp.ComLogin(users, new() { ["Control:Delivery"] = "queue" });
        var auth = app.Services.GetRequiredService<SmartPlc.Api.Auth.AuthService>();
        await auth.CriarAsync("op@empresa.com", "Op", "operador", "tanque-azul-42", default);
        await auth.CriarAsync("v@empresa.com", "V", "visualizador", "tanque-azul-42", default);

        var visual = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        await visual.PostAsJsonAsync("/api/auth/login", new { email = "v@empresa.com", senha = "tanque-azul-42" });
        Assert.Equal(HttpStatusCode.Forbidden, (await visual.PostAsync("/api/controle", TestApp.Json("""{"setpoint":70}"""))).StatusCode);

        var op = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        await op.PostAsJsonAsync("/api/auth/login", new { email = "op@empresa.com", senha = "tanque-azul-42" });
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsync("/api/controle", TestApp.Json("""{"setpoint":70}"""))).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/telemetria") { Content = TestApp.Json(Tel()) };
        req.Headers.Add("X-Api-Key", "chave-de-teste");
        var resp = await Corpo(await app.CreateClient().SendAsync(req));
        Assert.Equal(1, resp.GetProperty("comandos").GetArrayLength());
    }
}
