using System.Reflection;
using CanvasManagement.Interfaces;

namespace verpixeld.Layout;

/// <summary>
///     The order in which a configuration is applied to an extension. Parameters marked
///     <see cref="ExtensionParameterAttribute.AppliesFirst" /> (a preset or theme that sets other parameters)
///     come first, so the values the user set afterwards win; everything else keeps the given order.
/// </summary>
public static class ExtensionConfigOrder
{
    public static IEnumerable<KeyValuePair<string, object>> For(Type extensionType, IEnumerable<KeyValuePair<string, object>> config)
    {
        var list = config.ToList();
        bool First(string key) =>
            extensionType.GetProperty(key, BindingFlags.Public | BindingFlags.Instance)
                ?.GetCustomAttribute<ExtensionParameterAttribute>()?.AppliesFirst == true;
        return list.Where(kv => First(kv.Key)).Concat(list.Where(kv => !First(kv.Key)));
    }
}
