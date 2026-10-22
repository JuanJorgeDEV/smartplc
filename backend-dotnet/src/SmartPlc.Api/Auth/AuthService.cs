using System.Net.Mail;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Auth;

public enum LoginStatus { Ok, Invalido }

public sealed record LoginResult(LoginStatus Status, AppUser? Usuario = null);

public sealed record ResultadoOperacao(bool Ok, string? Erro = null, int Status = 200)
{
    public static ResultadoOperacao Sucesso() => new(true);
    public static ResultadoOperacao Falha(string erro, int status = 400) => new(false, erro, status);
}

/// <summary>
/// Regras de login e de gestão de usuários. Nada aqui conhece HTTP.
/// Falhas de login são sempre iguais para o chamador (não revelam se o e-mail existe, se a conta está bloqueada ou se a senha estava errada).
/// </summary>
public sealed class AuthService(IUserStore store, IOptions<AuthOptions> opcoes, TimeProvider clock, IMemoryCache cache, ILogger<AuthService> log)
{
    private readonly string _hashDeFachada = PasswordHasher.HashDeFachada(Math.Max(1, opcoes.Value.Pbkdf2Iterations));

    private AuthOptions O => opcoes.Value;

    public static string NormalizarEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public static bool EmailValido(string email)
    {
        if (email.Length is < 3 or > 200) return false;
        try { return new MailAddress(email).Address == email && email.Contains('@'); }
        catch (FormatException) { return false; }
    }

    public async Task<LoginResult> LoginAsync(string? emailBruto, string? senha, CancellationToken ct)
    {
        var email = NormalizarEmail(emailBruto);
        var usuario = email.Length > 0 ? await store.PorEmailAsync(email, ct) : null;
        var agora = clock.GetUtcNow();

        // Sempre calcula um hash, exista o usuário ou não, para o tempo de resposta não denunciar contas.
        var alvo = usuario?.SenhaHash ?? _hashDeFachada;
        var confere = PasswordHasher.Verificar(senha ?? "", alvo, O.Pbkdf2Iterations, out var refazer);

        if (usuario is null) return new LoginResult(LoginStatus.Invalido);

        if (usuario.BloqueadoAte is { } ate && ate > agora)
        {
            log.LogWarning("Login recusado: conta {Email} bloqueada até {Ate:u}", usuario.Email, ate);
            return new LoginResult(LoginStatus.Invalido);
        }

        if (!confere || !usuario.Ativo)
        {
            if (!confere)
            {
                usuario.FalhasConsecutivas++;
                if (usuario.FalhasConsecutivas >= O.MaxFailures)
                {
                    usuario.BloqueadoAte = agora.AddMinutes(O.LockoutMinutes);
                    usuario.FalhasConsecutivas = 0;
                    log.LogWarning("Conta {Email} bloqueada por {Min} min após tentativas erradas seguidas", usuario.Email, O.LockoutMinutes);
                }
                await store.AtualizarAsync(usuario, ct);
            }
            return new LoginResult(LoginStatus.Invalido);
        }

        usuario.FalhasConsecutivas = 0;
        usuario.BloqueadoAte = null;
        usuario.UltimoLoginEm = agora;
        if (refazer) usuario.SenhaHash = PasswordHasher.Hash(senha!, O.Pbkdf2Iterations);
        await store.AtualizarAsync(usuario, ct);
        return new LoginResult(LoginStatus.Ok, usuario);
    }

    /// <summary>Dados do usuário para a validação do cookie, com um cache curto (a revogação vale em segundos).</summary>
    public async Task<AppUser?> ConferirSessaoAsync(Guid id, CancellationToken ct)
    {
        var chave = $"usuario:{id}";
        if (cache.TryGetValue(chave, out AppUser? emCache)) return emCache;
        var u = await store.PorIdAsync(id, ct);
        cache.Set(chave, u, TimeSpan.FromSeconds(Math.Max(1, O.UserCacheSeconds)));
        return u;
    }

    private void Esquecer(Guid id) => cache.Remove($"usuario:{id}");

    public async Task<ResultadoOperacao> TrocarSenhaAsync(Guid id, string? atual, string? nova, CancellationToken ct)
    {
        var u = await store.PorIdAsync(id, ct);
        if (u is null || !u.Ativo) return ResultadoOperacao.Falha("usuário não encontrado", 404);
        if (!PasswordHasher.Verificar(atual ?? "", u.SenhaHash, O.Pbkdf2Iterations, out _))
            return ResultadoOperacao.Falha("a senha atual não confere", 400);
        if (PasswordHasher.Validar(nova, u.Email, O.PasswordMinLength) is { } erro) return ResultadoOperacao.Falha(erro);
        if (nova == atual) return ResultadoOperacao.Falha("a nova senha precisa ser diferente da atual");

        u.SenhaHash = PasswordHasher.Hash(nova!, O.Pbkdf2Iterations);
        u.CarimboSeguranca = Guid.NewGuid();     // encerra as outras sessões deste usuário
        await store.AtualizarAsync(u, ct);
        Esquecer(u.Id);
        return ResultadoOperacao.Sucesso();
    }

