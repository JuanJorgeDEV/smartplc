using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Lab;

public static class ExperimentTypes
{
    public const string RespostaSetpoint = "respostaSetpoint";
    public const string AutomaticoManual = "automaticoManual";

    public static readonly string[] Todos = [RespostaSetpoint, AutomaticoManual];

    public static string Rotulo(string tipo) => tipo switch
    {
        RespostaSetpoint => "Resposta ao setpoint",
        AutomaticoManual => "Automático versus manual",
        _ => tipo
    };
}

public static class ExperimentStatuses
{
    public const string Ativa = "ativa";
    public const string Encerrada = "encerrada";
    public const string Cancelada = "cancelada";
}

public sealed class ExperimentConflictException(string message, Guid? sessaoAtiva = null) : Exception(message)
{
    public Guid? SessaoAtiva { get; } = sessaoAtiva;
}

public sealed record SessionDto(
    Guid Id, string Nome, string Tipo, string TipoRotulo, string Status,
    DateTimeOffset InicioEm, DateTimeOffset? FimEm,
    bool PlcOnlineInicial,
    double? NivelInicial, double? SetpointInicial, double? ValvulaInicial, bool? ModoManualInicial,
    double? NivelFinal, double? SetpointFinal, double? ValvulaFinal, bool? ModoManualFinal,
    string? Observacoes)
{
    public static SessionDto De(ExperimentSession s) => new(
        s.Id, s.Nome, s.Tipo, ExperimentTypes.Rotulo(s.Tipo), s.Status, s.InicioEm, s.FimEm, s.PlcOnlineInicial,
        s.NivelInicial, s.SetpointInicial, s.ValvulaInicial, s.ModoManualInicial,
        s.NivelFinal, s.SetpointFinal, s.ValvulaFinal, s.ModoManualFinal, s.Observacoes);
}

public sealed record ValueRange(double? Inicial, double? Final, double? Min, double? Max);

public sealed record SetpointChange(DateTimeOffset T, double? De, double? Para);
public sealed record ModeChange(DateTimeOffset T, bool? ParaManual, string Detalhe);

public sealed record ModeStats(double Segundos, double? PvMin, double? PvMax, double? CvMedio);

public sealed record StepResponse(
    bool Calculada,
    string? Motivo,
    SetpointChange? Degrau,
    double? PvAntesDoDegrau,
    double? SegundosAteT63,
    double? SegundosT10aT90,
    double? SobreelevacaoPercentual,
    double? SegundosAcomodacao,
    double? ResolucaoMedianaSegundos,
    IReadOnlyList<string> Observacoes);

public sealed record SeriesPoint(DateTimeOffset T, double Pv, double? Sp, double Cv, bool? Manual);

public sealed record ExperimentSummary(
    double DuracaoSegundos,
    int Amostras,
    bool Truncado,
    ValueRange Pv,
    ValueRange Sp,
    ValueRange Cv,
    IReadOnlyList<SetpointChange> MudancasDeSetpoint,
    IReadOnlyList<ModeChange> MudancasDeModo,
    ModeStats? Automatico,
    ModeStats? Manual,
    StepResponse? Resposta);
