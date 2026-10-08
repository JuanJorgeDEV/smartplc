using System.Text.Json;
using SmartPlc.Api.Contracts;
using SmartPlc.Api.Options;

namespace SmartPlc.Api.Validation;

/// <summary>Lê e desserializa o corpo de uma requisição, convertendo falhas em erros de campo.</summary>
public static class PayloadReader
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Telemetria e comandos têm dezenas de bytes. O limite é aplicado aqui (e no Kestrel) para valer em qualquer servidor.</summary>
    public const int MaxBytes = 16 * 1024;

    public readonly record struct Leitura<T>(T? Valor, IReadOnlyList<CampoErro>? Erros, bool Excedeu = false) where T : class;

    public static async Task<Leitura<T>> LerAsync<T>(Stream corpo, CancellationToken ct)
        where T : class
    {
        using var buffer = new MemoryStream();
        var bloco = new byte[4096];
        int n;
        while ((n = await corpo.ReadAsync(bloco, ct)) > 0)
        {
            if (buffer.Length + n > MaxBytes) return new(null, null, Excedeu: true);
            buffer.Write(bloco, 0, n);
        }
        buffer.Position = 0;

        try
        {
            var valor = await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, ct);
            if (valor is null) return new(null, [new CampoErro("$", "o corpo deve ser um objeto JSON")]);
            return new(valor, null);
        }
        catch (JsonException ex)
        {
            // Path vem como "$.nivel"; o texto original do erro do serializador é técnico demais para o cliente.
            var campo = string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path.TrimStart('$', '.');
            if (campo.Length == 0) campo = "$";
            var mensagem = ex.InnerException is null && ex.Message.StartsWith("deve ser", StringComparison.Ordinal)
                ? ex.Message
                : ex.Message.Contains("could not be mapped", StringComparison.OrdinalIgnoreCase)
                    ? "campo desconhecido"
                    : MensagemPadrao(ex);
            return new(null, [new CampoErro(campo, mensagem)]);
        }
    }

    private static string MensagemPadrao(JsonException ex)
    {
        // As mensagens dos conversores customizados chegam com sufixo "Path: ..."; mantém só a primeira parte.
        var texto = ex.Message;
        var corte = texto.IndexOf(" Path:", StringComparison.Ordinal);
        if (corte > 0) texto = texto[..corte];
        return texto.Length > 0 ? texto.TrimEnd('.', ' ') : "JSON inválido";
    }
}

public static class TelemetriaValidator
{
    public static IReadOnlyList<CampoErro> Validar(TelemetriaRequest r, ProcessOptions p)
    {
        var erros = new List<CampoErro>();
        Obrigatorio(erros, "nivel", r.Nivel, p.LeituraMin, p.LeituraMax);
        Obrigatorio(erros, "valvula", r.Valvula, p.LeituraMin, p.LeituraMax);
        Faixa(erros, "setpoint", r.Setpoint, Limites.Percentual);
        Faixa(erros, "valvulaManual", r.ValvulaManual, Limites.Percentual);
        Faixa(erros, "kp", r.Kp, Limites.Kp);
        Faixa(erros, "ti", r.Ti, Limites.Tempo);
        Faixa(erros, "td", r.Td, Limites.Tempo);
        return erros;
    }

    private static void Obrigatorio(List<CampoErro> erros, string campo, double? valor, double min, double max)
    {
        if (valor is null) erros.Add(new CampoErro(campo, "campo obrigatório"));
        else Faixa(erros, campo, valor, (min, max));
    }

    internal static void Faixa(List<CampoErro> erros, string campo, double? valor, (double Min, double Max) limites)
    {
        if (valor is { } v && (v < limites.Min || v > limites.Max))
            erros.Add(new CampoErro(campo, $"deve estar entre {limites.Min} e {limites.Max}"));
    }
}

public static class ControleValidator
{
    public static IReadOnlyList<CampoErro> Validar(ControleRequest r)
    {
        var erros = new List<CampoErro>();

        if (r.Setpoint is null && r.ModoManual is null && r.ValvulaManual is null
            && r.Kp is null && r.Ti is null && r.Td is null)
            erros.Add(new CampoErro("$", "informe ao menos um campo de controle"));

        TelemetriaValidator.Faixa(erros, "setpoint", r.Setpoint, Limites.Percentual);
        TelemetriaValidator.Faixa(erros, "valvulaManual", r.ValvulaManual, Limites.Percentual);
        TelemetriaValidator.Faixa(erros, "kp", r.Kp, Limites.Kp);
        TelemetriaValidator.Faixa(erros, "ti", r.Ti, Limites.Tempo);
        TelemetriaValidator.Faixa(erros, "td", r.Td, Limites.Tempo);
        return erros;
    }
}

/// <summary>Mesmas faixas já usadas pelo frontend (app.js: specSp, specCv, specKp, specTi, specTd).</summary>
public static class Limites
{
    public static readonly (double Min, double Max) Percentual = (0, 100);
    public static readonly (double Min, double Max) Kp = (0, 1000);
    public static readonly (double Min, double Max) Tempo = (0, 100000);
}
