using Microsoft.Extensions.Options;
using SmartPlc.Api.Contracts;

namespace SmartPlc.Api.Services;

public sealed class CommandOptions
{
    public const string Section = "Command";

    /// <summary>Sem a telemetria trazer o valor pedido dentro deste prazo, o comando vira "expirado".</summary>
    public int ConfirmationSeconds { get; set; } = 10;

    /// <summary>Quantos comandos recentes ficam na memória (consulta e tela de Controle).</summary>
    public int KeepRecent { get; set; } = 100;
}

/// <summary>
/// Ciclo de vida de um comando. Cada estado só afirma o que foi OBSERVADO:
/// solicitado (validado, sendo enviado) -> recebido (Node-RED respondeu 2xx) -> confirmado (a telemetria trouxe o valor pedido).
/// Falhou: o Node-RED recusou, não respondeu ou estava inacessível. Expirado: ninguém confirmou no prazo.
/// Substituido: um comando posterior cobriu os mesmos campos antes da confirmação.
/// </summary>
public static class CommandStates
{
    public const string Solicitado = "solicitado";
    public const string Recebido = "recebido";
    public const string Confirmado = "confirmado";
    public const string Falhou = "falhou";
    public const string Expirado = "expirado";
    public const string Substituido = "substituido";
}

public sealed record CommandDto(
    Guid Id,
    string Estado,
    DateTimeOffset SolicitadoEm,
    DateTimeOffset AtualizadoEm,
    IReadOnlyDictionary<string, object> Campos,
    string? Detalhe,
    bool ConfirmadoPeloClp);

/// <summary>
/// Confirmação por RELEITURA (nível A de docs/comandos-estados.md): o comando só é "confirmado" quando uma telemetria
/// recebida DEPOIS do pedido traz, lidos do CLP, todos os valores solicitados. Limite do método: se o CLP já reportava o
/// valor pedido, a releitura não prova que houve escrita; o detalhe do comando informa isso.
/// Fica na memória (reiniciar o servidor esquece os comandos recentes).
/// </summary>
public sealed class CommandTracker(TimeProvider clock, IOptions<CommandOptions> options)
{
    private const double ToleranciaValor = 0.05;      // SP e válvula manual (mesma do frontend)
    private const double ToleranciaParametro = 0.001; // Kp, Ti, Td

    private sealed class Entrada
    {
        public Guid Id { get; init; }
        public DateTimeOffset SolicitadoEm { get; init; }
        public DateTimeOffset AtualizadoEm { get; set; }
        public ControleRequest Campos { get; init; } = new();
        public string Estado { get; set; } = CommandStates.Solicitado;
        public string? Detalhe { get; set; }
        public bool JaIgualAntes { get; init; }
    }

    private readonly Lock _lock = new();
    private readonly List<Entrada> _entradas = [];
    private ControleDto? _ultimoControle;
    private DateTimeOffset? _ultimaTelemetriaEm;

    public CommandDto Registrar(ControleRequest campos, ControleDto? controleAtual)
    {
        lock (_lock)
        {
            var agora = clock.GetUtcNow();
            var entrada = new Entrada
            {
                Id = Guid.NewGuid(),
                SolicitadoEm = agora,
                AtualizadoEm = agora,
                Campos = campos,
                JaIgualAntes = controleAtual is not null && Satisfaz(campos, controleAtual)
            };

            foreach (var antiga in _entradas.Where(e => e.Estado is CommandStates.Solicitado or CommandStates.Recebido))
                if (Cobre(campos, antiga.Campos))
                {
                    antiga.Estado = CommandStates.Substituido;
                    antiga.AtualizadoEm = agora;
                    antiga.Detalhe = "um comando mais recente cobriu os mesmos campos";
                }

            _entradas.Add(entrada);
            var excesso = _entradas.Count - Math.Max(10, options.Value.KeepRecent);
            if (excesso > 0) _entradas.RemoveRange(0, excesso);
            return Dto(entrada);
        }
    }

    /// <summary>O Node-RED respondeu 2xx. Se a telemetria mais recente já trouxe o valor pedido, confirma na hora.</summary>
    public CommandDto MarcarRecebido(Guid id)
    {
        lock (_lock)
        {
            var e = Buscar(id);
            if (e.Estado == CommandStates.Solicitado)
            {
                e.Estado = CommandStates.Recebido;
                e.AtualizadoEm = clock.GetUtcNow();
                if (_ultimoControle is { } c && _ultimaTelemetriaEm is { } t) TentarConfirmar(e, c, t);
            }
            return Dto(e);
        }
    }

    public CommandDto MarcarFalhou(Guid id, string detalhe)
    {
        lock (_lock)
        {
            var e = Buscar(id);
            if (e.Estado is CommandStates.Solicitado or CommandStates.Recebido)
            {
                e.Estado = CommandStates.Falhou;
                e.AtualizadoEm = clock.GetUtcNow();
                e.Detalhe = detalhe;
            }
            return Dto(e);
        }
    }

