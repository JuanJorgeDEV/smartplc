using Microsoft.Extensions.Time.Testing;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Services;

namespace SmartPlc.Api.Tests;

/// <summary>Confirmação de comandos por releitura: regras puras, relógio simulado.</summary>
public class CommandTrackerTests
{
    private static readonly ControleDto Base = new(Setpoint: 50, ModoManual: false, ValvulaManual: 0, Kp: 1, Ti: 20, Td: 0);

    private static (CommandTracker Tracker, FakeTimeProvider Relogio) Criar(int confirmacaoSeg = 10)
    {
        var relogio = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var tracker = new CommandTracker(relogio, Microsoft.Extensions.Options.Options.Create(new CommandOptions { ConfirmationSeconds = confirmacaoSeg }));
        return (tracker, relogio);
    }

    private static ControleRequest Pedido(double? sp = null, bool? manual = null, double? cv = null, double? kp = null, double? ti = null, double? td = null) =>
        new() { Setpoint = sp, ModoManual = manual, ValvulaManual = cv, Kp = kp, Ti = ti, Td = td };

    [Fact]
    public void Novo_comando_nasce_solicitado_e_nao_confirmado()
    {
        var (t, _) = Criar();
        var c = t.Registrar(Pedido(sp: 70), Base);
        Assert.Equal(CommandStates.Solicitado, c.Estado);
        Assert.False(c.ConfirmadoPeloClp);
        Assert.Equal(70.0, c.Campos["setpoint"]);
        Assert.Single(c.Campos);
    }

    [Fact]
    public void Recebido_pelo_Node_RED_nao_e_confirmado()
    {
        var (t, _) = Criar();
        var c = t.Registrar(Pedido(sp: 70), Base);
        var r = t.MarcarRecebido(c.Id);
        Assert.Equal(CommandStates.Recebido, r.Estado);
        Assert.False(r.ConfirmadoPeloClp);
    }

    [Fact]
    public void Telemetria_com_o_valor_pedido_confirma()
    {
        var (t, rel) = Criar();
        var c = t.Registrar(Pedido(sp: 70), Base);
        t.MarcarRecebido(c.Id);
        rel.Advance(TimeSpan.FromSeconds(1));

        var mudou = t.AoTelemetria(Base with { Setpoint = 70 }, rel.GetUtcNow());

        var confirmado = Assert.Single(mudou);
        Assert.Equal(CommandStates.Confirmado, confirmado.Estado);
        Assert.True(confirmado.ConfirmadoPeloClp);
    }

    [Fact]
    public void Telemetria_com_o_valor_antigo_nao_confirma()
    {
        var (t, rel) = Criar();
        var c = t.Registrar(Pedido(sp: 70), Base);
        t.MarcarRecebido(c.Id);
        rel.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(t.AoTelemetria(Base, rel.GetUtcNow()));
        Assert.Equal(CommandStates.Recebido, t.Obter(c.Id)!.Estado);
    }

    [Theory]
    [InlineData(70.04, true)]   // dentro da tolerância de 0,05
    [InlineData(70.05, true)]
    [InlineData(70.06, false)]
    [InlineData(69.94, false)]
    public void Setpoint_usa_tolerancia_de_0_05(double lido, bool confirma)
    {
        var (t, rel) = Criar();
        var c = t.Registrar(Pedido(sp: 70), Base);
        t.MarcarRecebido(c.Id);
        rel.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(confirma, t.AoTelemetria(Base with { Setpoint = lido }, rel.GetUtcNow()).Count == 1);
    }

    [Fact]
    public void Modo_exige_igualdade_exata()
    {
        var (t, rel) = Criar();
        var c = t.Registrar(Pedido(manual: true), Base);
        t.MarcarRecebido(c.Id);
        rel.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(t.AoTelemetria(Base with { ModoManual = false }, rel.GetUtcNow()));
        Assert.Single(t.AoTelemetria(Base with { ModoManual = true }, rel.GetUtcNow()));
    }

    [Fact]
    public void Parametros_PID_usam_tolerancia_pequena()
    {
        var (t, rel) = Criar();
        var c = t.Registrar(Pedido(kp: 2.5), Base);
        t.MarcarRecebido(c.Id);
        rel.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(t.AoTelemetria(Base with { Kp = 2.4 }, rel.GetUtcNow()));
        Assert.Single(t.AoTelemetria(Base with { Kp = 2.5004 }, rel.GetUtcNow()));
    }

    [Fact]
    public void Comando_com_varios_campos_so_confirma_quando_todos_chegaram()
    {
        var (t, rel) = Criar();
        var c = t.Registrar(Pedido(sp: 70, kp: 3, ti: 12), Base);
        t.MarcarRecebido(c.Id);
        rel.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(t.AoTelemetria(Base with { Setpoint = 70, Kp = 3 }, rel.GetUtcNow()));          // falta Ti
        Assert.Single(t.AoTelemetria(Base with { Setpoint = 70, Kp = 3, Ti = 12 }, rel.GetUtcNow()));
    }

    [Fact]
    public void Valvula_manual_confirma_pelo_valor_lido_do_CLP()
    {
        var (t, rel) = Criar();
        var c = t.Registrar(Pedido(cv: 33), Base);
        t.MarcarRecebido(c.Id);
        rel.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(t.AoTelemetria(Base with { ValvulaManual = 33 }, rel.GetUtcNow()));
    }

