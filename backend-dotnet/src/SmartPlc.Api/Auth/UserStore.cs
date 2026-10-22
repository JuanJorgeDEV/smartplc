using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Auth;

public sealed class UserConflictException(string email) : Exception($"já existe um usuário com o e-mail {email}");

public interface IUserStore
{
    bool Habilitado { get; }
    Task<int> ContarAsync(CancellationToken ct);
    Task<AppUser?> PorEmailAsync(string email, CancellationToken ct);
    Task<AppUser?> PorIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AppUser>> ListarAsync(CancellationToken ct);
    /// <summary>Lança <see cref="UserConflictException"/> se o e-mail já existe.</summary>
    Task CriarAsync(AppUser usuario, CancellationToken ct);
    Task AtualizarAsync(AppUser usuario, CancellationToken ct);
}

public sealed class DisabledUserStore : IUserStore
{
    public bool Habilitado => false;
    private static Exception Off() => new InvalidOperationException("Usuários exigem o banco de dados configurado.");
    public Task<int> ContarAsync(CancellationToken ct) => throw Off();
    public Task<AppUser?> PorEmailAsync(string email, CancellationToken ct) => throw Off();
    public Task<AppUser?> PorIdAsync(Guid id, CancellationToken ct) => throw Off();
    public Task<IReadOnlyList<AppUser>> ListarAsync(CancellationToken ct) => throw Off();
    public Task CriarAsync(AppUser usuario, CancellationToken ct) => throw Off();
    public Task AtualizarAsync(AppUser usuario, CancellationToken ct) => throw Off();
}

public sealed class EfUserStore(IDbContextFactory<SmartPlcDbContext> factory) : IUserStore
{
    private const string UniqueViolation = "23505";

    public bool Habilitado => true;

    public async Task<int> ContarAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AppUsers.CountAsync(ct);
    }

    public async Task<AppUser?> PorEmailAsync(string email, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AppUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
    }

    public async Task<AppUser?> PorIdAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AppUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public async Task<IReadOnlyList<AppUser>> ListarAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AppUsers.AsNoTracking().OrderBy(u => u.Nome).ThenBy(u => u.Email).ToListAsync(ct);
    }

    public async Task CriarAsync(AppUser usuario, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AppUsers.Add(usuario);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw new UserConflictException(usuario.Email);
        }
    }

    public async Task AtualizarAsync(AppUser usuario, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AppUsers.Update(usuario);
        await db.SaveChangesAsync(ct);
    }
}
