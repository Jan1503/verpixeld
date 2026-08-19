using System.Text.Json;

namespace verpixeld.Configuration;

/// <summary>
///     Converts deserialized parameter values (which arrive as <see cref="JsonElement" /> after JSON
///     round-tripping) into the CLR primitives the extension parameter binder expects. Object/array kinds
///     are left as raw JSON so the binder can deserialize structured parameters.
/// </summary>
public static class ConfigNormalizer
{
    public static Dictionary<string, object>? Normalize(Dictionary<string, object>? config)
    {
        if (config == null) return null;
        var result = new Dictionary<string, object>(config.Count);
        foreach (var (key, value) in config) result[key] = NormalizeValue(value);
        return result;
    }

    public static object NormalizeValue(object value)
    {
        if (value is not JsonElement je) return value;

        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString() ?? string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => je.TryGetInt32(out var i)
                ? i
                : je.TryGetInt64(out var l)
                    ? l
                    : je.GetDouble(),
            JsonValueKind.Object or JsonValueKind.Array => je.GetRawText(),
            _ => je.ToString()
        };
    }
}
