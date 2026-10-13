using SmartPlc.Api.Lab;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

/// <summary>
/// A análise do degrau é verificada contra respostas com solução analítica conhecida (primeira e segunda ordem),
/// e contra as situações em que NÃO deve calcular nada.
/// </summary>
public class ExperimentAnalyzerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
    private const double SegundosAteODegrau = 5;

    private static ExperimentSession Sessao(string tipo = ExperimentTypes.RespostaSetpoint) => new()
    {
        Id = Guid.NewGuid(), Nome = "t", Tipo = tipo, Status = ExperimentStatuses.Encerrada, InicioEm = T0, PlcOnlineInicial = true
    };

    private static TelemetrySample S(double seg, double pv, double sp, double cv = 40, bool? manual = false, SampleReason m = SampleReason.Periodic) =>
        new() { Id = (long)(seg * 1000) + 1, RecebidoEm = T0.AddSeconds(seg), Nivel = pv, Setpoint = sp, Valvula = cv, ModoManual = manual, MotivoGravacao = m };

    /// <summary>Amostra no instante do degrau (como a política grava) e a resposta amostrada a cada <paramref name="passo"/> s.</summary>
    private static (List<TelemetrySample> Amostras, SetpointChange Degrau) Degrau(
        Func<double, double> resposta, double spAnt, double spNovo, double pv0, double passo, double duracao)
    {
        var amostras = new List<TelemetrySample> { S(0, pv0, spAnt) };
        amostras.Add(S(SegundosAteODegrau, pv0, spNovo, m: SampleReason.Event));
        for (var t = passo; t <= duracao + 1e-9; t += passo)
            amostras.Add(S(SegundosAteODegrau + t, pv0 + (spNovo - pv0) * resposta(t), spNovo));
        return (amostras, new SetpointChange(T0.AddSeconds(SegundosAteODegrau), spAnt, spNovo));
    }

    private static StepResponse Analisar(List<TelemetrySample> a, params SetpointChange[] mudancas) =>
        ExperimentAnalyzer.AnalisarDegrau(a, mudancas, T0.AddSeconds(a[^1].RecebidoEm.Subtract(T0).TotalSeconds + 1));

    // ---------- Resposta de primeira ordem: PV = pv0 + Δ(1 - e^(-t/τ)) ----------

    [Fact]
    public void Primeira_ordem_t63_igual_a_constante_de_tempo()
    {
        var (a, d) = Degrau(t => 1 - Math.Exp(-t / 20.0), 30, 60, 30, 1, 150);
        var r = Analisar(a, d);

        Assert.True(r.Calculada, r.Motivo);
        Assert.InRange(r.SegundosAteT63!.Value, 19.5, 20.5);                        // τ = 20 s
        Assert.InRange(r.SegundosT10aT90!.Value, 43.0, 45.0);                       // τ·(ln 10 − ln(10/9)) = 43,95 s
        Assert.Equal(0, r.SobreelevacaoPercentual!.Value, 1);                       // sem sobre-elevação
        Assert.InRange(r.SegundosAcomodacao!.Value, 77.0, 80.0);                    // τ·ln 50 = 78,2 s (banda de ±2 %)
        Assert.Equal(1, r.ResolucaoMedianaSegundos!.Value, 3);
        Assert.Equal(30, r.PvAntesDoDegrau);
    }

    [Fact]
    public void Degrau_para_baixo_tambem_e_analisado()
    {
        var (a, d) = Degrau(t => 1 - Math.Exp(-t / 15.0), 70, 40, 70, 1, 120);
        var r = Analisar(a, d);
        Assert.True(r.Calculada, r.Motivo);
        Assert.InRange(r.SegundosAteT63!.Value, 14.5, 15.5);
        Assert.Equal(0, r.SobreelevacaoPercentual!.Value, 1);
    }

    // ---------- Segunda ordem sub-amortecida: sobre-elevação analítica = exp(-πζ/√(1-ζ²)) ----------

    [Fact]
    public void Segunda_ordem_subamortecida_reproduz_a_sobreelevacao_teorica()
    {
        const double zeta = 0.4, wn = 0.2;
        var wd = wn * Math.Sqrt(1 - zeta * zeta);
        var fi = Math.Acos(zeta);
        double Y(double t) => 1 - Math.Exp(-zeta * wn * t) / Math.Sqrt(1 - zeta * zeta) * Math.Sin(wd * t + fi);

        var (a, d) = Degrau(Y, 20, 60, 20, 0.5, 200);
        var r = Analisar(a, d);

        var teorico = Math.Exp(-Math.PI * zeta / Math.Sqrt(1 - zeta * zeta)) * 100; // ≈ 25,4 %
        Assert.True(r.Calculada, r.Motivo);
        Assert.InRange(r.SobreelevacaoPercentual!.Value, teorico - 1.5, teorico + 1.5);
        Assert.NotNull(r.SegundosAcomodacao);
    }

    // ---------- Quando NÃO calcular ----------

    [Fact]
    public void Sem_alteracao_de_setpoint_nao_calcula()
    {
        var r = Analisar([S(0, 30, 30), S(10, 31, 30)]);
        Assert.False(r.Calculada);
        Assert.Contains("nenhuma alteração de setpoint", r.Motivo);
    }

    [Fact]
    public void Degrau_pequeno_nao_calcula()
    {
        var (a, d) = Degrau(t => 1 - Math.Exp(-t / 10.0), 50, 53, 50, 1, 60);
        var r = Analisar(a, d);
        Assert.False(r.Calculada);
        Assert.Contains("menor que 5", r.Motivo);
    }

    [Fact]
    public void PV_ja_proxima_do_novo_setpoint_nao_calcula()
    {
        var (a, d) = Degrau(t => 1 - Math.Exp(-t / 10.0), 30, 60, 57, 1, 60); // PV já em 57
        var r = Analisar(a, d);
        Assert.False(r.Calculada);
        Assert.Contains("já estava a menos de", r.Motivo);
    }

    [Fact]
    public void Poucas_amostras_apos_o_degrau_nao_calcula()
    {
        var (a, d) = Degrau(t => 1 - Math.Exp(-t / 10.0), 30, 60, 30, 1, 4); // só 4 amostras
        var r = Analisar(a, d);
        Assert.False(r.Calculada);
        Assert.Contains("amostras após o degrau", r.Motivo);
    }

    [Fact]
    public void Resolucao_temporal_ruim_nao_calcula_e_informa_a_mediana()
    {
        var (a, d) = Degrau(t => 1 - Math.Exp(-t / 40.0), 30, 60, 30, 15, 300); // uma amostra a cada 15 s
        var r = Analisar(a, d);
        Assert.False(r.Calculada);
        Assert.Contains("resolução temporal insuficiente", r.Motivo);
        Assert.Equal(15, r.ResolucaoMedianaSegundos!.Value, 3);
        Assert.Null(r.SegundosAteT63);
    }

    [Fact]
    public void Amostragem_esparsa_mas_aceitavel_omite_so_o_que_nao_e_confiavel()
    {
        // τ = 4 s com amostras a cada 10 s: o cruzamento de 63 % ocorre na 1ª amostra (< 3 amostras até lá)
        var (a, d) = Degrau(t => 1 - Math.Exp(-t / 4.0), 30, 60, 30, 10, 100);
        var r = Analisar(a, d);
        Assert.True(r.Calculada, r.Motivo);
        Assert.Null(r.SegundosAteT63);
        Assert.Null(r.SegundosT10aT90);
        Assert.Contains(r.Observacoes, o => o.Contains("63"));
    }

    [Fact]
    public void PV_que_nao_chega_ao_novo_setpoint_nao_informa_sobreelevacao()
    {
        var (a, d) = Degrau(t => 1 - Math.Exp(-t / 1000.0), 30, 60, 30, 1, 60); // muito lenta
        var r = Analisar(a, d);
        Assert.True(r.Calculada, r.Motivo);
        Assert.Null(r.SobreelevacaoPercentual);
        Assert.Null(r.SegundosAcomodacao);
        Assert.Contains(r.Observacoes, o => o.Contains("não chegou"));
    }

    [Fact]
    public void Oscilacao_sustentada_nao_informa_acomodacao()
    {
        var (a, d) = Degrau(t => 1 + 0.2 * Math.Sin(t / 3.0) * (t > 20 ? 1 : 0) - Math.Exp(-t / 5.0), 30, 60, 30, 1, 120);
        var r = Analisar(a, d);
        Assert.True(r.Calculada, r.Motivo);
        Assert.Null(r.SegundosAcomodacao);
        Assert.Contains(r.Observacoes, o => o.Contains("estabilizou"));
    }

    [Fact]
    public void Sem_amostra_anterior_ao_degrau_nao_calcula()
    {
        var degrau = new SetpointChange(T0.AddSeconds(1), 30, 60);
        var depois = Enumerable.Range(2, 20).Select(i => S(i, 30 + i, 60)).ToList();
        var r = Analisar(depois, degrau);
        Assert.False(r.Calculada);
        Assert.Contains("não há amostra", r.Motivo);
    }

    [Fact]
    public void Varios_degraus_analisa_so_o_primeiro_e_avisa()
    {
        var (a, d) = Degrau(t => 1 - Math.Exp(-t / 20.0), 30, 60, 30, 1, 100);
        var segundo = new SetpointChange(T0.AddSeconds(90), 60, 20);
        var r = ExperimentAnalyzer.AnalisarDegrau(a, [d, segundo], T0.AddSeconds(200));
        Assert.True(r.Calculada, r.Motivo);
        Assert.Equal(d, r.Degrau);
        Assert.Contains(r.Observacoes, o => o.Contains("apenas a primeira"));
    }

    [Fact]
    public void Evento_de_setpoint_sem_valores_nao_calcula()
    {
        var r = ExperimentAnalyzer.AnalisarDegrau([S(0, 30, 30)], [new SetpointChange(T0.AddSeconds(1), null, 60)], T0.AddSeconds(10));
        Assert.False(r.Calculada);
    }

    // ---------- Resumo ----------

    [Fact]
    public void Resumo_usa_valores_do_inicio_e_do_fim_da_sessao_e_min_max_das_amostras()
    {
        var s = Sessao();
        s.NivelInicial = 31; s.NivelFinal = 59; s.SetpointInicial = 30; s.SetpointFinal = 60;
        var amostras = new List<TelemetrySample> { S(0, 32, 30), S(10, 45, 60), S(20, 58, 60) };
        var ev = new List<ProcessEvent> { new() { Tipo = ProcessEventType.SetpointAlterado, OcorridoEm = T0.AddSeconds(5), ValorAnterior = 30, ValorNovo = 60 } };

        var r = ExperimentAnalyzer.Resumir(s, T0.AddSeconds(25), amostras, ev, false);

        Assert.Equal(25, r.DuracaoSegundos);
        Assert.Equal(3, r.Amostras);
        Assert.Equal(new ValueRange(31, 59, 32, 58), r.Pv);
        Assert.Equal(30, r.Sp.Inicial);
        Assert.Equal(60, r.Sp.Final);
        Assert.Single(r.MudancasDeSetpoint);
        Assert.False(r.Truncado);
    }

    [Fact]
    public void Resumo_sem_amostras_nao_inventa_valores()
    {
        var s = Sessao();
        s.PlcOnlineInicial = true;
        var r = ExperimentAnalyzer.Resumir(s, T0.AddSeconds(30), [], [], false);
        Assert.Equal(0, r.Amostras);
        Assert.Null(r.Pv.Min);
        Assert.Null(r.Pv.Max);
        Assert.Null(r.Pv.Inicial);
        Assert.NotNull(r.Resposta);
        Assert.False(r.Resposta!.Calculada);
    }

    [Fact]
    public void Automatico_versus_manual_integra_o_tempo_em_cada_modo()
    {
        var s = Sessao(ExperimentTypes.AutomaticoManual);
        var amostras = new List<TelemetrySample>
        {
            S(0, 50, 50, cv: 40, manual: false),
            S(30, 52, 50, cv: 20, manual: false),   // 0..60 s em automático (cv 40 por 30 s e 20 por 30 s)
            S(60, 55, 50, cv: 70, manual: true),    // 60..100 s em manual
            S(90, 60, 50, cv: 70, manual: true)
        };
        var ev = new List<ProcessEvent>
        {
            new() { Tipo = ProcessEventType.ModoAlterado, OcorridoEm = T0.AddSeconds(60), Detalhe = "modo lido do CLP: automático -> manual" }
        };

        var r = ExperimentAnalyzer.Resumir(s, T0.AddSeconds(100), amostras, ev, false);

        Assert.Equal(60, r.Automatico!.Segundos, 3);
        Assert.Equal(40, r.Manual!.Segundos, 3);
        Assert.Equal(30, r.Automatico.CvMedio!.Value, 3);
        Assert.Equal(70, r.Manual.CvMedio!.Value, 3);
        Assert.Equal(50, r.Automatico.PvMin!.Value);
        Assert.Equal(52, r.Automatico.PvMax!.Value);
        Assert.Equal(55, r.Manual.PvMin!.Value);
        Assert.Equal(60, r.Manual.PvMax!.Value);
        var mudanca = Assert.Single(r.MudancasDeModo);
        Assert.True(mudanca.ParaManual);
        Assert.Null(r.Resposta); // análise de degrau não se aplica a este tipo
    }

    [Fact]
    public void Amostras_sem_informacao_de_modo_nao_entram_na_contagem_por_modo()
    {
        var s = Sessao(ExperimentTypes.AutomaticoManual);
        var r = ExperimentAnalyzer.Resumir(s, T0.AddSeconds(30), [S(0, 50, 50, manual: null), S(10, 51, 50, manual: null)], [], false);
        Assert.Null(r.Automatico);
        Assert.Null(r.Manual);
    }

    // ---------- Série para gráfico ----------

    [Fact]
    public void Serie_curta_passa_inteira_e_em_ordem()
    {
        var pontos = ExperimentAnalyzer.Serie([S(10, 2, 0), S(0, 1, 0), S(5, 3, 0)]);
        Assert.Equal([1.0, 3, 2], pontos.Select(p => p.Pv));
    }

    [Fact]
    public void Serie_longa_e_reduzida_preservando_extremos_e_picos()
    {
        var amostras = Enumerable.Range(0, 10_000).Select(i => S(i, 50 + 10 * Math.Sin(i / 50.0), 50)).ToList();
        amostras[3333] = S(3333, 99, 50);   // pico isolado
        amostras[7777] = S(7777, 1, 50);    // vale isolado

        var pontos = ExperimentAnalyzer.Serie(amostras, 600);

        Assert.True(pontos.Count <= 600, $"{pontos.Count} pontos");
        Assert.Equal(99, pontos.Max(p => p.Pv));
        Assert.Equal(1, pontos.Min(p => p.Pv));
        Assert.Equal(amostras[0].RecebidoEm, pontos[0].T);
        Assert.Equal(amostras[^1].RecebidoEm, pontos[^1].T);
        Assert.Equal(pontos.OrderBy(p => p.T).Select(p => p.T), pontos.Select(p => p.T)); // continua em ordem
    }
}
