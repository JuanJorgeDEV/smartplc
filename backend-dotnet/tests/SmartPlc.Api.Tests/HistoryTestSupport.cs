using SmartPlc.Api.History;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

/// <summary>Armazenamento em memória com a mesma semântica de consulta do EfHistoryStore (a real é validada no PostgreSQL).</summary>
public class InMemoryHistoryStore : IHistoryStore
{
    private long _id;
    public bool Habilitado { get; set; } = true;
    public List<TelemetrySample> Samples { get; } = [];
    public List<ProcessEvent> Events { get; } = [];
    public int SaveCalls { get; private set; }

    /// <summary>Quantas chamadas a SaveAsync devem falhar antes de voltar a funcionar (-1 = sempre falha).</summary>
    public int FailNext { get; set; }
    public Exception? FailWith { get; set; }
    public bool FailQueries { get; set; }

    public virtual Task<SaveResult> SaveAsync(IReadOnlyList<TelemetrySample> samples, IReadOnlyList<ProcessEvent> events, CancellationToken ct)
    {
        SaveCalls++;
        if (FailNext != 0)
        {
            if (FailNext > 0) FailNext--;
            throw FailWith ?? new InvalidOperationException("banco indisponível (simulado)");
        }

        int dup = 0, gravadas = 0;
        foreach (var s in samples)
        {
            if (s.AdquiridoEm is { } aq && Samples.Any(x => x.AdquiridoEm == aq)) { dup++; continue; }
            s.Id = ++_id;
            Samples.Add(s);
            gravadas++;
        }
        foreach (var e in events) { e.Id = ++_id; Events.Add(e); }
        return Task.FromResult(new SaveResult(gravadas, events.Count, dup));
    }

    public Task<Page<TelemetrySample>> QuerySamplesAsync(DateTimeOffset from, DateTimeOffset to, int limit, Cursor? after, CancellationToken ct)
    {
        if (FailQueries) throw new InvalidOperationException("banco indisponível (simulado)");
        var q = Samples.Where(s => s.RecebidoEm >= from && s.RecebidoEm <= to);
        if (after is { } c) q = q.Where(s => s.RecebidoEm > c.At || (s.RecebidoEm == c.At && s.Id > c.Id));
        var itens = q.OrderBy(s => s.RecebidoEm).ThenBy(s => s.Id).Take(limit + 1).ToList();
        return Task.FromResult(itens.Count > limit ? new Page<TelemetrySample>(itens.Take(limit).ToList(), true) : new Page<TelemetrySample>(itens, false));
    }

    public Task<Page<ProcessEvent>> QueryEventsAsync(DateTimeOffset from, DateTimeOffset to, int limit, Cursor? after, CancellationToken ct)
    {
        if (FailQueries) throw new InvalidOperationException("banco indisponível (simulado)");
        var q = Events.Where(e => e.OcorridoEm >= from && e.OcorridoEm <= to);
        if (after is { } c) q = q.Where(e => e.OcorridoEm > c.At || (e.OcorridoEm == c.At && e.Id > c.Id));
        var itens = q.OrderBy(e => e.OcorridoEm).ThenBy(e => e.Id).Take(limit + 1).ToList();
        return Task.FromResult(itens.Count > limit ? new Page<ProcessEvent>(itens.Take(limit).ToList(), true) : new Page<ProcessEvent>(itens, false));
    }

    public DateTimeOffset? LastSamplesCutoff { get; private set; }
    public DateTimeOffset? LastEventsCutoff { get; private set; }

    public Task<(int Samples, int Events)> DeleteExpiredAsync(DateTimeOffset samplesBefore, DateTimeOffset eventsBefore, int batchSize, CancellationToken ct)
    {
        LastSamplesCutoff = samplesBefore;
        LastEventsCutoff = eventsBefore;
        if (FailNext != 0) throw new InvalidOperationException("banco indisponível (simulado)");
        var a = Samples.RemoveAll(s => s.RecebidoEm < samplesBefore);
        var e = Events.RemoveAll(x => x.OcorridoEm < eventsBefore);
        return Task.FromResult((a, e));
    }
}

/// <summary>[Fact] que só roda se SMARTPLC_TEST_DB apontar para um PostgreSQL de TESTE (ver docs/testes.md).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variavel = "SMARTPLC_TEST_DB";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variavel)))
            Skip = $"defina {Variavel} com a connection string de um PostgreSQL de teste (banco com 'test' no nome)";
    }
}