    [Fact]
    public void Telemetria_anterior_ao_pedido_nao_confirma_nem_depois_do_recebido()
    {
        var (t, rel) = Criar();
        var antes = rel.GetUtcNow();
        rel.Advance(TimeSpan.FromSeconds(5));
        t.AoTelemetria(Base with { Setpoint = 70 }, antes);          // leitura velha, anterior ao pedido, já com 70
        var c = t.Registrar(Pedido(sp: 70), Base);
        var r = t.MarcarRecebido(c.Id);
        Assert.Equal(CommandStates.Recebido, r.Estado);               // a leitura velha não vale
    }

    [Fact]
    public void Eco_que_chega_antes_da_resposta_do_Node_RED_confirma_ao_marcar_recebido()
    {
        var (t, rel) = Criar();
        var c = t.Registrar(Pedido(sp: 70), Base);
        rel.Advance(TimeSpan.FromMilliseconds(200));
        t.AoTelemetria(Base with { Setpoint = 70 }, rel.GetUtcNow()); // a telemetria chega antes do 200 do Node-RED
        var r = t.MarcarRecebido(c.Id);
        Assert.Equal(CommandStates.Confirmado, r.Estado);
    }

    [Fact]
    public void Valor_que_o_CLP_ja_reportava_e_confirmado_com_ressalva_no_detalhe()
    {
        var (t, rel) = Criar();
        var c = t.Registrar(Pedido(sp: 50), Base);                    // já era 50
        t.MarcarRecebido(c.Id);
        rel.Advance(TimeSpan.FromSeconds(1));
        var confirmado = Assert.Single(t.AoTelemetria(Base, rel.GetUtcNow()));
        Assert.Contains("já reportava", confirmado.Detalhe);
    }

    [Fact]
    public void Sem_confirmacao_no_prazo_expira_uma_unica_vez()
    {
        var (t, rel) = Criar(10);
        var c = t.Registrar(Pedido(sp: 70), Base);
        t.MarcarRecebido(c.Id);

        rel.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(t.ExpirarVencidos());                            // exatamente no limite ainda vale

        rel.Advance(TimeSpan.FromMilliseconds(1));
        var exp = Assert.Single(t.ExpirarVencidos());
        Assert.Equal(CommandStates.Expirado, exp.Estado);
        Assert.Empty(t.ExpirarVencidos());                            // não repete
    }

    [Fact]
    public void Comando_expirado_nao_volta_a_ser_confirmado_por_telemetria_tardia()
    {
        var (t, rel) = Criar(10);
        var c = t.Registrar(Pedido(sp: 70), Base);
        t.MarcarRecebido(c.Id);
        rel.Advance(TimeSpan.FromSeconds(11));
        t.ExpirarVencidos();
        Assert.Empty(t.AoTelemetria(Base with { Setpoint = 70 }, rel.GetUtcNow()));
        Assert.Equal(CommandStates.Expirado, t.Obter(c.Id)!.Estado);
    }

    [Fact]
    public void Falha_do_Node_RED_marca_falhou_com_o_motivo()
    {
        var (t, _) = Criar();
        var c = t.Registrar(Pedido(sp: 70), Base);
        var f = t.MarcarFalhou(c.Id, "Node-RED inacessível");
        Assert.Equal(CommandStates.Falhou, f.Estado);
        Assert.Equal("Node-RED inacessível", f.Detalhe);
        Assert.Empty(t.ExpirarVencidos());
    }

    [Fact]
    public void Comando_posterior_que_cobre_os_mesmos_campos_substitui_o_anterior()
    {
        var (t, rel) = Criar();
        var a = t.Registrar(Pedido(sp: 60), Base);
        t.MarcarRecebido(a.Id);
        var b = t.Registrar(Pedido(sp: 80), Base);
        t.MarcarRecebido(b.Id);
        Assert.Equal(CommandStates.Substituido, t.Obter(a.Id)!.Estado);

        rel.Advance(TimeSpan.FromSeconds(1));
        var mudou = t.AoTelemetria(Base with { Setpoint = 80 }, rel.GetUtcNow());
        Assert.Equal(b.Id, Assert.Single(mudou).Id);
    }

    [Fact]
    public void Comando_que_toca_outro_campo_nao_substitui()
    {
        var (t, _) = Criar();
        var a = t.Registrar(Pedido(sp: 60), Base);
        t.Registrar(Pedido(kp: 3), Base);
        Assert.Equal(CommandStates.Solicitado, t.Obter(a.Id)!.Estado);
    }

    [Fact]
    public void Recentes_vem_do_mais_novo_para_o_mais_antigo_e_a_memoria_e_limitada()
    {
        var relogio = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var t = new CommandTracker(relogio, Microsoft.Extensions.Options.Options.Create(new CommandOptions { KeepRecent = 10 }));
        for (var i = 0; i < 25; i++) { t.Registrar(Pedido(sp: i), Base); relogio.Advance(TimeSpan.FromSeconds(1)); }
        var recentes = t.Recentes(100);
        Assert.Equal(10, recentes.Count);
        Assert.Equal(24.0, recentes[0].Campos["setpoint"]);
    }

    [Fact]
    public void Id_desconhecido_devolve_nulo()
    {
        var (t, _) = Criar();
        Assert.Null(t.Obter(Guid.NewGuid()));
    }
}
