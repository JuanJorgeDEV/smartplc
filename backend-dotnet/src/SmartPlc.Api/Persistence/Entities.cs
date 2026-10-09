namespace SmartPlc.Api.Persistence;

/// <summary>Por que uma amostra foi gravada. Guardado como smallint (a tabela é a de maior volume).</summary>
public enum SampleReason : short
{
    /// <summary>Primeira leitura desde que o servidor subiu (ou desde uma perda de gravação): ponto de partida.</summary>
    Initial = 0,
    /// <summary>Passou o intervalo configurado desde a última amostra gravada.</summary>
    Periodic = 1,
    /// <summary>Nível ou válvula variaram além do limiar em relação à última amostra gravada.</summary>
    Change = 2,
    /// <summary>Um evento relevante ocorreu nesta leitura.</summary>
    Event = 3
}

/// <summary>
/// Leitura selecionada pela política de histórico. NÃO é toda mensagem recebida.
/// Parâmetros PID e válvula manual não são repetidos aqui: mudanças aparecem em <see cref="ProcessEvent"/>.
/// </summary>
public sealed class TelemetrySample
{
    public long Id { get; set; }

    /// <summary>Hora (UTC) em que o backend recebeu a leitura. Sempre presente.</summary>
    public DateTimeOffset RecebidoEm { get; set; }

    /// <summary>Hora (UTC) da aquisição, somente se o Node-RED a informar. O fluxo atual não informa.</summary>
    public DateTimeOffset? AdquiridoEm { get; set; }

    public double Nivel { get; set; }
    public double Valvula { get; set; }
    public double? Setpoint { get; set; }
    public bool? ModoManual { get; set; }
    public SampleReason MotivoGravacao { get; set; }
}

public enum ProcessEventType
{
    NivelAlto,
    NivelAltoNormalizado,
    NivelBaixo,
    NivelBaixoNormalizado,
    ComunicacaoPerdida,
    ComunicacaoRestabelecida,
    /// <summary>Setpoint LIDO do CLP mudou entre duas telemetrias (não é "comando aplicado").</summary>
    SetpointAlterado,
    /// <summary>Modo (auto/manual) LIDO do CLP mudou.</summary>
    ModoAlterado,
    /// <summary>Kp, Ti ou Td LIDOS do CLP mudaram.</summary>
    PidAlterado
}

/// <summary>Fato observado na telemetria. Baixo volume, retenção longa.</summary>
public sealed class ProcessEvent
{
    public long Id { get; set; }
    public DateTimeOffset OcorridoEm { get; set; }
    public ProcessEventType Tipo { get; set; }
    public string? Detalhe { get; set; }
    public double? ValorAnterior { get; set; }
    public double? ValorNovo { get; set; }
}

/// <summary>
/// RESERVADA. A tabela existe, mas nada grava nela ainda: os estados solicitado/recebido/encaminhado/confirmado
/// dependem da análise do fluxo do Node-RED (ver docs/comandos-estados.md).
/// </summary>
public sealed class ControlCommand
{
    public Guid Id { get; set; }
    public DateTimeOffset SolicitadoEm { get; set; }
    public DateTimeOffset AtualizadoEm { get; set; }
    public string Estado { get; set; } = "";
    /// <summary>Campos do comando em JSON (jsonb).</summary>
    public string Campos { get; set; } = "{}";
    public string? Detalhe { get; set; }
}
