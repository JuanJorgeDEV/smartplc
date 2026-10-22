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
    /// <summary>Kp, Ti ou Td LIDOS do CLP mudaram (ou, numa sessão simulada, foram alterados pela pessoa).</summary>
    PidAlterado,
    /// <summary>Somente em sessões SIMULADAS do Laboratório: a pessoa abriu ou fechou o "dreno" de perturbação.</summary>
    PerturbacaoSimulada
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

/// <summary>
/// Sessão do Laboratório SmartPLC. Guarda só o enquadramento (quando, o quê, valores de partida e de chegada).
/// A telemetria da sessão NÃO é copiada: consulta-se telemetry_samples e process_events pela janela InicioEm..FimEm
/// (há um único processo simulado). Os campos *Inicial/*Final vêm do estado que o servidor recebeu do CLP, não do navegador.
/// </summary>
public sealed class ExperimentSession
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = "";
    /// <summary>respostaSetpoint | automaticoManual</summary>
    public string Tipo { get; set; } = "";
    /// <summary>ativa | encerrada | cancelada. No máximo uma sessão ativa (índice único parcial).</summary>
    public string Status { get; set; } = "";
    public DateTimeOffset InicioEm { get; set; }
    public DateTimeOffset? FimEm { get; set; }

    /// <summary>Estado da comunicação no início (exigido online para iniciar; guardado como evidência).</summary>
    public bool PlcOnlineInicial { get; set; }
    public double? NivelInicial { get; set; }
    public double? SetpointInicial { get; set; }
    public double? ValvulaInicial { get; set; }
    public bool? ModoManualInicial { get; set; }

    public double? NivelFinal { get; set; }
    public double? SetpointFinal { get; set; }
    public double? ValvulaFinal { get; set; }
    public bool? ModoManualFinal { get; set; }

    public string? Observacoes { get; set; }

    /// <summary>clp | simulacao. Sessões simuladas nunca usam o histórico do CLP.</summary>
    public string Fonte { get; set; } = "clp";

    /// <summary>
    /// Só em sessões simuladas encerradas: a série produzida (jsonb, ver <c>SimulatedSeries</c>). Em sessões do CLP fica nula,
    /// porque a telemetria é consultada no histórico pela janela da sessão.
    /// </summary>
    public string? SerieSimulada { get; set; }
}

/// <summary>
/// Usuário do sistema (funcionário). A senha nunca é guardada: só o hash PBKDF2 com sal. O e-mail é guardado em minúsculas e é único.
/// </summary>
public sealed class AppUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string Nome { get; set; } = "";
    public string SenhaHash { get; set; } = "";
    /// <summary>admin | operador | visualizador</summary>
    public string Papel { get; set; } = "visualizador";
    public bool Ativo { get; set; } = true;
    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset? UltimoLoginEm { get; set; }
    /// <summary>Tentativas erradas seguidas desde o último acesso bem-sucedido.</summary>
    public int FalhasConsecutivas { get; set; }
    public DateTimeOffset? BloqueadoAte { get; set; }
    /// <summary>Muda quando a senha, o papel ou o estado da conta mudam: derruba as sessões abertas.</summary>
    public Guid CarimboSeguranca { get; set; } = Guid.NewGuid();
}
