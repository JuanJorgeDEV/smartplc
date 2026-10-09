using System.Globalization;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Options;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.History;

/// <summary>Uma telemetria JÁ VALIDADA, com a hora de recebimento definida pelo servidor.</summary>
public readonly record struct Reading(
    DateTimeOffset RecebidoEm,
    DateTimeOffset? AdquiridoEm,
    double Nivel,
    double Valvula,
    double? Setpoint,
    bool? ModoManual,
    double? Kp,
    double? Ti,
    double? Td);

/// <summary>O que gravar por causa de uma leitura. Vazio = nada a gravar.</summary>
public sealed record HistoryDecision(TelemetrySample? Sample, IReadOnlyList<ProcessEvent> Events)
{
    public static readonly HistoryDecision Nothing = new(null, []);
    public bool IsEmpty => Sample is null && Events.Count == 0;
}

/// <summary>
/// Regra pura (sem banco, sem relógio próprio) que decide o que vale a pena gravar.
/// A) periódica, B) variação, C) eventos — ver docs/modelo-dados.md.
/// Só decide quando CHEGA uma leitura: o passar do tempo sozinho nunca gera gravação
/// (não existe "amostra de preenchimento" quando o CLP está offline).
/// </summary>
public sealed class HistoryPolicy(IOptions<HistoryOptions> history, IOptions<ProcessOptions> process)
{
    private readonly HistoryOptions _h = history.Value;
    private readonly ProcessOptions _p = process.Value;
    private readonly Lock _lock = new();

    // Referência da última amostra decidida (e, quando há falha final de gravação, zerada por ResetSampleBaseline)
    private bool _temAmostra;
    private DateTimeOffset _amostraEm;
    private double _amostraNivel, _amostraValvula;

    // Último estado observado (para detectar mudanças e não repetir eventos)
    private DateTimeOffset? _ultimoAdquirido;
    private bool? _online;
    private bool _alto, _baixo;
    private double? _sp, _kp, _ti, _td;
    private bool? _manual;

