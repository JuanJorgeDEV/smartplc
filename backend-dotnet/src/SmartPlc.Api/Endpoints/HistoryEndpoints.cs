using System.Globalization;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.History;

namespace SmartPlc.Api.Endpoints;

public static class HistoryEndpoints
{
    public static void MapHistoryApi(this WebApplication app)
    {
        app.MapGet("/health/ready", (IHistoryStore store, HistoryStatus s, IOptions<HistoryOptions> o) => Results.Ok(new
        {
            status = "ok",
            historico = new
            {
                habilitado = o.Value.Enabled && store.Habilitado,
                s.Enfileirados, s.AmostrasGravadas, s.EventosGravados, s.Duplicados,
                s.DescartadosFilaCheia, s.PerdidosAposFalhas, s.Falhas, s.UltimoErro, s.UltimaGravacaoEm
            }
        }));

        app.MapGet("/api/historico", (HttpRequest req, IHistoryStore store, IOptions<HistoryOptions> o, TimeProvider clock, ILogger<Program> log, CancellationToken ct) =>
            Consultar(req, store, o.Value, clock, log, ct, async (de, ate, limite, apos) =>
            {
                var pagina = await store.QuerySamplesAsync(de, ate, limite, apos, ct);
                var ultimo = pagina.Items.Count > 0 ? pagina.Items[^1] : null;
                return (new
                {
                    de,
                    ate,
                    quantidade = pagina.Items.Count,
                    amostras = pagina.Items.Select(s => new
                    {
                        t = s.RecebidoEm,
                        adquiridoEm = s.AdquiridoEm,
                        s.Nivel,
                        s.Valvula,
                        s.Setpoint,
                        s.ModoManual,
                        motivo = s.MotivoGravacao.ToString()
                    }),
                    proximo = pagina.HasMore && ultimo is not null ? CursorCodec.Encode(new Cursor(ultimo.RecebidoEm, ultimo.Id)) : null
                });
            }));

        app.MapGet("/api/eventos", (HttpRequest req, IHistoryStore store, IOptions<HistoryOptions> o, TimeProvider clock, ILogger<Program> log, CancellationToken ct) =>
            Consultar(req, store, o.Value, clock, log, ct, async (de, ate, limite, apos) =>
            {
                var pagina = await store.QueryEventsAsync(de, ate, limite, apos, ct);
                var ultimo = pagina.Items.Count > 0 ? pagina.Items[^1] : null;
                return (new
                {
                    de,
                    ate,
                    quantidade = pagina.Items.Count,
                    eventos = pagina.Items.Select(e => new
                    {
                        t = e.OcorridoEm,
                        tipo = e.Tipo.ToString(),
                        e.Detalhe,
                        e.ValorAnterior,
                        e.ValorNovo
                    }),
                    proximo = pagina.HasMore && ultimo is not null ? CursorCodec.Encode(new Cursor(ultimo.OcorridoEm, ultimo.Id)) : null
                });
            }));
    }

    private static async Task<IResult> Consultar(HttpRequest req, IHistoryStore store, HistoryOptions o, TimeProvider clock,
        ILogger log, CancellationToken ct, Func<DateTimeOffset, DateTimeOffset, int, Cursor?, Task<object>> executar)
    {
        if (!o.Enabled || !store.Habilitado)
            return Results.Json(ErroResponse.De("histórico desabilitado: nenhum banco configurado"), statusCode: StatusCodes.Status503ServiceUnavailable);

        var erros = new List<CampoErro>();
        var agora = clock.GetUtcNow();

        var ate = Data(req, "ate", agora, erros);
        var de = Data(req, "de", ate.AddMinutes(-o.DefaultQueryRangeMinutes), erros);

        var limite = o.DefaultQueryLimit;
        if (req.Query.TryGetValue("limite", out var l)
            && (!int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out limite) || limite < 1 || limite > o.MaxQueryLimit))
            erros.Add(new CampoErro("limite", $"deve ser um inteiro entre 1 e {o.MaxQueryLimit}"));

        Cursor? apos = null;
        if (req.Query.TryGetValue("proximo", out var p))
        {
            if (CursorCodec.TryDecode(p, out var c)) apos = c;
            else erros.Add(new CampoErro("proximo", "cursor inválido"));
        }

        if (erros.Count == 0)
        {
            if (de >= ate) erros.Add(new CampoErro("de", "deve ser anterior a 'ate'"));
            else if ((ate - de).TotalHours > o.MaxQueryRangeHours)
                erros.Add(new CampoErro("de", $"o período máximo por consulta é {o.MaxQueryRangeHours} h"));
        }

        if (erros.Count > 0) return Results.BadRequest(ErroResponse.De("parâmetros inválidos", erros));

        try
        {
            return Results.Ok(await executar(de, ate, limite, apos));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Falha ao consultar o histórico");
            return Results.Json(ErroResponse.De("banco de dados indisponível"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static DateTimeOffset Data(HttpRequest req, string nome, DateTimeOffset padrao, List<CampoErro> erros)
    {
        if (!req.Query.TryGetValue(nome, out var v) || v.Count == 0) return padrao;
        // Sem fuso explícito, assume UTC. Resultado sempre em UTC (o Npgsql só aceita offset 0 em timestamptz).
        if (DateTimeOffset.TryParse(v[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            return d.ToUniversalTime();
        erros.Add(new CampoErro(nome, "data/hora inválida (use ISO-8601, ex.: 2026-10-08T14:00:00Z)"));
        return padrao;
    }
}
