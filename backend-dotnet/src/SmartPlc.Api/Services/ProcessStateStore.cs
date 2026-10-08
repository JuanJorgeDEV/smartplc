using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Options;

namespace SmartPlc.Api.Services;

/// <summary>
/// Estado do processo em memória (a persistência em PostgreSQL entra no Marco 4).
/// Todas as leituras e escritas passam pelo mesmo lock; o snapshot é imutável.
/// </summary>
public sealed class ProcessStateStore(IOptions<ProcessOptions> process, IOptions<TelemetryOptions> telemetry, TimeProvider clock)
{
    private readonly Lock _lock = new();
    private readonly ProcessOptions _process = process.Value;
    private readonly TelemetryOptions _telemetry = telemetry.Value;

    private double _nivel, _valvula;
    private DateTimeOffset? _recebidoEm;
    private ControleDto _controle = new(0, false, 0, 1.0, 20.0, 0);

    /// <summary>Aplica uma telemetria JÁ VALIDADA. Campos de controle ausentes preservam o valor atual.</summary>
    public StatusResponse ApplyTelemetria(TelemetriaRequest t)
    {
        lock (_lock)
        {
            _nivel = t.Nivel!.Value;
            _valvula = t.Valvula!.Value;
            _recebidoEm = clock.GetUtcNow();
            _controle = Merge(_controle, t.Setpoint, t.ModoManual, t.ValvulaManual, t.Kp, t.Ti, t.Td);
            return Build();
        }
    }

    /// <summary>Aplica um comando JÁ VALIDADO e aceito pelo Node-RED. Não mexe na telemetria.</summary>
    public StatusResponse ApplyControle(ControleRequest c)
    {
        lock (_lock)
        {
            _controle = Merge(_controle, c.Setpoint, c.ModoManual, c.ValvulaManual, c.Kp, c.Ti, c.Td);
            return Build();
        }
    }

    public StatusResponse Snapshot()
    {
        lock (_lock) return Build();
    }

    private static ControleDto Merge(ControleDto atual, double? sp, bool? manual, double? cv, double? kp, double? ti, double? td) =>
        new(sp ?? atual.Setpoint, manual ?? atual.ModoManual, cv ?? atual.ValvulaManual,
            kp ?? atual.Kp, ti ?? atual.Ti, td ?? atual.Td);

    private StatusResponse Build()
    {
        var agora = clock.GetUtcNow();
        double? idade = _recebidoEm is { } r ? Math.Max(0, (agora - r).TotalSeconds) : null;
        var online = idade is { } i && i <= _telemetry.StaleAfterSeconds;

        // LEDs usam o valor bruto; o arredondamento é só de exibição (igual ao Express).
        // Sem nenhuma leitura não há faixa a indicar: nenhum LED (evita alarme de nível baixo falso na subida).
        var leds = _recebidoEm is null ? new LedsDto(false, false, false) : new LedsDto(
            Low: _nivel < _process.NivelBaixo,
            Normal: _nivel >= _process.NivelBaixo && _nivel <= _process.NivelAlto,
            High: _nivel > _process.NivelAlto);

        var tel = new TelemetriaDto(
            Nivel: Arredondar(_nivel),
            Valvula: Arredondar(_valvula),
            Leds: leds,
            Timestamp: _recebidoEm,
            PlcOnline: online,
            IdadeSegundos: idade is null ? null : Math.Round(idade.Value, 1));

        return new StatusResponse(tel, _controle);
    }

    private static double Arredondar(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
