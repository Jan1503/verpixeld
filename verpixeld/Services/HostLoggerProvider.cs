namespace verpixeld.Services;

/// <summary>
///     The host's <see cref="ILoggerProvider"/>: one line per message in the format the log has always had,
///     <c>[TAG] message</c> (warnings and errors marked), on the console and in <see cref="LogService"/> for the
///     web page. The tag of a class is its name without a Service/Endpoints suffix, upper case
///     (<c>ILogger&lt;AlertService&gt;</c> → <c>[ALERT]</c>); framework categories keep their full name.
/// </summary>
public sealed class HostLoggerProvider(LogService log) : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HostLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new HostLogger(log, name, Tag(name)));

    public void Dispose() => _loggers.Clear();

    /// <summary>
    ///     The class's <see cref="LogTagAttribute"/> if it has one, else verpixeld.Services.AlertService → ALERT;
    ///     Microsoft.AspNetCore.Hosting → as is.
    /// </summary>
    internal static string Tag(string category)
    {
        if (!category.StartsWith("verpixeld", StringComparison.Ordinal)) return category;
        var type = typeof(HostLoggerProvider).Assembly.GetType(category);
        if (type?.GetCustomAttributes(typeof(LogTagAttribute), false).FirstOrDefault() is LogTagAttribute tagged)
            return tagged.Tag;
        var name = category[(category.LastIndexOf('.') + 1)..];
        var plus = name.IndexOf('+');
        if (plus > 0) name = name[..plus];
        foreach (var suffix in new[] { "Service", "Endpoints", "Manager" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }

        return name.ToUpperInvariant();
    }

    private sealed class HostLogger(LogService log, string category, string tag) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Filtering by level happens in the logging builder (Program.ConfigureLogging).
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            var marker = logLevel switch
            {
                LogLevel.Warning => "WARN ",
                LogLevel.Error or LogLevel.Critical => "ERROR ",
                _ => ""
            };
            var line = $"[{tag}] {marker}{message}";
            if (exception != null && !message.Contains(exception.Message, StringComparison.Ordinal))
                line += $": {exception.GetType().Name}: {exception.Message}";
            log.Write(logLevel, category.StartsWith("verpixeld", StringComparison.Ordinal) ? tag : category, line);
        }
    }
}
