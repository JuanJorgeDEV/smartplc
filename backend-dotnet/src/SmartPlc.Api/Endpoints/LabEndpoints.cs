using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.History;
using SmartPlc.Api.Lab;
using SmartPlc.Api.Persistence;
using SmartPlc.Api.Services;
using SmartPlc.Api.Validation;

namespace SmartPlc.Api.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NovaSessaoRequest
{
    public string? Nome { get; set; }
    public string? Tipo { get; set; }
    public string? Observacoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EncerrarSessaoRequest
{
    public string? Observacoes { get; set; }
}

public static class LabEndpoints
{
    public const int NomeMax = 80;
    public const int ObservacoesMax = 500;

    public static void MapLabApi(this WebApplication app)
    {
        app.MapGet("/api/experimentos", Listar);
        app.MapPost("/api/experimentos", Iniciar);
        app.MapGet("/api/experimentos/{id:guid}", Detalhar);
        app.MapPost("/api/experimentos/{id:guid}/encerrar", Encerrar);
        app.MapPost("/api/experimentos/{id:guid}/cancelar", Cancelar);
    }

    /// <summary>Laboratório precisa de banco (as amostras vêm do histórico). Escrita pode ser desligada separadamente.</summary>
    private static IResult? Indisponivel(IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab, bool escrita)
    {
        if (!lab.Value.Enabled || !store.Habilitado || !historico.Habilitado)
            return Results.Json(ErroResponse.De("Laboratório indisponível: configure o banco de dados do histórico (PostgreSQL) para registrar experimentos"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        if (escrita && !lab.Value.WriteEnabled)
            return Results.Json(ErroResponse.De("criar e encerrar experimentos está desabilitado neste ambiente (somente leitura)"),
                statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    private static async Task<IResult> Listar(HttpRequest req, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab, ILogger<Program> log, CancellationToken ct)
    {
        if (Indisponivel(store, historico, lab, false) is { } negado) return negado;

        var erros = new List<CampoErro>();
        string? status = null;
        if (req.Query.TryGetValue("status", out var st))
        {
            status = st.ToString().ToLowerInvariant();
            if (status is not (ExperimentStatuses.Ativa or ExperimentStatuses.Encerrada or ExperimentStatuses.Cancelada))
                erros.Add(new CampoErro("status", "use ativa, encerrada ou cancelada"));
        }
        var limite = 20;
        if (req.Query.TryGetValue("limite", out var l)
            && (!int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out limite) || limite is < 1 or > 100))
            erros.Add(new CampoErro("limite", "deve ser um inteiro entre 1 e 100"));
        if (erros.Count > 0) return Results.BadRequest(ErroResponse.De("parâmetros inválidos", erros));

        try
        {
            return Results.Ok(new { sessoes = (await store.ListarAsync(limite, status, ct)).Select(SessionDto.De) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha ao listar experimentos");
            return Results.Json(ErroResponse.De("banco de dados indisponível"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> Iniciar(HttpRequest req, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab,
        ProcessStateStore estado, TimeProvider clock, ILogger<Program> log, CancellationToken ct)
    {
        if (Indisponivel(store, historico, lab, true) is { } negado) return negado;

        var leitura = await PayloadReader.LerAsync<NovaSessaoRequest>(req.Body, ct);
        if (leitura.Excedeu) return Results.Json(ErroResponse.De("corpo muito grande"), statusCode: StatusCodes.Status413PayloadTooLarge);
        var erros = new List<CampoErro>(leitura.Erros ?? []);
        var dados = leitura.Valor;

        string? tipo = null;
        if (dados is not null)
        {
            tipo = ExperimentTypes.Todos.FirstOrDefault(t => string.Equals(t, dados.Tipo?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (tipo is null) erros.Add(new CampoErro("tipo", $"use {string.Join(" ou ", ExperimentTypes.Todos)}"));
            if (dados.Nome is { } n && n.Trim().Length > NomeMax) erros.Add(new CampoErro("nome", $"no máximo {NomeMax} caracteres"));
            if (dados.Observacoes is { } o && o.Trim().Length > ObservacoesMax) erros.Add(new CampoErro("observacoes", $"no máximo {ObservacoesMax} caracteres"));
        }
        if (erros.Count > 0) return Results.BadRequest(ErroResponse.De("experimento inválido", erros));

        // Os valores de partida vêm do que o servidor recebeu do CLP, nunca do navegador.
        var snap = estado.Snapshot();
        if (!snap.Telemetria.PlcOnline)
            return Results.Json(ErroResponse.De("sem telemetria válida do CLP: conecte o Node-RED e aguarde a primeira leitura antes de iniciar"),
                statusCode: StatusCodes.Status409Conflict);

        var agora = clock.GetUtcNow();
        var nome = string.IsNullOrWhiteSpace(dados!.Nome)
            ? $"{ExperimentTypes.Rotulo(tipo!)} {agora:yyyy-MM-dd HH:mm} UTC"
            : dados.Nome.Trim();
        var sessao = new ExperimentSession
        {
            Id = Guid.NewGuid(),
            Nome = nome,
            Tipo = tipo!,
            Status = ExperimentStatuses.Ativa,
            InicioEm = agora,
            PlcOnlineInicial = true,
            NivelInicial = snap.Telemetria.Nivel,
            SetpointInicial = snap.Controle.Setpoint,
            ValvulaInicial = snap.Telemetria.Valvula,
            ModoManualInicial = snap.Controle.ModoManual,
            Observacoes = string.IsNullOrWhiteSpace(dados.Observacoes) ? null : dados.Observacoes.Trim()
        };

        try
        {
            await store.CriarAsync(sessao, ct);
            return Results.Created($"/api/experimentos/{sessao.Id}", new { sessao = SessionDto.De(sessao) });
        }
        catch (ExperimentConflictException ex)
        {
            return Results.Json(new { status = "erro", mensagem = "já existe um experimento em andamento; encerre-o antes de iniciar outro", sessaoAtiva = ex.SessaoAtiva },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha ao iniciar experimento");
            return Results.Json(ErroResponse.De("banco de dados indisponível"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Task<IResult> Encerrar(Guid id, HttpRequest req, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab,
        ProcessStateStore estado, TimeProvider clock, ILogger<Program> log, CancellationToken ct) =>
        Finalizar(id, req, ExperimentStatuses.Encerrada, store, historico, lab, estado, clock, log, ct);

    private static Task<IResult> Cancelar(Guid id, HttpRequest req, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab,
        ProcessStateStore estado, TimeProvider clock, ILogger<Program> log, CancellationToken ct) =>
        Finalizar(id, req, ExperimentStatuses.Cancelada, store, historico, lab, estado, clock, log, ct);

    private static async Task<IResult> Finalizar(Guid id, HttpRequest req, string novoStatus, IExperimentStore store, IHistoryStore historico,
        IOptions<LabOptions> lab, ProcessStateStore estado, TimeProvider clock, ILogger<Program> log, CancellationToken ct)
    {
        if (Indisponivel(store, historico, lab, true) is { } negado) return negado;

        string? observacoes = null;
        if (req.ContentLength is > 0)
        {
            var leitura = await PayloadReader.LerAsync<EncerrarSessaoRequest>(req.Body, ct);
            if (leitura.Excedeu) return Results.Json(ErroResponse.De("corpo muito grande"), statusCode: StatusCodes.Status413PayloadTooLarge);
            if (leitura.Erros is { Count: > 0 } e) return Results.BadRequest(ErroResponse.De("parâmetros inválidos", e));
            observacoes = leitura.Valor?.Observacoes?.Trim();
            if (observacoes is { Length: > ObservacoesMax })
                return Results.BadRequest(ErroResponse.De("parâmetros inválidos", [new CampoErro("observacoes", $"no máximo {ObservacoesMax} caracteres")]));
        }

        try
        {
            var sessao = await store.ObterAsync(id, ct);
            if (sessao is null) return Results.Json(ErroResponse.De("experimento não encontrado"), statusCode: StatusCodes.Status404NotFound);
            if (sessao.Status != ExperimentStatuses.Ativa)
                return Results.Json(ErroResponse.De($"o experimento já está {sessao.Status}"), statusCode: StatusCodes.Status409Conflict);

            sessao.Status = novoStatus;
            sessao.FimEm = clock.GetUtcNow();
            if (!string.IsNullOrWhiteSpace(observacoes)) sessao.Observacoes = observacoes;

            // Valores finais: só se o CLP está respondendo agora. Sem comunicação ficam em branco (não se repete o último valor).
            var snap = estado.Snapshot();
            if (snap.Telemetria.PlcOnline)
            {
                sessao.NivelFinal = snap.Telemetria.Nivel;
                sessao.SetpointFinal = snap.Controle.Setpoint;
                sessao.ValvulaFinal = snap.Telemetria.Valvula;
                sessao.ModoManualFinal = snap.Controle.ModoManual;
            }

            await store.AtualizarAsync(sessao, ct);
            return Results.Ok(new { sessao = SessionDto.De(sessao) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha ao finalizar experimento {Id}", id);
            return Results.Json(ErroResponse.De("banco de dados indisponível"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> Detalhar(Guid id, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab,
        TimeProvider clock, ILogger<Program> log, CancellationToken ct)
    {
        if (Indisponivel(store, historico, lab, false) is { } negado) return negado;

        try
        {
            var sessao = await store.ObterAsync(id, ct);
            if (sessao is null) return Results.Json(ErroResponse.De("experimento não encontrado"), statusCode: StatusCodes.Status404NotFound);

            var emAndamento = sessao.Status == ExperimentStatuses.Ativa;
            var fim = sessao.FimEm ?? clock.GetUtcNow();

            // Telemetria da sessão = o que já está persistido na janela (nada é copiado para outra tabela).
            var amostras = await historico.QuerySamplesAsync(sessao.InicioEm, fim, lab.Value.MaxAnalysisSamples, null, ct);
            var eventos = await historico.QueryEventsAsync(sessao.InicioEm, fim, lab.Value.MaxEventsPerSession, null, ct);

            var resumo = ExperimentAnalyzer.Resumir(sessao, fim, amostras.Items, eventos.Items, amostras.HasMore);
            return Results.Ok(new
            {
                sessao = SessionDto.De(sessao),
                janela = new { de = sessao.InicioEm, ate = fim, emAndamento },
                resumo,
                serie = ExperimentAnalyzer.Serie(amostras.Items),
                eventos = eventos.Items.Select(e => new { t = e.OcorridoEm, tipo = e.Tipo.ToString(), e.Detalhe, e.ValorAnterior, e.ValorNovo }),
                eventosTruncados = eventos.HasMore
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha ao consultar experimento {Id}", id);
            return Results.Json(ErroResponse.De("banco de dados indisponível"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
