using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Options;

namespace SmartPlc.Api.Services;

public enum ResultadoEnvio
{
    /// <summary>O Node-RED respondeu 2xx. Isto NÃO prova que o CLP executou o comando.</summary>
    Aceito,
    /// <summary>O Node-RED respondeu, mas com erro (não 2xx).</summary>
    Recusado,
    /// <summary>Sem resposta dentro do prazo.</summary>
    Timeout,
    /// <summary>Conexão recusada, DNS, etc.</summary>
    Inacessivel
}

public interface INodeRedClient
{
    Task<ResultadoEnvio> EnviarComandoAsync(ControleRequest comando, CancellationToken ct);
}

/// <summary>Encaminha comandos validados a POST {ComandoUrl}. Só envia os campos conhecidos, sem nulos.</summary>
public sealed class NodeRedClient(HttpClient http, IOptions<NodeRedOptions> options, ILogger<NodeRedClient> log) : INodeRedClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ResultadoEnvio> EnviarComandoAsync(ControleRequest comando, CancellationToken ct)
    {
        var cfg = options.Value;
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        try
        {
            using var resp = await http.PostAsJsonAsync(cfg.ComandoUrl, comando, Json, limite.Token);
            if (resp.IsSuccessStatusCode) return ResultadoEnvio.Aceito;
            log.LogWarning("Node-RED recusou o comando: HTTP {Status}", (int)resp.StatusCode);
            return ResultadoEnvio.Recusado;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Node-RED sem resposta em {Seg} s", cfg.TimeoutSeconds);
            return ResultadoEnvio.Timeout;
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("Node-RED inacessível: {Msg}", ex.Message);
            return ResultadoEnvio.Inacessivel;
        }
    }
}
