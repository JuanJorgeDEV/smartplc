using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.History;

/// <summary>Chamado pela API depois que a telemetria foi validada e transmitida. Nunca lança nem bloqueia.</summary>
public interface IHistoryRecorder
{
    void RegistrarTelemetria(TelemetriaRequest dados, DateTimeOffset recebidoEm);
    void RegistrarPerdaComunicacao(DateTimeOffset agora, double? idadeSegundos);
}

/// <summary>Contadores para diagnóstico (/health/ready). Nada aqui afeta o tempo real.</summary>
public sealed class HistoryStatus
{
    private long _enfileirados, _amostrasGravadas, _eventosGravados, _duplicados, _descartadosFilaCheia, _perdidosAposFalhas, _falhas;
    private string? _ultimoErro;
    private long _ultimaGravacaoTicks;

    public long Enfileirados => Interlocked.Read(ref _enfileirados);
    public long AmostrasGravadas => Interlocked.Read(ref _amostrasGravadas);
    public long EventosGravados => Interlocked.Read(ref _eventosGravados);
    public long Duplicados => Interlocked.Read(ref _duplicados);
    public long DescartadosFilaCheia => Interlocked.Read(ref _descartadosFilaCheia);
    public long PerdidosAposFalhas => Interlocked.Read(ref _perdidosAposFalhas);
    public long Falhas => Interlocked.Read(ref _falhas);
    public string? UltimoErro => Volatile.Read(ref _ultimoErro);
    public DateTimeOffset? UltimaGravacaoEm => Interlocked.Read(ref _ultimaGravacaoTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    internal void Enfileirou() => Interlocked.Increment(ref _enfileirados);
    internal void DescartouFilaCheia() => Interlocked.Increment(ref _descartadosFilaCheia);
    internal void PerdeuAposFalhas() => Interlocked.Increment(ref _perdidosAposFalhas);
    internal void Falhou(Exception ex)
    {
        Interlocked.Increment(ref _falhas);
        Volatile.Write(ref _ultimoErro, $"{ex.GetType().Name}: {ex.Message}");
    }
    internal void Gravou(SaveResult r, DateTimeOffset agora)
    {
        Interlocked.Add(ref _amostrasGravadas, r.Samples);
        Interlocked.Add(ref _eventosGravados, r.Events);
        Interlocked.Add(ref _duplicados, r.Duplicates);
        Interlocked.Exchange(ref _ultimaGravacaoTicks, agora.UtcTicks);
    }
}

public sealed class NullHistoryRecorder : IHistoryRecorder
{
    public void RegistrarTelemetria(TelemetriaRequest dados, DateTimeOffset recebidoEm) { }
    public void RegistrarPerdaComunicacao(DateTimeOffset agora, double? idadeSegundos) { }
}

/// <summary>
/// Liga a política ao banco sem acoplar o tempo real à gravação:
///   API -> (política, em memória, instantânea) -> fila limitada -> gravador -> PostgreSQL.
/// Fila cheia: descarta o item novo, registra no log e conta em <see cref="HistoryStatus"/>.
/// Reinício do processo: o que está na fila se perde (a janela é de milissegundos em operação normal).
/// </summary>
public sealed class HistoryPipeline : IHistoryRecorder
{
    private readonly HistoryPolicy _policy;
    private readonly IHistoryStore _store;
    private readonly HistoryOptions _opt;
    private readonly HistoryStatus _status;
    private readonly ILogger<HistoryPipeline> _log;
    private readonly TimeProvider _clock;
    private readonly Channel<HistoryDecision> _fila;

