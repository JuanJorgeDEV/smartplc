using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SmartPlc.Api.Auth;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

/// <summary>Login contra PostgreSQL REAL: tabela de usuários, e-mail único, RLS e sessões que sobrevivem a um reinício do servidor.</summary>
[Collection("pg")]
public class PostgresAuthTests(PostgresFixture fx)
{
    private static AppUser Usuario(string email) => new()
    {
        Id = Guid.NewGuid(), Email = email, Nome = "Teste", SenhaHash = PasswordHasher.Hash("tanque-azul-42", 1000), Papel = Papeis.Operador,
        Ativo = true, CriadoEm = DateTimeOffset.Parse("2026-01-01T12:00:00Z")
    };

    private async Task Limpar()
    {
        await using var db = fx.NovoContexto();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE app_users, data_protection_keys");
    }

    [PostgresFact]
    public async Task Usuario_grava_le_atualiza_e_email_repetido_e_conflito()
    {
        await Limpar();
        var loja = new EfUserStore(new PostgresFixture.Fabrica(fx.Options!));
        var u = Usuario("ana@empresa.com");
        await loja.CriarAsync(u, default);

        var lido = await loja.PorEmailAsync("ana@empresa.com", default);
        Assert.NotNull(lido);
        Assert.Equal(u.Id, lido!.Id);
        Assert.Equal(u.CarimboSeguranca, lido.CarimboSeguranca);
        Assert.StartsWith("pbkdf2-sha256$", lido.SenhaHash);
        Assert.Equal(1, await loja.ContarAsync(default));

        await Assert.ThrowsAsync<UserConflictException>(() => loja.CriarAsync(Usuario("ana@empresa.com"), default));
        Assert.Equal(1, await loja.ContarAsync(default));

        lido.Ativo = false;
        lido.BloqueadoAte = DateTimeOffset.Parse("2026-01-01T13:00:00Z");
        await loja.AtualizarAsync(lido, default);
        var depois = await loja.PorIdAsync(u.Id, default);
        Assert.False(depois!.Ativo);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T13:00:00Z"), depois.BloqueadoAte);
        Assert.Single(await loja.ListarAsync(default));
    }

    [PostgresFact]
    public async Task Sessao_continua_valida_depois_de_reiniciar_o_servidor_porque_as_chaves_ficam_no_banco()
    {
        await Limpar();
        var config = new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true",
            ["Auth:AdminEmail"] = "chefe@empresa.com",
            ["Auth:AdminPassword"] = "senha-inicial-forte-1",
            ["Auth:Pbkdf2Iterations"] = "1000",
            ["ConnectionStrings:SmartPlc"] = fx.Raw,
            ["History:MigrateOnStartup"] = "false"
        };
        WebApplicationFactory<Program> Servidor() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in config) b.UseSetting(k, v);
        });

        string cookie;
        await using (var s1 = Servidor())
        {
            var c = s1.CreateClient();
            // O administrador inicial é criado em segundo plano; espera aparecer.
            var limite = Environment.TickCount64 + 15_000;
            HttpResponseMessage r;
            do
            {
                r = await c.PostAsJsonAsync("/api/auth/login", new { email = "chefe@empresa.com", senha = "senha-inicial-forte-1" });
                if (r.StatusCode != HttpStatusCode.OK) await Task.Delay(300);
            } while (r.StatusCode != HttpStatusCode.OK && Environment.TickCount64 < limite);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            cookie = r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("smartplc.sid=")).Split(';')[0];

            var req = new HttpRequestMessage(HttpMethod.Get, "/api/status");
            req.Headers.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(req)).StatusCode);
        }

        await using (var db = fx.NovoContexto())
            Assert.True(await db.DataProtectionKeys.CountAsync() >= 1, "a chave do cookie deveria ter sido gravada no banco");

        // "Reinício": outro servidor, memória zerada. O mesmo cookie continua valendo.
        await using (var s2 = Servidor())
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            req.Headers.Add("Cookie", cookie);
            var me = await (await s2.CreateClient().SendAsync(req)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.True(me.GetProperty("autenticado").GetBoolean(), "a sessão deveria sobreviver ao reinício");
            Assert.Equal("chefe@empresa.com", me.GetProperty("usuario").GetProperty("email").GetString());
        }

        // O administrador inicial só é criado uma vez, e a senha nunca fica em texto no banco.
        await using var conferir = fx.NovoContexto();
        var u = await conferir.AppUsers.SingleAsync();
        Assert.DoesNotContain("senha-inicial", u.SenhaHash);
        Assert.Equal(Papeis.Admin, u.Papel);
    }
}
