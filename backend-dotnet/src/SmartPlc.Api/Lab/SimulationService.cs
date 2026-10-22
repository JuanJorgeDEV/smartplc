using System.Globalization;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Lab;

public static class ExperimentSources
{
    public const string Clp = "clp";
    public const string Simulacao = "simulacao";

    public static readonly string[] Todas = [Clp, Simulacao];
}

public sealed record SimulationParameters(double NivelInicial, double Setpoint, double Kp, double Ti, double Td, double Velocidade);

/// <summary>Alterações que a pessoa faz durante a simulação. Campos nulos não mudam.</summary>
public sealed record SimulationCommand(
    double? Setpoint = null, bool? ModoManual = null, double? ValvulaManual = null,
    double? Kp = null, double? Ti = null, double? Td = null, double? Perturbacao = null, double? Velocidade = null)
{
    public bool Vazio => Setpoint is null && ModoManual is null && ValvulaManual is null && Kp is null && Ti is null && Td is null
                         && Perturbacao is null && Velocidade is null;
}

public sealed record SimulationState(
    double Decorrido, double Nivel, double Valvula, double Setpoint, bool Manual, double ValvulaManual,
    double Kp, double Ti, double Td, double Perturbacao, double Velocidade, bool Truncado);

public sealed record SimulationSnapshot(
    SimulationState Estado, DateTimeOffset Inicio, IReadOnlyList<TelemetrySample> Amostras, IReadOnlyList<ProcessEvent> Eventos);

/// <summary>
/// Executa a sessão de Laboratório em modo SIMULAÇÃO: uma por vez, em memória. Avança o <see cref="TankSimulator"/> em passos de 0,1 s
/// de tempo simulado e registra uma amostra por segundo simulado, além de uma amostra imediata a cada alteração feita pela pessoa.
/// O relógio da sessão é o tempo SIMULADO (Início + decorrido): com velocidade 5×, um minuto real vira cinco minutos de experimento.
/// Ao encerrar, a série vai para o banco (coluna SerieSimulada); nada disso toca o ProcessStateStore nem o histórico do CLP.
/// </summary>
public sealed class SimulationService(TimeProvider clock, ILogger<SimulationService> log) : BackgroundService
{
    public const double PassoSegundos = 0.1;
    public const double IntervaloAmostraSegundos = 1.0;
    public const int MaxAmostras = 5000;
    public const int MaxEventos = 500;
    public static readonly double[] Velocidades = [1, 2, 5, 10];

    private sealed class Execucao
    {
        public required Guid Id { get; init; }
        public required TankSimulator Sim { get; init; }
        public required DateTimeOffset Inicio { get; init; }
        public double Decorrido;
        public double ProximaAmostra;
        public double Velocidade = 1;
        public bool Truncado;
        public readonly List<TelemetrySample> Amostras = [];
        public readonly List<ProcessEvent> Eventos = [];
    }

    private readonly object _trava = new();
    private Execucao? _exec;

    public bool Ativa(Guid id) { lock (_trava) return _exec?.Id == id; }
    public Guid? IdAtiva { get { lock (_trava) return _exec?.Id; } }

    public SimulationState Iniciar(Guid id, DateTimeOffset inicio, SimulationParameters p)
    {
        lock (_trava)
        {
            if (_exec is not null) throw new InvalidOperationException("já existe uma simulação em andamento");
            var sim = new TankSimulator(p.NivelInicial, p.Setpoint, p.Kp, p.Ti, p.Td);
            _exec = new Execucao { Id = id, Sim = sim, Inicio = inicio, Velocidade = p.Velocidade };
            Registrar(_exec, SampleReason.Initial);
            _exec.ProximaAmostra = IntervaloAmostraSegundos;
            return Estado(_exec);
        }
    }

    /// <summary>Avança a simulação. Chamado pelo laço em segundo plano; os testes o chamam direto, sem esperar.</summary>
    public void Avancar(double segundosSimulados)
    {
        lock (_trava)
        {
            if (_exec is not { } x || segundosSimulados <= 0) return;
            var restante = segundosSimulados;
            while (restante > 1e-9)
            {
                var dt = Math.Min(PassoSegundos, restante);
                x.Sim.Passo(dt);
                x.Decorrido += dt;
                restante -= dt;
                if (x.Decorrido + 1e-9 >= x.ProximaAmostra)
                {
                    Registrar(x, SampleReason.Periodic);
                    x.ProximaAmostra += IntervaloAmostraSegundos;
                }
            }
        }
    }

