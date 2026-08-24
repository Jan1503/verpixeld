using System.Collections.Concurrent;
using System.Text;

namespace verpixeld.Services;

/// <summary>
///     Captures console output into a ring buffer for streaming to the GUI.
///     Wraps Console.Out so all Console.WriteLine calls are intercepted.
/// </summary>
public class LogService
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private long _sequence;
    private const int MaxEntries = 500;

    /// <summary>
    ///     Install the log interceptor on Console.Out
    /// </summary>
    public void Install()
    {
        var original = Console.Out;
        Console.SetOut(new InterceptingTextWriter(original, this));
        Console.WriteLine("[LOG] Console log capture installed");
    }

    internal void AddLine(string line)
    {
        var seq = Interlocked.Increment(ref _sequence);
        _entries.Enqueue(new LogEntry
        {
            Sequence = seq,
            Timestamp = DateTime.UtcNow,
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
    public string Message { get; set; } = "";
}
