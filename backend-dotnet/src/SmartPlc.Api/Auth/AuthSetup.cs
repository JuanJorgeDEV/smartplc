using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Auth;

public static class AuthSetup
{
    public const string Esquema = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string ClaimCarimbo = "smartplc:carimbo";

    /// <summary>Login ligado? Explícito em Auth:Enabled; sem valor, ligado fora de Development.</summary>
    public static bool Habilitado(IConfiguration cfg, IHostEnvironment env) =>
        cfg.GetSection(AuthOptions.Section).Get<AuthOptions>()?.Enabled ?? !env.IsDevelopment();

    public static void AddSmartPlcAuth(this WebApplicationBuilder builder, bool habilitado, bool temBanco)
    {
        var s = builder.Services;
        var cfg = builder.Configuration;
        var opcoes = cfg.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        s.Configure<AuthOptions>(cfg.GetSection(AuthOptions.Section));
        s.AddMemoryCache();
        s.AddSingleton<AuthService>();
        s.AddSingleton(new AuthRuntime(habilitado));
        s.AddSingleton<LoginThrottle>();

        if (habilitado && !temBanco)
            throw new InvalidOperationException("O login exige o banco de dados (ConnectionStrings:SmartPlc). Configure-o, ou desligue o login com Auth:Enabled=false (somente para uso local).");

        if (temBanco)
        {
            s.AddSingleton<IUserStore, EfUserStore>();
            if (habilitado) s.AddHostedService<AdminBootstrapper>();
        }
        else
        {
            s.AddSingleton<IUserStore, DisabledUserStore>();
        }

        // Chaves do cookie no banco: sem isso, cada reinício do Render derrubaria todas as sessões.
        var dp = s.AddDataProtection().SetApplicationName("SmartPLC");
        if (habilitado && temBanco && opcoes.PersistKeysInDatabase) dp.PersistKeysToDbContext<SmartPlcDbContext>();

        s.AddAuthentication(Esquema).AddCookie(Esquema, o =>
        {
            o.Cookie.Name = "smartplc.sid";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, opcoes.SessionHours));
            o.SlidingExpiration = false;           // duração absoluta: depois de N horas, entra de novo
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = ValidarSessaoAsync;
        });

        s.AddAuthorization(o =>
        {
            // Negar por padrão: toda rota exige login, salvo as marcadas com AllowAnonymous.
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            o.AddPolicy(Politicas.Operador, p => p.RequireAuthenticatedUser().RequireRole(Papeis.Admin, Papeis.Operador));
            o.AddPolicy(Politicas.Admin, p => p.RequireAuthenticatedUser().RequireRole(Papeis.Admin));
        });

        // Atrás do proxy do Render o IP e o esquema chegam em X-Forwarded-*. O Render é a única porta de entrada do serviço.
        s.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });
    }

    private static async Task ValidarSessaoAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        var idTxt = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idTxt, out var id)) { await Rejeitar(ctx); return; }

        try
        {
            var usuario = await ctx.HttpContext.RequestServices.GetRequiredService<AuthService>().ConferirSessaoAsync(id, ctx.HttpContext.RequestAborted);
            if (usuario is null || !usuario.Ativo || usuario.CarimboSeguranca.ToString() != principal!.FindFirstValue(ClaimCarimbo))
                await Rejeitar(ctx);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Banco indisponível: não derruba quem já está logado (o cookie é assinado e tem prazo).
            ctx.HttpContext.RequestServices.GetRequiredService<ILogger<AuthService>>()
                .LogWarning("Não foi possível reconferir a sessão no banco: {Erro}", ex.GetBaseException().Message);
        }
    }

    private static async Task Rejeitar(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(Esquema);
    }

    public static ClaimsPrincipal CriarPrincipal(AppUser u)
    {
        var id = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(ClaimTypes.Name, u.Nome),
            new Claim(ClaimTypes.Email, u.Email),
            new Claim(ClaimTypes.Role, u.Papel),
            new Claim(ClaimCarimbo, u.CarimboSeguranca.ToString())
        ], Esquema);
        return new ClaimsPrincipal(id);
    }

    public static void UseSmartPlcAuth(this WebApplication app, bool habilitado)
    {
        app.UseForwardedHeaders();
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "same-origin";
            await next();
        });
        app.UseStaticFiles();
        app.UseMiddleware<OriginCheckMiddleware>();
        if (habilitado) app.UseAuthentication();
        else app.UseMiddleware<LocalAccessMiddleware>();
        app.UseAuthorization();
    }
}

