using Microsoft.Extensions.Options;
using SmartPlc.Api.History;
using SmartPlc.Api.Options;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

/// <summary>Regras de armazenamento: puras, sem banco e sem relógio real.</summary>
public class HistoryPolicyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

    private static HistoryPolicy Criar(Action<HistoryOptions>? ajustar = null)
    {
        var h = new HistoryOptions { SampleIntervalSeconds = 10, MinimumLevelChange = 0.5, MinimumValveChange = 2.0 };
        ajustar?.Invoke(h);
        return new HistoryPolicy(Microsoft.Extensions.Options.Options.Create(h), Microsoft.Extensions.Options.Options.Create(new ProcessOptions()));
    }

    private static Reading L(double seg, double nivel = 50, double valvula = 40, double? sp = null, bool? manual = null,
        double? kp = null, double? ti = null, double? td = null, DateTimeOffset? adquirido = null) =>
        new(T0.AddSeconds(seg), adquirido, nivel, valvula, sp, manual, kp, ti, td);

    // ---------- A) amostragem periódica ----------

    [Fact]
    public void Primeira_leitura_vira_amostra_inicial()
    {
        var d = Criar().Evaluate(L(0));
        Assert.Equal(SampleReason.Initial, d.Sample!.MotivoGravacao);
        Assert.Equal(T0, d.Sample.RecebidoEm);
    }

    [Fact]
    public void Sem_mudanca_e_dentro_do_intervalo_nada_e_gravado()
    {
        var p = Criar();
        p.Evaluate(L(0));
        for (var s = 1; s < 10; s++) Assert.True(p.Evaluate(L(s)).IsEmpty, $"t={s}s");
    }

    [Fact]
    public void Passado_o_intervalo_grava_amostra_periodica_somente_quando_chega_leitura()
    {
        var p = Criar();
        p.Evaluate(L(0));
        Assert.True(p.Evaluate(L(9.99)).IsEmpty);
        var d = p.Evaluate(L(10));
        Assert.Equal(SampleReason.Periodic, d.Sample!.MotivoGravacao);
        Assert.True(p.Evaluate(L(15)).IsEmpty); // referência avançou para t=10
        Assert.Equal(SampleReason.Periodic, p.Evaluate(L(20)).Sample!.MotivoGravacao);
    }

    [Fact]
    public void Intervalo_e_configuravel()
    {
        var p = Criar(h => h.SampleIntervalSeconds = 60);
        p.Evaluate(L(0));
        Assert.True(p.Evaluate(L(59)).IsEmpty);
        Assert.NotNull(p.Evaluate(L(60)).Sample);
    }

    // ---------- B) amostragem por variação ----------

    [Fact]
    public void Variacao_e_medida_contra_a_ultima_amostra_gravada_e_nao_contra_a_mensagem_anterior()
    {
        var p = Criar();
        p.Evaluate(L(0, nivel: 50.0));                                   // gravada
        Assert.True(p.Evaluate(L(1, nivel: 50.3)).IsEmpty);              // +0,3 vs gravada
        var d = p.Evaluate(L(2, nivel: 50.6));                           // +0,3 vs anterior, mas +0,6 vs gravada
        Assert.Equal(SampleReason.Change, d.Sample!.MotivoGravacao);
        Assert.True(p.Evaluate(L(3, nivel: 50.9)).IsEmpty);              // nova referência é 50.6
    }

    [Theory]
    [InlineData(50.49, false)]
    [InlineData(50.5, true)]   // limiar é inclusivo
    [InlineData(49.5, true)]   // vale para queda também
    [InlineData(49.51, false)]
    public void Limiar_de_nivel_e_inclusivo_nos_dois_sentidos(double nivel, bool grava)
    {
        var p = Criar();
        p.Evaluate(L(0, nivel: 50));
        Assert.Equal(grava, !p.Evaluate(L(1, nivel: nivel)).IsEmpty);
    }

    [Fact]
    public void Variacao_da_valvula_tambem_grava()
    {
        var p = Criar();
        p.Evaluate(L(0, valvula: 40));
        Assert.True(p.Evaluate(L(1, valvula: 41.9)).IsEmpty);
        Assert.Equal(SampleReason.Change, p.Evaluate(L(2, valvula: 42.0)).Sample!.MotivoGravacao);
    }

    [Fact]
    public void Limiar_configuravel()
    {
        var p = Criar(h => h.MinimumLevelChange = 5);
        p.Evaluate(L(0, nivel: 50));
        Assert.True(p.Evaluate(L(1, nivel: 54.9)).IsEmpty);
        Assert.NotNull(p.Evaluate(L(2, nivel: 55)).Sample);
    }

    // ---------- C) eventos ----------

    [Fact]
    public void Primeiro_setpoint_e_baseline_e_nao_evento()
    {
        var d = Criar().Evaluate(L(0, sp: 60));
        Assert.Empty(d.Events);
    }

    [Fact]
    public void Mudanca_de_setpoint_gera_um_evento_com_valores_e_forca_amostra()
    {
        var p = Criar();
        p.Evaluate(L(0, sp: 60));
        var d = p.Evaluate(L(1, sp: 70));

        var ev = Assert.Single(d.Events);
        Assert.Equal(ProcessEventType.SetpointAlterado, ev.Tipo);
        Assert.Equal(60, ev.ValorAnterior);
        Assert.Equal(70, ev.ValorNovo);
        Assert.Equal(SampleReason.Event, d.Sample!.MotivoGravacao);
        Assert.Equal(70, d.Sample.Setpoint);

        Assert.True(p.Evaluate(L(2, sp: 70)).IsEmpty); // repetir o mesmo valor não gera nada
    }

    [Fact]
    public void Campo_ausente_nao_e_mudanca_nem_apaga_a_memoria()
    {
        var p = Criar();
        p.Evaluate(L(0, sp: 60, manual: false, kp: 2));
        Assert.True(p.Evaluate(L(1)).IsEmpty);                     // sem sp/modo/kp
        Assert.True(p.Evaluate(L(2, sp: 60, manual: false, kp: 2)).IsEmpty); // voltou igual: sem evento
    }

    [Fact]
    public void Mudanca_de_modo_gera_evento_e_nao_repete()
    {
        var p = Criar();
        p.Evaluate(L(0, manual: false));
        var ev = Assert.Single(p.Evaluate(L(1, manual: true)).Events);
        Assert.Equal(ProcessEventType.ModoAlterado, ev.Tipo);
        Assert.Contains("manual", ev.Detalhe);
        Assert.True(p.Evaluate(L(2, manual: true)).IsEmpty);
    }

    [Fact]
    public void Mudanca_no_PID_gera_um_unico_evento_listando_o_que_mudou()
    {
        var p = Criar();
        p.Evaluate(L(0, kp: 1, ti: 20, td: 0));
        var ev = Assert.Single(p.Evaluate(L(1, kp: 2, ti: 20, td: 1)).Events);
        Assert.Equal(ProcessEventType.PidAlterado, ev.Tipo);
        Assert.Contains("kp 1 -> 2", ev.Detalhe);
        Assert.Contains("td 0 -> 1", ev.Detalhe);
        Assert.DoesNotContain("ti", ev.Detalhe);
    }

    [Fact]
    public void Variacao_abaixo_da_tolerancia_de_parametro_e_ruido()
    {
        var p = Criar();
        p.Evaluate(L(0, sp: 60, kp: 2));
        Assert.True(p.Evaluate(L(1, sp: 60.0005, kp: 2.0005)).IsEmpty);
    }

    [Fact]
    public void Alarme_de_nivel_alto_so_na_transicao()
    {
        var p = Criar();
        p.Evaluate(L(0, nivel: 80));
        Assert.Equal(ProcessEventType.NivelAlto, Assert.Single(p.Evaluate(L(1, nivel: 91)).Events).Tipo);
        Assert.True(p.Evaluate(L(2, nivel: 91)).Events.Count == 0);      // continua alto: sem novo evento
        Assert.True(p.Evaluate(L(3, nivel: 95)).Events.Count == 0);
        Assert.Equal(ProcessEventType.NivelAltoNormalizado, Assert.Single(p.Evaluate(L(4, nivel: 89)).Events).Tipo);
    }

    [Fact]
    public void Alarme_de_nivel_baixo_so_na_transicao()
    {
        var p = Criar();
        p.Evaluate(L(0, nivel: 50));
        Assert.Equal(ProcessEventType.NivelBaixo, Assert.Single(p.Evaluate(L(1, nivel: 9.9)).Events).Tipo);
        Assert.Empty(p.Evaluate(L(2, nivel: 5)).Events);
        Assert.Equal(ProcessEventType.NivelBaixoNormalizado, Assert.Single(p.Evaluate(L(3, nivel: 10)).Events).Tipo); // 10 já é normal
    }

    [Fact]
    public void Limites_exatos_10_e_90_nao_sao_alarme()
    {
        var p = Criar();
        Assert.Empty(p.Evaluate(L(0, nivel: 90)).Events);
        Assert.Empty(p.Evaluate(L(1, nivel: 10)).Events);
    }

    [Fact]
    public void Primeira_leitura_ja_em_alarme_registra_o_alarme_ativo()
    {
        var d = Criar().Evaluate(L(0, nivel: 95));
        Assert.Equal(ProcessEventType.NivelAlto, Assert.Single(d.Events).Tipo);
    }

    [Fact]
    public void Evento_gravado_mesmo_sem_variacao_de_nivel_e_antes_do_intervalo()
    {
        var p = Criar();
        p.Evaluate(L(0, sp: 60));
        var d = p.Evaluate(L(1, sp: 61)); // nível igual, 1 s depois
        Assert.NotNull(d.Sample);
        Assert.Equal(SampleReason.Event, d.Sample!.MotivoGravacao);
    }

    [Fact]
    public void RecordEvents_desligado_nao_gera_eventos_mas_mantem_amostras()
    {
        var p = Criar(h => h.RecordEvents = false);
        p.Evaluate(L(0, sp: 60));
        var d = p.Evaluate(L(1, sp: 70));
        Assert.Empty(d.Events);
        Assert.NotNull(d.Sample);
        Assert.Null(p.OnCommunicationLost(T0.AddSeconds(30), 10));
    }

    // ---------- Comunicação ----------

    [Fact]
    public void Perda_de_comunicacao_sem_nunca_ter_recebido_nao_gera_evento()
    {
        Assert.Null(Criar().OnCommunicationLost(T0, null));
    }

    [Fact]
    public void Perda_gera_um_evento_e_nao_se_repete()
    {
        var p = Criar();
        p.Evaluate(L(0));
        var ev = p.OnCommunicationLost(T0.AddSeconds(6), 6);
        Assert.Equal(ProcessEventType.ComunicacaoPerdida, ev!.Tipo);
        Assert.Contains("6", ev.Detalhe);
        Assert.Null(p.OnCommunicationLost(T0.AddSeconds(7), 7));
    }

    [Fact]
    public void Retorno_da_telemetria_apos_perda_gera_restabelecida_e_amostra()
    {
        var p = Criar();
        p.Evaluate(L(0));
        p.OnCommunicationLost(T0.AddSeconds(6), 6);

        var d = p.Evaluate(L(60));
        Assert.Equal(ProcessEventType.ComunicacaoRestabelecida, Assert.Single(d.Events).Tipo);
        Assert.Equal(SampleReason.Event, d.Sample!.MotivoGravacao);
        Assert.True(p.Evaluate(L(61)).IsEmpty);
    }

    [Fact]
    public void Nao_existe_amostra_de_preenchimento_durante_a_perda()
    {
        // A política só decide quando chega leitura: sem leitura não há API para "gravar o tempo passando".
        var p = Criar();
        p.Evaluate(L(0));
        p.OnCommunicationLost(T0.AddSeconds(6), 6);
        // 1 hora depois, a primeira coisa que existe é a leitura de retorno; nada foi inventado no intervalo.
        var d = p.Evaluate(L(3600));
        Assert.Single(d.Events);
        Assert.NotNull(d.Sample);
    }

    // ---------- Reentrega e reconexão ----------

    [Fact]
    public void Leitura_repetida_ou_antiga_pela_hora_de_aquisicao_e_ignorada()
    {
        var p = Criar();
        var t = T0.AddSeconds(-5);
        Assert.NotNull(p.Evaluate(L(0, adquirido: t)).Sample);
        Assert.True(p.Evaluate(L(1, nivel: 70, adquirido: t)).IsEmpty);                 // mesma hora
        Assert.True(p.Evaluate(L(2, nivel: 70, adquirido: t.AddSeconds(-1))).IsEmpty);  // mais antiga
        Assert.NotNull(p.Evaluate(L(3, nivel: 70, adquirido: t.AddSeconds(1))).Sample); // mais nova
    }

    [Fact]
    public void Sem_hora_de_aquisicao_nao_ha_como_detectar_duplicata_e_a_leitura_segue_a_regra_normal()
    {
        var p = Criar();
        p.Evaluate(L(0, nivel: 50));
        Assert.NotNull(p.Evaluate(L(1, nivel: 60)).Sample);
    }

    [Fact]
    public void Evento_usa_a_hora_de_aquisicao_quando_existe()
    {
        var aq = T0.AddSeconds(-2);
        var p = Criar();
        p.Evaluate(L(0, sp: 60, adquirido: aq.AddSeconds(-1)));
        var d = p.Evaluate(L(1, sp: 70, adquirido: aq));
        Assert.Equal(aq, d.Events[0].OcorridoEm);
        Assert.Equal(aq, d.Sample!.AdquiridoEm);
        Assert.Equal(T0.AddSeconds(1), d.Sample.RecebidoEm);
    }

    [Fact]
    public void Perda_de_gravacao_reinicia_a_referencia_e_a_proxima_leitura_e_gravada()
    {
        var p = Criar();
        p.Evaluate(L(0));
        Assert.True(p.Evaluate(L(1)).IsEmpty);
        p.ResetSampleBaseline();
        Assert.Equal(SampleReason.Initial, p.Evaluate(L(2)).Sample!.MotivoGravacao);
    }
}
