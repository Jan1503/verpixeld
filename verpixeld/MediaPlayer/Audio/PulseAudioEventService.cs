using System.Diagnostics;
using System.Text.RegularExpressions;
using verpixeld.Services;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     Background service that monitors PulseAudio events using 'pactl subscribe'
///     and broadcasts volume/sink changes to connected clients via SSE.
/// </summary>
public class PulseAudioEventService : BackgroundService
{
    private static DateTime _lastSubscribeStartLog = DateTime.MinValue;
    private static int _subscribeRestartCount;
    private readonly object _clientLock = new();
    private readonly List<StreamWriter> _sseClients = new();
    private readonly IAudioOutputService _audioOutputService;
    private Process? _subscribeProcess;

    // Event types we care about
    public event Action<string, string>? OnSinkChanged; // (sinkName, changeType)

    public PulseAudioEventService(IAudioOutputService audioOutputService)
    {
        _audioOutputService = audioOutputService;
    }

    /// <summary>
    ///     Register an SSE client to receive events
    /// </summary>
    public void RegisterClient(StreamWriter writer)
    {
        lock (_clientLock)
        {
            _sseClients.Add(writer);
        }
    }

    /// <summary>
    ///     Unregister an SSE client
    /// </summary>
    public void UnregisterClient(StreamWriter writer)
    {
        lock (_clientLock)
        {
            _sseClients.Remove(writer);
        }
    }

    /// <summary>
    ///     Broadcast an event to all connected SSE clients
    /// </summary>
    private async Task BroadcastEventAsync(string eventType, string data)
    {
        List<StreamWriter> deadClients = new();

        lock (_clientLock)
        {
            foreach (var client in _sseClients)
                try
                {
                    // SSE format: "event: type\ndata: json\n\n"
                    client.Write($"event: {eventType}\ndata: {data}\n\n");
                    client.Flush();
                }
                catch
                {
                    deadClients.Add(client);
                }

            // Remove dead clients
            if (deadClients.Count > 0)
            {
                foreach (var dead in deadClients) _sseClients.Remove(dead);
                Console.WriteLine($"[PULSE-SSE] Removed {deadClients.Count} dead client(s)");
            }
        }

        await Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if PulseAudio is available
        if (!_audioOutputService.IsPulseAudioAvailable())
        {
            Console.WriteLine("[PULSE-SSE] PulseAudio not available, event service disabled");
            return;
        }

        Console.WriteLine("[PULSE-SSE] Starting PulseAudio event monitor...");

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                await RunSubscribeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PULSE-SSE] Error: {ex.Message}, restarting in 5s...");
                await Task.Delay(5000, stoppingToken);
            }

        Console.WriteLine("[PULSE-SSE] Event monitor stopped");
    }

    private async Task RunSubscribeAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pactl", "subscribe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // For system-mode PulseAudio, set PULSE_SERVER
        PulseAudioHelper.ApplyPulseEnv(psi);

        _subscribeProcess = Process.Start(psi);
        if (_subscribeProcess == null)
        {
            Console.WriteLine("[PULSE-SSE] Failed to start pactl subscribe");
            // Add delay to prevent rapid retry loop
            await Task.Delay(5000, ct);
            return;
        }

        // Rate-limit logging to prevent spam
        var now = DateTime.Now;
        _subscribeRestartCount++;
        if ((now - _lastSubscribeStartLog).TotalSeconds >= 30 || _subscribeRestartCount == 1)
        {
            Console.WriteLine(
                $"[PULSE-SSE] pactl subscribe started (PID: {_subscribeProcess.Id}){(_subscribeRestartCount > 1 ? $", restarts: {_subscribeRestartCount}" : "")}");
            _lastSubscribeStartLog = now;
        }

        // Read events from pactl subscribe output
        // Format varies: "Event 'change' on sink #0" or "Event 'change' on sink-input #123"
        // We want to catch any sink-related changes
        var sinkEventRegex =
            new Regex(@"Event '(\w+)' on (sink|card)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        try
        {
            while (!ct.IsCancellationRequested && !_subscribeProcess.HasExited)
            {
                // Use ReadLineAsync without cancellation token for broader compatibility
                var readTask = _subscribeProcess.StandardOutput.ReadLineAsync();
                var completedTask = await Task.WhenAny(readTask, Task.Delay(-1, ct));

                if (completedTask != readTask)
                    // Cancellation requested
                    break;

                var line = await readTask;
                if (line == null) break;

                // Parse sink/card change events (volume changes appear as sink or card events)
                var match = sinkEventRegex.Match(line);
                if (match.Success)
                {
                    var changeType = match.Groups[1].Value; // "change", "new", "remove"
                    var objectType = match.Groups[2].Value; // "sink" or "card"

                    // Broadcast change events (volume, mute, profile changes)
                    if (changeType == "change")
                    {
                        // Only log if we have clients (reduce noise)
                        var hasClients = false;
                        lock (_clientLock)
                        {
                            hasClients = _sseClients.Count > 0;
                        }

                        if (hasClients)
                            // Broadcast to SSE clients
                            await BroadcastEventAsync("sink-change",
                                $"{{\"type\":\"{changeType}\",\"object\":\"{objectType}\"}}");

                        // Fire event for internal use
                        OnSinkChanged?.Invoke(objectType, changeType);
                    }
                }
            }
        }
        finally
        {
            if (_subscribeProcess != null && !_subscribeProcess.HasExited)
                try
                {
                    _subscribeProcess.Kill();
                }
                catch
                {
                }

            _subscribeProcess?.Dispose();
            _subscribeProcess = null;

            // Add delay before restarting to prevent rapid loop when PulseAudio is restarting
            if (!ct.IsCancellationRequested) await Task.Delay(2000, ct);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribeProcess != null && !_subscribeProcess.HasExited)
            try
            {
                _subscribeProcess.Kill();
            }
            catch
            {
            }

        await base.StopAsync(cancellationToken);
    }
}