    public HistoryDecision Evaluate(Reading r)
    {
        lock (_lock)
        {
            // Reentrega ou mensagem atrasada: só detectável se a origem informou a hora de aquisição.
            if (r.AdquiridoEm is { } aq)
            {
                if (_ultimoAdquirido is { } ultimo && aq <= ultimo) return HistoryDecision.Nothing;
                _ultimoAdquirido = aq;
            }

            var quando = r.AdquiridoEm ?? r.RecebidoEm;
            var eventos = new List<ProcessEvent>();

            // --- Comunicação ---
            if (_online == false)
                eventos.Add(Evento(ProcessEventType.ComunicacaoRestabelecida, quando, "telemetria voltou a chegar"));
            _online = true;

            // --- Alarmes de nível (só na transição) ---
            var alto = r.Nivel > _p.NivelAlto;
            var baixo = r.Nivel < _p.NivelBaixo;
            if (alto != _alto)
                eventos.Add(Evento(alto ? ProcessEventType.NivelAlto : ProcessEventType.NivelAltoNormalizado, quando,
                    $"nível {Fmt(r.Nivel)} % (limite {Fmt(_p.NivelAlto)} %)", novo: r.Nivel));
            if (baixo != _baixo)
                eventos.Add(Evento(baixo ? ProcessEventType.NivelBaixo : ProcessEventType.NivelBaixoNormalizado, quando,
                    $"nível {Fmt(r.Nivel)} % (limite {Fmt(_p.NivelBaixo)} %)", novo: r.Nivel));
            _alto = alto;
            _baixo = baixo;

            // --- Parâmetros lidos do CLP (campo ausente na telemetria = sem informação, não é mudança) ---
            if (r.Setpoint is { } sp)
            {
                if (_sp is { } spAnt && Math.Abs(sp - spAnt) > _h.ParameterTolerance)
                    eventos.Add(Evento(ProcessEventType.SetpointAlterado, quando, "setpoint lido do CLP mudou", spAnt, sp));
                _sp = sp;
            }

            if (r.ModoManual is { } manual)
            {
                if (_manual is { } manualAnt && manual != manualAnt)
                    eventos.Add(Evento(ProcessEventType.ModoAlterado, quando,
                        manual ? "modo lido do CLP: automático -> manual" : "modo lido do CLP: manual -> automático"));
                _manual = manual;
            }

            var pid = new List<string>();
            Mudou(pid, "kp", ref _kp, r.Kp);
            Mudou(pid, "ti", ref _ti, r.Ti);
            Mudou(pid, "td", ref _td, r.Td);
            if (pid.Count > 0)
                eventos.Add(Evento(ProcessEventType.PidAlterado, quando, "PID lido do CLP mudou: " + string.Join("; ", pid)));

            // --- Amostra ---
            SampleReason? motivo = null;
            if (!_temAmostra) motivo = SampleReason.Initial;
            else if (eventos.Count > 0) motivo = SampleReason.Event;
            else if (Math.Abs(r.Nivel - _amostraNivel) >= _h.MinimumLevelChange
                  || Math.Abs(r.Valvula - _amostraValvula) >= _h.MinimumValveChange) motivo = SampleReason.Change;
            else if ((r.RecebidoEm - _amostraEm).TotalSeconds >= _h.SampleIntervalSeconds) motivo = SampleReason.Periodic;

            TelemetrySample? amostra = null;
            if (motivo is { } m)
            {
                amostra = new TelemetrySample
                {
                    RecebidoEm = r.RecebidoEm,
                    AdquiridoEm = r.AdquiridoEm,
                    Nivel = r.Nivel,
                    Valvula = r.Valvula,
                    Setpoint = r.Setpoint,
                    ModoManual = r.ModoManual,
                    MotivoGravacao = m
                };
                _temAmostra = true;
                _amostraEm = r.RecebidoEm;
                _amostraNivel = r.Nivel;
                _amostraValvula = r.Valvula;
            }

            if (!_h.RecordEvents) eventos.Clear();
            return amostra is null && eventos.Count == 0 ? HistoryDecision.Nothing : new HistoryDecision(amostra, eventos);
        }
    }

    /// <summary>O monitor percebeu que a telemetria parou. Gera o evento só na primeira vez (online -> offline).</summary>
    public ProcessEvent? OnCommunicationLost(DateTimeOffset agora, double? idadeSegundos)
    {
        lock (_lock)
        {
            if (_online != true) return null;
            _online = false;
            if (!_h.RecordEvents) return null;
            var detalhe = idadeSegundos is { } i
                ? $"sem telemetria há {i.ToString("0.#", CultureInfo.InvariantCulture)} s"
                : "sem telemetria";
            return Evento(ProcessEventType.ComunicacaoPerdida, agora, detalhe);
        }
    }

    /// <summary>
    /// Chamado quando uma gravação foi PERDIDA de vez (fila cheia ou falhas esgotadas): a referência de amostra
    /// deixa de valer, então a próxima leitura é gravada como ponto de partida.
    /// </summary>
    public void ResetSampleBaseline()
    {
        lock (_lock) _temAmostra = false;
    }

    private void Mudou(List<string> mudancas, string nome, ref double? atual, double? novo)
    {
        if (novo is not { } n) return;
        if (atual is { } a && Math.Abs(n - a) > _h.ParameterTolerance)
            mudancas.Add($"{nome} {Fmt(a)} -> {Fmt(n)}");
        atual = n;
    }

    private static ProcessEvent Evento(ProcessEventType tipo, DateTimeOffset quando, string detalhe, double? anterior = null, double? novo = null) =>
        new() { Tipo = tipo, OcorridoEm = quando, Detalhe = detalhe, ValorAnterior = anterior, ValorNovo = novo };

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
