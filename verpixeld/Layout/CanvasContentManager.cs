using System.Reflection;
using System.Text.Json;
using CanvasManagement.Interfaces;
using SkiaSharp;
using verpixeld.Interfaces;
using verpixeld.WebApi;

namespace verpixeld.Layout;

/// <summary>
///     Manages content (extensions, static content) on named canvases
/// </summary>
public class CanvasContentManager(IDisplayLayoutManager layoutManager, IExtensionDiscovery? extensionDiscovery = null)
    : ICanvasContentManager
{
    private readonly Dictionary<string, CanvasContent> _canvasContents = new();
    private readonly IExtensionDiscovery? _extensionDiscovery = extensionDiscovery;

    // Serializes all content mutations (assign/stop/resize/update/rename) so concurrent HTTP requests
    // (e.g. rapid drag-resize) can't recreate/dispose the same canvas from two threads at once — that race
    // crashed the native renderer (segfault). Monitor is reentrant, so nested calls are fine.
    private readonly object _opLock = new();

    private readonly IDisplayLayoutManager _layoutManager =
        layoutManager ?? throw new ArgumentNullException(nameof(layoutManager));

    /// <summary>
    ///     Gets the number of canvases with active content
    /// </summary>
    public int ActiveContentCount => _canvasContents.Count;

    /// <summary>
    ///     Assigns a dynamic extension to a specific canvas
    /// </summary>
    public CanvasContent AssignExtension(string canvasName, string extensionDisplayName,
        Dictionary<string, object>? config = null)
    {
        lock (_opLock)
        {
        var canvas = _layoutManager.GetCanvas(canvasName);
        if (canvas == null) throw new InvalidOperationException($"Canvas '{canvasName}' not found in current layout");

        // Stop existing content on this canvas
        StopContent(canvasName);

        Console.WriteLine($"[CONTENT] Assigning extension '{extensionDisplayName}' to canvas '{canvasName}'");

        try
        {
            // Create extension
            var ext = canvas.CreateDynamicExtensionByDisplayName(extensionDisplayName);

            // Apply configuration
            if (config != null)
            {
                // Get the actual extension instance from the DynamicExtension wrapper
                // Use the public Instance property instead of reflection
                var targetInstance = ext.Instance;

                Console.WriteLine($"[CONTENT] Extension instance type: {targetInstance.GetType().Name}");

                foreach (var (key, value) in config)
                    try
                    {
                        var propertyInfo = targetInstance.GetType().GetProperty(key);
                        if (propertyInfo == null)
                        {
                            // Fallback to SetProperty on the wrapper
                            ext.SetProperty(key, value);
                            Console.WriteLine($"[CONTENT]   Set {key} = {value} (via SetProperty)");
                        }
                        else if (propertyInfo.CanWrite)
                        {
                            var convertedValue = ConvertParam(value, propertyInfo.PropertyType);
                            propertyInfo.SetValue(targetInstance, convertedValue);
                            Console.WriteLine($"[CONTENT]   Set {key} = {convertedValue}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CONTENT]   Failed to set {key}: {ex.Message}");
                    }
            }

            // Start extension - with workaround for ambiguous methods
            try
            {
                ext.Start();
            }
            catch (AmbiguousMatchException)
            {
                Console.WriteLine("[CONTENT] Ambiguous Start() method detected, using reflection workaround");

                // Workaround: Get the Start method more specifically
                var instanceType = ext.GetType();
                var baseType = instanceType.BaseType;

                // Try to get Start method from the base type (DynamicExtension)
                var startMethod = baseType?.GetMethod("Start",
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly);

                if (startMethod != null)
                {
                    startMethod.Invoke(ext, null);
                    Console.WriteLine("[CONTENT] Started extension using base type method");
                }
                else
                {
                    // Last resort: use the public Instance property
                    var instance = ext.Instance;
                    var instanceStartMethod = instance.GetType().GetMethod("Start");
                    instanceStartMethod?.Invoke(instance, null);
                    Console.WriteLine("[CONTENT] Started extension using Instance property");
                }
            }

            // Track content
            var content = new CanvasContent
            {
                CanvasName = canvasName,
                ContentType = ContentType.DynamicExtension,
                ExtensionDisplayName = extensionDisplayName,
                ExtensionInstance = ext,
                Configuration = config ?? new Dictionary<string, object>(),
                StartedAt = DateTime.UtcNow
            };

            _canvasContents[canvasName] = content;

            Console.WriteLine($"[CONTENT] Extension '{extensionDisplayName}' started on '{canvasName}'");

            return content;
        }
        catch (Exception ex)
        {
            // Unwrap reflection's "target of an invocation" so the real cause is visible.
            var root = ex;
            while (root.InnerException != null) root = root.InnerException;
            Console.WriteLine(
                $"[CONTENT] Error assigning extension '{extensionDisplayName}': {root.GetType().Name}: {root.Message}");
            Console.WriteLine($"[CONTENT]   {root.StackTrace}");
            throw;
        }
        }
    }

    /// <summary>
    ///     Updates configuration of running extension on a canvas
    /// </summary>
    public void UpdateConfiguration(string canvasName, Dictionary<string, object> config)
    {
        lock (_opLock)
        {
        if (!_canvasContents.TryGetValue(canvasName, out var content))
            throw new InvalidOperationException($"No content running on canvas '{canvasName}'");

        if (content.ContentType != ContentType.DynamicExtension || content.ExtensionInstance == null)
            throw new InvalidOperationException($"Canvas '{canvasName}' does not have a dynamic extension");

        Console.WriteLine($"[CONTENT] Updating configuration for '{canvasName}'");

        // Cast to DynamicExtension and get the actual extension instance using the public Instance property
        var dynamicExt = content.ExtensionInstance as dynamic;
        object targetInstance = dynamicExt.Instance;

        Console.WriteLine($"[CONTENT] Extension instance type: {targetInstance.GetType().Name}");

        foreach (var (key, value) in config)
            try
            {
                var propertyInfo = targetInstance.GetType().GetProperty(key);

                if (propertyInfo != null && propertyInfo.CanWrite)
                {
                    var convertedValue = ConvertParam(value, propertyInfo.PropertyType);
                    propertyInfo.SetValue(targetInstance, convertedValue);
                    Console.WriteLine($"[CONTENT]   ✓ Updated {key} = {convertedValue}");
                }
                else if (propertyInfo == null)
                {
                    // Fallback to SetProperty on the wrapper if property is not directly accessible
                    try
                    {
                        dynamic wrapper = content.ExtensionInstance;
                        wrapper.SetProperty(key, value);
                        Console.WriteLine($"[CONTENT]   ✓ Updated {key} = {value} (via SetProperty fallback)");
                    }
                    catch (Exception fallbackEx)
                    {
                        Console.WriteLine($"[CONTENT]   ✗ SetProperty fallback also failed: {fallbackEx.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[CONTENT]   ✗ Property {key} is read-only");
                }

                // Update stored configuration (store original value)
                content.Configuration[key] = value;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CONTENT]   Failed to update {key}: {ex.Message}");
                Console.WriteLine($"[CONTENT]   Stack trace: {ex.StackTrace}");
            }
        }
    }

    /// <summary>
    ///     Binds a structured parameter value (a nested config object or a list of them) to its target type.
    ///     The value may arrive as a raw JSON string (from the GUI/config endpoints), a <see cref="JsonElement" />
    ///     (from a reloaded layout), or an already-typed instance.
    /// </summary>
    /// <summary>
    ///     Converts a parameter value (which may arrive as a string, a JSON-derived number, or an already-typed
    ///     instance) into the property's CLR type. Handles enums (by name OR numeric), SKColor (hex string),
    ///     all numeric widenings/narrowings (e.g. JSON double → int), bool, string and structured types.
    /// </summary>
    private static object? ConvertParam(object? value, Type propType)
    {
        if (value == null) return null;

        var t = Nullable.GetUnderlyingType(propType) ?? propType;
        if (t.IsInstanceOfType(value)) return value; // already the right type

        try
        {
            if (t.IsEnum)
                return value is string es
                    ? Enum.Parse(t, es, true)
                    : Enum.ToObject(t, Convert.ToInt64(value));

            if (t == typeof(SKColor))
            {
                if (value is string cs && SKColor.TryParse(cs, out var c)) return c;
                return value; // leave as-is if not a parseable colour string
            }

            if (t == typeof(int)) return Convert.ToInt32(value);
            if (t == typeof(long)) return Convert.ToInt64(value);
            if (t == typeof(short)) return Convert.ToInt16(value);
            if (t == typeof(byte)) return Convert.ToByte(value);
            if (t == typeof(float)) return Convert.ToSingle(value);
            if (t == typeof(double)) return Convert.ToDouble(value);
            if (t == typeof(decimal)) return Convert.ToDecimal(value);
            if (t == typeof(bool)) return value is bool b ? b : Convert.ToBoolean(value);
            if (t == typeof(string)) return value as string ?? value.ToString();

            // Structured parameters (nested config objects / lists of them) arrive as raw JSON.
            if (!ExtensionJson.IsScalarType(t)) return DeserializeStructured(value, t) ?? value;

            return Convert.ChangeType(value, t);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONTENT]   Convert '{value}' -> {t.Name} failed: {ex.Message}; leaving as-is");
            return value;
        }
    }

    private static object? DeserializeStructured(object? value, Type propType)
    {
        if (value == null) return null;
        if (propType.IsInstanceOfType(value)) return value; // already the right type

        try
        {
            var json = value switch
            {
                string s => s,
                JsonElement je => je.GetRawText(),
                _ => JsonSerializer.Serialize(value, ExtensionJson.Options)
            };

            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize(json, propType, ExtensionJson.Options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONTENT]   Structured bind failed for {propType.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Resizes/repositions a canvas and, if it currently hosts an extension, rebuilds that extension on the
    ///     new canvas so it re-reads the new dimensions (most extensions cache their scale in Start()). The
    ///     extension's current parameter values are preserved across the rebuild.
    /// </summary>
    public bool ResizeCanvas(string canvasName, int x, int y, int width, int height)
    {
        lock (_opLock)
        {
        string? extName = null;
        Dictionary<string, object>? config = null;

        if (_canvasContents.TryGetValue(canvasName, out var content) &&
            content.ContentType == ContentType.DynamicExtension &&
            content.ExtensionInstance != null)
        {
            extName = content.ExtensionDisplayName;
            // Reapply only the USER-supplied config (assign + edits), not reflected live values. This lets the
            // rebuilt extension's constructor re-run its size auto-fit for the new dimensions (e.g. BinaryClock
            // LED size) instead of having the old auto-fitted values frozen back onto it.
            config = content.Configuration.Count > 0
                ? new Dictionary<string, object>(content.Configuration)
                : null;
        }

        // Tear down the current content before the backbuffer is reallocated.
        StopContent(canvasName);

        var newCanvas = _layoutManager.ResizeCanvas(canvasName, x, y, width, height);
        if (newCanvas == null)
        {
            Console.WriteLine($"[CONTENT] ResizeCanvas: canvas '{canvasName}' not found");
            return false;
        }

        if (!string.IsNullOrEmpty(extName))
            AssignExtension(canvasName, extName, config);

        return true;
        }
    }

    /// <summary>
    ///     Re-keys the tracked content from an old canvas name to a new one (used when a canvas is renamed).
    /// </summary>
    public void RenameCanvasContent(string oldName, string newName)
    {
        lock (_opLock)
        {
            if (oldName == newName) return;
            if (!_canvasContents.TryGetValue(oldName, out var content)) return;

            _canvasContents.Remove(oldName);
            _canvasContents[newName] = new CanvasContent
            {
                CanvasName = newName,
                ContentType = content.ContentType,
                ExtensionDisplayName = content.ExtensionDisplayName,
                ExtensionInstance = content.ExtensionInstance,
                Configuration = content.Configuration,
                StartedAt = content.StartedAt
            };
        }
    }

    /// <summary>
    ///     Stops content on a specific canvas
    /// </summary>
    public void StopContent(string canvasName)
    {
        lock (_opLock)
        {
        if (!_canvasContents.TryGetValue(canvasName, out var content)) return; // Nothing to stop

        Console.WriteLine($"[CONTENT] Stopping content on canvas '{canvasName}'");

        try
        {
            if (content.ExtensionInstance != null)
            {
                try
                {
                    // Stop extension
                    var stopMethod = content.ExtensionInstance.GetType().GetMethod("Stop");
                    stopMethod?.Invoke(content.ExtensionInstance, null);
                    Console.WriteLine("[CONTENT]   Extension stopped");
                }
                catch (AggregateException aggEx)
                {
                    // Handle aggregate exceptions (from async operations)
                    var realEx = aggEx.InnerException ?? aggEx;

                    if (realEx is TaskCanceledException || realEx is OperationCanceledException)
                        Console.WriteLine("[CONTENT]   Extension cancelled gracefully (expected during shutdown)");
                    else
                        Console.WriteLine($"[CONTENT]   Error stopping extension: {realEx.Message}");
                }
                catch (TaskCanceledException)
                {
                    // Expected when stopping async operations
                    Console.WriteLine("[CONTENT]   Extension cancelled gracefully");
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping async operations
                    Console.WriteLine("[CONTENT]   Extension cancelled gracefully");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CONTENT]   Error stopping extension: {ex.Message}");
                }

                try
                {
                    // Dispose if possible
                    if (content.ExtensionInstance is IDisposable disposable)
                    {
                        disposable.Dispose();
                        Console.WriteLine("[CONTENT]   Extension disposed");
                    }
                }
                catch (Exception disposeEx)
                {
                    Console.WriteLine($"[CONTENT]   Error disposing extension: {disposeEx.Message}");
                }
            }

            // Clear the canvas
            try
            {
                var canvas = _layoutManager.GetCanvas(canvasName);
                canvas?.Clear();
                Console.WriteLine("[CONTENT]   Canvas cleared");
            }
            catch (Exception clearEx)
            {
                Console.WriteLine($"[CONTENT]   Error clearing canvas: {clearEx.Message}");
            }

            _canvasContents.Remove(canvasName);

            Console.WriteLine($"[CONTENT] ✓ Content stopped on '{canvasName}'");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONTENT] Error stopping content on '{canvasName}': {ex.Message}");
            // Remove from tracking even if there was an error
            _canvasContents.Remove(canvasName);
        }
        }
    }

    /// <summary>
    ///     Stops all content on all canvases
    /// </summary>
    public void StopAllContent()
    {
        if (_canvasContents.Count == 0)
        {
            Console.WriteLine("[CONTENT] No active content to stop");
            return;
        }

        Console.WriteLine($"[CONTENT] Stopping all content ({_canvasContents.Count} canvases)");

        var canvasNames = _canvasContents.Keys.ToList();
        var stopCount = 0;
        var errorCount = 0;

        foreach (var canvasName in canvasNames)
            try
            {
                StopContent(canvasName);
                stopCount++;
            }
            catch (Exception ex)
            {
                errorCount++;
                Console.WriteLine($"[CONTENT] Failed to stop content on '{canvasName}': {ex.Message}");
                // Continue with other canvases
            }

        Console.WriteLine($"[CONTENT] Stopped {stopCount}/{canvasNames.Count} canvases ({errorCount} errors)");
    }

    /// <summary>
    ///     Gets content information for a specific canvas
    /// </summary>
    public CanvasContent? GetContent(string canvasName)
    {
        return _canvasContents.TryGetValue(canvasName, out var content) ? content : null;
    }

    /// <summary>
    ///     Gets all active canvas contents
    /// </summary>
    public IEnumerable<CanvasContent> GetAllContents()
    {
        return _canvasContents.Values;
    }

    /// <summary>
    ///     Restarts content on a canvas (stops and starts with same configuration)
    /// </summary>
    public void RestartContent(string canvasName)
    {
        if (!_canvasContents.TryGetValue(canvasName, out var content))
            throw new InvalidOperationException($"No content running on canvas '{canvasName}'");

        if (content.ContentType != ContentType.DynamicExtension)
            throw new InvalidOperationException($"Cannot restart non-extension content on '{canvasName}'");

        Console.WriteLine($"[CONTENT] Restarting content on '{canvasName}'");

        var extensionName = content.ExtensionDisplayName!;
        var config = content.Configuration;

        StopContent(canvasName);
        AssignExtension(canvasName, extensionName, config);
    }

    /// <summary>
    ///     Invokes a method on a running extension
    /// </summary>
    public object? InvokeMethod(string canvasName, string methodName, object[]? args = null)
    {
        if (!_canvasContents.TryGetValue(canvasName, out var content))
            throw new InvalidOperationException($"No content running on canvas '{canvasName}'");

        if (content.ContentType != ContentType.DynamicExtension || content.ExtensionInstance == null)
            throw new InvalidOperationException($"Canvas '{canvasName}' does not have a dynamic extension");

        Console.WriteLine($"[CONTENT] Invoking method '{methodName}' on canvas '{canvasName}'");

        try
        {
            // The ExtensionInstance is a DynamicExtension which has InvokeMethod
            dynamic dynamicExt = content.ExtensionInstance;

            // Try using TryInvokeMethod first for better error handling
            if (args == null || args.Length == 0)
            {
                // No arguments
                if (dynamicExt.TryInvokeMethod(methodName, out object? result, out string? error))
                {
                    Console.WriteLine($"[CONTENT] Method '{methodName}' invoked successfully, result: {result}");
                    return result;
                }

                throw new InvalidOperationException($"Method invocation failed: {error}");
            }
            else
            {
                // With arguments - need to pass them correctly
                // Build the args array for the TryInvokeMethod call
                var invokeArgs = new object?[] { methodName, null, null }.Concat(args).ToArray();

                // Use InvokeMethod directly with args
                var result = dynamicExt.InvokeMethod(methodName, args);
                Console.WriteLine($"[CONTENT] Method '{methodName}' invoked with {args.Length} args, result: {result}");
                return result;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONTENT] Error invoking method '{methodName}': {ex.Message}");
            throw;
        }
    }

    /// <summary>
    ///     Gets available methods for a running extension
    /// </summary>
    public IEnumerable<ExtensionMethodInfo> GetAvailableMethods(string canvasName)
    {
        if (!_canvasContents.TryGetValue(canvasName, out var content))
            throw new InvalidOperationException($"No content running on canvas '{canvasName}'");

        if (content.ContentType != ContentType.DynamicExtension || content.ExtensionInstance == null)
            throw new InvalidOperationException($"Canvas '{canvasName}' does not have a dynamic extension");

        try
        {
            dynamic dynamicExt = content.ExtensionInstance;

            // Get the extension type info which contains method metadata
            var extensionName = content.ExtensionDisplayName;
            var extensionInfo = _extensionDiscovery?.GetAvailableInfo()
                .FirstOrDefault(e => e.DisplayName == extensionName);

            if (extensionInfo?.Methods != null)
                return extensionInfo.Methods.Select(m => new ExtensionMethodInfo
                {
                    Name = m.Name,
                    DisplayName = m.DisplayName ?? m.Name,
                    Category = m.Category ?? "General",
                    Description = m.Description ?? "",
                    Parameters = m.Parameters?.Select(p => new ExtensionMethodParameterInfo
                    {
                        Name = p.Name,
                        ParameterType = p.TypeName ?? "Object",
                        DefaultValue = p.DefaultValue,
                        IsOptional = p.IsOptional,
                        IsParams = p.IsParams
                    }).ToList() ?? new List<ExtensionMethodParameterInfo>()
                });

            return Enumerable.Empty<ExtensionMethodInfo>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONTENT] Error getting methods for '{canvasName}': {ex.Message}");
            throw;
        }
    }

    /// <summary>
    ///     Checks if a canvas has active content
    /// </summary>
    public bool HasContent(string canvasName)
    {
        return _canvasContents.ContainsKey(canvasName);
    }

    /// <summary>
    ///     Gets summary of all active content
    /// </summary>
    public ContentSummary GetSummary()
    {
        return new ContentSummary
        {
            TotalCanvases = _layoutManager.CanvasCount,
            CanvasesWithContent = _canvasContents.Count,
            CanvasesEmpty = _layoutManager.CanvasCount - _canvasContents.Count,
            Contents = _canvasContents.Values.Select(c => new ContentInfo
            {
                CanvasName = c.CanvasName,
                ContentType = c.ContentType.ToString(),
                ExtensionName = c.ExtensionDisplayName,
                Uptime = (DateTime.UtcNow - c.StartedAt).TotalSeconds
            }).ToList()
        };
    }
}

