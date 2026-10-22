using System.Text.Json;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Lab;

/// <summary>
/// Formato em que a série de uma sessão SIMULADA é guardada (coluna jsonb experiment_sessions.SerieSimulada).
/// Compacto de propósito: uma sessão chega a 5000 amostras. O instante é gravado em milissegundos desde o início da sessão.
/// </summary>
public static class SimulatedSeries
{
    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web);

    private sealed record Ponto(long T, double Pv, double Cv, double? Sp, bool? M);
    private sealed record Marca(long T, string Tipo, string? D, double? A, double? N);
    private sealed record Documento(int Versao, bool Truncado, List<Ponto> P, List<Marca> E);

    public static string Serializar(DateTimeOffset inicio, IReadOnlyList<TelemetrySample> amostras, IReadOnlyList<ProcessEvent> eventos, bool truncado)
    {
        long Ms(DateTimeOffset t) => (long)Math.Round((t - inicio).TotalMilliseconds);
        var doc = new Documento(1, truncado,
            [.. amostras.Select(a => new Ponto(Ms(a.RecebidoEm), Math.Round(a.Nivel, 3), Math.Round(a.Valvula, 3), a.Setpoint, a.ModoManual))],
            [.. eventos.Select(e => new Marca(Ms(e.OcorridoEm), e.Tipo.ToString(), e.Detalhe, e.ValorAnterior, e.ValorNovo))]);
        return JsonSerializer.Serialize(doc, Opcoes);
    }

    public static (List<TelemetrySample> Amostras, List<ProcessEvent> Eventos, bool Truncado) Ler(DateTimeOffset inicio, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ([], [], false);
        var doc = JsonSerializer.Deserialize<Documento>(json, Opcoes);
        if (doc is null) return ([], [], false);

        var amostras = doc.P.Select((p, i) => new TelemetrySample
        {
            Id = i + 1, RecebidoEm = inicio.AddMilliseconds(p.T), Nivel = p.Pv, Valvula = p.Cv, Setpoint = p.Sp, ModoManual = p.M,
            MotivoGravacao = SampleReason.Periodic
        }).ToList();
        var eventos = doc.E.Select((e, i) => new ProcessEvent
        {
            Id = i + 1, OcorridoEm = inicio.AddMilliseconds(e.T),
            Tipo = Enum.TryParse<ProcessEventType>(e.Tipo, out var t) ? t : ProcessEventType.PidAlterado,
            Detalhe = e.D, ValorAnterior = e.A, ValorNovo = e.N
        }).ToList();
        return (amostras, eventos, doc.Truncado);
    }
}
