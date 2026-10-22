using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SmartPlc.Api.Auth;
using SmartPlc.Api.Endpoints;
using SmartPlc.Api.History;
using SmartPlc.Api.Lab;
using SmartPlc.Api.Persistence;
using SmartPlc.Api.Options;
using SmartPlc.Api.Services;
using SmartPlc.Api.Validation;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.Configure<ProcessOptions>(cfg.GetSection(ProcessOptions.Section));
builder.Services.Configure<TelemetryOptions>(cfg.GetSection(TelemetryOptions.Section));
builder.Services.Configure<IngestOptions>(cfg.GetSection(IngestOptions.Section));
builder.Services.Configure<ControlOptions>(cfg.GetSection(ControlOptions.Section));
builder.Services.Configure<NodeRedOptions>(cfg.GetSection(NodeRedOptions.Section));

// Render (e outros PaaS) informam a porta em PORT e exigem escuta em 0.0.0.0. Local continua em http://localhost:5000.
var portaPaas = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(portaPaas)
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrEmpty(cfg["urls"]))
    builder.WebHost.UseUrls($"http://0.0.0.0:{portaPaas}");

// Corpos de telemetria/controle têm poucas dezenas de bytes; 16 KB é folga de sobra.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 16 * 1024);

builder.Services.Configure<HistoryOptions>(cfg.GetSection(HistoryOptions.Section));
builder.Services.Configure<CommandOptions>(cfg.GetSection(CommandOptions.Section));
builder.Services.Configure<LabOptions>(cfg.GetSection(LabOptions.Section));
builder.Services.Configure<IntegrationOptions>(cfg.GetSection(IntegrationOptions.Section));
builder.Services.AddSingleton<CommandTracker>();
builder.Services.AddSingleton<SimulationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimulationService>());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProcessStateStore>();
builder.Services.AddSingleton<ITelemetryNotifier, SignalRTelemetryNotifier>();
builder.Services.AddSingleton<StalenessMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StalenessMonitor>());
builder.Services.AddHttpClient<INodeRedClient, NodeRedClient>(c => c.Timeout = Timeout.InfiniteTimeSpan); // prazo controlado em NodeRedClient

// ---- Histórico (PostgreSQL). Sem connection string => desligado, o restante funciona como antes. ----
var historyOptions = cfg.GetSection(HistoryOptions.Section).Get<HistoryOptions>() ?? new HistoryOptions();
var connectionString = historyOptions.Enabled ? DbConnection.Resolve(cfg) : null;
builder.Services.AddSingleton<HistoryStatus>();
builder.Services.AddSingleton<HistoryPolicy>();
if (connectionString is not null)
{
    builder.Services.AddDbContextFactory<SmartPlcDbContext>(o => o.UseNpgsql(connectionString));
    builder.Services.AddSingleton<IHistoryStore, EfHistoryStore>();
    builder.Services.AddSingleton<IExperimentStore, EfExperimentStore>();
    if (historyOptions.MigrateOnStartup) builder.Services.AddHostedService<SchemaMigrator>();
}
else
{
    builder.Services.AddSingleton<IHistoryStore, DisabledHistoryStore>();
    builder.Services.AddSingleton<IExperimentStore, DisabledExperimentStore>();
}
builder.Services.AddSingleton<HistoryPipeline>();
builder.Services.AddSingleton<IHistoryRecorder>(sp => sp.GetRequiredService<HistoryPipeline>());
builder.Services.AddHostedService<HistoryWriter>();
builder.Services.AddHostedService<RetentionService>();

// ---- Login (cookie de sessão). Ligado por padrão fora de Development; exige o banco. ----
var authHabilitado = AuthSetup.Habilitado(cfg, builder.Environment);
builder.AddSmartPlcAuth(authHabilitado, connectionString is not null);

builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web));

// CORS: por padrão nenhuma origem externa. O dashboard é servido pela própria aplicação (mesma origem).
var origens = cfg.GetSection(CorsOptions.Section).Get<CorsOptions>()?.AllowedOrigins ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origens.Length > 0) p.WithOrigins(origens).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

// Fora de Development, não sobe sem segredos configurados (evita um ambiente aberto por esquecimento).
if (!app.Environment.IsDevelopment())
{
    if (string.IsNullOrEmpty(cfg[$"{IngestOptions.Section}:ApiKey"]))
        throw new InvalidOperationException("Ingest:ApiKey é obrigatória fora de Development.");
    if (!authHabilitado)
        app.Logger.LogWarning("Auth:Enabled=false fora de Development: o painel está ABERTO a quem alcançar o servidor, sem login.");
    if (!authHabilitado && cfg.GetValue<bool>($"{ControlOptions.Section}:Enabled") && string.IsNullOrEmpty(cfg[$"{ControlOptions.Section}:OperatorApiKey"]))
        throw new InvalidOperationException("Control:OperatorApiKey é obrigatória quando Control:Enabled=true, sem login, fora de Development.");
}
else
{
    if (string.IsNullOrEmpty(cfg[$"{IngestOptions.Section}:ApiKey"]))
        app.Logger.LogWarning("Ingest:ApiKey vazia: POST /api/telemetria está ABERTO (aceitável só em Development).");
    if (cfg.GetValue<bool>($"{ControlOptions.Section}:Enabled"))
        app.Logger.LogWarning("Control:Enabled=true: comandos serão encaminhados ao Node-RED.");
}

if (connectionString is null)
{
    app.Logger.LogInformation("Histórico DESLIGADO (defina ConnectionStrings__SmartPlc para ativá-lo).");
}
else
{
    app.Logger.LogInformation("Histórico ligado: {Banco}", DbConnection.Describe(connectionString));
    if (historyOptions.MigrateOnStartup)
        app.Logger.LogWarning("History:MigrateOnStartup=true: as migrations serão aplicadas em segundo plano no banco acima.");
}

app.UseCors();
app.UseDefaultFiles();
app.UseSmartPlcAuth(authHabilitado);          // cabeçalhos, arquivos estáticos, origem, login e autorização
app.MapAuthApi(authHabilitado);
app.MapSmartPlcApi();
app.MapHistoryApi();
app.MapConfigApi();
app.MapLabApi();

app.Run();

// Necessário para WebApplicationFactory<Program> nos testes.
public partial class Program;