    /// <summary>Aplica as alterações e grava os eventos correspondentes. Devolve o estado depois da alteração.</summary>
    public SimulationState Comandar(Guid id, SimulationCommand c)
    {
        lock (_trava)
        {
            if (_exec is not { } x || x.Id != id) throw new InvalidOperationException("simulação não está em andamento");
            var s = x.Sim;
            var mudou = false;

            if (c.Setpoint is { } sp && Math.Abs(sp - s.Setpoint) > 1e-9)
            {
                Evento(x, ProcessEventType.SetpointAlterado, $"SP {Fmt(s.Setpoint)} -> {Fmt(sp)}", s.Setpoint, sp);
                s.Setpoint = sp; mudou = true;
            }
            if (c.ModoManual is { } manual && manual != s.Manual)
            {
                Evento(x, ProcessEventType.ModoAlterado, manual ? "automático -> manual" : "manual -> automático", null, null);
                s.DefinirManual(manual); mudou = true;
            }
            if (c.ValvulaManual is { } vm && Math.Abs(vm - s.ValvulaManual) > 1e-9) { s.ValvulaManual = vm; mudou = true; }
            if (c.Kp is { } kp && Math.Abs(kp - s.Kp) > 1e-9) { Evento(x, ProcessEventType.PidAlterado, $"Kp {Fmt(s.Kp)} -> {Fmt(kp)}", s.Kp, kp); s.Kp = kp; mudou = true; }
            if (c.Ti is { } ti && Math.Abs(ti - s.Ti) > 1e-9) { Evento(x, ProcessEventType.PidAlterado, $"Ti {Fmt(s.Ti)} -> {Fmt(ti)}", s.Ti, ti); s.Ti = ti; mudou = true; }
            if (c.Td is { } td && Math.Abs(td - s.Td) > 1e-9) { Evento(x, ProcessEventType.PidAlterado, $"Td {Fmt(s.Td)} -> {Fmt(td)}", s.Td, td); s.Td = td; mudou = true; }
            if (c.Perturbacao is { } p && Math.Abs(p - s.Perturbacao) > 1e-9)
            {
                Evento(x, ProcessEventType.PerturbacaoSimulada, $"dreno {Fmt(s.Perturbacao)} -> {Fmt(p)}", s.Perturbacao, p);
                s.Perturbacao = p; mudou = true;
            }
            if (c.Velocidade is { } v) x.Velocidade = v;

            // Amostra imediata: a alteração fica visível no instante em que foi feita, não só no próximo segundo.
            if (mudou) Registrar(x, SampleReason.Event);
            return Estado(x);
        }
    }

    public SimulationSnapshot? Snapshot(Guid id)
    {
        lock (_trava)
        {
            if (_exec is not { } x || x.Id != id) return null;
            return new SimulationSnapshot(Estado(x), x.Inicio, [.. x.Amostras], [.. x.Eventos]);
        }
    }

    /// <summary>Para a simulação e devolve tudo o que ela produziu. Depois disso a execução deixa de existir.</summary>
    public SimulationSnapshot? Encerrar(Guid id)
    {
        lock (_trava)
        {
            if (_exec is not { } x || x.Id != id) return null;
            Registrar(x, SampleReason.Event);   // ponto final exato
            var snap = new SimulationSnapshot(Estado(x), x.Inicio, [.. x.Amostras], [.. x.Eventos]);
            _exec = null;
            return snap;
        }
    }

    /// <summary>Descarta a simulação sem guardar nada (usado quando a sessão é cancelada ou fica órfã).</summary>
    public void Descartar(Guid id) { lock (_trava) { if (_exec?.Id == id) _exec = null; } }

    /// <summary>Instante simulado arredondado ao milissegundo (evita 4,9999999 s por acúmulo de ponto flutuante).</summary>
    private static DateTimeOffset Instante(Execucao x) => x.Inicio.AddMilliseconds(Math.Round(x.Decorrido * 1000));

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static SimulationState Estado(Execucao x) => new(
        x.Decorrido, x.Sim.Nivel, x.Sim.Valvula, x.Sim.Setpoint, x.Sim.Manual, x.Sim.ValvulaManual,
        x.Sim.Kp, x.Sim.Ti, x.Sim.Td, x.Sim.Perturbacao, x.Velocidade, x.Truncado);

    private static void Registrar(Execucao x, SampleReason motivo)
    {
        if (x.Amostras.Count >= MaxAmostras) { x.Truncado = true; return; }
        x.Amostras.Add(new TelemetrySample
        {
            Id = x.Amostras.Count + 1,
            RecebidoEm = Instante(x),
            Nivel = x.Sim.Nivel,
            Valvula = x.Sim.Valvula,
            Setpoint = x.Sim.Setpoint,
            ModoManual = x.Sim.Manual,
            MotivoGravacao = motivo
        });
    }

    private static void Evento(Execucao x, ProcessEventType tipo, string detalhe, double? antes, double? depois)
    {
        if (x.Eventos.Count >= MaxEventos) return;
        x.Eventos.Add(new ProcessEvent
        {
            Id = x.Eventos.Count + 1, OcorridoEm = Instante(x), Tipo = tipo,
            Detalhe = detalhe, ValorAnterior = antes, ValorNovo = depois
        });
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        const int periodoMs = 200;
        var ultimo = clock.GetTimestamp();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(periodoMs), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var agora = clock.GetTimestamp();
                var real = clock.GetElapsedTime(ultimo, agora).TotalSeconds;
                ultimo = agora;
                double velocidade;
                lock (_trava) velocidade = _exec?.Velocidade ?? 1;
                // Limita o salto: se o processo ficou parado (pausa do host), não "recupera" minutos de uma vez.
                Avancar(Math.Min(real, 1.0) * velocidade);
            }
        }
        catch (OperationCanceledException) { /* encerrando */ }
        catch (Exception ex) { log.LogError(ex, "Falha no laço da simulação"); }
    }
}
