namespace SmartPlc.Api.Lab;

/// <summary>
/// Modelo simples do tanque TQ-101 para o Laboratório em modo SIMULAÇÃO (não depende de CLP nem de Node-RED).
///
/// Processo:  dh/dt = 0,036·u − 0,2547·√h − 0,018·p     (h = nível %, u = válvula %, p = perturbação 0–100)
///   · o termo 0,2547·√h é a vazão de saída pela gravidade; em h = 50 % e u ≈ 50 % o tanque fica em equilíbrio;
///   · p é uma saída extra (um "dreno" que o aluno pode abrir) e vale até ≈ metade da saída nominal.
/// Controlador PID posicional com anti-windup (integração suspensa quando a saída satura) e transferência sem salto
/// de manual para automático. Ti = 0 desliga a ação integral e Td = 0 a derivativa.
///
/// É determinístico (sem ruído aleatório): o mesmo roteiro produz sempre a mesma curva.
/// Os dados simulados são SEMPRE rotulados como simulação e nunca se misturam com o histórico do CLP.
/// </summary>
public sealed class TankSimulator
{
    public const double GanhoEntrada = 0.036;
    public const double GanhoSaida = 0.2547;
    public const double GanhoPerturbacao = 0.018;

    public double Nivel { get; private set; }
    public double Valvula { get; private set; }
    public double Setpoint { get; set; }
    public bool Manual { get; private set; }
    public double ValvulaManual { get; set; }
    public double Kp { get; set; }
    public double Ti { get; set; }
    public double Td { get; set; }
    public double Perturbacao { get; set; }

    private double _integral;
    private double _erroAnterior;
    private bool _temErroAnterior;

    public TankSimulator(double nivelInicial = 30, double setpoint = 30, double kp = 2, double ti = 30, double td = 0)
    {
        Nivel = Math.Clamp(nivelInicial, 0, 100);
        Setpoint = Math.Clamp(setpoint, 0, 100);
        Kp = kp; Ti = ti; Td = td;
        ValvulaManual = 0;
        // Começa em regime: a válvula inicial é a que mantém o nível parado, e o integrador já a contém.
        Valvula = _integral = Math.Clamp(Equilibrio(Nivel), 0, 100);
    }

    /// <summary>Abertura da válvula (%) que mantém o nível h parado, sem perturbação.</summary>
    public static double Equilibrio(double nivel) => GanhoSaida * Math.Sqrt(Math.Max(nivel, 0)) / GanhoEntrada;

    public void DefinirManual(bool manual)
    {
        if (manual == Manual) return;
        Manual = manual;
        if (manual)
        {
            // O manual parte da abertura em que a válvula já estava (sem salto).
            ValvulaManual = Valvula;
        }
        else
        {
            // Volta ao automático sem salto: o integrador assume a diferença entre a válvula atual e o termo proporcional.
            var e = Setpoint - Nivel;
            _integral = Valvula - Kp * e;   // sem limitar: o termo P pode ser negativo e o integrador precisa compensá-lo
            _temErroAnterior = false;
        }
    }

    /// <summary>Avança a simulação em <paramref name="dt"/> segundos (use passos pequenos, como 0,1 s).</summary>
    public void Passo(double dt)
    {
        double u;
        if (Manual)
        {
            u = Math.Clamp(ValvulaManual, 0, 100);
        }
        else
        {
            var e = Setpoint - Nivel;
            var p = Kp * e;
            var d = Td > 0 && _temErroAnterior ? Kp * Td * (e - _erroAnterior) / dt : 0;
            var incremento = Ti > 0 ? Kp * e * dt / Ti : 0;
            var semSaturar = p + _integral + incremento + d;
            u = Math.Clamp(semSaturar, 0, 100);
            // Anti-windup: só integra quando não satura, ou quando o erro afasta a saída do limite.
            var saturaAlto = semSaturar > 100 && incremento > 0;
            var saturaBaixo = semSaturar < 0 && incremento < 0;
            if (!saturaAlto && !saturaBaixo) _integral += incremento;
            u = Math.Clamp(p + _integral + d, 0, 100);
            _erroAnterior = e;
            _temErroAnterior = true;
        }
        Valvula = u;

        var dh = GanhoEntrada * u - GanhoSaida * Math.Sqrt(Math.Max(Nivel, 0)) - GanhoPerturbacao * Math.Clamp(Perturbacao, 0, 100);
        Nivel = Math.Clamp(Nivel + dh * dt, 0, 100);
    }
}
