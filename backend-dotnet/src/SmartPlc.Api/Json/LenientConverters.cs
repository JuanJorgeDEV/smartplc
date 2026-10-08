using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartPlc.Api.Json;

/// <summary>
/// Aceita número JSON ou texto numérico inequívoco ("12.5"), como o parseFloat do Express,
/// mas rejeita o que lá virava 0 ou NaN: "abc", "", "NaN", "Infinity", true/false, objetos.
/// </summary>
public sealed class LenientDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetDouble();

            case JsonTokenType.String:
                var texto = reader.GetString()!.Trim();
                if (double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var valor)
                    && double.IsFinite(valor))
                    return valor;
                break;
        }
        throw new JsonException("deve ser um número finito");
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is { } v) writer.WriteNumberValue(v); else writer.WriteNullValue();
    }
}

/// <summary>
/// Aceita true/false, 0/1 e os textos "true"/"false"/"0"/"1". Qualquer outra coisa é erro.
/// (No Express, Boolean("false") era true.)
/// </summary>
public sealed class LenientBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;

            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var n) && n is 0 or 1) return n == 1;
                break;

            case JsonTokenType.String:
                switch (reader.GetString()!.Trim().ToLowerInvariant())
                {
                    case "true" or "1": return true;
                    case "false" or "0": return false;
                }
                break;
        }
        throw new JsonException("deve ser booleano (true/false ou 0/1)");
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is { } v) writer.WriteBooleanValue(v); else writer.WriteNullValue();
    }
}
