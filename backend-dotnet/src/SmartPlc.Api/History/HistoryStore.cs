using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.History;

public readonly record struct SaveResult(int Samples, int Events, int Duplicates);

/// <summary>Posição de continuação de uma consulta paginada (ordenação por hora e depois Id).</summary>
public readonly record struct Cursor(DateTimeOffset At, long Id);

public sealed record Page<T>(IReadOnlyList<T> Items, bool HasMore);

/// <summary>Acesso ao histórico. A implementação real usa EF Core + Npgsql; os testes usam uma em memória.</summary>
public interface IHistoryStore
{
    /// <summary>false => histórico desligado (sem connection string ou History:Enabled=false).</summary>
    bool Habilitado { get; }

    /// <summary>Grava tudo ou lança exceção. Não pode devolver "sucesso" antes de o banco confirmar.</summary>
    Task<SaveResult> SaveAsync(IReadOnlyList<TelemetrySample> samples, IReadOnlyList<ProcessEvent> events, CancellationToken ct);

    Task<Page<TelemetrySample>> QuerySamplesAsync(DateTimeOffset from, DateTimeOffset to, int limit, Cursor? after, CancellationToken ct);
    Task<Page<ProcessEvent>> QueryEventsAsync(DateTimeOffset from, DateTimeOffset to, int limit, Cursor? after, CancellationToken ct);

    /// <summary>Apaga em lotes o que for mais antigo que os limites. Devolve (amostras, eventos) apagados.</summary>
    Task<(int Samples, int Events)> DeleteExpiredAsync(DateTimeOffset samplesBefore, DateTimeOffset eventsBefore, int batchSize, CancellationToken ct);
}

public sealed class DisabledHistoryStore : IHistoryStore
{
    public bool Habilitado => false;
    private static Exception Off() => new InvalidOperationException("Histórico desabilitado.");
    public Task<SaveResult> SaveAsync(IReadOnlyList<TelemetrySample> s, IReadOnlyList<ProcessEvent> e, CancellationToken ct) => throw Off();
    public Task<Page<TelemetrySample>> QuerySamplesAsync(DateTimeOffset f, DateTimeOffset t, int l, Cursor? a, CancellationToken ct) => throw Off();
    public Task<Page<ProcessEvent>> QueryEventsAsync(DateTimeOffset f, DateTimeOffset t, int l, Cursor? a, CancellationToken ct) => throw Off();
    public Task<(int, int)> DeleteExpiredAsync(DateTimeOffset a, DateTimeOffset b, int n, CancellationToken ct) => throw Off();
}

public sealed class EfHistoryStore(IDbContextFactory<SmartPlcDbContext> factory, ILogger<EfHistoryStore> log) : IHistoryStore
{
    private const string UniqueViolation = "23505";

    public bool Habilitado => true;

    public async Task<SaveResult> SaveAsync(IReadOnlyList<TelemetrySample> samples, IReadOnlyList<ProcessEvent> events, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.TelemetrySamples.AddRange(samples);
        db.ProcessEvents.AddRange(events);

        try
        {
            await db.SaveChangesAsync(ct); // uma transação: ou grava tudo ou nada
            return new SaveResult(samples.Count, events.Count, 0);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation } && samples.Count > 0)
        {
            // Mesma hora de aquisição já gravada (reentrega, ou reinício do servidor): descarta só a amostra repetida.
            log.LogInformation("Amostra duplicada ignorada (AdquiridoEm já existe)");
            db.ChangeTracker.Clear();
            db.ProcessEvents.AddRange(events.Select(CopiaSemId));
            if (events.Count > 0) await db.SaveChangesAsync(ct);
            return new SaveResult(0, events.Count, samples.Count);
        }
    }

    // Depois do primeiro SaveChanges falho as entidades podem ter ficado marcadas; cópias limpas evitam efeitos colaterais.
    private static ProcessEvent CopiaSemId(ProcessEvent e) => new()
    {
        OcorridoEm = e.OcorridoEm, Tipo = e.Tipo, Detalhe = e.Detalhe, ValorAnterior = e.ValorAnterior, ValorNovo = e.ValorNovo
    };

    public async Task<Page<TelemetrySample>> QuerySamplesAsync(DateTimeOffset from, DateTimeOffset to, int limit, Cursor? after, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.TelemetrySamples.AsNoTracking().Where(s => s.RecebidoEm >= from && s.RecebidoEm <= to);
        if (after is { } c)
            q = q.Where(s => s.RecebidoEm > c.At || (s.RecebidoEm == c.At && s.Id > c.Id));

        var itens = await q.OrderBy(s => s.RecebidoEm).ThenBy(s => s.Id).Take(limit + 1).ToListAsync(ct);
        return Cortar(itens, limit);
    }

    public async Task<Page<ProcessEvent>> QueryEventsAsync(DateTimeOffset from, DateTimeOffset to, int limit, Cursor? after, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.ProcessEvents.AsNoTracking().Where(e => e.OcorridoEm >= from && e.OcorridoEm <= to);
        if (after is { } c)
            q = q.Where(e => e.OcorridoEm > c.At || (e.OcorridoEm == c.At && e.Id > c.Id));

        var itens = await q.OrderBy(e => e.OcorridoEm).ThenBy(e => e.Id).Take(limit + 1).ToListAsync(ct);
        return Cortar(itens, limit);
    }

    public async Task<(int Samples, int Events)> DeleteExpiredAsync(DateTimeOffset samplesBefore, DateTimeOffset eventsBefore, int batchSize, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        int amostras = 0, eventos = 0, n;

        // Lotes pequenos: cada DELETE é uma transação curta, sem travar o banco por muito tempo.
        do
        {
            n = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""DELETE FROM telemetry_samples WHERE "Id" IN (SELECT "Id" FROM telemetry_samples WHERE "RecebidoEm" < {samplesBefore} LIMIT {batchSize})""", ct);
            amostras += n;
        } while (n >= batchSize && !ct.IsCancellationRequested);

        do
        {
            n = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""DELETE FROM process_events WHERE "Id" IN (SELECT "Id" FROM process_events WHERE "OcorridoEm" < {eventsBefore} LIMIT {batchSize})""", ct);
            eventos += n;
        } while (n >= batchSize && !ct.IsCancellationRequested);

        return (amostras, eventos);
    }

    private static Page<T> Cortar<T>(List<T> itens, int limit) =>
        itens.Count > limit ? new Page<T>(itens.Take(limit).ToList(), true) : new Page<T>(itens, false);
}
