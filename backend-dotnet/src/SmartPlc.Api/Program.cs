using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SmartPlc.Api.Endpoints;
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

// Corpos de telemetria/controle têm poucas dezenas de bytes; 16 KB é folga de sobra.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 16 * 1024);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProcessStateStore>();
builder.Services.AddSingleton<ITelemetryNotifier, SignalRTelemetryNotifier>();
builder.Services.AddSingleton<StalenessMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StalenessMonitor>());
builder.Services.AddHttpClient<INodeRedClient, NodeRedClient>(c => c.Timeout = Timeout.InfiniteTimeSpan); // prazo controlado em NodeRedClient

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
    if (cfg.GetValue<bool>($"{ControlOptions.Section}:Enabled") && string.IsNullOrEmpty(cfg[$"{ControlOptions.Section}:OperatorApiKey"]))
        throw new InvalidOperationException("Control:OperatorApiKey é obrigatória quando Control:Enabled=true fora de Development.");
}
else
{
    if (string.IsNullOrEmpty(cfg[$"{IngestOptions.Section}:ApiKey"]))
        app.Logger.LogWarning("Ingest:ApiKey vazia: POST /api/telemetria está ABERTO (aceitável só em Development).");
    if (cfg.GetValue<bool>($"{ControlOptions.Section}:Enabled"))
        app.Logger.LogWarning("Control:Enabled=true: comandos serão encaminhados ao Node-RED.");
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapSmartPlcApi();

app.Run();

// Necessário para WebApplicationFactory<Program> nos testes.
public partial class Program;
