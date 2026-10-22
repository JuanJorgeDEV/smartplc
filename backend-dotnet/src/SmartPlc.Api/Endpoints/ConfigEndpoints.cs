using System.Globalization;
using Microsoft.Extensions.Options;
using SmartPlc.Api.History;
using SmartPlc.Api.Lab;
using SmartPlc.Api.Options;
using SmartPlc.Api.Services;
using SmartPlc.Api.Validation;

namespace SmartPlc.Api.Endpoints;

public sealed class IntegrationOptions
{
    public const string Section = "Integration";

    /// <summary>Mostra os endereços S7 (DB/offset) no catálogo. Desligue em ambientes públicos se não quiser divulgá-los.</summary>
    public bool ShowAddresses { get; set; } = true;
}

/// <summary>Uma das variáveis do SmartPLC (documentadas em docs/integracao-node-red.md).</summary>
public sealed record VariavelCatalogo(
    string Campo,          // nome no JSON da telemetria
    string Identificador,  // nome da variável no nó s7 do Node-RED
    string Nome,
    string? Endereco,      // endereço S7 (nulo quando oculto por configuração)
    string Tipo,
    string Unidade,
    string Acesso,         // "leitura" ou "leitura e escrita"
    string Descricao);

public static class ConfigEndpoints
{
    /// <summary>Catálogo fixo das 8 variáveis do TQ-101. Os endereços vêm do fluxo do Node-RED da equipe.</summary>
    public static readonly IReadOnlyList<VariavelCatalogo> Catalogo =
    [
        new("nivel", "Nivel_Tanque", "Nível do tanque (PV)", "DB3,REAL0", "REAL", "%", "leitura", "Valor medido do processo."),
        new("valvula", "Valvula_Output", "Saída da válvula (CV)", "DB4,REAL0", "REAL", "%", "leitura", "Abertura da válvula calculada pelo controlador."),
        new("setpoint", "setpoint", "Setpoint (SP)", "DB5,REAL0", "REAL", "%", "leitura e escrita", "Nível desejado."),
        new("modoManual", "modo_manual", "Modo manual", "DB5,X8.0", "BOOL", "", "leitura e escrita", "Verdadeiro = manual; falso = automático."),
        new("kp", "kp", "Ganho proporcional (Kp)", "DB5,REAL10", "REAL", "", "leitura e escrita", "Intensidade da reação ao erro."),
        new("ti", "ti", "Tempo integral (Ti)", "DB5,REAL14", "REAL", "s", "leitura e escrita", "Ação sobre o erro acumulado."),
        new("td", "td", "Tempo derivativo (Td)", "DB5,REAL18", "REAL", "s", "leitura e escrita", "Ação sobre a velocidade do erro."),
        new("valvulaManual", "valvula_manual", "Abertura manual da válvula", "DB5,REAL22", "REAL", "%", "leitura e escrita", "Abertura usada quando o modo é manual.")
    ];

    public static void MapConfigApi(this WebApplication app)
    {
        // Somente o que é seguro divulgar: nenhuma chave, string de conexão ou IP.
        app.MapGet("/api/config", (IOptions<ControlOptions> controle, IOptions<ProcessOptions> processo, IOptions<TelemetryOptions> tel,
            IOptions<HistoryOptions> hist, IHistoryStore historico, IOptions<LabOptions> lab, IExperimentStore experimentos, IOptions<CommandOptions> comando, SmartPlc.Api.Auth.AuthRuntime auth) =>
        {
            var historicoOn = hist.Value.Enabled && historico.Habilitado;
            return Results.Ok(new
            {
                controle = new
                {
                    habilitado = controle.Value.Enabled,
                    exigeChaveDeOperador = !auth.Habilitado && !string.IsNullOrEmpty(controle.Value.OperatorApiKey),
                    confirmacaoSegundos = comando.Value.ConfirmationSeconds,
                    limites = new
                    {
                        setpoint = new[] { Limites.Percentual.Min, Limites.Percentual.Max },
                        valvulaManual = new[] { Limites.Percentual.Min, Limites.Percentual.Max },
                        kp = new[] { Limites.Kp.Min, Limites.Kp.Max },
                        ti = new[] { Limites.Tempo.Min, Limites.Tempo.Max },
                        td = new[] { Limites.Tempo.Min, Limites.Tempo.Max }
                    }
                },
                processo = new
                {
                    nivelBaixo = processo.Value.NivelBaixo,
                    nivelAlto = processo.Value.NivelAlto,
                    obsoletoAposSegundos = tel.Value.StaleAfterSeconds
                },
                historico = new
                {
                    habilitado = historicoOn,
                    periodoMaximoHoras = hist.Value.MaxQueryRangeHours,
                    limitePadrao = hist.Value.DefaultQueryLimit,
                    limiteMaximo = hist.Value.MaxQueryLimit
                },
                laboratorio = new
                {
                    habilitado = lab.Value.Enabled && experimentos.Habilitado && historicoOn,
                    escrita = lab.Value.WriteEnabled
                },
                integracao = new { tipo = "Siemens S7", camada = "Node-RED", variaveisConfiguradas = Catalogo.Count }
            });
        });

        app.MapGet("/api/variaveis", (IOptions<IntegrationOptions> opt) =>
            Results.Ok(new
            {
                enderecosVisiveis = opt.Value.ShowAddresses,
                variaveis = Catalogo.Select(v => v with { Endereco = opt.Value.ShowAddresses ? v.Endereco : null })
            }));

        app.MapGet("/api/comandos", (HttpRequest req, CommandTracker comandos) =>
        {
            var n = 20;
            if (req.Query.TryGetValue("limite", out var l) && (!int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n is < 1 or > 100))
                return Results.BadRequest(Contracts.ErroResponse.De("parâmetros inválidos", [new Contracts.CampoErro("limite", "deve ser um inteiro entre 1 e 100")]));
            comandos.ExpirarVencidos();
            return Results.Ok(new { comandos = comandos.Recentes(n) });
        });

        app.MapGet("/api/comandos/{id:guid}", (Guid id, CommandTracker comandos) =>
        {
            comandos.ExpirarVencidos();
            return comandos.Obter(id) is { } c
                ? Results.Ok(c)
                : Results.Json(Contracts.ErroResponse.De("comando não encontrado (a lista guarda só os mais recentes e é zerada ao reiniciar)"), statusCode: StatusCodes.Status404NotFound);
        });
    }
}
