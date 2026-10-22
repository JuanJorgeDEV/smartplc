using Microsoft.EntityFrameworkCore;
using SmartPlc.Api.Persistence;

namespace SmartPlc.Api.History;

/// <summary>
/// Aplica as migrations do banco em segundo plano (History:MigrateOnStartup=true), SEM bloquear nem derrubar o servidor.
/// Se o banco estiver fora do ar (por exemplo, projeto do Supabase pausado), o painel e o tempo real continuam funcionando
/// e a migration é tentada de novo, com espera crescente, até dar certo. Antes disso a falha derrubava o processo na subida.
/// </summary>
public sealed class SchemaMigrator(IDbContextFactory<SmartPlcDbContext> fabrica, TimeProvider clock, ILogger<SchemaMigrator> log) : BackgroundService
{
    public static readonly TimeSpan EsperaInicial = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromMinutes(2);

    public int Tentativas { get; private set; }
    public bool Concluida { get; private set; }
    public string? UltimoErro { get; private set; }

    /// <summary>Esperas usadas entre as tentativas (para diagnóstico e testes).</summary>
    public IReadOnlyList<TimeSpan> Esperas => _esperas;
    private readonly List<TimeSpan> _esperas = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var espera = EsperaInicial;
        while (!ct.IsCancellationRequested)
        {
            Tentativas++;
            try
            {
                await using var db = await fabrica.CreateDbContextAsync(ct);
                await db.Database.MigrateAsync(ct);
                Concluida = true;
                UltimoErro = null;
                log.LogInformation("Migrations aplicadas (tentativa {N}).", Tentativas);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                UltimoErro = ex.GetBaseException().Message;
                log.LogError("Não foi possível aplicar as migrations (tentativa {N}): {Erro}. O servidor continua no ar; nova tentativa em {Espera:0} s.",
                    Tentativas, UltimoErro, espera.TotalSeconds);
                _esperas.Add(espera);
                try { await Task.Delay(espera, clock, ct); }
                catch (OperationCanceledException) { return; }
                espera = TimeSpan.FromTicks(Math.Min(espera.Ticks * 2, EsperaMaxima.Ticks));
            }
        }
    }
}
