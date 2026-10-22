using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using SmartPlc.Api.Auth;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.History;
using SmartPlc.Api.Lab;
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
    public List<CommandDto> Comandos { get; } = [];

    public Task NotificarAsync(StatusResponse status, CancellationToken ct = default)
    {
        Enviados.Add(status);
        return Task.CompletedTask;
    }

    public Task NotificarComandoAsync(CommandDto comando, CancellationToken ct = default)
    {
        Comandos.Add(comando);
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
    private readonly IHistoryStore? _historyStore;
    private readonly IExperimentStore? _experimentStore;
    private readonly IUserStore? _userStore;

    /// <summary>Roda como ambiente de Produção (cookie Secure, regras de partida estritas).</summary>
    public bool Producao { get; set; }

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
    public FakeNodeRedClient NodeRed { get; } = new();
    public RecordingNotifier Notifier { get; } = new();

    public TestApp(Dictionary<string, string?>? config = null, bool notifierReal = false, IHistoryStore? historyStore = null, IExperimentStore? experimentStore = null, IUserStore? userStore = null)
    {
        _userStore = userStore;
        _experimentStore = experimentStore;
        _config = config ?? [];
        _notifierReal = notifierReal;
        _historyStore = historyStore;
    }

    /// <summary>Histórico ligado com armazenamento em memória; sem espera entre retentativas.</summary>
    public static TestApp ComHistorico(IHistoryStore store, Dictionary<string, string?>? extra = null)
    {
        var cfg = new Dictionary<string, string?> { ["History:RetryDelayMilliseconds"] = "0" };
        foreach (var (k, v) in extra ?? []) cfg[k] = v;
        return new TestApp(cfg, historyStore: store);
    }

    /// <summary>Histórico e Laboratório ligados, ambos em memória.</summary>
    public static TestApp ComLaboratorio(IHistoryStore historico, IExperimentStore experimentos, Dictionary<string, string?>? extra = null)
    {
        var cfg = new Dictionary<string, string?> { ["History:RetryDelayMilliseconds"] = "0" };
        foreach (var (k, v) in extra ?? []) cfg[k] = v;
        return new TestApp(cfg, historyStore: historico, experimentStore: experimentos);
    }

    public HistoryPipeline Pipeline => Services.GetRequiredService<HistoryPipeline>();
    public HistoryStatus HistStatus => Services.GetRequiredService<HistoryStatus>();

    /// <summary>Login LIGADO, com usuários e Laboratório em memória. Cookies e chaves de sessão ficam na memória do teste.</summary>
    public static TestApp ComLogin(InMemoryUserStore usuarios, Dictionary<string, string?>? extra = null, IExperimentStore? experimentos = null)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true",
            ["Auth:PersistKeysInDatabase"] = "false",
            ["Auth:Pbkdf2Iterations"] = "1000",          // testes rápidos; a produção usa 600 000
            ["ConnectionStrings:SmartPlc"] = "Host=127.0.0.1;Port=1;Database=teste_dummy;Username=x;Password=x",   // só liga os repositórios; nenhum é usado de verdade
            ["History:RetryDelayMilliseconds"] = "0",
            ["Control:Enabled"] = "true",
            ["Ingest:ApiKey"] = "chave-de-teste"
        };
        foreach (var (k, v) in extra ?? []) cfg[k] = v;
        return new TestApp(cfg, historyStore: new InMemoryHistoryStore(), experimentStore: experimentos ?? new InMemoryExperimentStore(), userStore: usuarios);
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
        if (Producao) builder.UseEnvironment("Production");

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

            if (_historyStore is not null)
            {
                s.RemoveAll<IHistoryStore>();
                s.AddSingleton(_historyStore);
            }

            if (_experimentStore is not null)
            {
                s.RemoveAll<IExperimentStore>();
                s.AddSingleton(_experimentStore);
            }

            if (_userStore is not null)
            {
                s.RemoveAll<IUserStore>();
                s.AddSingleton(_userStore);
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