/// <summary>
/// Com o login desligado (Auth:Enabled=false), todos são tratados como administradores locais. Existe só para o desenvolvimento
/// na própria máquina e para os testes: fora de Development o login vem ligado por padrão.
/// </summary>
public sealed class LocalAccessMiddleware(RequestDelegate next)
{
    public static readonly ClaimsPrincipal Local = new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString()),
        new Claim(ClaimTypes.Name, "Acesso local"),
        new Claim(ClaimTypes.Role, Papeis.Admin)
    ], "local"));

    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.User = Local;
        return next(ctx);
    }
}

/// <summary>
/// Defesa extra contra requisições forjadas por outros sites: pedidos que alteram dados e trazem o cabeçalho Origin precisam vir
/// do próprio endereço do painel. (O cookie já é SameSite=Strict; isto cobre navegadores antigos.) Node-RED e curl não enviam Origin.
/// </summary>
public sealed class OriginCheckMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var m = ctx.Request.Method;
        if ((HttpMethods.IsPost(m) || HttpMethods.IsPut(m) || HttpMethods.IsPatch(m) || HttpMethods.IsDelete(m))
            && ctx.Request.Headers.Origin.ToString() is { Length: > 0 } origem)
        {
            if (!Uri.TryCreate(origem, UriKind.Absolute, out var uri) || !string.Equals(uri.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return ctx.Response.WriteAsJsonAsync(new { status = "erro", mensagem = "origem não permitida" });
            }
        }
        return next(ctx);
    }
}

/// <summary>Cria o primeiro administrador quando o banco já está pronto (com nova tentativa se a tabela ainda não existe).</summary>
public sealed class AdminBootstrapper(AuthService auth, ILogger<AdminBootstrapper> log, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var espera = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await auth.GarantirAdministradorInicialAsync(ct);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogWarning("Ainda não foi possível verificar os usuários ({Erro}); nova tentativa em {S:0} s.", ex.GetBaseException().Message, espera.TotalSeconds);
                try { await Task.Delay(espera, clock, ct); } catch (OperationCanceledException) { return; }
                espera = TimeSpan.FromTicks(Math.Min(espera.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
            }
        }
    }
}

/// <summary>Limite de tentativas de login por endereço IP (janela deslizante em memória), além do bloqueio por conta.</summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int MaxPorJanela = 20;
    public static readonly TimeSpan Janela = TimeSpan.FromMinutes(5);

    private readonly object _trava = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _porIp = [];

    /// <summary>Registra a tentativa e diz se ainda é permitida.</summary>
    public bool Permitir(string ip)
    {
        var agora = clock.GetUtcNow();
        lock (_trava)
        {
            if (_porIp.Count > 5000) _porIp.Clear();     // proteção de memória contra varredura de IPs
            if (!_porIp.TryGetValue(ip, out var fila)) _porIp[ip] = fila = new Queue<DateTimeOffset>();
            while (fila.Count > 0 && agora - fila.Peek() > Janela) fila.Dequeue();
            if (fila.Count >= MaxPorJanela) return false;
            fila.Enqueue(agora);
            return true;
        }
    }
}

/// <summary>Se o login está ligado nesta execução (decidido na subida).</summary>
public sealed record AuthRuntime(bool Habilitado);
