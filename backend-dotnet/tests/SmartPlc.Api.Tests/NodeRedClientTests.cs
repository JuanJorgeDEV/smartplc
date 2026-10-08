using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Options;
using SmartPlc.Api.Services;

namespace SmartPlc.Api.Tests;

public class NodeRedClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) : HttpMessageHandler
    {
        public string? UltimoCorpo { get; private set; }
        public Uri? UltimaUrl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            UltimaUrl = request.RequestUri;
            UltimoCorpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await fn(request, ct);
        }
    }

    private static NodeRedClient Criar(Handler h, int timeoutSeg = 2) =>
        new(new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan },
            Microsoft.Extensions.Options.Options.Create(new NodeRedOptions { ComandoUrl = "http://nodered.teste/api/comando-clp", TimeoutSeconds = timeoutSeg }),
            NullLogger<NodeRedClient>.Instance);

    [Fact]
    public async Task Envia_somente_os_campos_presentes_em_camelCase_sem_nulos()
    {
        var h = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var r = await Criar(h).EnviarComandoAsync(new ControleRequest { Setpoint = 40, ModoManual = true }, CancellationToken.None);

        Assert.Equal(ResultadoEnvio.Aceito, r);
        Assert.Equal("http://nodered.teste/api/comando-clp", h.UltimaUrl!.ToString());
        using var doc = JsonDocument.Parse(h.UltimoCorpo!);
        Assert.Equal(["modoManual", "setpoint"], doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Qualquer_2xx_e_aceito(HttpStatusCode codigo)
    {
        var h = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(codigo)));
        Assert.Equal(ResultadoEnvio.Aceito, await Criar(h).EnviarComandoAsync(new ControleRequest { Kp = 1 }, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Resposta_nao_2xx_e_recusa(HttpStatusCode codigo)
    {
        var h = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(codigo)));
        Assert.Equal(ResultadoEnvio.Recusado, await Criar(h).EnviarComandoAsync(new ControleRequest { Kp = 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task Conexao_recusada_e_inacessivel()
    {
        var h = new Handler((_, _) => throw new HttpRequestException("conexão recusada"));
        Assert.Equal(ResultadoEnvio.Inacessivel, await Criar(h).EnviarComandoAsync(new ControleRequest { Kp = 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task Resposta_que_nao_chega_no_prazo_e_timeout()
    {
        var h = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var cliente = Criar(h, timeoutSeg: 1);

        var inicio = DateTime.UtcNow;
        var r = await cliente.EnviarComandoAsync(new ControleRequest { Kp = 1 }, CancellationToken.None);

        Assert.Equal(ResultadoEnvio.Timeout, r);
        Assert.InRange((DateTime.UtcNow - inicio).TotalSeconds, 0.8, 3);
    }

    [Fact]
    public async Task Cancelamento_do_chamador_nao_e_confundido_com_timeout()
    {
        var h = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Criar(h, timeoutSeg: 10).EnviarComandoAsync(new ControleRequest { Kp = 1 }, cts.Token));
    }
}