    /// <summary>Chamado a cada telemetria válida, com o controle LIDO do CLP. Devolve só os comandos que mudaram de estado.</summary>
    public IReadOnlyList<CommandDto> AoTelemetria(ControleDto lido, DateTimeOffset recebidoEm)
    {
        lock (_lock)
        {
            _ultimoControle = lido;
            _ultimaTelemetriaEm = recebidoEm;
            var mudaram = new List<CommandDto>();
            foreach (var e in _entradas.Where(e => e.Estado == CommandStates.Recebido))
                if (TentarConfirmar(e, lido, recebidoEm)) mudaram.Add(Dto(e));
            return mudaram;
        }
    }

    /// <summary>Marca como expirado o que passou do prazo sem confirmação. Devolve só os que mudaram.</summary>
    public IReadOnlyList<CommandDto> ExpirarVencidos()
    {
        lock (_lock)
        {
            var agora = clock.GetUtcNow();
            var prazo = TimeSpan.FromSeconds(Math.Max(1, options.Value.ConfirmationSeconds));
            var mudaram = new List<CommandDto>();
            foreach (var e in _entradas.Where(e => e.Estado is CommandStates.Solicitado or CommandStates.Recebido && agora - e.SolicitadoEm > prazo))
            {
                var semResposta = e.Estado == CommandStates.Solicitado;
                e.Estado = CommandStates.Expirado;
                e.AtualizadoEm = agora;
                e.Detalhe = semResposta
                    ? "o Node-RED não respondeu a tempo"
                    : $"a telemetria não trouxe o valor pedido em {prazo.TotalSeconds:0} s";
                mudaram.Add(Dto(e));
            }
            return mudaram;
        }
    }

    public IReadOnlyList<CommandDto> Recentes(int n)
    {
        lock (_lock) return _entradas.AsEnumerable().Reverse().Take(Math.Clamp(n, 1, 100)).Select(Dto).ToList();
    }

    public CommandDto? Obter(Guid id)
    {
        lock (_lock) return _entradas.FirstOrDefault(e => e.Id == id) is { } e ? Dto(e) : null;
    }

    // ---------- regras ----------

    private bool TentarConfirmar(Entrada e, ControleDto lido, DateTimeOffset telemetriaEm)
    {
        if (telemetriaEm < e.SolicitadoEm || !Satisfaz(e.Campos, lido)) return false;
        e.Estado = CommandStates.Confirmado;
        e.AtualizadoEm = clock.GetUtcNow();
        e.Detalhe = e.JaIgualAntes
            ? "o CLP já reportava este valor antes do comando; a releitura não prova que houve escrita"
            : "o CLP reportou o valor pedido";
        return true;
    }

    /// <summary>Todos os campos pedidos batem com o que o CLP reportou.</summary>
    internal static bool Satisfaz(ControleRequest c, ControleDto lido) =>
        (c.Setpoint is not { } sp || Math.Abs(sp - lido.Setpoint) <= ToleranciaValor)
        && (c.ModoManual is not { } m || m == lido.ModoManual)
        && (c.ValvulaManual is not { } cv || Math.Abs(cv - lido.ValvulaManual) <= ToleranciaValor)
        && (c.Kp is not { } kp || Math.Abs(kp - lido.Kp) <= ToleranciaParametro)
        && (c.Ti is not { } ti || Math.Abs(ti - lido.Ti) <= ToleranciaParametro)
        && (c.Td is not { } td || Math.Abs(td - lido.Td) <= ToleranciaParametro);

    /// <summary>O comando novo toca todos os campos do antigo (logo o antigo ficou sem efeito).</summary>
    private static bool Cobre(ControleRequest novo, ControleRequest antigo) =>
        (antigo.Setpoint is null || novo.Setpoint is not null)
        && (antigo.ModoManual is null || novo.ModoManual is not null)
        && (antigo.ValvulaManual is null || novo.ValvulaManual is not null)
        && (antigo.Kp is null || novo.Kp is not null)
        && (antigo.Ti is null || novo.Ti is not null)
        && (antigo.Td is null || novo.Td is not null);

    private Entrada Buscar(Guid id) => _entradas.First(e => e.Id == id);

    private static CommandDto Dto(Entrada e)
    {
        var campos = new Dictionary<string, object>();
        if (e.Campos.Setpoint is { } sp) campos["setpoint"] = sp;
        if (e.Campos.ModoManual is { } m) campos["modoManual"] = m;
        if (e.Campos.ValvulaManual is { } cv) campos["valvulaManual"] = cv;
        if (e.Campos.Kp is { } kp) campos["kp"] = kp;
        if (e.Campos.Ti is { } ti) campos["ti"] = ti;
        if (e.Campos.Td is { } td) campos["td"] = td;
        return new CommandDto(e.Id, e.Estado, e.SolicitadoEm, e.AtualizadoEm, campos, e.Detalhe, e.Estado == CommandStates.Confirmado);
    }
}
