using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Json;
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

    /// <summary>clp (padrão) ou simulacao.</summary>
    public string? Fonte { get; set; }

    /// <summary>Só vale com fonte = simulacao. Tudo opcional.</summary>
    public SimulacaoRequest? Simulacao { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SimulacaoRequest
{
    [JsonConverter(typeof(LenientDoubleConverter))] public double? NivelInicial { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Setpoint { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Kp { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Ti { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Td { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Velocidade { get; set; }
}

/// <summary>Alterações feitas pela pessoa durante uma sessão simulada. Só afetam a simulação, nunca o CLP.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ComandoSimulacaoRequest
{
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Setpoint { get; set; }
    [JsonConverter(typeof(LenientBoolConverter))] public bool? ModoManual { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? ValvulaManual { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Kp { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Ti { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Td { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Perturbacao { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Velocidade { get; set; }
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
        app.MapPost("/api/experimentos/{id:guid}/simulacao", ComandarSimulacao);
    }

    private static void ValidarSimulacao(List<CampoErro> erros, SimulacaoRequest? s)
    {
        if (s is null) return;
        TelemetriaValidator.Faixa(erros, "simulacao.nivelInicial", s.NivelInicial, Limites.Percentual);
        TelemetriaValidator.Faixa(erros, "simulacao.setpoint", s.Setpoint, Limites.Percentual);
        TelemetriaValidator.Faixa(erros, "simulacao.kp", s.Kp, Limites.Kp);
        TelemetriaValidator.Faixa(erros, "simulacao.ti", s.Ti, Limites.Tempo);
        TelemetriaValidator.Faixa(erros, "simulacao.td", s.Td, Limites.Tempo);
        if (s.Velocidade is { } v && !SimulationService.Velocidades.Contains(v))
            erros.Add(new CampoErro("simulacao.velocidade", $"use {string.Join(", ", SimulationService.Velocidades)}"));
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

    private static async Task<IResult> Listar(HttpRequest req, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab,
        SimulationService simulacao, TimeProvider clock, ILogger<Program> log, CancellationToken ct)
    {
        if (Indisponivel(store, historico, lab, false) is { } negado) return negado;
        await CancelarOrfasAsync(store, simulacao, clock, log, ct);

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
        ProcessStateStore estado, SimulationService simulacao, TimeProvider clock, ILogger<Program> log, CancellationToken ct)
    {
        if (Indisponivel(store, historico, lab, true) is { } negado) return negado;

        var leitura = await PayloadReader.LerAsync<NovaSessaoRequest>(req.Body, ct);
        if (leitura.Excedeu) return Results.Json(ErroResponse.De("corpo muito grande"), statusCode: StatusCodes.Status413PayloadTooLarge);
        var erros = new List<CampoErro>(leitura.Erros ?? []);
        var dados = leitura.Valor;

        string? tipo = null, fonte = ExperimentSources.Clp;
        if (dados is not null)
        {
            if (dados.Fonte is { } f)
            {
                fonte = ExperimentSources.Todas.FirstOrDefault(x => string.Equals(x, f.Trim(), StringComparison.OrdinalIgnoreCase));
                if (fonte is null) erros.Add(new CampoErro("fonte", $"use {string.Join(" ou ", ExperimentSources.Todas)}"));
            }
            if (dados.Simulacao is not null && fonte != ExperimentSources.Simulacao)
                erros.Add(new CampoErro("simulacao", "só pode ser informada quando a fonte é simulacao"));
            ValidarSimulacao(erros, dados.Simulacao);
            tipo = ExperimentTypes.Todos.FirstOrDefault(t => string.Equals(t, dados.Tipo?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (tipo is null) erros.Add(new CampoErro("tipo", $"use {string.Join(" ou ", ExperimentTypes.Todos)}"));
            if (dados.Nome is { } n && n.Trim().Length > NomeMax) erros.Add(new CampoErro("nome", $"no máximo {NomeMax} caracteres"));
            if (dados.Observacoes is { } o && o.Trim().Length > ObservacoesMax) erros.Add(new CampoErro("observacoes", $"no máximo {ObservacoesMax} caracteres"));
        }
        if (erros.Count > 0) return Results.BadRequest(ErroResponse.De("experimento inválido", erros));

        var agora = clock.GetUtcNow();
        var simulada = fonte == ExperimentSources.Simulacao;
        SimulationState? estadoSim = null;
        var id = Guid.NewGuid();

        // Uma simulação interrompida (servidor reiniciado) não pode bloquear a próxima: é cancelada antes.
        if (simulada) await CancelarOrfasAsync(store, simulacao, clock, log, ct);

        // CLP: os valores de partida vêm do que o servidor recebeu, nunca do navegador. Simulação: do próprio simulador.
        var snap = estado.Snapshot();
        if (!simulada && !snap.Telemetria.PlcOnline)
            return Results.Json(ErroResponse.De("sem telemetria válida do CLP: conecte o Node-RED e aguarde a primeira leitura antes de iniciar, ou use a simulação"),
                statusCode: StatusCodes.Status409Conflict);

        if (simulada)
        {
            var sp = dados!.Simulacao;
            var nivelIni = sp?.NivelInicial ?? 30;
            try
            {
                estadoSim = simulacao.Iniciar(id, agora, new SimulationParameters(
                    nivelIni, sp?.Setpoint ?? nivelIni, sp?.Kp ?? 2, sp?.Ti ?? 30, sp?.Td ?? 0, sp?.Velocidade ?? 1));
            }
            catch (InvalidOperationException)
            {
                return Results.Json(new { status = "erro", mensagem = "já existe um experimento em andamento; encerre-o antes de iniciar outro", sessaoAtiva = simulacao.IdAtiva },
                    statusCode: StatusCodes.Status409Conflict);
            }
        }

        var nome = string.IsNullOrWhiteSpace(dados!.Nome)
            ? $"{ExperimentTypes.Rotulo(tipo!)} {agora:yyyy-MM-dd HH:mm} UTC"
            : dados.Nome.Trim();
        var sessao = new ExperimentSession
        {
            Id = id,
            Nome = nome,
            Tipo = tipo!,
            Status = ExperimentStatuses.Ativa,
            InicioEm = agora,
            Fonte = fonte!,
            PlcOnlineInicial = !simulada,
            NivelInicial = simulada ? estadoSim!.Nivel : snap.Telemetria.Nivel,
            SetpointInicial = simulada ? estadoSim!.Setpoint : snap.Controle.Setpoint,
            ValvulaInicial = simulada ? estadoSim!.Valvula : snap.Telemetria.Valvula,
            ModoManualInicial = simulada ? estadoSim!.Manual : snap.Controle.ModoManual,
            Observacoes = string.IsNullOrWhiteSpace(dados.Observacoes) ? null : dados.Observacoes.Trim()
        };

        try
        {
            await store.CriarAsync(sessao, ct);
            return Results.Created($"/api/experimentos/{sessao.Id}", new { sessao = SessionDto.De(sessao) });
        }
        catch (ExperimentConflictException ex)
        {
            if (simulada) simulacao.Descartar(id);
            return Results.Json(new { status = "erro", mensagem = "já existe um experimento em andamento; encerre-o antes de iniciar outro", sessaoAtiva = ex.SessaoAtiva },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (simulada) simulacao.Descartar(id);
            log.LogError(ex, "Falha ao iniciar experimento");
            return Results.Json(ErroResponse.De("banco de dados indisponível"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }


    private static Task<IResult> Encerrar(Guid id, HttpRequest req, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab,
        ProcessStateStore estado, SimulationService simulacao, TimeProvider clock, ILogger<Program> log, CancellationToken ct) =>
        Finalizar(id, req, ExperimentStatuses.Encerrada, store, historico, lab, estado, simulacao, clock, log, ct);

    private static Task<IResult> Cancelar(Guid id, HttpRequest req, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab,
        ProcessStateStore estado, SimulationService simulacao, TimeProvider clock, ILogger<Program> log, CancellationToken ct) =>
        Finalizar(id, req, ExperimentStatuses.Cancelada, store, historico, lab, estado, simulacao, clock, log, ct);

    private static async Task<IResult> Finalizar(Guid id, HttpRequest req, string novoStatus, IExperimentStore store, IHistoryStore historico,
        IOptions<LabOptions> lab, ProcessStateStore estado, SimulationService simulacao, TimeProvider clock, ILogger<Program> log, CancellationToken ct)
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

            if (sessao.Fonte == ExperimentSources.Simulacao)
            {
                // Cancelar descarta a série; encerrar a guarda. O fim da sessão é o instante SIMULADO (a velocidade pode ser maior que 1×).
                var snapSim = novoStatus == ExperimentStatuses.Encerrada ? simulacao.Encerrar(id) : null;
                if (novoStatus == ExperimentStatuses.Cancelada) simulacao.Descartar(id);
                if (snapSim is not null)
                {
                    var f = snapSim.Estado;
                    sessao.FimEm = snapSim.Inicio.AddSeconds(f.Decorrido);
                    sessao.NivelFinal = f.Nivel; sessao.SetpointFinal = f.Setpoint; sessao.ValvulaFinal = f.Valvula; sessao.ModoManualFinal = f.Manual;
                    sessao.SerieSimulada = SimulatedSeries.Serializar(snapSim.Inicio, snapSim.Amostras, snapSim.Eventos, f.Truncado);
                }
                else if (novoStatus == ExperimentStatuses.Encerrada)
                {
                    // A simulação não está mais na memória (o servidor reiniciou): não há o que resumir.
                    sessao.Status = ExperimentStatuses.Cancelada;
                    sessao.Observacoes = Truncar((sessao.Observacoes is null ? "" : sessao.Observacoes + " · ") + "simulação interrompida por reinício do servidor");
                }
            }
            else
            {
                // Valores finais: só se o CLP está respondendo agora. Sem comunicação ficam em branco (não se repete o último valor).
                var snap = estado.Snapshot();
                if (snap.Telemetria.PlcOnline)
                {
                    sessao.NivelFinal = snap.Telemetria.Nivel;
                    sessao.SetpointFinal = snap.Controle.Setpoint;
                    sessao.ValvulaFinal = snap.Telemetria.Valvula;
                    sessao.ModoManualFinal = snap.Controle.ModoManual;
                }
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

    private static string Truncar(string texto) => texto.Length <= ObservacoesMax ? texto : texto[..ObservacoesMax];

    /// <summary>
    /// Sessão simulada marcada como ativa no banco, mas sem execução em memória (o servidor reiniciou): é cancelada,
    /// para não bloquear novas sessões nem mostrar uma simulação que já não existe.
    /// </summary>
    internal static async Task<bool> CancelarOrfasAsync(IExperimentStore store, SimulationService simulacao, TimeProvider clock, ILogger log, CancellationToken ct)
    {
        try
        {
            var ativa = await store.ObterAtivaAsync(ct);
            if (ativa is not { Fonte: ExperimentSources.Simulacao } || simulacao.Ativa(ativa.Id)) return false;
            ativa.Status = ExperimentStatuses.Cancelada;
            ativa.FimEm = clock.GetUtcNow();
            ativa.Observacoes = Truncar((ativa.Observacoes is null ? "" : ativa.Observacoes + " · ") + "simulação interrompida por reinício do servidor");
            await store.AtualizarAsync(ativa, ct);
            log.LogWarning("Sessão simulada {Id} cancelada: a execução em memória não existe mais.", ativa.Id);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Não foi possível verificar sessões simuladas órfãs");
            return false;
        }
    }

    private static async Task<IResult> ComandarSimulacao(Guid id, HttpRequest req, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab,
        SimulationService simulacao, ILogger<Program> log, CancellationToken ct)
    {
        if (Indisponivel(store, historico, lab, true) is { } negado) return negado;

        var leitura = await PayloadReader.LerAsync<ComandoSimulacaoRequest>(req.Body, ct);
        if (leitura.Excedeu) return Results.Json(ErroResponse.De("corpo muito grande"), statusCode: StatusCodes.Status413PayloadTooLarge);
        var erros = new List<CampoErro>(leitura.Erros ?? []);
        var c = leitura.Valor;
        if (c is not null)
        {
            var cmd = new SimulationCommand(c.Setpoint, c.ModoManual, c.ValvulaManual, c.Kp, c.Ti, c.Td, c.Perturbacao, c.Velocidade);
            if (cmd.Vazio) erros.Add(new CampoErro("$", "informe ao menos um campo"));
            TelemetriaValidator.Faixa(erros, "setpoint", c.Setpoint, Limites.Percentual);
            TelemetriaValidator.Faixa(erros, "valvulaManual", c.ValvulaManual, Limites.Percentual);
            TelemetriaValidator.Faixa(erros, "perturbacao", c.Perturbacao, Limites.Percentual);
            TelemetriaValidator.Faixa(erros, "kp", c.Kp, Limites.Kp);
            TelemetriaValidator.Faixa(erros, "ti", c.Ti, Limites.Tempo);
            TelemetriaValidator.Faixa(erros, "td", c.Td, Limites.Tempo);
            if (c.Velocidade is { } v && !SimulationService.Velocidades.Contains(v))
                erros.Add(new CampoErro("velocidade", $"use {string.Join(", ", SimulationService.Velocidades)}"));
        }
        if (erros.Count > 0) return Results.BadRequest(ErroResponse.De("comando inválido", erros));

        try
        {
            var sessao = await store.ObterAsync(id, ct);
            if (sessao is null) return Results.Json(ErroResponse.De("experimento não encontrado"), statusCode: StatusCodes.Status404NotFound);
            if (sessao.Fonte != ExperimentSources.Simulacao)
                return Results.Json(ErroResponse.De("este experimento usa o CLP: os comandos só valem em sessões de simulação"), statusCode: StatusCodes.Status409Conflict);
            if (sessao.Status != ExperimentStatuses.Ativa || !simulacao.Ativa(id))
                return Results.Json(ErroResponse.De("a simulação não está em andamento"), statusCode: StatusCodes.Status409Conflict);

            var estado = simulacao.Comandar(id, new SimulationCommand(c!.Setpoint, c.ModoManual, c.ValvulaManual, c.Kp, c.Ti, c.Td, c.Perturbacao, c.Velocidade));
            return Results.Ok(new { status = "ok", simulacao = estado });
        }
        catch (InvalidOperationException)
        {
            return Results.Json(ErroResponse.De("a simulação não está em andamento"), statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha ao comandar a simulação {Id}", id);
            return Results.Json(ErroResponse.De("banco de dados indisponível"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> Detalhar(Guid id, IExperimentStore store, IHistoryStore historico, IOptions<LabOptions> lab,
        SimulationService simulacao, TimeProvider clock, ILogger<Program> log, CancellationToken ct)
    {
        if (Indisponivel(store, historico, lab, false) is { } negado) return negado;

        try
        {
            var sessao = await store.ObterAsync(id, ct);
            if (sessao is null) return Results.Json(ErroResponse.De("experimento não encontrado"), statusCode: StatusCodes.Status404NotFound);

            var emAndamento = sessao.Status == ExperimentStatuses.Ativa;
            SimulationState? estadoSim = null;
            DateTimeOffset fim;
            IReadOnlyList<TelemetrySample> amostras;
            IReadOnlyList<ProcessEvent> eventos;
            bool truncado, eventosTruncados;

            if (sessao.Fonte == ExperimentSources.Simulacao)
            {
                // Simulação: a série vem da memória (em andamento) ou do que foi guardado ao encerrar. Nunca do histórico do CLP.
                if (emAndamento && simulacao.Snapshot(id) is { } snapSim)
                {
                    estadoSim = snapSim.Estado;
                    fim = snapSim.Inicio.AddSeconds(snapSim.Estado.Decorrido);
                    amostras = snapSim.Amostras; eventos = snapSim.Eventos; truncado = snapSim.Estado.Truncado;
                }
                else
                {
                    if (emAndamento)
                    {
                        // Ativa no banco, mas sem execução: o servidor reiniciou. Cancela e mostra o que existe (nada).
                        await CancelarOrfasAsync(store, simulacao, clock, log, ct);
                        sessao = await store.ObterAsync(id, ct) ?? sessao;
                        emAndamento = false;
                    }
                    var (a, e, t) = SimulatedSeries.Ler(sessao.InicioEm, sessao.SerieSimulada);
                    fim = sessao.FimEm ?? sessao.InicioEm;
                    amostras = a; eventos = e; truncado = t;
                }
                eventosTruncados = eventos.Count >= SimulationService.MaxEventos;
            }
            else
            {
                fim = sessao.FimEm ?? clock.GetUtcNow();
                // Telemetria da sessão = o que já está persistido na janela (nada é copiado para outra tabela).
                var am = await historico.QuerySamplesAsync(sessao.InicioEm, fim, lab.Value.MaxAnalysisSamples, null, ct);
                var ev = await historico.QueryEventsAsync(sessao.InicioEm, fim, lab.Value.MaxEventsPerSession, null, ct);
                amostras = am.Items; truncado = am.HasMore;
                eventos = ev.Items; eventosTruncados = ev.HasMore;
            }

            var resumo = ExperimentAnalyzer.Resumir(sessao, fim, amostras, eventos, truncado);
            return Results.Ok(new
            {
                sessao = SessionDto.De(sessao),
                janela = new { de = sessao.InicioEm, ate = fim, emAndamento },
                simulacao = estadoSim,
                resumo,
                serie = ExperimentAnalyzer.Serie(amostras),
                eventos = eventos.Select(e => new { t = e.OcorridoEm, tipo = e.Tipo.ToString(), e.Detalhe, e.ValorAnterior, e.ValorNovo }),
                eventosTruncados
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha ao consultar experimento {Id}", id);
            return Results.Json(ErroResponse.De("banco de dados indisponível"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
