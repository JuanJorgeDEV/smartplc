using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmartPlc.Api.Tests;

public class ConfigEndpointTests
{
    [Fact]
    public async Task Config_padrao_mostra_modo_somente_leitura_e_historico_desligado()
    {
        using var app = new TestApp();
        var j = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/config");

        Assert.False(j.GetProperty("controle").GetProperty("habilitado").GetBoolean());
        Assert.False(j.GetProperty("historico").GetProperty("habilitado").GetBoolean());
        Assert.False(j.GetProperty("laboratorio").GetProperty("habilitado").GetBoolean());
        Assert.Equal(10, j.GetProperty("processo").GetProperty("nivelBaixo").GetDouble());
        Assert.Equal(90, j.GetProperty("processo").GetProperty("nivelAlto").GetDouble());
        Assert.Equal(5, j.GetProperty("processo").GetProperty("obsoletoAposSegundos").GetDouble());
        Assert.Equal("Siemens S7", j.GetProperty("integracao").GetProperty("tipo").GetString());
        Assert.Equal(8, j.GetProperty("integracao").GetProperty("variaveisConfiguradas").GetInt32());
    }

    [Fact]
    public async Task Config_com_tudo_ligado_reflete_a_configuracao_do_servidor()
    {
        using var app = TestApp.ComLaboratorio(new InMemoryHistoryStore(), new InMemoryExperimentStore(),
            new() { ["Control:Enabled"] = "true", ["Control:OperatorApiKey"] = "x", ["Lab:WriteEnabled"] = "false", ["Command:ConfirmationSeconds"] = "7" });
        var j = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/config");

        Assert.True(j.GetProperty("controle").GetProperty("habilitado").GetBoolean());
        Assert.True(j.GetProperty("controle").GetProperty("exigeChaveDeOperador").GetBoolean());
        Assert.Equal(7, j.GetProperty("controle").GetProperty("confirmacaoSegundos").GetInt32());
        Assert.True(j.GetProperty("historico").GetProperty("habilitado").GetBoolean());
        Assert.Equal(72, j.GetProperty("historico").GetProperty("periodoMaximoHoras").GetInt32());
        Assert.True(j.GetProperty("laboratorio").GetProperty("habilitado").GetBoolean());
        Assert.False(j.GetProperty("laboratorio").GetProperty("escrita").GetBoolean());
        Assert.Equal(100, j.GetProperty("controle").GetProperty("limites").GetProperty("setpoint")[1].GetDouble());
    }

    [Fact]
    public async Task Config_nunca_expoe_chaves_senhas_ou_enderecos_de_rede()
    {
        using var app = new TestApp(new()
        {
            ["Control:Enabled"] = "true",
            ["Control:OperatorApiKey"] = "SEGREDO-OPERADOR-123",
            ["Ingest:ApiKey"] = "SEGREDO-INGESTAO-456",
            ["ConnectionStrings:SmartPlc"] = "Host=db.exemplo.com;Username=u;Password=SEGREDO-BANCO-789;Database=d",
            ["NodeRed:ComandoUrl"] = "http://192.168.0.77:1880/api/comando-clp"
        });
        var texto = await app.CreateClient().GetStringAsync("/api/config");

        foreach (var proibido in new[] { "SEGREDO", "192.168", "db.exemplo.com", "Password", "1880" })
            Assert.DoesNotContain(proibido, texto);
    }

    [Fact]
    public async Task Catalogo_tem_as_8_variaveis_com_os_enderecos_do_fluxo_do_Node_RED()
    {
        using var app = new TestApp();
        var j = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/variaveis");

        Assert.True(j.GetProperty("enderecosVisiveis").GetBoolean());
        var porCampo = j.GetProperty("variaveis").EnumerateArray().ToDictionary(v => v.GetProperty("campo").GetString()!);
        Assert.Equal(8, porCampo.Count);

        // Fonte: node-red/flows.original.json (nó s7 endpoint "PLCSIM_Advanced")
        var esperado = new Dictionary<string, (string Id, string Endereco, string Tipo)>
        {
            ["nivel"] = ("Nivel_Tanque", "DB3,REAL0", "REAL"),
            ["valvula"] = ("Valvula_Output", "DB4,REAL0", "REAL"),
            ["setpoint"] = ("setpoint", "DB5,REAL0", "REAL"),
            ["modoManual"] = ("modo_manual", "DB5,X8.0", "BOOL"),
            ["kp"] = ("kp", "DB5,REAL10", "REAL"),
            ["ti"] = ("ti", "DB5,REAL14", "REAL"),
            ["td"] = ("td", "DB5,REAL18", "REAL"),
            ["valvulaManual"] = ("valvula_manual", "DB5,REAL22", "REAL")
        };
        foreach (var (campo, (id, endereco, tipo)) in esperado)
        {
            Assert.Equal(id, porCampo[campo].GetProperty("identificador").GetString());
            Assert.Equal(endereco, porCampo[campo].GetProperty("endereco").GetString());
            Assert.Equal(tipo, porCampo[campo].GetProperty("tipo").GetString());
        }
    }

    [Fact]
    public async Task Catalogo_pode_ocultar_enderecos_por_configuracao()
    {
        using var app = new TestApp(new() { ["Integration:ShowAddresses"] = "false" });
        var j = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/variaveis");
        Assert.False(j.GetProperty("enderecosVisiveis").GetBoolean());
        Assert.All(j.GetProperty("variaveis").EnumerateArray(), v => Assert.Equal(JsonValueKind.Null, v.GetProperty("endereco").ValueKind));
    }

    [Fact]
    public async Task Catalogo_cobre_exatamente_os_campos_do_contrato_de_telemetria()
    {
        using var app = new TestApp();
        var campos = (await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/variaveis")).GetProperty("variaveis")
            .EnumerateArray().Select(v => v.GetProperty("campo").GetString()!).Order().ToArray();
        var telemetria = (await Estado(app)).GetProperty("controle").EnumerateObject().Select(p => p.Name)
            .Concat(["nivel", "valvula"]).Order().ToArray();
        Assert.Equal(telemetria, campos);
    }

    private static async Task<JsonElement> Estado(TestApp app) => await app.Status();

    [Fact]
    public async Task Rotas_de_leitura_nao_exigem_chave()
    {
        using var app = new TestApp(new() { ["Ingest:ApiKey"] = "k", ["Control:OperatorApiKey"] = "k2" });
        var c = app.CreateClient();
        foreach (var rota in new[] { "/api/config", "/api/variaveis", "/api/comandos", "/api/status" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(rota)).StatusCode);
    }
}
