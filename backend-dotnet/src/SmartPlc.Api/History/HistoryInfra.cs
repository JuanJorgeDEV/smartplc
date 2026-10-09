using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SmartPlc.Api.History;

/// <summary>
/// Lê ConnectionStrings:SmartPlc (variável de ambiente ConnectionStrings__SmartPlc), em formato Npgsql
/// ("Host=...;Username=...;Password=...") ou URI ("postgresql://usuario:senha@host:5432/postgres").
/// Aplica padrões seguros que o texto original não definiu: TLS obrigatório fora de localhost e pool pequeno.
/// </summary>
public static class DbConnection
{
    public const string Name = "SmartPlc";

    public static string? Resolve(IConfiguration cfg)
    {
        var raw = cfg.GetConnectionString(Name);
        return string.IsNullOrWhiteSpace(raw) ? null : Normalize(raw);
    }

    public static string Normalize(string raw)
    {
        raw = raw.Trim();
        var b = raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
             || raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            ? FromUri(raw)
            : new NpgsqlConnectionStringBuilder(raw);

        var local = b.Host is null || b.Host is "localhost" or "127.0.0.1" or "::1" or "host.docker.internal";
        if (!b.ShouldSerialize("SSL Mode")) b.SslMode = local ? SslMode.Prefer : SslMode.Require;
        if (!b.ShouldSerialize("Maximum Pool Size")) b.MaxPoolSize = 5; // o pooler gratuito do Supabase tem poucas conexões
        if (!b.ShouldSerialize("Application Name")) b.ApplicationName = "SmartPlc";
        return b.ConnectionString;
    }

    private static NpgsqlConnectionStringBuilder FromUri(string raw)
    {
        var uri = new Uri(raw);
        var credenciais = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(credenciais[0]),
            Database = uri.AbsolutePath.Trim('/') is { Length: > 0 } db ? db : "postgres"
        };
        if (credenciais.Length > 1) b.Password = Uri.UnescapeDataString(credenciais[1]);

        foreach (var par in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = par.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<SslMode>(kv[1].Replace("-", ""), true, out var modo))
                b.SslMode = modo;
        }
        return b;
    }

    /// <summary>Texto seguro para log: nunca inclui a senha.</summary>
    public static string Describe(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        return $"{b.Username}@{b.Host}:{b.Port}/{b.Database} (ssl={b.SslMode}, pool<={b.MaxPoolSize})";
    }
}

/// <summary>Cursor opaco de paginação: hora (ticks UTC) + Id, em base64url.</summary>
public static class CursorCodec
{
    public static string Encode(Cursor c) =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes($"{c.At.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{c.Id.ToString(CultureInfo.InvariantCulture)}"));

    public static bool TryDecode(string? text, out Cursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var partes = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(text)).Split(':');
            if (partes.Length != 2
                || !long.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !long.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || ticks < 0 || ticks > DateTime.MaxValue.Ticks) return false;
            cursor = new Cursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>Plano B da retenção. Só roda com History:Retention:Enabled=true (o caminho preferido é o pg_cron do Supabase).</summary>
public sealed class RetentionService(IHistoryStore store, IOptions<HistoryOptions> options, TimeProvider clock, ILogger<RetentionService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled || !o.Retention.Enabled || !store.Habilitado) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, o.Retention.IntervalMinutes)), clock);
        do
        {
            await ExecutarUmaVezAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ExecutarUmaVezAsync(CancellationToken ct)
    {
        var r = options.Value.Retention;
        var agora = clock.GetUtcNow();
        try
        {
            var (amostras, eventos) = await store.DeleteExpiredAsync(
                agora.AddDays(-r.SamplesDays), agora.AddDays(-r.EventsDays), Math.Max(100, r.BatchSize), ct);
            log.LogInformation("Retenção executada: {A} amostras e {E} eventos expirados apagados (limites: {DA} e {DE} dias)",
                amostras, eventos, r.SamplesDays, r.EventsDays);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha na rotina de retenção; nada foi apagado além do que já constava como confirmado");
        }
    }
}