    public HistoryPipeline(HistoryPolicy policy, IHistoryStore store, IOptions<HistoryOptions> opt,
        HistoryStatus status, ILogger<HistoryPipeline> log, TimeProvider clock)
    {
        _policy = policy;
        _store = store;
        _opt = opt.Value;
        _status = status;
        _log = log;
        _clock = clock;
        _fila = Channel.CreateBounded<HistoryDecision>(
            new BoundedChannelOptions(Math.Max(1, _opt.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.Wait, // com Wait, TryWrite devolve false quando cheio; o descarte é declarado em Enfileirar
                SingleReader = true
            });
    }

    public void RegistrarTelemetria(TelemetriaRequest d, DateTimeOffset recebidoEm)
    {
        if (!_opt.Enabled || !_store.Habilitado) return;
        try
        {
            var decisao = _policy.Evaluate(new Reading(recebidoEm, d.AdquiridoEm, d.Nivel!.Value, d.Valvula!.Value,
                d.Setpoint, d.ModoManual, d.Kp, d.Ti, d.Td));
            Enfileirar(decisao);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao avaliar a política de histórico (a telemetria em tempo real não é afetada)");
        }
    }

    public void RegistrarPerdaComunicacao(DateTimeOffset agora, double? idadeSegundos)
    {
        if (!_opt.Enabled || !_store.Habilitado) return;
        try
        {
            if (_policy.OnCommunicationLost(agora, idadeSegundos) is { } ev)
                Enfileirar(new HistoryDecision(null, [ev]));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao registrar perda de comunicação");
        }
    }

    private void Enfileirar(HistoryDecision decisao)
    {
        if (decisao.IsEmpty) return;
        if (_fila.Writer.TryWrite(decisao))
        {
            _status.Enfileirou();
            return;
        }

        _status.DescartouFilaCheia();
        _policy.ResetSampleBaseline();
        _log.LogError("Fila de histórico cheia ({Cap}): registro descartado. O banco está lento ou indisponível?", _opt.QueueCapacity);
    }

    /// <summary>Grava tudo o que está na fila. Devolve quantos itens foram processados.</summary>
    public async Task<int> ProcessarPendentesAsync(CancellationToken ct)
    {
        var n = 0;
        while (_fila.Reader.TryRead(out var item))
        {
            await GravarComRetentativasAsync(item, ct);
            n++;
        }
        return n;
    }

    public async Task AguardarEProcessarAsync(CancellationToken ct)
    {
        if (await _fila.Reader.WaitToReadAsync(ct)) await ProcessarPendentesAsync(ct);
    }

    private async Task GravarComRetentativasAsync(HistoryDecision item, CancellationToken ct)
    {
        IReadOnlyList<TelemetrySample> amostras = item.Sample is null ? [] : [item.Sample];
        for (var tentativa = 0; ; tentativa++)
        {
            try
            {
                // Só conta como gravado DEPOIS de o banco confirmar.
                var r = await _store.SaveAsync(amostras, item.Events, ct);
                _status.Gravou(r, _clock.GetUtcNow());
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _status.Falhou(ex);
                if (tentativa >= _opt.MaxWriteRetries)
                {
                    _status.PerdeuAposFalhas();
                    _policy.ResetSampleBaseline();
                    _log.LogError(ex, "Registro de histórico PERDIDO após {N} tentativas", tentativa + 1);
                    return;
                }
                _log.LogWarning("Falha ao gravar histórico (tentativa {T}): {Msg}", tentativa + 1, ex.Message);
                var espera = _opt.RetryDelayMilliseconds * (1 << tentativa);
                if (espera > 0) await Task.Delay(TimeSpan.FromMilliseconds(espera), _clock, ct);
            }
        }
    }
}

public sealed class HistoryWriter(HistoryPipeline pipeline, ILogger<HistoryWriter> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await pipeline.AguardarEProcessarAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Erro inesperado no gravador de histórico");
                await Task.Delay(1000, stoppingToken);
            }
        }
        // Encerramento limpo: tenta gravar o que sobrou, sem esperar indefinidamente.
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await pipeline.ProcessarPendentesAsync(limite.Token); }
        catch (Exception ex) { log.LogWarning(ex, "Registros pendentes não gravados no encerramento"); }
    }
}
