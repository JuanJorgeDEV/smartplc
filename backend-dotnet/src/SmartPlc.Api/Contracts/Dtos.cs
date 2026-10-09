using System.Text.Json.Serialization;
using SmartPlc.Api.Json;

namespace SmartPlc.Api.Contracts;

/// <summary>
/// Corpo de POST /api/telemetria (Node-RED -> backend). Campos extras são ignorados.
/// nivel e valvula são obrigatórios; o restante é opcional, como no Express.
/// </summary>
public sealed class TelemetriaRequest
{
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Nivel { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Valvula { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Setpoint { get; set; }
    [JsonConverter(typeof(LenientBoolConverter))] public bool? ModoManual { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? ValvulaManual { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Kp { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Ti { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Td { get; set; }

    /// <summary>
    /// Opcional. Hora em que o CLP/Node-RED leu os valores (ISO-8601 com fuso, ou milissegundos Unix).
    /// O fluxo atual não envia; quando existir, separa "hora da aquisição" de "hora do recebimento" no histórico.
    /// </summary>
    [JsonConverter(typeof(LenientTimestampConverter))] public DateTimeOffset? AdquiridoEm { get; set; }
}

/// <summary>
/// Corpo de POST /api/controle (dashboard -> backend). Ao contrário da telemetria,
/// campos desconhecidos são rejeitados: nada que o usuário não pediu segue para o Node-RED.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ControleRequest
{
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Setpoint { get; set; }
    [JsonConverter(typeof(LenientBoolConverter))] public bool? ModoManual { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? ValvulaManual { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Kp { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Ti { get; set; }
    [JsonConverter(typeof(LenientDoubleConverter))] public double? Td { get; set; }
}

// ---------- Respostas (mesmo formato do Express, com campos aditivos) ----------

public sealed record LedsDto(bool Low, bool Normal, bool High);

/// <param name="Timestamp">Instante (UTC) do último recebimento; null antes da primeira leitura.</param>
/// <param name="PlcOnline">Aditivo: houve telemetria dentro do prazo configurado.</param>
/// <param name="IdadeSegundos">Aditivo: segundos desde a última telemetria (null se nunca houve).</param>
public sealed record TelemetriaDto(
    double Nivel,
    double Valvula,
    LedsDto Leds,
    DateTimeOffset? Timestamp,
    bool PlcOnline,
    double? IdadeSegundos);

public sealed record ControleDto(
    double Setpoint,
    bool ModoManual,
    double ValvulaManual,
    double Kp,
    double Ti,
    double Td);

/// <summary>Formato de GET /api/status e do evento telemetria_update.</summary>
public sealed record StatusResponse(TelemetriaDto Telemetria, ControleDto Controle);

public sealed record CampoErro(string Campo, string Mensagem);

public sealed record ErroResponse(string Status, string Mensagem, IReadOnlyList<CampoErro>? Erros = null)
{
    public static ErroResponse De(string mensagem, IReadOnlyList<CampoErro>? erros = null) => new("erro", mensagem, erros);
}
