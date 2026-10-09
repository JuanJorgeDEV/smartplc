using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartPlc.Api.History;
using SmartPlc.Api.Persistence;
using SmartPlc.Api.Services;

namespace SmartPlc.Api.Tests;

/// <summary>API recebe -> SignalR/estado -> política -> fila -> armazenamento (em memória aqui).</summary>
public class HistoryPipelineTests
{
    private static string Tel(double nivel, double valvula = 40, string extra = "") =>
        $$"""{"nivel":{{nivel.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"valvula":{{valvula.ToString(System.Globalization.CultureInfo.InvariantCulture)}}{{extra}}}""";

    [Fact]
    public async Task Telemetria_valida_e_gravada_somente_depois_do_processamento_da_fila()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);

        await app.PostTelemetria(TestApp.TelemetriaOk);
        Assert.Empty(store.Samples);                       // enfileirada, ainda não confirmada pelo banco
        Assert.Equal(0, app.HistStatus.AmostrasGravadas);

        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        var s = Assert.Single(store.Samples);
        Assert.Equal(55.5678, s.Nivel);                    // valor bruto, não o arredondado de exibição
        Assert.Equal(60, s.Setpoint);
        Assert.Equal(app.Clock.GetUtcNow(), s.RecebidoEm);
        Assert.Null(s.AdquiridoEm);                        // o fluxo atual não informa
        Assert.Equal(SampleReason.Initial, s.MotivoGravacao);
        Assert.Equal(1, app.HistStatus.AmostrasGravadas);  // contado só após a confirmação
    }

    [Fact]
    public async Task Telemetria_invalida_nao_gera_registro()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);

        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostTelemetria("""{"nivel":"abc","valvula":1}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostTelemetria("""{"nivel":500,"valvula":1}""")).StatusCode);
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Empty(store.Samples);
        Assert.Empty(store.Events);
        Assert.Equal(0, app.HistStatus.Enfileirados);
    }

    [Fact]
    public async Task Mensagens_repetidas_dentro_do_intervalo_geram_uma_unica_amostra()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);

        for (var i = 0; i < 9; i++)
        {
            await app.PostTelemetria(Tel(50));
            app.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Single(store.Samples);
    }

    [Fact]
    public async Task Intervalo_periodico_e_variacao_funcionam_pela_api()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store, new() { ["History:SampleIntervalSeconds"] = "10" });

        await app.PostTelemetria(Tel(50));
        app.Clock.Advance(TimeSpan.FromSeconds(10));
        await app.PostTelemetria(Tel(50));                 // periódica
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await app.PostTelemetria(Tel(51));                 // variação de 1 ponto
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Equal([SampleReason.Initial, SampleReason.Periodic, SampleReason.Change], store.Samples.Select(s => s.MotivoGravacao));
    }

    [Fact]
    public async Task Historico_desligado_nao_afeta_a_telemetria()
    {
        var store = new InMemoryHistoryStore { Habilitado = false };
        using var app = TestApp.ComHistorico(store);

        Assert.Equal(HttpStatusCode.OK, (await app.PostTelemetria(TestApp.TelemetriaOk)).StatusCode);
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Empty(store.Samples);
        Assert.Single(app.Notifier.Enviados);
    }

    // ---------- Eventos ----------

    [Fact]
    public async Task Mudanca_de_setpoint_lida_do_CLP_vira_evento_e_nao_comando()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);

        await app.PostTelemetria(Tel(50, extra: ""","setpoint":60"""));
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await app.PostTelemetria(Tel(50, extra: ""","setpoint":70"""));
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        var ev = Assert.Single(store.Events);
        Assert.Equal(ProcessEventType.SetpointAlterado, ev.Tipo);
        Assert.Equal(2, store.Samples.Count);
        Assert.Equal(SampleReason.Event, store.Samples[1].MotivoGravacao);
    }

    [Fact]
    public async Task Perda_e_retorno_de_comunicacao_geram_um_evento_cada()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);
        var monitor = app.Services.GetRequiredService<StalenessMonitor>();

        await app.PostTelemetria(Tel(50));
        app.Clock.Advance(TimeSpan.FromSeconds(6));
        await monitor.VerificarAsync();
        await monitor.VerificarAsync();                    // segunda checagem: sem novo evento
        await app.PostTelemetria(Tel(50));
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Equal([ProcessEventType.ComunicacaoPerdida, ProcessEventType.ComunicacaoRestabelecida], store.Events.Select(e => e.Tipo));
    }

    [Fact]
    public async Task Nao_ha_amostra_enquanto_o_CLP_esta_offline()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);
        var monitor = app.Services.GetRequiredService<StalenessMonitor>();

        await app.PostTelemetria(Tel(50));
        for (var i = 0; i < 10; i++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(1));
            await monitor.VerificarAsync();
        }
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Single(store.Samples);                      // só a leitura real; nenhuma "repetição" do último valor
        Assert.Single(store.Events);                       // só a perda
    }

    // ---------- Hora de aquisição ----------

    [Fact]
    public async Task Hora_de_aquisicao_informada_e_gravada_separada_da_de_recebimento()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);
        var aq = app.Clock.GetUtcNow().AddSeconds(-3);

        await app.PostTelemetria(Tel(50, extra: $$""","adquiridoEm":"{{aq:O}}" """));
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        var s = Assert.Single(store.Samples);
        Assert.Equal(aq, s.AdquiridoEm);
        Assert.Equal(app.Clock.GetUtcNow(), s.RecebidoEm);
    }

    [Fact]
    public async Task Hora_de_aquisicao_em_milissegundos_unix_e_aceita()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);
        var aq = app.Clock.GetUtcNow().AddSeconds(-1);

        var r = await app.PostTelemetria(Tel(50, extra: $",\"adquiridoEm\":{aq.ToUnixTimeMilliseconds()}"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);
        Assert.Equal(aq.ToUnixTimeMilliseconds(), store.Samples[0].AdquiridoEm!.Value.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData("\"ontem\"")]
    [InlineData("\"2026-01-01T12:00:00\"")]       // sem fuso: ambíguo
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("12")]                            // 1970
    [InlineData("-5")]
    public async Task Hora_de_aquisicao_invalida_devolve_400(string valor)
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);
        var r = await app.PostTelemetria(Tel(50, extra: $",\"adquiridoEm\":{valor}"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("erros").EnumerateArray(),
            e => e.GetProperty("campo").GetString() == "adquiridoEm");
    }

    [Fact]
    public async Task Hora_de_aquisicao_muito_no_futuro_e_rejeitada_e_pequena_diferenca_de_relogio_e_tolerada()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);
        var agora = app.Clock.GetUtcNow();

        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostTelemetria(Tel(50, extra: $$""","adquiridoEm":"{{agora.AddMinutes(6):O}}" """))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.PostTelemetria(Tel(50, extra: $$""","adquiridoEm":"{{agora.AddMinutes(4):O}}" """))).StatusCode);
    }

    [Fact]
    public async Task Reentrega_com_a_mesma_hora_de_aquisicao_nao_duplica()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store);
        var json = Tel(50, extra: $$""","adquiridoEm":"{{app.Clock.GetUtcNow().AddSeconds(-2):O}}" """);

        Assert.Equal(HttpStatusCode.OK, (await app.PostTelemetria(json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.PostTelemetria(json)).StatusCode); // o tempo real continua aceitando
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Single(store.Samples);
    }

    // ---------- Falhas do banco ----------

    [Fact]
    public async Task Falha_do_banco_nao_interrompe_o_tempo_real()
    {
        var store = new InMemoryHistoryStore { FailNext = -1 };
        using var app = TestApp.ComHistorico(store);

        var r = await app.PostTelemetria(TestApp.TelemetriaOk);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Single(app.Notifier.Enviados);
        Assert.True((await app.Status()).GetProperty("telemetria").GetProperty("plcOnline").GetBoolean());
    }

    [Fact]
    public async Task Falhas_sao_contadas_registradas_e_o_registro_perdido_e_declarado_nao_silenciado()
    {
        var store = new InMemoryHistoryStore { FailNext = -1 };
        using var app = TestApp.ComHistorico(store, new() { ["History:MaxWriteRetries"] = "2" });

        await app.PostTelemetria(TestApp.TelemetriaOk);
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Equal(3, store.SaveCalls);                  // 1 tentativa + 2 retentativas
        Assert.Equal(3, app.HistStatus.Falhas);
        Assert.Equal(1, app.HistStatus.PerdidosAposFalhas);
        Assert.Equal(0, app.HistStatus.AmostrasGravadas);  // nada contado como salvo
        Assert.Contains("banco indisponível", app.HistStatus.UltimoErro);
    }

    [Fact]
    public async Task Recuperacao_apos_falha_transitoria_grava_sem_perder()
    {
        var store = new InMemoryHistoryStore { FailNext = 2 };
        using var app = TestApp.ComHistorico(store, new() { ["History:MaxWriteRetries"] = "3" });

        await app.PostTelemetria(TestApp.TelemetriaOk);
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Single(store.Samples);
        Assert.Equal(2, app.HistStatus.Falhas);
        Assert.Equal(0, app.HistStatus.PerdidosAposFalhas);
        Assert.Equal(1, app.HistStatus.AmostrasGravadas);
    }

    [Fact]
    public async Task Depois_de_perder_um_registro_a_proxima_leitura_e_gravada_como_ponto_de_partida()
    {
        var store = new InMemoryHistoryStore { FailNext = -1 };
        using var app = TestApp.ComHistorico(store, new() { ["History:MaxWriteRetries"] = "0" });

        await app.PostTelemetria(Tel(50));
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);   // perdida
        store.FailNext = 0;                                                    // banco volta
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await app.PostTelemetria(Tel(50));                                     // sem mudança e dentro do intervalo
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        var s = Assert.Single(store.Samples);
        Assert.Equal(SampleReason.Initial, s.MotivoGravacao);
    }

    [Fact]
    public async Task Fila_cheia_descarta_o_item_novo_e_declara()
    {
        var store = new InMemoryHistoryStore();
        using var app = TestApp.ComHistorico(store, new() { ["History:QueueCapacity"] = "2" });

        for (var i = 0; i < 5; i++)                        // cada nível difere >= 0,5: 5 decisões
            await app.PostTelemetria(Tel(10 + i * 10));
        Assert.Equal(2, app.HistStatus.Enfileirados);
        Assert.Equal(3, app.HistStatus.DescartadosFilaCheia);

        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);
        Assert.Equal([10.0, 20.0], store.Samples.Select(s => s.Nivel));   // os mais antigos foram preservados
    }

    // ---------- Duplicata no banco ----------

    [Fact]
    public async Task Duplicata_detectada_pelo_banco_e_contada_e_nao_e_falha()
    {
        var store = new InMemoryHistoryStore();
        var aq = DateTimeOffset.Parse("2026-01-01T11:59:58Z");
        store.Samples.Add(new TelemetrySample { Id = 99, AdquiridoEm = aq, RecebidoEm = aq, Nivel = 1 }); // gravada antes de um reinício

        using var app = TestApp.ComHistorico(store);
        await app.PostTelemetria(Tel(50, extra: $$""","adquiridoEm":"{{aq:O}}" """));
        await app.Pipeline.ProcessarPendentesAsync(CancellationToken.None);

        Assert.Single(store.Samples);
        Assert.Equal(1, app.HistStatus.Duplicados);
        Assert.Equal(0, app.HistStatus.Falhas);
    }

    // ---------- Diagnóstico ----------

    [Fact]
    public async Task Health_ready_expoe_contadores_sem_consultar_o_banco()
    {
        var store = new InMemoryHistoryStore { FailQueries = true, FailNext = -1 };
        using var app = TestApp.ComHistorico(store);

        var r = await app.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);     // liveness não depende do banco (o Render reiniciaria o serviço)
        var h = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("historico");
        Assert.True(h.GetProperty("habilitado").GetBoolean());
        Assert.Equal(0, h.GetProperty("enfileirados").GetInt32());
    }

    [Fact]
    public async Task Health_simples_continua_independente_do_banco()
    {
        using var app = TestApp.ComHistorico(new InMemoryHistoryStore { FailQueries = true, FailNext = -1 });
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/health")).StatusCode);
    }
}
