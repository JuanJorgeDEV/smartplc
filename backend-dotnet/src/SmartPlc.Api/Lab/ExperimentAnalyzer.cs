using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Lab;

/// <summary>
/// Calcula o resumo de uma sessão a partir das amostras e eventos JÁ persistidos (nenhum dado é inventado).
/// As métricas da resposta ao degrau só são devolvidas quando os critérios abaixo são atendidos; caso contrário
/// o campo vem nulo e o motivo é explicado. Critérios documentados em docs/laboratorio.md.
/// </summary>
public static class ExperimentAnalyzer
{
    // Critérios (constantes públicas para os testes e a documentação citarem os mesmos números)
    public const double DegrauMinimo = 5.0;            // pontos percentuais entre o SP antigo e o novo
    public const double DistanciaMinimaAoAlvo = 5.0;   // |SP novo - PV antes do degrau|
    public const int AmostrasMinimasPosDegrau = 6;
    public const int AmostrasMinimasAteT63 = 3;
    public const int AmostrasMinimasAteT90 = 5;
    public const double ResolucaoMaximaSegundos = 10.0; // mediana do intervalo entre amostras após o degrau
    public const double BandaAcomodacaoFracao = 0.02;   // ±2 % do degrau efetivo
    public const double BandaAcomodacaoMinima = 0.5;    // pontos percentuais
    public const int AmostrasMinimasNaBanda = 3;

    public static ExperimentSummary Resumir(
        ExperimentSession sessao,
        DateTimeOffset fim,
        IReadOnlyList<TelemetrySample> amostras,
        IReadOnlyList<ProcessEvent> eventos,
        bool truncado)
    {
        var ordenadas = amostras.OrderBy(a => a.RecebidoEm).ThenBy(a => a.Id).ToList();

        var mudancasSp = eventos.Where(e => e.Tipo == ProcessEventType.SetpointAlterado)
            .OrderBy(e => e.OcorridoEm)
            .Select(e => new SetpointChange(e.OcorridoEm, e.ValorAnterior, e.ValorNovo)).ToList();
        var mudancasModo = eventos.Where(e => e.Tipo == ProcessEventType.ModoAlterado)
            .OrderBy(e => e.OcorridoEm)
            .Select(e => new ModeChange(e.OcorridoEm, e.Detalhe?.EndsWith("-> manual", StringComparison.Ordinal), e.Detalhe ?? "")).ToList();

        var pv = Faixa(ordenadas, a => a.Nivel, sessao.NivelInicial, sessao.NivelFinal);
        var sp = Faixa(ordenadas, a => a.Setpoint, sessao.SetpointInicial, sessao.SetpointFinal);
        var cv = Faixa(ordenadas, a => a.Valvula, sessao.ValvulaInicial, sessao.ValvulaFinal);

        ModeStats? auto = null, manual = null;
        if (sessao.Tipo == ExperimentTypes.AutomaticoManual)
            (auto, manual) = PorModo(ordenadas, sessao.InicioEm, fim);

        StepResponse? resposta = null;
        if (sessao.Tipo == ExperimentTypes.RespostaSetpoint)
            resposta = AnalisarDegrau(ordenadas, mudancasSp, fim);

        return new ExperimentSummary(
            Math.Max(0, (fim - sessao.InicioEm).TotalSeconds), ordenadas.Count, truncado,
            pv, sp, cv, mudancasSp, mudancasModo, auto, manual, resposta);
    }

    private static ValueRange Faixa(List<TelemetrySample> amostras, Func<TelemetrySample, double?> f, double? inicial, double? final)
    {
        var valores = amostras.Select(f).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return new ValueRange(
            inicial ?? valores.Cast<double?>().FirstOrDefault(),
            final ?? valores.Cast<double?>().LastOrDefault(),
            valores.Count > 0 ? valores.Min() : null,
            valores.Count > 0 ? valores.Max() : null);
    }

