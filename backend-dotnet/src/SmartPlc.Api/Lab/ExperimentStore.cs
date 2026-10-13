using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Lab;

public sealed class LabOptions
{
    public const string Section = "Lab";

    /// <summary>O Laboratório também exige o histórico (PostgreSQL) ligado: ele consulta as amostras persistidas.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Permite iniciar, encerrar e cancelar sessões. Não afeta o CLP, mas grava no banco: em ambiente público, deixe
    /// desligado até existir autenticação (Marco 7).
    /// </summary>
    public bool WriteEnabled { get; set; } = true;

    public int MaxAnalysisSamples { get; set; } = 5000;
    public int MaxEventsPerSession { get; set; } = 500;
}

public interface IExperimentStore
{
    bool Habilitado { get; }

    /// <summary>Grava a sessão. Lança <see cref="ExperimentConflictException"/> se já existe uma ativa.</summary>
    Task CriarAsync(ExperimentSession sessao, CancellationToken ct);
    Task<ExperimentSession?> ObterAsync(Guid id, CancellationToken ct);
    Task<ExperimentSession?> ObterAtivaAsync(CancellationToken ct);
    Task<IReadOnlyList<ExperimentSession>> ListarAsync(int limite, string? status, CancellationToken ct);
    Task AtualizarAsync(ExperimentSession sessao, CancellationToken ct);
}

public sealed class DisabledExperimentStore : IExperimentStore
{
    public bool Habilitado => false;
    private static Exception Off() => new InvalidOperationException("Laboratório desabilitado.");
    public Task CriarAsync(ExperimentSession s, CancellationToken ct) => throw Off();
    public Task<ExperimentSession?> ObterAsync(Guid id, CancellationToken ct) => throw Off();
    public Task<ExperimentSession?> ObterAtivaAsync(CancellationToken ct) => throw Off();
    public Task<IReadOnlyList<ExperimentSession>> ListarAsync(int limite, string? status, CancellationToken ct) => throw Off();
    public Task AtualizarAsync(ExperimentSession s, CancellationToken ct) => throw Off();
}

public sealed class EfExperimentStore(IDbContextFactory<SmartPlcDbContext> factory) : IExperimentStore
{
    private const string UniqueViolation = "23505";

    public bool Habilitado => true;

    public async Task CriarAsync(ExperimentSession sessao, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ExperimentSessions.Add(sessao);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            var ativa = await db.ExperimentSessions.AsNoTracking().Where(s => s.Status == ExperimentStatuses.Ativa).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            throw new ExperimentConflictException("já existe uma sessão ativa", ativa);
        }
    }

    public async Task<ExperimentSession?> ObterAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ExperimentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<ExperimentSession?> ObterAtivaAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ExperimentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Status == ExperimentStatuses.Ativa, ct);
    }

    public async Task<IReadOnlyList<ExperimentSession>> ListarAsync(int limite, string? status, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.ExperimentSessions.AsNoTracking().AsQueryable();
        if (status is not null) q = q.Where(s => s.Status == status);
        return await q.OrderByDescending(s => s.InicioEm).Take(limite).ToListAsync(ct);
    }

    public async Task AtualizarAsync(ExperimentSession sessao, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ExperimentSessions.Update(sessao);
        await db.SaveChangesAsync(ct);
    }
}
