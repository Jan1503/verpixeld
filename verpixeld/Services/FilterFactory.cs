using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CanvasManagement.Interfaces;
using SkiaSharp;
using verpixeld.Layout;

namespace verpixeld.Services;

/// <summary>
///     Turns saved filter settings into live filters and back, for the wall-wide chain and the per-canvas
///     effect chains alike: one place that knows how a filter is found (type or display name), which of its
///     properties are settings (<see cref="FilterParameterAttribute" /> plus Intensity and Enabled) and how
///     values from JSON, the API or a saved scene are converted (numbers, bools, enums, colours).
/// </summary>
public sealed class FilterFactory(IFilterDiscovery discovery, ILogger<FilterFactory> log)
{
    private const string IntensityKey = nameof(ICanvasFilter.Intensity);
    private const string EnabledKey = nameof(ICanvasFilter.Enabled);

    /// <summary>A new filter for <paramref name="config" />, settings applied; null when the type is unknown.</summary>
    public ICanvasFilter? Create(FilterConfiguration config)
    {
        var filter = Create(config.FilterType);
        if (filter == null)
        {
            log.LogWarning("Unknown filter '{FilterType}'", config.FilterType);
            return null;
        }

        Apply(filter, config.Parameters);
        return filter;
    }

    /// <summary>A new filter by type name or display name (as the UI and older scenes store it).</summary>
    public ICanvasFilter? Create(string typeOrDisplayName)
    {
        if (string.IsNullOrWhiteSpace(typeOrDisplayName)) return null;
        return discovery.Create(typeOrDisplayName)
               ?? discovery.CreateByDisplayName(typeOrDisplayName)
               ?? (discovery.GetByDisplayName(typeOrDisplayName)?.Name is { Length: > 0 } typeName
                   ? discovery.Create(typeName)
                   : null);
    }

    /// <summary>Filters for every config that can be created, in order; unknown ones are skipped (and logged).</summary>
    public List<ICanvasFilter> CreateAll(IEnumerable<FilterConfiguration>? configs) =>
        (configs ?? []).Select(Create).OfType<ICanvasFilter>().ToList();

    /// <summary>The settings of a live filter, ready to save (enums as names, colours as #AARRGGBB).</summary>
    public static FilterConfiguration Describe(ICanvasFilter filter)
    {
        var config = new FilterConfiguration { FilterType = filter.GetType().Name };
        config.Parameters[IntensityKey] = filter.Intensity;
        config.Parameters[EnabledKey] = filter.Enabled;
        foreach (var prop in SettingProperties(filter.GetType()))
        {
            var value = prop.GetValue(filter);
            if (value == null) continue;
            config.Parameters[prop.Name] = value switch
            {
                SKColor c => c.ToString(),
                Enum e => e.ToString(),
                _ => value
            };
        }
        return config;
    }

    /// <summary>
    ///     Sets the given settings on a filter. Unknown keys and values that do not convert are skipped, so an
    ///     old scene or a stray UI field never breaks loading.
    /// </summary>
    public static void Apply(object filter, IReadOnlyDictionary<string, object>? parameters)
    {
        if (parameters == null) return;
        var type = filter.GetType();
        foreach (var (key, raw) in parameters)
        {
            var prop = type.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is not { CanWrite: true } || prop.GetIndexParameters().Length != 0) continue;
            if (TryConvert(raw, prop.PropertyType, out var value)) prop.SetValue(filter, value);
        }
    }

    /// <summary>Public settable properties marked with <see cref="FilterParameterAttribute" />.</summary>
    private static IEnumerable<PropertyInfo> SettingProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 &&
                        p.GetCustomAttribute<FilterParameterAttribute>() != null);

    /// <summary>Converts a JSON element, string or number to <paramref name="target" />.</summary>
    internal static bool TryConvert(object? raw, Type target, out object? value)
    {
        value = null;
        if (raw is JsonElement json)
            raw = json.ValueKind switch
            {
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Number => json.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        if (raw == null) return false;

        try
        {
            var inv = CultureInfo.InvariantCulture;
            if (target == typeof(string)) value = Convert.ToString(raw, inv);
            else if (target == typeof(bool))
                value = raw is string s ? bool.Parse(s) : Convert.ToBoolean(raw, inv);
            else if (target.IsEnum)
                value = raw is string name
                    ? Enum.Parse(target, name, ignoreCase: true)
                    : Enum.ToObject(target, Convert.ToInt32(raw, inv));
            else if (target == typeof(SKColor))
            {
                if (raw is not string hex || !SKColor.TryParse(hex, out var color)) return false;
                value = color;
            }
            else if (target == typeof(float)) value = Convert.ToSingle(raw, inv);
            else if (target == typeof(double)) value = Convert.ToDouble(raw, inv);
            else if (target == typeof(int)) value = Convert.ToInt32(Math.Round(Convert.ToDouble(raw, inv)));
            else if (target == typeof(byte)) value = (byte)Math.Clamp(Math.Round(Convert.ToDouble(raw, inv)), 0, 255);
            else return false;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return false;
        }
    }
}
