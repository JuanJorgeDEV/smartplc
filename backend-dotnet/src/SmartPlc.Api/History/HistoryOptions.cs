namespace SmartPlc.Api.History;

/// <summary>
/// Política de persistência. Os valores padrão são pontos de partida para teste, não números definitivos:
/// ajustar depois de observar a malha real e o consumo medido do PostgreSQL.
/// </summary>
public sealed class HistoryOptions
{
    public const string Section = "History";

    /// <summary>Só tem efeito se houver connection string. Sem ela, o histórico fica desligado e o resto funciona igual.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A) Grava uma amostra se passou este tempo desde a última amostra gravada (avaliado quando CHEGA telemetria).</summary>
    public int SampleIntervalSeconds { get; set; } = 10;

    /// <summary>B) Grava se o nível variou pelo menos isto (pontos percentuais) em relação à última amostra gravada.</summary>
    public double MinimumLevelChange { get; set; } = 0.5;

    /// <summary>B) Idem para a saída da válvula.</summary>
    public double MinimumValveChange { get; set; } = 2.0;

    /// <summary>C) Registra eventos (alarmes, comunicação, SP, modo, PID).</summary>
    public bool RecordEvents { get; set; } = true;

    /// <summary>Diferença mínima para considerar que SP/PID mudaram (evita ruído de ponto flutuante).</summary>
    public double ParameterTolerance { get; set; } = 0.001;

    /// <summary>Tamanho do buffer em memória entre a API e o banco. Cheio => descarta o mais novo e registra no log.</summary>
    public int QueueCapacity { get; set; } = 1000;

    public int MaxWriteRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 1000;

    /// <summary>Aplica as migrations ao iniciar. DESLIGADO por padrão: ninguém altera um banco remoto sem pedir.</summary>
    public bool MigrateOnStartup { get; set; }

    // Consultas
    public int DefaultQueryLimit { get; set; } = 1000;
    public int MaxQueryLimit { get; set; } = 5000;
    public int DefaultQueryRangeMinutes { get; set; } = 60;
    public int MaxQueryRangeHours { get; set; } = 72;

    public RetentionOptions Retention { get; set; } = new();
}

/// <summary>
/// Plano B da retenção, caso o Supabase Cron (pg_cron) não possa ser usado.
/// O caminho preferido é <c>db/retencao-pg_cron.sql</c>; este serviço fica DESLIGADO por padrão.
/// </summary>
public sealed class RetentionOptions
{
    public bool Enabled { get; set; }
    public int SamplesDays { get; set; } = 14;
    public int EventsDays { get; set; } = 365;
    public int BatchSize { get; set; } = 5000;
    public int IntervalMinutes { get; set; } = 60;
}
