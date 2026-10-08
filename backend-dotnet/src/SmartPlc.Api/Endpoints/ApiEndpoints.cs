using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Options;
using SmartPlc.Api.Services;
using SmartPlc.Api.Validation;

namespace SmartPlc.Api.Endpoints;

public static class ApiEndpoints
{
    public const string HeaderIngest = "X-Api-Key";
    public const string HeaderOperador = "X-Operator-Key";

    public static void MapSmartPlcApi(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/status", (ProcessStateStore store) => Results.Ok(store.Snapshot()));
        app.MapPost("/api/telemetria", ReceberTelemetria);
        app.MapPost("/api/controle", EnviarControle);
        app.MapHub<TelemetriaHub>(TelemetriaHub.Rota);
    }

    // Node-RED -> backend
    private static async Task<IResult> ReceberTelemetria(
        HttpRequest req,
        ProcessStateStore store,
        ITelemetryNotifier notifier,
        StalenessMonitor monitor,
        IOptions<IngestOptions> ingest,
        IOptions<ProcessOptions> process,
        ILogger<Program> log,
        CancellationToken ct)
    {
        if (!ChaveValida(req, HeaderIngest, ingest.Value.ApiKey))
            return Results.Json(ErroResponse.De("chave de API inválida ou ausente"), statusCode: StatusCodes.Status401Unauthorized);

        var leitura = await PayloadReader.LerAsync<TelemetriaRequest>(req.Body, ct);
        if (leitura.Excedeu) return PayloadGrande();
        var dados = leitura.Valor;
        var erros = leitura.Erros ?? TelemetriaValidator.Validar(dados!, process.Value);
        if (erros.Count > 0)
        {
            log.LogWarning("Telemetria rejeitada: {Erros}", string.Join("; ", erros.Select(e => $"{e.Campo}: {e.Mensagem}")));
            return Results.BadRequest(ErroResponse.De("telemetria inválida", erros));
        }

        var status = store.ApplyTelemetria(dados!);
        monitor.MarcarEnviado(status);
        await notifier.NotificarAsync(status, ct);
        return Results.Ok(new { status = "ok" });
    }

    // Dashboard -> backend -> Node-RED
    private static async Task<IResult> EnviarControle(
        HttpRequest req,
        ProcessStateStore store,
        ITelemetryNotifier notifier,
        INodeRedClient nodeRed,
        IOptions<ControlOptions> control,
        ILogger<Program> log,
        CancellationToken ct)
    {
        if (!control.Value.Enabled)
            return Results.Json(ErroResponse.De("controle desabilitado neste ambiente (modo supervisório)"), statusCode: StatusCodes.Status403Forbidden);

        if (!ChaveValida(req, HeaderOperador, control.Value.OperatorApiKey))
            return Results.Json(ErroResponse.De("chave de operador inválida ou ausente"), statusCode: StatusCodes.Status401Unauthorized);

        var leitura = await PayloadReader.LerAsync<ControleRequest>(req.Body, ct);
        if (leitura.Excedeu) return PayloadGrande();
        var comando = leitura.Valor;
        var erros = leitura.Erros ?? ControleValidator.Validar(comando!);
        if (erros.Count > 0)
            return Results.BadRequest(ErroResponse.De("comando inválido", erros));

        var resultado = await nodeRed.EnviarComandoAsync(comando!, ct);
        switch (resultado)
        {
            case ResultadoEnvio.Aceito:
                // Estado só muda DEPOIS que o Node-RED aceitou. A confirmação real vem na próxima telemetria.
                var status = store.ApplyControle(comando!);
                await notifier.NotificarAsync(status, ct);
                log.LogInformation("Comando encaminhado ao Node-RED: {@Comando}", comando);
                return Results.Ok(new
                {
                    status = "ok",
                    controle = status.Controle,
                    estadoComando = "encaminhado",
                    confirmadoPeloClp = false
                });

            case ResultadoEnvio.Timeout:
                return Results.Json(ErroResponse.De("Node-RED sem resposta (tempo esgotado)"), statusCode: StatusCodes.Status504GatewayTimeout);

            case ResultadoEnvio.Recusado:
                return Results.Json(ErroResponse.De("Node-RED não aceitou o comando"), statusCode: StatusCodes.Status502BadGateway);

            default:
                return Results.Json(ErroResponse.De("Node-RED inacessível"), statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult PayloadGrande() =>
        Results.Json(ErroResponse.De($"corpo maior que {PayloadReader.MaxBytes} bytes"), statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>Chave vazia na configuração = verificação desligada (a inicialização impede isso fora de Development).</summary>
    internal static bool ChaveValida(HttpRequest req, string header, string esperada)
    {
        if (string.IsNullOrEmpty(esperada)) return true;
        var recebida = req.Headers[header].ToString();
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(recebida));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(esperada));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
