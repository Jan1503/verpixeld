using System.Collections.Concurrent;
using System.Text;

namespace verpixeld.Services;

/// <summary>
///     The host's log: a ring buffer the web page streams from, fed by <see cref="HostLoggerProvider"/>
///     (<c>ILogger</c>, with level and category) and, while code still uses it, by <c>Console.WriteLine</c>
///     (intercepted on <see cref="Console.Out"/>; the level is guessed from the text).
/// </summary>
public class LogService
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private long _sequence;
    private const int MaxEntries = 500;
    private TextWriter _console = Console.Out;
    private readonly object _consoleLock = new();

    /// <summary>
    ///     Install the log interceptor on Console.Out
    /// </summary>
    public void Install()
    {
        _console = Console.Out;
        Console.SetOut(new InterceptingTextWriter(_console, this));
        Console.WriteLine("[LOG] Console log capture installed");
    }

    /// <summary>A line written with <c>Console.WriteLine</c>: level guessed from the text.</summary>
    internal void AddLine(string line) => Add(GuessLevel(line), Category(line), line);

    /// <summary>A message from <see cref="HostLoggerProvider"/>: written to the real console and kept.</summary>
    internal void Write(LogLevel level, string category, string line)
    {
        lock (_consoleLock)
        {
            _console.WriteLine(line);
            _console.Flush();
        }
        Add(level, category, line);
    }

    private void Add(LogLevel level, string category, string line)
    {
        var seq = Interlocked.Increment(ref _sequence);
        _entries.Enqueue(new LogEntry
        {
            Sequence = seq,
            Timestamp = DateTime.UtcNow,
            Level = LevelName(level),
            Category = category,
            Message = line
        });

        // Trim excess entries
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
    }

    /// <summary>
    ///     Get all entries after a given sequence number
    /// </summary>
    public (IReadOnlyList<LogEntry> Entries, long LatestSequence) GetEntriesSince(long afterSequence)
    {
        var result = _entries.Where(e => e.Sequence > afterSequence).ToList();
        return (result, _sequence);
    }

    /// <summary>
    ///     Get the latest N entries
    /// </summary>
    public (IReadOnlyList<LogEntry> Entries, long LatestSequence) GetLatest(int count = 200)
    {
        var all = _entries.ToArray();
        var result = all.Length <= count ? all.ToList() : all[^count..].ToList();
        return (result, _sequence);
    }

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Warning => "warn",
        LogLevel.Error or LogLevel.Critical => "error",
        _ => "info"
    };

    /// <summary>For console lines that carry no level: errors and warnings by their usual wording.</summary>
    internal static LogLevel GuessLevel(string line)
    {
        if (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("Exception", StringComparison.Ordinal) ||
            line.Contains(" error", StringComparison.OrdinalIgnoreCase) || line.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;
        if (line.Contains("WARN", StringComparison.Ordinal) || line.Contains("Warning", StringComparison.Ordinal) ||
            line.StartsWith("[PERF]", StringComparison.Ordinal))
            return LogLevel.Warning;
        return LogLevel.Information;
    }

    /// <summary>The "[TAG]" a console line starts with, without brackets; empty if none.</summary>
    internal static string Category(string line)
    {
        var s = line.TrimStart();
        if (s.Length < 3 || s[0] != '[') return "";
        var end = s.IndexOf(']');
        return end > 1 && end < 32 ? s[1..end] : "";
    }

    /// <summary>
    ///     A TextWriter that passes through to the original Console.Out
    ///     while also capturing lines into the LogService
    /// </summary>
    private class InterceptingTextWriter : TextWriter
    {
        private readonly TextWriter _original;
        private readonly LogService _logService;
        private readonly StringBuilder _lineBuffer = new();

        // Many extensions run on their own threads and all call Console.WriteLine; the shared line buffer
        // must be synchronised or concurrent writes corrupt it (which previously broke capture entirely).
        private readonly object _sync = new();

        public InterceptingTextWriter(TextWriter original, LogService logService)
        {
            _original = original;
            _logService = logService;
        }

        public override Encoding Encoding => _original.Encoding;

        public override void Write(char value)
        {
            lock (_sync)
            {
                _original.Write(value);

                if (value == '\n')
                {
                    var line = _lineBuffer.ToString().TrimEnd('\r');
                    if (line.Length > 0)
                        _logService.AddLine(line);
                    _lineBuffer.Clear();
                    _original.Flush();
                }
                else
                {
                    _lineBuffer.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            lock (_sync)
            {
                _original.Write(value);
                if (value == null) return;

                foreach (var c in value)
                    if (c == '\n')
                    {
                        var line = _lineBuffer.ToString().TrimEnd('\r');
                        if (line.Length > 0)
                            _logService.AddLine(line);
                        _lineBuffer.Clear();
                    }
                    else
                    {
                        _lineBuffer.Append(c);
                    }
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                _original.WriteLine(value);
                _original.Flush();
                var line = value ?? "";
                if (_lineBuffer.Length > 0)
                {
                    line = _lineBuffer + line;
                    _lineBuffer.Clear();
                }

                if (line.Length > 0)
                    _logService.AddLine(line);
            }
        }

        public override void Flush()
        {
            _original.Flush();
        }

        public override Task FlushAsync()
        {
            return _original.FlushAsync();
        }
    }
}

public class LogEntry
{
    public long Sequence { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>debug, info, warn or error.</summary>
    public string Level { get; set; } = "info";

    /// <summary>Short source tag (ALERT, MEDIA, Microsoft.AspNetCore…), empty when unknown.</summary>
    public string Category { get; set; } = "";

    public string Message { get; set; } = "";
}
