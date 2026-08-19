using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace verpixeld.WebApi;

/// <summary>
///     Shared JSON settings for (de)serializing structured extension parameter values (nested config
///     objects and lists of them). Colours round-trip as hex strings and enums as their names so the
///     values match exactly what the web GUI sends and displays.
/// </summary>
public static class ExtensionJson
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new SKColorJsonConverter());
        return o;
    }

    /// <summary>
    ///     True for types we treat as a single scalar value (handled by the existing per-type coercion),
    ///     so callers know when to fall back to full JSON (de)serialization for structured types.
    /// </summary>
    public static bool IsScalarType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) ||
               t == typeof(DateTime) || t == typeof(SKColor);
    }
}

/// <summary>Serializes <see cref="SKColor" /> as "#AARRGGBB" and parses common hex / numeric forms back.</summary>
public sealed class SKColorJsonConverter : JsonConverter<SKColor>
{
    public override SKColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return SKColors.Black;
                if (SKColor.TryParse(s, out var c)) return c;
                // Bare hex without '#'
                if (SKColor.TryParse("#" + s.TrimStart('#'), out var c2)) return c2;
                return SKColors.Black;
            }
            case JsonTokenType.Number:
                // 0xAARRGGBB packed integer.
                return new SKColor((uint)reader.GetInt64());
            default:
                reader.Skip();
                return SKColors.Black;
        }
    }

    public override void Write(Utf8JsonWriter writer, SKColor value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"#{value.Alpha:X2}{value.Red:X2}{value.Green:X2}{value.Blue:X2}");
    }
}
