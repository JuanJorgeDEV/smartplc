using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartPlc.Api.Auth;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_e_verificacao_ida_e_volta_com_sal_diferente_a_cada_vez()
    {
        var a = PasswordHasher.Hash("uma senha longa 123", 1000);
        var b = PasswordHasher.Hash("uma senha longa 123", 1000);
        Assert.NotEqual(a, b);
        Assert.StartsWith("pbkdf2-sha256$1000$", a);
        Assert.True(PasswordHasher.Verificar("uma senha longa 123", a, 1000, out var refazer));
        Assert.False(refazer);
        Assert.False(PasswordHasher.Verificar("outra senha qualquer", a, 1000, out _));
        Assert.DoesNotContain("uma senha longa", a);
    }

    [Fact]
    public void Hash_com_menos_iteracoes_que_o_atual_pede_para_ser_refeito()
    {
        var antigo = PasswordHasher.Hash("senha-segura-123", 500);
        Assert.True(PasswordHasher.Verificar("senha-segura-123", antigo, 1000, out var refazer));
        Assert.True(refazer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lixo")]
    [InlineData("pbkdf2-sha256$x$y$z")]
    [InlineData("pbkdf2-sha256$1000$naoebase64!$naoebase64!")]
    [InlineData("md5$1$a$b")]
    public void Hash_guardado_invalido_nunca_confere_e_nunca_lanca(string guardado) =>
        Assert.False(PasswordHasher.Verificar("qualquer", guardado, 1000, out _));

    [Theory]
    [InlineData(null, "informe")]
    [InlineData("curta", "pelo menos")]
    [InlineData("aaaaaaaaaaaa", "repetitiva")]
    [InlineData("          ", "espaços")]
    [InlineData("ana@exemplo.com", "igual ao e-mail")]
    [InlineData("1234567890", "comum")]
    public void Senhas_fracas_sao_recusadas_com_o_motivo(string? senha, string trecho) =>
        Assert.Contains(trecho, PasswordHasher.Validar(senha, "ana@exemplo.com", 10));

    [Fact]
    public void Senha_razoavel_e_aceita_e_o_limite_superior_vale()
    {
        Assert.Null(PasswordHasher.Validar("tanque-azul-42!", "ana@exemplo.com", 10));
        Assert.NotNull(PasswordHasher.Validar(new string('x', 5) + string.Concat(Enumerable.Range(0, 200).Select(i => (char)('a' + i % 26))), "a@b.co", 10));
    }
}

public class AuthTests
{
    private const string Senha = "tanque-azul-42";

    private static (TestApp App, InMemoryUserStore Users) Criar(Dictionary<string, string?>? extra = null)
    {
        var users = new InMemoryUserStore();
        return (TestApp.ComLogin(users, extra), users);
    }

    private static async Task<AppUser> Novo(TestApp app, string email, string papel, string senha = Senha, string nome = "Pessoa")
    {
        var (r, u) = await app.Services.GetRequiredService<AuthService>().CriarAsync(email, nome, papel, senha, default);
        Assert.True(r.Ok, r.Erro);
        return u!;
    }

    private static HttpClient Cliente(TestApp app) => app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> Entrar(HttpClient c, string email, string senha = Senha) =>
        c.PostAsJsonAsync("/api/auth/login", new { email, senha });

    private static async Task<HttpClient> Logado(TestApp app, string email, string papel = Papeis.Admin)
    {
        await Novo(app, email, papel);
        var c = Cliente(app);
        Assert.Equal(HttpStatusCode.OK, (await Entrar(c, email)).StatusCode);
        return c;
    }

    private static async Task<JsonElement> Corpo(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static StringContent Json(string j) => TestApp.Json(j);

    // ---------- negar por padrão ----------

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/config")]
    [InlineData("/api/variaveis")]
    [InlineData("/api/comandos")]
    [InlineData("/api/historico")]
    [InlineData("/api/eventos")]
    [InlineData("/api/experimentos")]
    [InlineData("/api/usuarios")]
    [InlineData("/hubs/telemetria/negotiate")]
    public async Task Sem_login_as_rotas_protegidas_respondem_401(string rota)
    {
        var (app, _) = Criar();
        using var lixo = app;
        var r = rota.StartsWith("/hubs") ? await Cliente(app).PostAsync(rota, null) : await Cliente(app).GetAsync(rota);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Rotas_de_escrita_sem_login_tambem_respondem_401()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var c = Cliente(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/controle", Json("""{"setpoint":50}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/experimentos", Json("""{"tipo":"respostaSetpoint","fonte":"simulacao"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/usuarios", Json("""{}"""))).StatusCode);
    }

    [Fact]
    public async Task Saude_e_me_ficam_abertos_e_o_detalhe_do_readiness_so_vai_para_quem_entrou()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var c = Cliente(app);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);

        var ready = await Corpo(await c.GetAsync("/health/ready"));
        Assert.False(ready.GetProperty("historico").TryGetProperty("ultimoErro", out _));
        Assert.False(ready.GetProperty("historico").TryGetProperty("enfileirados", out _));

        var me = await Corpo(await c.GetAsync("/api/auth/me"));
        Assert.True(me.GetProperty("autenticacaoAtiva").GetBoolean());
        Assert.False(me.GetProperty("autenticado").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("usuario").ValueKind);

        await Novo(app, "ana@empresa.com", Papeis.Operador);
        await Entrar(c, "ana@empresa.com");
        var detalhado = await Corpo(await c.GetAsync("/health/ready"));
        Assert.True(detalhado.GetProperty("historico").TryGetProperty("ultimoErro", out _));
    }

    [Fact]
    public async Task Telemetria_continua_protegida_so_pela_chave_de_ingestao_e_nao_pede_cookie()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var c = Cliente(app);
        var corpo = """{"nivel":50,"valvula":40,"setpoint":50,"modoManual":false,"valvulaManual":0,"kp":1,"ti":20,"td":0}""";

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/telemetria", Json(corpo))).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/telemetria") { Content = Json(corpo) };
        req.Headers.Add("X-Api-Key", "chave-de-teste");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(req)).StatusCode);

        var errada = new HttpRequestMessage(HttpMethod.Post, "/api/telemetria") { Content = Json(corpo) };
        errada.Headers.Add("X-Api-Key", "outra");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(errada)).StatusCode);
    }

    [Fact]
    public async Task Respostas_trazem_cabecalhos_de_seguranca()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var r = await Cliente(app).GetAsync("/health");
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", r.Headers.GetValues("Referrer-Policy").Single());
    }

    // ---------- login ----------

    [Fact]
    public async Task Login_correto_abre_a_sessao_com_cookie_HttpOnly_SameSite_Strict_e_sem_vazar_dados_sensiveis()
    {
        var (app, _) = Criar();
        using var lixo = app;
        await Novo(app, "ana@empresa.com", Papeis.Operador, nome: "Ana");
        var c = Cliente(app);

        var r = await Entrar(c, "  ANA@Empresa.com ");        // e-mail normalizado
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cookie = r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("smartplc.sid="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires", cookie, StringComparison.OrdinalIgnoreCase);   // cookie de sessão

        var texto = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("senha", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pbkdf2", texto);
        Assert.DoesNotContain("carimbo", texto, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/status")).StatusCode);
        var me = await Corpo(await c.GetAsync("/api/auth/me"));
        Assert.Equal("Ana", me.GetProperty("usuario").GetProperty("nome").GetString());
        Assert.Equal("operador", me.GetProperty("usuario").GetProperty("papel").GetString());
    }

    [Fact]
    public async Task Em_producao_o_cookie_e_Secure()
    {
        var users = new InMemoryUserStore();
        var app = TestApp.ComLogin(users);
        app.Producao = true;
        using var lixo = app;
        await Novo(app, "ana@empresa.com", Papeis.Operador);
        var r = await Entrar(Cliente(app), "ana@empresa.com");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("secure", r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("smartplc.sid=")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Todas_as_falhas_de_login_sao_indistinguiveis()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var u = await Novo(app, "ana@empresa.com", Papeis.Operador);
        await Novo(app, "inativa@empresa.com", Papeis.Operador);
        await app.Services.GetRequiredService<AuthService>().AtualizarAsync((await app.Services.GetRequiredService<IUserStore>().PorEmailAsync("inativa@empresa.com", default))!.Id, u.Id, null, false, null, null, default);

        var respostas = new List<(HttpStatusCode, string)>();
        foreach (var (email, senha) in new[] { ("ana@empresa.com", "errada-errada-1"), ("ninguem@empresa.com", Senha), ("inativa@empresa.com", Senha), ("", ""), ("ana@empresa.com", "") })
        {
            var r = await Entrar(Cliente(app), email, senha);
            respostas.Add((r.StatusCode, await r.Content.ReadAsStringAsync()));
        }
        Assert.All(respostas, x => Assert.Equal(HttpStatusCode.Unauthorized, x.Item1));
        Assert.Single(respostas.Select(x => x.Item2).Distinct());
    }

    [Fact]
    public async Task Cinco_erros_seguidos_bloqueiam_a_conta_mesmo_com_a_senha_certa_e_o_bloqueio_passa()
    {
        var (app, users) = Criar();
        using var lixo = app;
        await Novo(app, "ana@empresa.com", Papeis.Operador);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(Cliente(app), "ana@empresa.com", "errada-errada-1")).StatusCode);

        Assert.NotNull(users.Usuarios[0].BloqueadoAte);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(Cliente(app), "ana@empresa.com")).StatusCode);   // senha certa, conta bloqueada

        app.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.OK, (await Entrar(Cliente(app), "ana@empresa.com")).StatusCode);
        Assert.Equal(0, users.Usuarios[0].FalhasConsecutivas);
        Assert.Null(users.Usuarios[0].BloqueadoAte);
    }

    [Fact]
    public async Task Acerto_zera_o_contador_de_erros()
    {
        var (app, users) = Criar();
        using var lixo = app;
        await Novo(app, "ana@empresa.com", Papeis.Operador);
        for (var i = 0; i < 4; i++) await Entrar(Cliente(app), "ana@empresa.com", "errada-errada-1");
        Assert.Equal(HttpStatusCode.OK, (await Entrar(Cliente(app), "ana@empresa.com")).StatusCode);
        Assert.Equal(0, users.Usuarios[0].FalhasConsecutivas);
        for (var i = 0; i < 4; i++) await Entrar(Cliente(app), "ana@empresa.com", "errada-errada-1");
        Assert.Equal(HttpStatusCode.OK, (await Entrar(Cliente(app), "ana@empresa.com")).StatusCode);   // 4 + acerto + 4: nunca chegou a 5 seguidos
    }

    [Fact]
    public async Task Mais_de_20_tentativas_do_mesmo_endereco_em_5_minutos_recebem_429()
    {
        var (app, _) = Criar();
        using var lixo = app;
        HttpStatusCode? ultimo = null;
        for (var i = 0; i < LoginThrottle.MaxPorJanela + 1; i++) ultimo = (await Entrar(Cliente(app), $"x{i}@empresa.com", "qualquer-coisa-1")).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, ultimo);

        app.Clock.Advance(LoginThrottle.Janela + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(Cliente(app), "x@empresa.com", "qualquer-coisa-1")).StatusCode);
    }

    [Fact]
    public async Task Corpo_com_campos_desconhecidos_e_banco_fora_do_ar_nao_viram_500_nem_vazam_detalhes()
    {
        var (app, users) = Criar();
        using var lixo = app;
        await Novo(app, "ana@empresa.com", Papeis.Operador);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cliente(app).PostAsync("/api/auth/login", Json("""{"email":"a@b.co","senha":"x","admin":true}"""))).StatusCode);

        users.FalharSempre = true;
        var r = await Entrar(Cliente(app), "ana@empresa.com");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.DoesNotContain("simulado", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logout_encerra_a_sessao()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var c = await Logado(app, "ana@empresa.com");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task Cookie_forjado_ou_adulterado_nao_autentica()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        req.Headers.Add("Cookie", "smartplc.sid=CfDJ8" + new string('A', 200));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Cliente(app).SendAsync(req)).StatusCode);
    }

    // ---------- papéis ----------

    [Fact]
    public async Task Visualizador_so_le()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var c = await Logado(app, "v@empresa.com", Papeis.Visualizador);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/experimentos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/controle", Json("""{"setpoint":50}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/experimentos", Json("""{"tipo":"respostaSetpoint","fonte":"simulacao"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/experimentos/{Guid.NewGuid()}/simulacao", Json("""{"setpoint":50}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/usuarios")).StatusCode);
    }

    [Fact]
    public async Task Operador_comanda_e_usa_o_laboratorio_mas_nao_gerencia_usuarios()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var c = await Logado(app, "op@empresa.com", Papeis.Operador);
        var r = await c.PostAsync("/api/controle", Json("""{"setpoint":55}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);                       // chave de operador não é pedida com login ligado
        Assert.Single(app.NodeRed.Recebidos);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/api/experimentos", Json("""{"tipo":"respostaSetpoint","fonte":"simulacao"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/usuarios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/usuarios", Json("""{"email":"a@b.co","nome":"x","papel":"admin","senha":"tanque-azul-42"}"""))).StatusCode);
    }

    [Fact]
    public async Task Com_login_ligado_a_config_nao_pede_chave_de_operador()
    {
        var (app, _) = Criar(new() { ["Control:OperatorApiKey"] = "legado" });
        using var lixo = app;
        var c = await Logado(app, "op@empresa.com", Papeis.Operador);
        var cfg = await Corpo(await c.GetAsync("/api/config"));
        Assert.False(cfg.GetProperty("controle").GetProperty("exigeChaveDeOperador").GetBoolean());
    }

    // ---------- origem (CSRF) ----------

    [Fact]
    public async Task Pedido_que_altera_dados_vindo_de_outro_site_e_recusado()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var c = await Logado(app, "op@empresa.com", Papeis.Operador);

        var estranho = new HttpRequestMessage(HttpMethod.Post, "/api/controle") { Content = Json("""{"setpoint":10}""") };
        estranho.Headers.Add("Origin", "https://site-malicioso.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(estranho)).StatusCode);
        Assert.Empty(app.NodeRed.Recebidos);

        var proprio = new HttpRequestMessage(HttpMethod.Post, "/api/controle") { Content = Json("""{"setpoint":10}""") };
        proprio.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(proprio)).StatusCode);

        var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = Json("""{"email":"a@b.co","senha":"x"}""") };
        login.Headers.Add("Origin", "https://site-malicioso.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await Cliente(app).SendAsync(login)).StatusCode);
    }

    // ---------- trocar a própria senha ----------

    [Fact]
    public async Task Troca_de_senha_exige_a_atual_aplica_as_regras_e_derruba_as_outras_sessoes()
    {
        var (app, _) = Criar();
        using var lixo = app;
        await Novo(app, "ana@empresa.com", Papeis.Operador);
        var a = Cliente(app); var b = Cliente(app);
        await Entrar(a, "ana@empresa.com"); await Entrar(b, "ana@empresa.com");

        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/auth/senha", new { senhaAtual = "errada-errada-1", novaSenha = "nova-senha-forte-9" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/auth/senha", new { senhaAtual = Senha, novaSenha = "curta" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/auth/senha", new { senhaAtual = Senha, novaSenha = Senha })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/api/auth/senha", new { senhaAtual = Senha, novaSenha = "nova-senha-forte-9" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/status")).StatusCode);              // quem trocou continua logado
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/status")).StatusCode);    // a outra sessão caiu

        Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(Cliente(app), "ana@empresa.com", Senha)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Entrar(Cliente(app), "ana@empresa.com", "nova-senha-forte-9")).StatusCode);
    }

    // ---------- administração de usuários ----------

    [Fact]
    public async Task Administrador_cria_lista_e_edita_usuarios_e_a_lista_nunca_traz_hash()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var admin = await Logado(app, "adm@empresa.com", Papeis.Admin);

        var criado = await admin.PostAsJsonAsync("/api/usuarios", new { email = "Novo@Empresa.com", nome = "Novo", papel = "operador", senha = "senha-boa-1234" });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        Assert.Equal("novo@empresa.com", (await Corpo(criado)).GetProperty("usuario").GetProperty("email").GetString());

        var lista = await admin.GetStringAsync("/api/usuarios");
        Assert.Contains("novo@empresa.com", lista);
        Assert.DoesNotContain("pbkdf2", lista);
        Assert.DoesNotContain("senhaHash", lista, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await Entrar(Cliente(app), "novo@empresa.com", "senha-boa-1234")).StatusCode);
    }

    [Theory]
    [InlineData("""{"email":"invalido","nome":"x","papel":"operador","senha":"senha-boa-1234"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"email":"a@empresa.com","nome":"","papel":"operador","senha":"senha-boa-1234"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"email":"a@empresa.com","nome":"x","papel":"deus","senha":"senha-boa-1234"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"email":"a@empresa.com","nome":"x","papel":"operador","senha":"fraca"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"email":"adm@empresa.com","nome":"x","papel":"operador","senha":"senha-boa-1234"}""", HttpStatusCode.Conflict)]
    [InlineData("""{"email":"a@empresa.com","nome":"x","papel":"operador","senha":"senha-boa-1234","extra":1}""", HttpStatusCode.BadRequest)]
    public async Task Criacao_de_usuario_valida_tudo(string corpo, HttpStatusCode esperado)
    {
        var (app, users) = Criar();
        using var lixo = app;
        var admin = await Logado(app, "adm@empresa.com", Papeis.Admin);
        Assert.Equal(esperado, (await admin.PostAsync("/api/usuarios", Json(corpo))).StatusCode);
        Assert.Single(users.Usuarios);
    }

    [Fact]
    public async Task Desativar_derruba_a_sessao_na_hora_e_impede_novo_login()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var admin = await Logado(app, "adm@empresa.com", Papeis.Admin);
        var alvo = await Novo(app, "op@empresa.com", Papeis.Operador);
        var sessao = Cliente(app);
        await Entrar(sessao, "op@empresa.com");
        Assert.Equal(HttpStatusCode.OK, (await sessao.GetAsync("/api/status")).StatusCode);

        var r = await admin.PatchAsJsonAsync($"/api/usuarios/{alvo.Id}", new { ativo = false });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await sessao.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(Cliente(app), "op@empresa.com")).StatusCode);

        await admin.PatchAsJsonAsync($"/api/usuarios/{alvo.Id}", new { ativo = true });
        Assert.Equal(HttpStatusCode.OK, (await Entrar(Cliente(app), "op@empresa.com")).StatusCode);
    }

    [Fact]
    public async Task Mudar_o_papel_vale_na_hora_para_sessoes_abertas()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var admin = await Logado(app, "adm@empresa.com", Papeis.Admin);
        var alvo = await Novo(app, "op@empresa.com", Papeis.Operador);
        var sessao = Cliente(app);
        await Entrar(sessao, "op@empresa.com");
        Assert.Equal(HttpStatusCode.OK, (await sessao.PostAsync("/api/controle", Json("""{"setpoint":50}"""))).StatusCode);

        await admin.PatchAsJsonAsync($"/api/usuarios/{alvo.Id}", new { papel = "visualizador" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await sessao.PostAsync("/api/controle", Json("""{"setpoint":51}"""))).StatusCode);   // sessão antiga cai; precisa entrar de novo
        var nova = Cliente(app);
        await Entrar(nova, "op@empresa.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await nova.PostAsync("/api/controle", Json("""{"setpoint":51}"""))).StatusCode);
    }

    [Fact]
    public async Task Redefinir_a_senha_pelo_administrador_destrava_a_conta()
    {
        var (app, users) = Criar();
        using var lixo = app;
        var admin = await Logado(app, "adm@empresa.com", Papeis.Admin);
        var alvo = await Novo(app, "op@empresa.com", Papeis.Operador);
        for (var i = 0; i < 5; i++) await Entrar(Cliente(app), "op@empresa.com", "errada-errada-1");
        Assert.NotNull(users.Usuarios.Single(u => u.Id == alvo.Id).BloqueadoAte);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/usuarios/{alvo.Id}", new { novaSenha = "fraca" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/usuarios/{alvo.Id}", new { novaSenha = "outra-senha-boa-77" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Entrar(Cliente(app), "op@empresa.com", "outra-senha-boa-77")).StatusCode);
    }

    [Fact]
    public async Task Nunca_remove_o_ultimo_administrador_nem_desativa_a_propria_conta()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var u = await Novo(app, "adm@empresa.com", Papeis.Admin);
        var admin = Cliente(app);
        await Entrar(admin, "adm@empresa.com");

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/usuarios/{u.Id}", new { papel = "operador" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/usuarios/{u.Id}", new { ativo = false })).StatusCode);

        var outro = await Novo(app, "adm2@empresa.com", Papeis.Admin);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/usuarios/{u.Id}", new { ativo = false })).StatusCode);   // continua sendo a própria conta
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/usuarios/{outro.Id}", new { papel = "operador" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/usuarios/{u.Id}", new { papel = "visualizador" })).StatusCode);   // agora é o único
    }

    [Fact]
    public async Task Atualizacao_sem_campos_ou_de_usuario_inexistente_falha_com_clareza()
    {
        var (app, _) = Criar();
        using var lixo = app;
        var admin = await Logado(app, "adm@empresa.com", Papeis.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsync($"/api/usuarios/{Guid.NewGuid()}", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PatchAsJsonAsync($"/api/usuarios/{Guid.NewGuid()}", new { ativo = false })).StatusCode);
    }

    // ---------- administrador inicial ----------

    [Fact]
    public async Task Administrador_inicial_so_e_criado_quando_nao_ha_nenhum_usuario()
    {
        var (app, users) = Criar(new() { ["Auth:AdminEmail"] = "Chefe@Empresa.com", ["Auth:AdminPassword"] = "senha-inicial-forte-1", ["Auth:AdminName"] = "Chefe" });
        using var lixo = app;
        var auth = app.Services.GetRequiredService<AuthService>();

        Assert.True(await auth.GarantirAdministradorInicialAsync(default));
        Assert.Equal(("chefe@empresa.com", "admin"), (users.Usuarios.Single().Email, users.Usuarios.Single().Papel));
        Assert.False(await auth.GarantirAdministradorInicialAsync(default));        // não repete
        Assert.Single(users.Usuarios);
        Assert.Equal(HttpStatusCode.OK, (await Entrar(Cliente(app), "chefe@empresa.com", "senha-inicial-forte-1")).StatusCode);
    }

    [Fact]
    public async Task Administrador_inicial_com_senha_fraca_ou_sem_configuracao_nao_e_criado()
    {
        var (app, users) = Criar(new() { ["Auth:AdminEmail"] = "chefe@empresa.com", ["Auth:AdminPassword"] = "fraca" });
        using var lixo = app;
        Assert.False(await app.Services.GetRequiredService<AuthService>().GarantirAdministradorInicialAsync(default));
        Assert.Empty(users.Usuarios);

        var (app2, users2) = Criar();
        using var lixo2 = app2;
        Assert.False(await app2.Services.GetRequiredService<AuthService>().GarantirAdministradorInicialAsync(default));
        Assert.Empty(users2.Usuarios);
    }

    [Fact]
    public async Task Login_antigo_com_menos_iteracoes_e_refeito_no_acesso()
    {
        var (app, users) = Criar();
        using var lixo = app;
        await Novo(app, "ana@empresa.com", Papeis.Operador);
        users.Usuarios[0].SenhaHash = PasswordHasher.Hash(Senha, 10);        // hash antigo, fraco
        Assert.Equal(HttpStatusCode.OK, (await Entrar(Cliente(app), "ana@empresa.com")).StatusCode);
        Assert.StartsWith("pbkdf2-sha256$1000$", users.Usuarios[0].SenhaHash);
    }
}

/// <summary>Sem login (Development/local): tudo continua aberto, como antes, e a tela sabe disso.</summary>
public class AuthDesligadoTests
{
    [Fact]
    public async Task Com_login_desligado_todos_sao_administradores_locais_e_me_informa()
    {
        using var app = new TestApp();
        var c = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/status")).StatusCode);
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.False(me.GetProperty("autenticacaoAtiva").GetBoolean());
        Assert.True(me.GetProperty("autenticado").GetBoolean());
        Assert.Equal("admin", me.GetProperty("usuario").GetProperty("papel").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/auth/login", new { email = "a@b.co", senha = "x" })).StatusCode);   // nem existe a rota
    }

    [Fact]
    public void Fora_de_Development_o_login_vem_ligado_por_padrao_e_sem_banco_a_subida_e_recusada()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Assert.True(AuthSetup.Habilitado(cfg, new FakeEnv("Production")));
        Assert.False(AuthSetup.Habilitado(cfg, new FakeEnv("Development")));
        var explicito = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Enabled"] = "false" }).Build();
        Assert.False(AuthSetup.Habilitado(explicito, new FakeEnv("Production")));

        using var semBanco = new TestApp(new() { ["Auth:Enabled"] = "true", ["Ingest:ApiKey"] = "k" });
        Assert.Throws<InvalidOperationException>(() => semBanco.CreateClient());
    }

    private sealed class FakeEnv(string nome) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nome;
        public string ApplicationName { get; set; } = "x";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
