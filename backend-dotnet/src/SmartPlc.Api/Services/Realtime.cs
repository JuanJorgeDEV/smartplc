using Microsoft.AspNetCore.SignalR;
using SmartPlc.Api.Contracts;

namespace SmartPlc.Api.Services;

/// <summary>Hub de tempo real. Só o servidor envia; o dashboard apenas escuta.</summary>
public sealed class TelemetriaHub : Hub
{
    public const string Rota = "/hubs/telemetria";

    /// <summary>Mesmo nome do evento Socket.IO do Express, para o frontend mudar o mínimo.</summary>
    public const string EventoAtualizacao = "telemetria_update";
}

public interface ITelemetryNotifier
{
    Task NotificarAsync(StatusResponse status, CancellationToken ct = default);
}

public sealed class SignalRTelemetryNotifier(IHubContext<TelemetriaHub> hub) : ITelemetryNotifier
{
    public Task NotificarAsync(StatusResponse status, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync(TelemetriaHub.EventoAtualizacao, status, ct);
}

/// <summary>
/// Sem telemetria nova, nenhum POST chega para disparar o evento. Este monitor percebe a passagem
/// online -> offline e avisa os dashboards (o Express nunca avisava).
/// </summary>
public sealed class StalenessMonitor(ProcessStateStore store, ITelemetryNotifier notifier, ILogger<StalenessMonitor> log, TimeProvider clock)
    : BackgroundService
{
    private volatile bool _ultimoOnline;

    /// <summary>Chamado pelo ingest para o monitor saber que o último estado enviado era "online".</summary>
    public void MarcarEnviado(StatusResponse s) => _ultimoOnline = s.Telemetria.PlcOnline;

    public async Task VerificarAsync(CancellationToken ct = default)
    {
        var s = store.Snapshot();
        if (_ultimoOnline && !s.Telemetria.PlcOnline)
        {
            log.LogWarning("Sem telemetria do CLP há mais de {Idade:0.0} s", s.Telemetria.IdadeSegundos);
            _ultimoOnline = false;
            await notifier.NotificarAsync(s, ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await VerificarAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha no monitor de telemetria"); }
        }
    }
}
