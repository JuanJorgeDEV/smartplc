using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Persistence;
using SmartPlc.Api.Validation;

namespace SmartPlc.Api.Auth;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LoginRequest
{
    public string? Email { get; set; }
    public string? Senha { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TrocarSenhaRequest
{
    public string? SenhaAtual { get; set; }
    public string? NovaSenha { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NovoUsuarioRequest
{
    public string? Email { get; set; }
    public string? Nome { get; set; }
    public string? Papel { get; set; }
    public string? Senha { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AtualizarUsuarioRequest
{
    public string? Nome { get; set; }
    public string? Papel { get; set; }
    public bool? Ativo { get; set; }
    public string? NovaSenha { get; set; }
}

public sealed record UsuarioDto(Guid Id, string Email, string Nome, string Papel, string PapelRotulo, bool Ativo,
    DateTimeOffset? CriadoEm, DateTimeOffset? UltimoLoginEm, bool Bloqueado)
{
    public static UsuarioDto De(AppUser u, DateTimeOffset agora) => new(
        u.Id, u.Email, u.Nome, u.Papel, Papeis.Rotulo(u.Papel), u.Ativo, u.CriadoEm, u.UltimoLoginEm, u.BloqueadoAte is { } b && b > agora);

    public static UsuarioDto De(ClaimsPrincipal p) => new(
        Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty,
        p.FindFirstValue(ClaimTypes.Email) ?? "", p.FindFirstValue(ClaimTypes.Name) ?? "",
        p.FindFirstValue(ClaimTypes.Role) ?? Papeis.Visualizador, Papeis.Rotulo(p.FindFirstValue(ClaimTypes.Role) ?? Papeis.Visualizador),
        true, null, null, false);
}

public static class AuthEndpoints
{
    public const string MensagemLoginInvalido = "e-mail ou senha inválidos (após várias tentativas erradas a conta fica bloqueada por alguns minutos)";

    public static void MapAuthApi(this WebApplication app, bool habilitado)
    {
        app.MapGet("/api/auth/me", (HttpContext ctx) =>
        {
            var logado = ctx.User.Identity?.IsAuthenticated == true;
            return Results.Ok(new
            {
                autenticacaoAtiva = habilitado,
                autenticado = logado,
                usuario = logado ? UsuarioDto.De(ctx.User) : null
            });
        }).AllowAnonymous();

        if (!habilitado) return;

        app.MapPost("/api/auth/login", Login).AllowAnonymous();
        app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(AuthSetup.Esquema);
            return Results.Ok(new { status = "ok" });
        }).AllowAnonymous();
        app.MapPost("/api/auth/senha", TrocarSenha);

        app.MapGet("/api/usuarios", ListarUsuarios).RequireAuthorization(Politicas.Admin);
        app.MapPost("/api/usuarios", CriarUsuario).RequireAuthorization(Politicas.Admin);
        app.MapPatch("/api/usuarios/{id:guid}", AtualizarUsuario).RequireAuthorization(Politicas.Admin);
    }

    private static async Task<IResult> Login(HttpContext ctx, AuthService auth, LoginThrottle limite, IOptions<AuthOptions> opcoes, TimeProvider clock, CancellationToken ct)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
        if (!limite.Permitir(ip))
            return Results.Json(ErroResponse.De("muitas tentativas deste endereço; aguarde alguns minutos"), statusCode: StatusCodes.Status429TooManyRequests);

        var leitura = await PayloadReader.LerAsync<LoginRequest>(ctx.Request.Body, ct);
        if (leitura.Excedeu) return Results.Json(ErroResponse.De("corpo muito grande"), statusCode: StatusCodes.Status413PayloadTooLarge);
        if (leitura.Erros is { Count: > 0 } e) return Results.BadRequest(ErroResponse.De("requisição inválida", e));
        var dados = leitura.Valor!;
        // Limites antes de gastar tempo de CPU com o hash.
        if (string.IsNullOrWhiteSpace(dados.Email) || string.IsNullOrEmpty(dados.Senha) || dados.Email.Length > 200 || dados.Senha.Length > AuthOptions.PasswordMaxLength)
            return Results.Json(ErroResponse.De(MensagemLoginInvalido), statusCode: StatusCodes.Status401Unauthorized);

        LoginResult r;
        try
        {
            r = await auth.LoginAsync(dados.Email, dados.Senha, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.RequestServices.GetRequiredService<ILogger<AuthService>>().LogError(ex, "Falha ao consultar usuários no login");
            return Results.Json(ErroResponse.De("banco de dados indisponível; tente novamente em instantes"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (r.Status != LoginStatus.Ok)
            return Results.Json(ErroResponse.De(MensagemLoginInvalido), statusCode: StatusCodes.Status401Unauthorized);

        var props = new AuthenticationProperties
        {
            IsPersistent = false,    // cookie de sessão: some ao fechar o navegador, e vale no máximo SessionHours
            ExpiresUtc = clock.GetUtcNow().AddHours(Math.Max(1, opcoes.Value.SessionHours)),
            AllowRefresh = false
        };
        await ctx.SignInAsync(AuthSetup.Esquema, AuthSetup.CriarPrincipal(r.Usuario!), props);
        return Results.Ok(new { status = "ok", usuario = UsuarioDto.De(r.Usuario!, clock.GetUtcNow()) });
    }

    private static async Task<IResult> TrocarSenha(HttpContext ctx, AuthService auth, IUserStore store, IOptions<AuthOptions> opcoes, TimeProvider clock, CancellationToken ct)
    {
        if (!Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) || id == Guid.Empty)
            return Results.Json(ErroResponse.De("troca de senha indisponível sem login"), statusCode: StatusCodes.Status400BadRequest);

        var leitura = await PayloadReader.LerAsync<TrocarSenhaRequest>(ctx.Request.Body, ct);
        if (leitura.Excedeu) return Results.Json(ErroResponse.De("corpo muito grande"), statusCode: StatusCodes.Status413PayloadTooLarge);
        if (leitura.Erros is { Count: > 0 } e) return Results.BadRequest(ErroResponse.De("requisição inválida", e));

        var r = await auth.TrocarSenhaAsync(id, leitura.Valor!.SenhaAtual, leitura.Valor.NovaSenha, ct);
        if (!r.Ok) return Results.Json(ErroResponse.De(r.Erro!), statusCode: r.Status);

        // A senha mudou: o carimbo mudou, então a sessão atual também seria derrubada. Entra de novo com o carimbo novo.
        var u = await store.PorIdAsync(id, ct);
        if (u is not null)
            await ctx.SignInAsync(AuthSetup.Esquema, AuthSetup.CriarPrincipal(u),
                new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, ExpiresUtc = clock.GetUtcNow().AddHours(Math.Max(1, opcoes.Value.SessionHours)) });
        return Results.Ok(new { status = "ok" });
    }

    private static async Task<IResult> ListarUsuarios(IUserStore store, TimeProvider clock, CancellationToken ct)
    {
        var agora = clock.GetUtcNow();
        return Results.Ok(new { usuarios = (await store.ListarAsync(ct)).Select(u => UsuarioDto.De(u, agora)) });
    }

    private static async Task<IResult> CriarUsuario(HttpContext ctx, AuthService auth, TimeProvider clock, CancellationToken ct)
    {
        var leitura = await PayloadReader.LerAsync<NovoUsuarioRequest>(ctx.Request.Body, ct);
        if (leitura.Excedeu) return Results.Json(ErroResponse.De("corpo muito grande"), statusCode: StatusCodes.Status413PayloadTooLarge);
        if (leitura.Erros is { Count: > 0 } e) return Results.BadRequest(ErroResponse.De("requisição inválida", e));
        var d = leitura.Valor!;
        var (r, u) = await auth.CriarAsync(d.Email, d.Nome, d.Papel, d.Senha, ct);
        if (!r.Ok) return Results.Json(ErroResponse.De(r.Erro!), statusCode: r.Status);
        return Results.Created($"/api/usuarios/{u!.Id}", new { usuario = UsuarioDto.De(u, clock.GetUtcNow()) });
    }

    private static async Task<IResult> AtualizarUsuario(Guid id, HttpContext ctx, AuthService auth, TimeProvider clock, CancellationToken ct)
    {
        var leitura = await PayloadReader.LerAsync<AtualizarUsuarioRequest>(ctx.Request.Body, ct);
        if (leitura.Excedeu) return Results.Json(ErroResponse.De("corpo muito grande"), statusCode: StatusCodes.Status413PayloadTooLarge);
        if (leitura.Erros is { Count: > 0 } e) return Results.BadRequest(ErroResponse.De("requisição inválida", e));
        var d = leitura.Valor!;
        if (d.Nome is null && d.Papel is null && d.Ativo is null && d.NovaSenha is null)
            return Results.BadRequest(ErroResponse.De("informe ao menos um campo para alterar"));

        Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var quemFaz);
        var (r, u) = await auth.AtualizarAsync(id, quemFaz, d.Papel, d.Ativo, d.NovaSenha, d.Nome, ct);
        if (!r.Ok) return Results.Json(ErroResponse.De(r.Erro!), statusCode: r.Status);
        return Results.Ok(new { usuario = UsuarioDto.De(u!, clock.GetUtcNow()) });
    }
}