    /// <summary>Tempo em cada modo, integrando os intervalos entre amostras (o modo de uma amostra vale até a próxima).</summary>
    private static (ModeStats? Auto, ModeStats? Manual) PorModo(List<TelemetrySample> amostras, DateTimeOffset inicio, DateTimeOffset fim)
    {
        double tAuto = 0, tManual = 0, cvAutoPeso = 0, cvManualPeso = 0;
        double? pvMinA = null, pvMaxA = null, pvMinM = null, pvMaxM = null;

        for (var i = 0; i < amostras.Count; i++)
        {
            var a = amostras[i];
            if (a.ModoManual is not { } manual) continue;
            var de = a.RecebidoEm < inicio ? inicio : a.RecebidoEm;
            var ate = i + 1 < amostras.Count ? amostras[i + 1].RecebidoEm : fim;
            var dt = Math.Max(0, (ate - de).TotalSeconds);

            if (manual) { tManual += dt; cvManualPeso += a.Valvula * dt; pvMinM = Min(pvMinM, a.Nivel); pvMaxM = Max(pvMaxM, a.Nivel); }
            else { tAuto += dt; cvAutoPeso += a.Valvula * dt; pvMinA = Min(pvMinA, a.Nivel); pvMaxA = Max(pvMaxA, a.Nivel); }
        }

        return (
            tAuto > 0 || pvMinA is not null ? new ModeStats(tAuto, pvMinA, pvMaxA, tAuto > 0 ? cvAutoPeso / tAuto : null) : null,
            tManual > 0 || pvMinM is not null ? new ModeStats(tManual, pvMinM, pvMaxM, tManual > 0 ? cvManualPeso / tManual : null) : null);
    }

    private static double Min(double? a, double b) => a is { } x ? Math.Min(x, b) : b;
    private static double Max(double? a, double b) => a is { } x ? Math.Max(x, b) : b;

    /// <summary>Resposta ao PRIMEIRO degrau de setpoint observado na janela.</summary>
    public static StepResponse AnalisarDegrau(List<TelemetrySample> amostras, IReadOnlyList<SetpointChange> mudancas, DateTimeOffset fim)
    {
        var obs = new List<string>();
        if (mudancas.Count == 0)
            return Nao("nenhuma alteração de setpoint foi observada na telemetria durante a sessão", null, null, obs);

        var degrau = mudancas[0];
        if (degrau.De is not { } spAnt || degrau.Para is not { } spNovo)
            return Nao("a alteração de setpoint não trouxe os valores antigo e novo", degrau, null, obs);
        if (mudancas.Count > 1)
            obs.Add($"houve {mudancas.Count} alterações de setpoint na janela; a análise considera apenas a primeira");

        if (Math.Abs(spNovo - spAnt) < DegrauMinimo)
            return Nao($"degrau de setpoint menor que {DegrauMinimo:0} pontos ({Math.Abs(spNovo - spAnt):0.##})", degrau, null, obs);

        var antes = amostras.LastOrDefault(a => a.RecebidoEm <= degrau.T);
        if (antes is null)
            return Nao("não há amostra de PV no instante do degrau ou antes dele", degrau, null, obs);

        var pv0 = antes.Nivel;
        var distancia = spNovo - pv0;
        if (Math.Abs(distancia) < DistanciaMinimaAoAlvo)
            return Nao($"a PV ({pv0:0.##}) já estava a menos de {DistanciaMinimaAoAlvo:0} pontos do novo setpoint", degrau, pv0, obs);

        var depois = amostras.Where(a => a.RecebidoEm > degrau.T && a.RecebidoEm <= fim).ToList();
        if (depois.Count < AmostrasMinimasPosDegrau)
            return Nao($"apenas {depois.Count} amostras após o degrau (mínimo {AmostrasMinimasPosDegrau})", degrau, pv0, obs);

        var intervalos = depois.Zip(depois.Skip(1), (a, b) => (b.RecebidoEm - a.RecebidoEm).TotalSeconds).OrderBy(x => x).ToList();
        var resolucao = intervalos.Count > 0 ? intervalos[intervalos.Count / 2] : double.NaN;
        if (double.IsNaN(resolucao) || resolucao > ResolucaoMaximaSegundos)
            return new StepResponse(false, $"resolução temporal insuficiente (mediana de {resolucao:0.#} s entre amostras; máximo {ResolucaoMaximaSegundos:0} s)",
                degrau, pv0, null, null, null, null, double.IsNaN(resolucao) ? null : resolucao, obs);

        // Progresso normalizado: 0 = PV antes do degrau, 1 = novo setpoint.
        var serie = depois.Select(a => (T: (a.RecebidoEm - degrau.T).TotalSeconds, P: (a.Nivel - pv0) / distancia, Pv: a.Nivel)).ToList();

        double? Cruzamento(double fracao, out int amostrasAte)
        {
            amostrasAte = 0;
            for (var i = 0; i < serie.Count; i++)
            {
                if (serie[i].P < fracao) continue;
                amostrasAte = i + 1;
                if (i == 0) return serie[0].T;
                var (t0, p0, _) = serie[i - 1];
                var (t1, p1, _) = serie[i];
                return p1 == p0 ? t1 : t0 + (fracao - p0) / (p1 - p0) * (t1 - t0); // interpolação linear entre as duas amostras
            }
            return null;
        }

        double? t63 = null, subida = null;
        var t63v = Cruzamento(0.632, out var n63);
        if (t63v is { } v63)
        {
            if (n63 >= AmostrasMinimasAteT63) t63 = v63;
            else obs.Add($"tempo até 63 % não informado: só {n63} amostras até o cruzamento (mínimo {AmostrasMinimasAteT63})");
        }

        var t10 = Cruzamento(0.10, out _);
        var t90 = Cruzamento(0.90, out var n90);
        if (t10 is { } a10 && t90 is { } a90)
        {
            if (n90 >= AmostrasMinimasAteT90) subida = a90 - a10;
            else obs.Add($"tempo de subida 10–90 % não informado: só {n90} amostras até 90 % (mínimo {AmostrasMinimasAteT90})");
        }

        // Banda de acomodação (±2 % do degrau efetivo, no mínimo 0,5 ponto).
        var banda = Math.Max(BandaAcomodacaoMinima, BandaAcomodacaoFracao * Math.Abs(distancia));

        // Sobre-elevação: só faz sentido se a PV chegou ao novo setpoint. Uma resposta sem sobre-elevação
        // se aproxima do SP sem nunca tocá-lo, então "chegou" significa entrar na banda de acomodação.
        double? sobre = null;
        var alcancou = serie.Any(s => s.P >= 1.0 || Math.Abs(s.Pv - spNovo) <= banda);
        if (alcancou)
        {
            var pico = serie.Max(s => s.P);
            sobre = Math.Max(0, pico - 1.0) * 100.0;
            if (intervalos.Count > 0 && intervalos[^1] > 2 * ResolucaoMaximaSegundos)
                obs.Add("há intervalos longos entre amostras; o pico real pode ser maior que o observado");
        }
        else obs.Add("a PV não chegou ao novo setpoint dentro da janela: sobre-elevação não calculada");

        // Acomodação: último instante fora da banda; exige permanência final dentro dela.
        double? acomodacao = null;
        var ultimaFora = -1;
        for (var i = 0; i < serie.Count; i++)
            if (Math.Abs(serie[i].Pv - spNovo) > banda) ultimaFora = i;
        var dentroNoFim = serie.Count - 1 - ultimaFora;
        if (ultimaFora < serie.Count - 1 && dentroNoFim >= AmostrasMinimasNaBanda)
            acomodacao = serie[ultimaFora + 1].T;
        else obs.Add($"ainda não estabilizou na banda de ±{banda:0.##} pontos do setpoint (precisa de {AmostrasMinimasNaBanda} amostras seguidas dentro dela)");

        return new StepResponse(true, null, degrau, pv0, t63, subida, sobre, acomodacao, resolucao, obs);
    }

