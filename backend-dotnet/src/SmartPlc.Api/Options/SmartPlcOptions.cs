namespace SmartPlc.Api.Options;

/// <summary>Limiares do processo. Antes ficavam fixos em server.js (10 e 90).</summary>
public sealed class ProcessOptions
{
    public const string Section = "Process";

    public double NivelBaixo { get; set; } = 10.0;
    public double NivelAlto { get; set; } = 90.0;

    /// <summary>Faixa aceita para nível e válvula vindos do Node-RED (escala 0–100 %).</summary>
    public double LeituraMin { get; set; } = 0.0;
    public double LeituraMax { get; set; } = 100.0;
}

public sealed class TelemetryOptions
{
    public const string Section = "Telemetry";

    /// <summary>Sem telemetria por mais que isso => plcOnline = false.</summary>
    public int StaleAfterSeconds { get; set; } = 5;
}

public sealed class IngestOptions
{
    public const string Section = "Ingest";

    /// <summary>Chave esperada no cabeçalho X-Api-Key de POST /api/telemetria. Vazia = aberto (somente em Development).</summary>
    public string ApiKey { get; set; } = "";
}

public static class DeliveryModes
{
    public const string Push = "push";
    public const string Queue = "queue";
}

public sealed class ControlOptions
{
    public const string Section = "Control";

    /// <summary>Desligado por padrão: o sistema nasce como supervisório somente leitura.</summary>
    public bool Enabled { get; set; }

    /// <summary>Chave do operador (X-Operator-Key). Só vale com o login desligado; com login, o papel manda.</summary>
    public string OperatorApiKey { get; set; } = "";

    /// <summary>
    /// Como o comando chega ao Node-RED. "push" (padrão, uso local): o servidor chama o Node-RED. "queue" (nuvem): o servidor
    /// guarda o comando e o Node-RED o recebe na RESPOSTA da próxima telemetria que enviar. Só a segunda funciona com o servidor
    /// no Render, porque a conexão sempre parte do Node-RED (o servidor não alcança a máquina dele).
    /// </summary>
    public string Delivery { get; set; } = DeliveryModes.Push;
}

public sealed class NodeRedOptions
{
    public const string Section = "NodeRed";

    public string ComandoUrl { get; set; } = "http://127.0.0.1:1880/api/comando-clp";
    public int TimeoutSeconds { get; set; } = 2;
}

public sealed class CorsOptions
{
    public const string Section = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}