/// <summary>
///     Information about an extension method
/// </summary>
public class ExtensionMethodInfo
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<ExtensionMethodParameterInfo> Parameters { get; init; } = new();
}

/// <summary>
///     Information about an extension method parameter
/// </summary>
public class ExtensionMethodParameterInfo
{
    public string Name { get; init; } = string.Empty;
    public string ParameterType { get; init; } = string.Empty;
    public object? DefaultValue { get; init; }
    public bool IsOptional { get; init; }
    public bool IsParams { get; init; }
}

/// <summary>
///     Represents content assigned to a canvas
/// </summary>
public class CanvasContent
{
    public string CanvasName { get; init; } = string.Empty;
    public ContentType ContentType { get; init; }
    public string? ExtensionDisplayName { get; init; }
    public object? ExtensionInstance { get; init; }
    public Dictionary<string, object> Configuration { get; init; } = new();
    public DateTime StartedAt { get; init; }

    public double Uptime => (DateTime.UtcNow - StartedAt).TotalSeconds;
}

/// <summary>
///     Type of content on a canvas
/// </summary>
public enum ContentType
{
    None,
    DynamicExtension,
    StaticContent,
    TetrisAnimation
}

/// <summary>
///     Summary of all active content
/// </summary>
public class ContentSummary
{
    public int TotalCanvases { get; init; }
    public int CanvasesWithContent { get; init; }
    public int CanvasesEmpty { get; init; }
    public List<ContentInfo> Contents { get; init; } = new();
}

/// <summary>
///     Information about content on a canvas
/// </summary>
public class ContentInfo
{
    public string CanvasName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string? ExtensionName { get; init; }
    public double Uptime { get; init; }
}