    private static StepResponse Nao(string motivo, SetpointChange? degrau, double? pv0, List<string> obs) =>
        new(false, motivo, degrau, pv0, null, null, null, null, null, obs);

    /// <summary>
    /// Reduz a série a no máximo <paramref name="max"/> pontos preservando picos: em cada faixa mantém a primeira, a última,
    /// a de menor e a de maior PV (na ordem do tempo).
    /// </summary>
    public static List<SeriesPoint> Serie(IReadOnlyList<TelemetrySample> amostras, int max = 600)
    {
        var pontos = amostras.OrderBy(a => a.RecebidoEm).ThenBy(a => a.Id)
            .Select(a => new SeriesPoint(a.RecebidoEm, a.Nivel, a.Setpoint, a.Valvula, a.ModoManual)).ToList();
        if (pontos.Count <= max) return pontos;

        var faixas = Math.Max(1, max / 4);
        var saida = new SortedSet<int>();
        for (var f = 0; f < faixas; f++)
        {
            var ini = f * pontos.Count / faixas;
            var fimIdx = (f + 1) * pontos.Count / faixas;
            if (fimIdx <= ini) continue;
            var janela = Enumerable.Range(ini, fimIdx - ini).ToList();
            saida.Add(janela.First());
            saida.Add(janela.Last());
            saida.Add(janela.MinBy(i => pontos[i].Pv));
            saida.Add(janela.MaxBy(i => pontos[i].Pv));
        }
        return saida.Select(i => pontos[i]).ToList();
    }
}
