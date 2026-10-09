using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartPlc.Api.History;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

public class HistoryQueryTests
{
    private static readonly DateTimeOffset Agora = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

    private static InMemoryHistoryStore ComAmostras(int n, int passoSeg = 10)
    {
        var store = new InMemoryHistoryStore();
        for (var i = 0; i < n; i++)
            store.Samples.Add(new TelemetrySample
            {
                Id = i + 1,
                RecebidoEm = Agora.AddSeconds(-(n - i) * passoSeg),   // do mais antigo para o mais novo
                Nivel = i,
                Valvula = 40,
                MotivoGravacao = SampleReason.Periodic
            });
        return store;
    }

    /// <summary>ISO-8601 em UTC com "Z" (o "+00:00" do formato "O" vira espaço numa query string).</summary>
    private static string Z(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(TestApp app, string url, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        var r = await app.CreateClient().GetAsync(url);
        Assert.Equal(esperado, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Janela_padrao_e_a_ultima_hora_ordenada_por_hora()
    {
        var store = ComAmostras(5);
        store.Samples.Add(new TelemetrySample { Id = 100, RecebidoEm = Agora.AddHours(-2), Nivel = 99 }); // fora da janela
        using var app = TestApp.ComHistorico(store);

        var j = await Get(app, "/api/historico");

        var niveis = j.GetProperty("amostras").EnumerateArray().Select(a => a.GetProperty("nivel").GetDouble()).ToArray();
        Assert.Equal([0.0, 1, 2, 3, 4], niveis);
        Assert.Equal(5, j.GetProperty("quantidade").GetInt32());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("proximo").ValueKind);
    }

    [Fact]
    public async Task Formato_para_grafico_tem_hora_nivel_valvula_setpoint_e_motivo()
    {
        var store = ComAmostras(1);
        store.Samples[0].Setpoint = 60;
        store.Samples[0].ModoManual = false;
        using var app = TestApp.ComHistorico(store);

        var a = (await Get(app, "/api/historico")).GetProperty("amostras")[0];

        Assert.Equal(["adquiridoEm", "modoManual", "motivo", "nivel", "setpoint", "t", "valvula"],
            a.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Periodic", a.GetProperty("motivo").GetString());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("adquiridoEm").ValueKind);
        Assert.EndsWith("+00:00", a.GetProperty("t").GetString());   // UTC (deslocamento zero)
    }

    [Fact]
    public async Task Filtra_por_periodo_inclusive_nos_dois_extremos()
    {
        var store = ComAmostras(6);   // -60 s ... -10 s
        using var app = TestApp.ComHistorico(store);

        var j = await Get(app, $"/api/historico?de={Z(Agora.AddSeconds(-40))}&ate={Z(Agora.AddSeconds(-20))}");

        Assert.Equal([2.0, 3, 4], j.GetProperty("amostras").EnumerateArray().Select(a => a.GetProperty("nivel").GetDouble()));
    }

    [Fact]
    public async Task Intervalo_sem_dados_devolve_200_com_lista_vazia()
    {
        using var app = TestApp.ComHistorico(ComAmostras(3));
        var j = await Get(app, $"/api/historico?de={Z(Agora.AddHours(-10))}&ate={Z(Agora.AddHours(-9))}");
        Assert.Equal(0, j.GetProperty("quantidade").GetInt32());
        Assert.Empty(j.GetProperty("amostras").EnumerateArray());
    }

    [Fact]
    public async Task Banco_vazio_devolve_200_com_lista_vazia()
    {
        using var app = TestApp.ComHistorico(new InMemoryHistoryStore());
        Assert.Equal(0, (await Get(app, "/api/historico")).GetProperty("quantidade").GetInt32());
    }

    [Fact]
    public async Task Paginacao_percorre_tudo_sem_repetir_nem_perder()
    {
        var store = ComAmostras(25);
        using var app = TestApp.ComHistorico(store);

        var vistos = new List<double>();
        string? cursor = null;
        var paginas = 0;
        do
        {
            var url = $"/api/historico?de={Z(Agora.AddHours(-1))}&limite=10" + (cursor is null ? "" : $"&proximo={cursor}");
            var j = await Get(app, url);
            vistos.AddRange(j.GetProperty("amostras").EnumerateArray().Select(a => a.GetProperty("nivel").GetDouble()));
            cursor = j.GetProperty("proximo").ValueKind == JsonValueKind.Null ? null : j.GetProperty("proximo").GetString();
            paginas++;
        } while (cursor is not null && paginas < 10);

        Assert.Equal(3, paginas);
        Assert.Equal(Enumerable.Range(0, 25).Select(i => (double)i), vistos);
    }

    [Fact]
    public async Task Pagina_cheia_exatamente_nao_gera_cursor_para_pagina_vazia()
    {
        using var app = TestApp.ComHistorico(ComAmostras(10));
        var j = await Get(app, "/api/historico?limite=10");
        Assert.Equal(10, j.GetProperty("quantidade").GetInt32());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("proximo").ValueKind);
    }

    [Fact]
    public async Task Mesma_hora_e_desempatada_pelo_id()
    {
        var store = new InMemoryHistoryStore();
        for (var i = 1; i <= 5; i++)
            store.Samples.Add(new TelemetrySample { Id = i, RecebidoEm = Agora.AddSeconds(-5), Nivel = i });
        using var app = TestApp.ComHistorico(store);

        var vistos = new List<double>();
        string? cursor = null;
        do
        {
            var j = await Get(app, "/api/historico?limite=2" + (cursor is null ? "" : $"&proximo={cursor}"));
            vistos.AddRange(j.GetProperty("amostras").EnumerateArray().Select(a => a.GetProperty("nivel").GetDouble()));
            cursor = j.GetProperty("proximo").ValueKind == JsonValueKind.Null ? null : j.GetProperty("proximo").GetString();
        } while (cursor is not null);

        Assert.Equal([1.0, 2, 3, 4, 5], vistos);
    }

    // ---------- Validação e limites ----------

    [Theory]
    [InlineData("de=ontem", "de")]
    [InlineData("ate=32/13/2026", "ate")]
    [InlineData("limite=0", "limite")]
    [InlineData("limite=-1", "limite")]
    [InlineData("limite=abc", "limite")]
    [InlineData("limite=5001", "limite")]
    [InlineData("limite=99999999999999", "limite")]
    [InlineData("proximo=lixo", "proximo")]
    [InlineData("proximo=", "proximo")]
    public async Task Parametros_invalidos_devolvem_400_com_o_campo(string query, string campo)
    {
        using var app = TestApp.ComHistorico(ComAmostras(3));
        var j = await Get(app, "/api/historico?" + query, HttpStatusCode.BadRequest);
        Assert.Contains(j.GetProperty("erros").EnumerateArray(), e => e.GetProperty("campo").GetString() == campo);
    }

    [Fact]
    public async Task Intervalo_invertido_ou_vazio_e_rejeitado()
    {
        using var app = TestApp.ComHistorico(ComAmostras(3));
        await Get(app, $"/api/historico?de={Z(Agora)}&ate={Z(Agora.AddMinutes(-5))}", HttpStatusCode.BadRequest);
        await Get(app, $"/api/historico?de={Z(Agora)}&ate={Z(Agora)}", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Periodo_acima_do_maximo_e_rejeitado_e_o_maximo_exato_e_aceito()
    {
        using var app = TestApp.ComHistorico(ComAmostras(3));
        await Get(app, $"/api/historico?de={Z(Agora.AddHours(-73))}&ate={Z(Agora)}", HttpStatusCode.BadRequest);
        await Get(app, $"/api/historico?de={Z(Agora.AddHours(-72))}&ate={Z(Agora)}");
    }

    [Fact]
    public async Task Maximo_de_registros_por_pagina_e_configuravel()
    {
        using var app = TestApp.ComHistorico(ComAmostras(3), new() { ["History:MaxQueryLimit"] = "2" });
        await Get(app, "/api/historico?limite=3", HttpStatusCode.BadRequest);
        Assert.Equal(2, (await Get(app, "/api/historico?limite=2")).GetProperty("quantidade").GetInt32());
    }

    [Fact]
    public async Task Sem_limite_informado_aplica_o_padrao_e_nao_devolve_a_tabela_inteira()
    {
        using var app = TestApp.ComHistorico(ComAmostras(30), new() { ["History:DefaultQueryLimit"] = "7" });
        var j = await Get(app, "/api/historico");
        Assert.Equal(7, j.GetProperty("quantidade").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, j.GetProperty("proximo").ValueKind);
    }

    [Fact]
    public async Task Datas_sem_fuso_sao_tratadas_como_UTC()
    {
        using var app = TestApp.ComHistorico(ComAmostras(6));
        var j = await Get(app, "/api/historico?de=2026-01-01T11:59:20&ate=2026-01-01T11:59:40");
        Assert.Equal(3, j.GetProperty("quantidade").GetInt32());
    }

    // ---------- Indisponibilidade ----------

    [Fact]
    public async Task Historico_desligado_devolve_503_explicativo()
    {
        using var app = TestApp.ComHistorico(new InMemoryHistoryStore { Habilitado = false });
        var j = await Get(app, "/api/historico", HttpStatusCode.ServiceUnavailable);
        Assert.Contains("desabilitado", j.GetProperty("mensagem").GetString());
    }

    [Fact]
    public async Task Sem_banco_configurado_o_historico_responde_503_e_o_resto_funciona()
    {
        using var app = new TestApp();   // sem ConnectionStrings:SmartPlc => DisabledHistoryStore
        await Get(app, "/api/historico", HttpStatusCode.ServiceUnavailable);
        await Get(app, "/api/eventos", HttpStatusCode.ServiceUnavailable);
        Assert.Equal(HttpStatusCode.OK, (await app.PostTelemetria(TestApp.TelemetriaOk)).StatusCode);
    }

    [Fact]
    public async Task Falha_do_banco_na_consulta_devolve_503_sem_vazar_detalhes()
    {
        using var app = TestApp.ComHistorico(new InMemoryHistoryStore { FailQueries = true });
        var r = await app.CreateClient().GetAsync("/api/historico");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var texto = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("simulado", texto);
        Assert.DoesNotContain("InvalidOperation", texto);
    }

    [Fact]
    public async Task Consulta_nao_exige_chave_e_a_ingestao_continua_protegida()
    {
        using var app = new TestApp(new() { ["Ingest:ApiKey"] = "segredo" }, historyStore: ComAmostras(1));
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/api/historico")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostTelemetria(TestApp.TelemetriaOk)).StatusCode);
    }

    // ---------- Eventos ----------

    [Fact]
    public async Task Eventos_sao_listados_por_periodo_com_tipo_legivel()
    {
        var store = new InMemoryHistoryStore();
        store.Events.Add(new ProcessEvent { Id = 1, OcorridoEm = Agora.AddMinutes(-5), Tipo = ProcessEventType.NivelAlto, Detalhe = "nível 91 %", ValorNovo = 91 });
        store.Events.Add(new ProcessEvent { Id = 2, OcorridoEm = Agora.AddMinutes(-2), Tipo = ProcessEventType.ComunicacaoPerdida });
        store.Events.Add(new ProcessEvent { Id = 3, OcorridoEm = Agora.AddHours(-5), Tipo = ProcessEventType.PidAlterado });
        using var app = TestApp.ComHistorico(store);

        var j = await Get(app, "/api/eventos");

        Assert.Equal(["NivelAlto", "ComunicacaoPerdida"], j.GetProperty("eventos").EnumerateArray().Select(e => e.GetProperty("tipo").GetString()));
    }

    // ---------- Cursor e configuração ----------

    [Fact]
    public void Cursor_codifica_e_decodifica_sem_perder_precisao()
    {
        var c = new Cursor(DateTimeOffset.Parse("2026-01-01T12:00:00.1234567Z"), 42);
        Assert.True(CursorCodec.TryDecode(CursorCodec.Encode(c), out var volta));
        Assert.Equal(c, volta);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não-é-base64!!")]
    [InlineData("YWJj")]                  // "abc": sem ':'
    public void Cursor_invalido_e_recusado(string? texto)
    {
        Assert.False(CursorCodec.TryDecode(texto, out _));
    }

    [Fact]
    public void Cursor_com_ticks_absurdos_e_recusado()
    {
        var absurdo = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("4000000000000000000:1")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(CursorCodec.TryDecode(absurdo, out _));
    }

    // ---------- Retenção (plano B) ----------

    [Fact]
    public async Task Retencao_apaga_so_o_expirado_usando_os_limites_configurados()
    {
        var relogio = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Agora);
        var store = new InMemoryHistoryStore();
        store.Samples.Add(new TelemetrySample { Id = 1, RecebidoEm = Agora.AddDays(-15) });     // expirada (14 d)
        store.Samples.Add(new TelemetrySample { Id = 2, RecebidoEm = Agora.AddDays(-13) });     // mantida
        store.Events.Add(new ProcessEvent { Id = 3, OcorridoEm = Agora.AddDays(-20) });         // evento: retenção maior, mantido
        store.Events.Add(new ProcessEvent { Id = 4, OcorridoEm = Agora.AddDays(-400) });        // expirado (365 d)
        var opt = Microsoft.Extensions.Options.Options.Create(new HistoryOptions { Retention = new RetentionOptions { Enabled = true } });

        await new RetentionService(store, opt, relogio, NullLogger<RetentionService>.Instance).ExecutarUmaVezAsync(CancellationToken.None);

        Assert.Equal([2L], store.Samples.Select(s => s.Id));
        Assert.Equal([3L], store.Events.Select(e => e.Id));
        Assert.Equal(Agora.AddDays(-14), store.LastSamplesCutoff);
        Assert.Equal(Agora.AddDays(-365), store.LastEventsCutoff);
    }

    [Fact]
    public async Task Falha_na_retencao_nao_lanca_e_nao_apaga()
    {
        var relogio = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Agora);
        var store = new InMemoryHistoryStore { FailNext = -1 };
        store.Samples.Add(new TelemetrySample { Id = 1, RecebidoEm = Agora.AddDays(-30) });
        var opt = Microsoft.Extensions.Options.Options.Create(new HistoryOptions { Retention = new RetentionOptions { Enabled = true } });

        await new RetentionService(store, opt, relogio, NullLogger<RetentionService>.Instance).ExecutarUmaVezAsync(CancellationToken.None);

        Assert.Single(store.Samples);
    }
}
