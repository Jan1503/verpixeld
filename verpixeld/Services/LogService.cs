using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace verpixeld.Services;

/// <summary>
///     The host's log: a ring buffer the web page streams from, fed by <see cref="HostLoggerProvider"/>
///     (<c>ILogger</c>, with level and category) and, while code still uses it, by <c>Console.WriteLine</c>
///     (intercepted on <see cref="Console.Out"/>; the level is guessed from the text).
///     <para>
///         The real console (the Docker log) gets only what matters unless <see cref="ConsoleAll"/> is set:
///         the first minute after start, warnings and errors (the same line at most once a minute) and the
///         health line every five minutes. It is written by its own task through a bounded queue, so a
///         console that does not take output (a stalled Docker log driver) can never stop the host: lines
///         are dropped and counted instead. Before, every Console.WriteLine wrote to stdout under a lock,
///         and one blocked write froze every thread that logged, the web server included.
///     </para>
/// </summary>
public class LogService
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private long _sequence;

    // Numbering and queueing in one step: with threads racing, a line could be queued after a later
    // number, and a page that had already polled past that number never showed it.
    private readonly object _addLock = new();
    private const int MaxEntries = 1000;
    private TextWriter _console = Console.Out;

    private readonly Channel<string> _out = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropWrite
    });

    private long _dropped;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private DateTime _lastHealthUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _recent = new();

    private static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RepeatEvery = TimeSpan.FromMinutes(1);

    /// <summary>Every line on the console, as before (App:ConsoleLog = "all", or verbose logging).</summary>
    public bool ConsoleAll { get; set; }

    /// <summary>
    ///     Install the log interceptor on Console.Out
    /// </summary>
    public void Install()
    {
        _console = Console.Out;
        Console.SetOut(new InterceptingTextWriter(this));
        _ = Task.Factory.StartNew(PumpConsoleAsync, TaskCreationOptions.LongRunning).Unwrap();
        // The last lines before an exit (restart from the Studio, a fatal error) still reach the console.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DrainConsole();
        Console.WriteLine("[LOG] Console log capture installed");
    }

    private void DrainConsole()
    {
        try
        {
            while (_out.Reader.TryRead(out var line))
                _console.WriteLine(line);
            _console.Flush();
        }
        catch
        {
            // Console gone.
        }
    }

    /// <summary>A line written with <c>Console.WriteLine</c>: level guessed from the text.</summary>
    internal void AddLine(string line) => Add(GuessLevel(line), Category(line), line);

    /// <summary>A message from <see cref="HostLoggerProvider"/>: kept, and on the console if it matters.</summary>
    internal void Write(LogLevel level, string category, string line) => Add(level, category, line);

    private void Add(LogLevel level, string category, string line)
    {
        bool print;
        lock (_addLock)
        {
            var now = DateTime.UtcNow;
            _entries.Enqueue(new LogEntry
            {
                Sequence = ++_sequence,
                Timestamp = now,
                Level = LevelName(level),
                Category = category,
                Message = line
            });

            // Trim excess entries
            while (_entries.Count > MaxEntries)
                _entries.TryDequeue(out _);

            print = ShouldPrint(level, category, line, now);
        }

        if (print && !_out.Writer.TryWrite(line))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>Whether a line goes to the real console. Under <see cref="_addLock" />.</summary>
    private bool ShouldPrint(LogLevel level, string category, string line, DateTime now)
    {
        if (ConsoleAll || now - _startedUtc < StartupWindow)
            return true;
        if (category == "HEALTH")
        {
            if (now - _lastHealthUtc < HealthEvery) return false;
            _lastHealthUtc = now;
            return true;
        }
        if (level < LogLevel.Warning)
            return false;

        // A warning that repeats (a camera that is down, a missing tool) once a minute, not every time.
        var key = RepeatKey(line);
        if (_recent.TryGetValue(key, out var last) && now - last < RepeatEvery)
            return false;
        if (_recent.Count > 500)
            _recent.Clear();
        _recent[key] = now;
        return true;
    }

    /// <summary>The line with its numbers blanked, so counters and times do not make every repeat new.</summary>
    internal static string RepeatKey(string line)
    {
        var sb = new StringBuilder(Math.Min(line.Length, 200));
        foreach (var c in line)
        {
            if (sb.Length >= 200) break;
            if (!char.IsAsciiDigit(c))
                sb.Append(c);
            else if (sb.Length == 0 || sb[^1] != '#')
                sb.Append('#');
        }
        return sb.ToString();
    }

    private async Task PumpConsoleAsync()
    {
        var reader = _out.Reader;
        while (await reader.WaitToReadAsync())
        {
            try
            {
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    _console.WriteLine($"[LOG] WARN {dropped} console line(s) dropped: the console did not take output in time");
                while (reader.TryRead(out var line))
                    _console.WriteLine(line);
                _console.Flush();
            }
            catch
            {
                // The console is gone (closed pipe): keep draining so the queue never fills.
            }
        }
    }

    /// <summary>
    ///     Get all entries after a given sequence number
    /// </summary>
    public (IReadOnlyList<LogEntry> Entries, long LatestSequence) GetEntriesSince(long afterSequence)
    {
        var result = _entries.Where(e => e.Sequence > afterSequence).ToList();
        return (result, result.Count > 0 ? result[^1].Sequence : Interlocked.Read(ref _sequence));
    }

    /// <summary>
    ///     Get the latest N entries
    /// </summary>
    public (IReadOnlyList<LogEntry> Entries, long LatestSequence) GetLatest(int count = 200)
    {
        var all = _entries.ToArray();
        var result = all.Length <= count ? all.ToList() : all[^count..].ToList();
        return (result, result.Count > 0 ? result[^1].Sequence : Interlocked.Read(ref _sequence));
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
    ///     Console.Out while the host runs: collects lines into the <see cref="LogService" />, which decides
    ///     what reaches the real console. Nothing here waits on stdout.
    /// </summary>
    private class InterceptingTextWriter : TextWriter
    {
        private readonly LogService _logService;
        private readonly StringBuilder _lineBuffer = new();

        // Many extensions run on their own threads and all call Console.WriteLine; the shared line buffer
        // must be synchronised or concurrent writes corrupt it (which previously broke capture entirely).
        private readonly object _sync = new();

        public InterceptingTextWriter(LogService logService)
        {
            _logService = logService;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_sync)
                Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            lock (_sync)
                foreach (var c in value)
                    Append(c);
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                var line = value ?? "";
                if (_lineBuffer.Length > 0)
                {
                    line = _lineBuffer + line;
                    _lineBuffer.Clear();
                }

                line = line.TrimEnd('\r');
                if (line.Length > 0)
                    _logService.AddLine(line);
            }
        }

        private void Append(char c)
        {
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
