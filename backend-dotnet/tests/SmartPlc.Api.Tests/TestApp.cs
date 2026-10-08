using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Services;

namespace SmartPlc.Api.Tests;

public sealed class FakeNodeRedClient : INodeRedClient
{
    public ResultadoEnvio Resultado { get; set; } = ResultadoEnvio.Aceito;
    public List<ControleRequest> Recebidos { get; } = [];

    public Task<ResultadoEnvio> EnviarComandoAsync(ControleRequest comando, CancellationToken ct)
    {
        Recebidos.Add(comando);
        return Task.FromResult(Resultado);
    }
}

public sealed class RecordingNotifier : ITelemetryNotifier
{
    public List<StatusResponse> Enviados { get; } = [];

    public Task NotificarAsync(StatusResponse status, CancellationToken ct = default)
    {
        Enviados.Add(status);
        return Task.CompletedTask;
    }
}

/// <summary>
/// API em memória (TestServer) com relógio e Node-RED falsos.
/// O monitor de frescor em segundo plano é removido: os testes o chamam manualmente, sem corrida.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _config;
    private readonly bool _notifierReal;

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
    public FakeNodeRedClient NodeRed { get; } = new();
    public RecordingNotifier Notifier { get; } = new();

    public TestApp(Dictionary<string, string?>? config = null, bool notifierReal = false)
    {
        _config = config ?? [];
        _notifierReal = notifierReal;
    }

    public static TestApp ComControle(Dictionary<string, string?>? extra = null)
    {
        var cfg = new Dictionary<string, string?> { ["Control:Enabled"] = "true" };
        foreach (var (k, v) in extra ?? []) cfg[k] = v;
        return new TestApp(cfg);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (k, v) in _config) builder.UseSetting(k, v);

        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);

            s.RemoveAll<INodeRedClient>();
            s.AddSingleton<INodeRedClient>(NodeRed);

            if (!_notifierReal)
            {
                s.RemoveAll<ITelemetryNotifier>();
                s.AddSingleton<ITelemetryNotifier>(Notifier);
            }

            s.RemoveAll<IHostedService>();
        });
    }

    // ---------- helpers ----------

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public Task<HttpResponseMessage> PostTelemetria(string json, HttpClient? c = null) =>
        (c ?? CreateClient()).PostAsync("/api/telemetria", Json(json));

    public Task<HttpResponseMessage> PostControle(string json, HttpClient? c = null) =>
        (c ?? CreateClient()).PostAsync("/api/controle", Json(json));

    public async Task<JsonElement> Status() =>
        await CreateClient().GetFromJsonAsync<JsonElement>("/api/status");

    public const string TelemetriaOk = """{"nivel":55.5678,"valvula":30,"setpoint":60,"modoManual":false,"valvulaManual":0,"kp":2,"ti":15,"td":0}""";
}