    public async Task<(ResultadoOperacao Resultado, AppUser? Usuario)> CriarAsync(string? emailBruto, string? nome, string? papel, string? senha, CancellationToken ct)
    {
        var email = NormalizarEmail(emailBruto);
        if (!EmailValido(email)) return (ResultadoOperacao.Falha("e-mail inválido"), null);
        var nomeLimpo = (nome ?? "").Trim();
        if (nomeLimpo.Length is < 1 or > 80) return (ResultadoOperacao.Falha("informe o nome (até 80 caracteres)"), null);
        if (papel is null || !Papeis.Todos.Contains(papel)) return (ResultadoOperacao.Falha($"papel inválido: use {string.Join(", ", Papeis.Todos)}"), null);
        if (PasswordHasher.Validar(senha, email, O.PasswordMinLength) is { } erro) return (ResultadoOperacao.Falha(erro), null);

        var u = new AppUser
        {
            Id = Guid.NewGuid(), Email = email, Nome = nomeLimpo, Papel = papel, Ativo = true, CriadoEm = clock.GetUtcNow(),
            SenhaHash = PasswordHasher.Hash(senha!, O.Pbkdf2Iterations), CarimboSeguranca = Guid.NewGuid()
        };
        try
        {
            await store.CriarAsync(u, ct);
        }
        catch (UserConflictException)
        {
            return (ResultadoOperacao.Falha("já existe um usuário com esse e-mail", 409), null);
        }
        return (ResultadoOperacao.Sucesso(), u);
    }

    /// <summary>Altera papel, estado ou senha. Nunca deixa o sistema sem administrador ativo.</summary>
    public async Task<(ResultadoOperacao Resultado, AppUser? Usuario)> AtualizarAsync(Guid id, Guid quemFaz, string? papel, bool? ativo, string? novaSenha, string? nome, CancellationToken ct)
    {
        var u = await store.PorIdAsync(id, ct);
        if (u is null) return (ResultadoOperacao.Falha("usuário não encontrado", 404), null);

        var perdeAdmin = u.Papel == Papeis.Admin && u.Ativo
                         && ((papel is not null && papel != Papeis.Admin) || ativo == false);
        if (perdeAdmin)
        {
            var admins = (await store.ListarAsync(ct)).Count(x => x.Papel == Papeis.Admin && x.Ativo);
            if (admins <= 1) return (ResultadoOperacao.Falha("não é possível remover o último administrador ativo", 409), null);
        }
        if (id == quemFaz && ativo == false) return (ResultadoOperacao.Falha("você não pode desativar a própria conta", 409), null);

        var mudouSeguranca = false;
        if (papel is not null)
        {
            if (!Papeis.Todos.Contains(papel)) return (ResultadoOperacao.Falha($"papel inválido: use {string.Join(", ", Papeis.Todos)}"), null);
            if (papel != u.Papel) { u.Papel = papel; mudouSeguranca = true; }
        }
        if (ativo is { } a && a != u.Ativo) { u.Ativo = a; mudouSeguranca = true; if (a) { u.FalhasConsecutivas = 0; u.BloqueadoAte = null; } }
        if (nome is not null)
        {
            var n = nome.Trim();
            if (n.Length is < 1 or > 80) return (ResultadoOperacao.Falha("informe o nome (até 80 caracteres)"), null);
            u.Nome = n;
        }
        if (novaSenha is not null)
        {
            if (PasswordHasher.Validar(novaSenha, u.Email, O.PasswordMinLength) is { } erro) return (ResultadoOperacao.Falha(erro), null);
            u.SenhaHash = PasswordHasher.Hash(novaSenha, O.Pbkdf2Iterations);
            u.FalhasConsecutivas = 0; u.BloqueadoAte = null;
            mudouSeguranca = true;
        }
        if (mudouSeguranca) u.CarimboSeguranca = Guid.NewGuid();

        await store.AtualizarAsync(u, ct);
        Esquecer(u.Id);
        return (ResultadoOperacao.Sucesso(), u);
    }

    /// <summary>Cria o primeiro administrador se ainda não existe nenhum usuário e as credenciais iniciais foram configuradas.</summary>
    public async Task<bool> GarantirAdministradorInicialAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(O.AdminEmail) || string.IsNullOrEmpty(O.AdminPassword)) return false;
        if (await store.ContarAsync(ct) > 0) return false;
        var (res, _) = await CriarAsync(O.AdminEmail, O.AdminName, Papeis.Admin, O.AdminPassword, ct);
        if (!res.Ok) { log.LogError("Não foi possível criar o administrador inicial: {Erro}", res.Erro); return false; }
        log.LogWarning("Administrador inicial criado ({Email}). Depois de entrar, troque a senha e remova Auth__AdminPassword da configuração.", NormalizarEmail(O.AdminEmail));
        return true;
    }
}
