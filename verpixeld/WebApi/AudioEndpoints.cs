using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.MediaPlayer.Audio;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for audio output management and Bluetooth speaker control
/// </summary>
public static class AudioEndpoints
{
    /// <summary>Set when the endpoints are mapped; handlers in helper methods log through it too.</summary>
    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void MapAudioEndpoints(this WebApplication app)
    {
        Log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AudioEndpoints));
        var audioService = app.Services.GetRequiredService<IAudioOutputService>();
        var bluetoothService = app.Services.GetRequiredService<BluetoothAudioService>();
        var vap1 = app.Services.GetRequiredService<PixToneOutputService>();

        List<AudioOutputService.AudioSink> WithBoards(List<AudioOutputService.AudioSink> sinks)
        {
            if (vap1.IsSelected)
                foreach (var sink in sinks) sink.IsDefault = false;
            var boards = vap1.Seen.ToList();
            if (!string.IsNullOrWhiteSpace(vap1.Host) && boards.All(b => b.Host != vap1.Host))
                boards.Insert(0, new PixTone.AudioAnnouncement { Host = vap1.Host, Name = vap1.Host, Volume = vap1.Volume, Muted = vap1.Muted });
            foreach (var board in boards)
            {
                sinks.Insert(0, new AudioOutputService.AudioSink
                {
                    Name = "pixtone:" + board.Host,
                    Description = string.IsNullOrWhiteSpace(board.Name) ? board.Host : $"{board.Name} ({board.Host})",
                    Type = AudioOutputService.AudioOutputType.Network,
                    IsDefault = vap1.IsSelected && board.Host == vap1.Host,
                    Volume = vap1.Volume,
                    IsMuted = vap1.Muted
                });
            }
            return sinks;
        }

        var group = app.MapGroup("/api/audio");

        // ========================================================================
        // AUDIO OUTPUT MANAGEMENT
        // ========================================================================

        group.MapGet("/status", async () =>
        {
            try
            {
                var btAdapterPresent = bluetoothService.IsAdapterPresent();
                var btPoweredOn = btAdapterPresent && bluetoothService.IsPoweredOn();

                var status = new
                {
                    pulseAudioAvailable = audioService.IsPulseAudioAvailable(),
                    bluetoothAdapterPresent = btAdapterPresent,
                    bluetoothPoweredOn = btPoweredOn,
                    bluetoothAvailable = btPoweredOn,
                    defaultSink = audioService.IsPulseAudioAvailable() ? await audioService.GetDefaultSinkAsync() : "",
                    availableSinks = WithBoards(audioService.IsPulseAudioAvailable()
                        ? await audioService.GetAudioSinksAsync()
                        : new List<AudioOutputService.AudioSink>()),
                    pairedDevices = btPoweredOn
                        ? await bluetoothService.GetPairedDevicesAsync()
                        : new List<BluetoothAudioService.BluetoothDevice>(),
                    wallPreview = vap1.WallPreview,
                    previewFps = vap1.PreviewFps,
                    previewWidth = vap1.PreviewWidth,
                    previewFill = vap1.PreviewFill
                };

                return Results.Json(new { success = true, data = status }, _jsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, _jsonOptions);
            }
        });

        group.MapGet("/outputs", async () =>
        {
            try
            {
                var sinks = WithBoards(audioService.IsPulseAudioAvailable()
                    ? await audioService.GetAudioSinksAsync()
                    : new List<AudioOutputService.AudioSink>());
                return Results.Json(new { success = true, data = sinks }, _jsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, _jsonOptions);
            }
        });

        group.MapGet("/output", async () =>
        {
            try
            {
                var defaultSink = await audioService.GetDefaultSinkAsync();
                var sinks = await audioService.GetAudioSinksAsync();
                var currentSink = sinks.FirstOrDefault(s => s.Name == defaultSink);

                return Results.Json(new
                {
                    success = true,
                    data = new { sinkName = defaultSink, sink = currentSink }
                }, _jsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, _jsonOptions);
            }
        });

        group.MapPost("/output", async ([FromQuery] string sinkName) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sinkName))
                    return Results.Json(new { success = false, error = "Sink name required" });

                if (sinkName.StartsWith("pixtone:", StringComparison.OrdinalIgnoreCase) ||
                    sinkName.StartsWith("vap1:", StringComparison.OrdinalIgnoreCase))
                {
                    var host = sinkName[(sinkName.IndexOf(':') + 1)..];
                    vap1.Select(host);
                    return Results.Json(new { success = true, message = $"Audio output set to {host}" });
                }

                vap1.Deselect();
                var success = await audioService.SetDefaultSinkAsync(sinkName);

                if (success)
                    return Results.Json(new { success = true, message = $"Audio output set to: {sinkName}" });

                return Results.Json(new { success = false, error = "Failed to set audio output" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        group.MapPost("/pixtone/preview", ([FromQuery] bool on) =>
        {
            vap1.SetWallPreview(on);
            return Results.Json(new { success = true, on });
        });

        group.MapPost("/pixtone/preview-fps", ([FromQuery] int fps) =>
        {
            vap1.SetPreviewFps(fps);
            return Results.Json(new { success = true, fps = vap1.PreviewFps });
        });

        group.MapPost("/pixtone/preview-width", ([FromQuery] int width) =>
        {
            vap1.SetPreviewWidth(width);
            return Results.Json(new { success = true, width = vap1.PreviewWidth });
        });

        group.MapPost("/pixtone/preview-fill", ([FromQuery] bool on) =>
        {
            vap1.SetPreviewFill(on);
            return Results.Json(new { success = true, on });
        });

        async Task<IResult> ScanBoards(CancellationToken ct)
        {
            try
            {
                var boards = await vap1.ScanAsync(ct);
                return Results.Json(new { success = true, count = boards.Count, data = boards }, _jsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        group.MapPost("/pixtone/scan", ScanBoards);
        group.MapPost("/vap1/scan", ScanBoards);

        group.MapPost("/volume", async ([FromQuery] int volume) =>
        {
            try
            {
                if (vap1.IsSelected)
                {
                    vap1.SetVolume(Math.Clamp(volume, 0, 100));
                    return Results.Json(new { success = true, volume = vap1.Volume }, _jsonOptions);
                }
                var clampedVolume = Math.Clamp(volume, 0, 150);
                var success = await audioService.SetVolumeAsync(clampedVolume);
                Log.LogInformation("Volume API: set to {ClampedVolume}%, success={Success}", clampedVolume, success);
                return Results.Json(new { success, volume = clampedVolume }, _jsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, _jsonOptions);
            }
        });

        group.MapPost("/mute/toggle", async () =>
        {
            try
            {
                if (vap1.IsSelected)
                {
                    vap1.ToggleMute();
                    return Results.Json(new { success = true, muted = vap1.Muted });
                }
                var success = await audioService.ToggleMuteAsync();
                return Results.Json(new { success });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // ========================================================================
        // BLUETOOTH MANAGEMENT
        // ========================================================================

        group.MapPost("/bluetooth/power", async ([FromQuery] bool on = true) =>
        {
            try
            {
                if (!bluetoothService.IsAdapterPresent())
                    return Results.Json(new
                    {
                        success = false,
                        error = "No Bluetooth adapter found. Check if bluetooth service is installed.",
                        hint = "Try: sudo apt install bluez pulseaudio-module-bluetooth"
                    });

                bool success;
                if (on)
                    success = await bluetoothService.PowerOnAsync();
                else
                    success = await bluetoothService.PowerOffAsync();

                var poweredOn = bluetoothService.IsPoweredOn();

                if (on && !poweredOn)
                    return Results.Json(new
                    {
                        success = false,
                        poweredOn = false,
                        error = "Could not power on Bluetooth",
                        hint = "Try running on the Pi: sudo rfkill unblock bluetooth && sudo systemctl restart bluetooth"
                    });

                return Results.Json(new
                {
                    success = poweredOn == on,
                    poweredOn,
                    message = poweredOn ? "Bluetooth powered on" : "Bluetooth powered off"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        group.MapGet("/bluetooth/devices", async () =>
        {
            try
            {
                if (!bluetoothService.IsAvailable())
                    return Results.Json(new
                    {
                        success = false,
                        error = "Bluetooth not available or powered off",
                        available = false,
                        adapterPresent = bluetoothService.IsAdapterPresent(),
                        poweredOn = bluetoothService.IsPoweredOn()
                    });

                var devices = await bluetoothService.GetPairedDevicesAsync();
                return Results.Json(new { success = true, available = true, data = devices });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        group.MapGet("/bluetooth/discovered", async () =>
        {
            try
            {
                var devices = await bluetoothService.GetDiscoveredDevicesAsync();
                return Results.Json(new { success = true, data = devices });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        group.MapPost("/bluetooth/scan", async ([FromQuery] int duration = 10) =>
        {
            try
            {
                if (!bluetoothService.IsAvailable())
                {
                    await bluetoothService.PowerOnAsync();
                    await Task.Delay(1000);

                    if (!bluetoothService.IsAvailable())
                        return Results.Json(new
                        {
                            success = false,
                            error = "Bluetooth not available or could not be powered on"
                        });
                }

                duration = Math.Clamp(duration, 5, 30);
                var discovered = await bluetoothService.StartScanAsync(duration);

                return Results.Json(new
                {
                    success = true,
                    message = $"Scan complete ({duration}s) - found {discovered.Count} devices",
                    count = discovered.Count,
                    data = discovered
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        group.MapPost("/bluetooth/pair/{address}", async (string address) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(address))
                    return Results.Json(new { success = false, error = "Device address required" });

                var (success, message) = await bluetoothService.PairDeviceAsync(address);

                return Results.Json(new
                {
                    success,
                    message = success ? message : null,
                    error = success ? null : message
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        group.MapPost("/bluetooth/connect/{address}", async (string address) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(address))
                    return Results.Json(new { success = false, error = "Device address required" });

                var devices = await bluetoothService.GetPairedDevicesAsync();
                var device = devices.FirstOrDefault(d =>
                    d.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
                var deviceName = device?.Name ?? address;

                var success = await bluetoothService.ConnectDeviceAsync(address);

                var audioSinkDetected = false;
                if (success)
                {
                    await Task.Delay(1000);
                    var sinks = await audioService.GetAudioSinksAsync();
                    audioSinkDetected = sinks.Any(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                }

                if (success && !audioSinkDetected)
                    return Results.Json(new
                    {
                        success = true,
                        audioSinkDetected = false,
                        showManualConnect = true,
                        deviceName,
                        message =
                            $"Connected to {deviceName}, but audio profile not detected. Try manual connection."
                    });

                return Results.Json(new
                {
                    success,
                    audioSinkDetected,
                    showManualConnect = !success,
                    deviceName,
                    message = success ? $"Connected to {deviceName}" : "Connection failed"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, showManualConnect = true, error = ex.Message });
            }
        });

        group.MapPost("/bluetooth/disconnect/{address}", async (string address) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(address))
                    return Results.Json(new { success = false, error = "Device address required" });

                var success = await bluetoothService.DisconnectDeviceAsync(address);

                return Results.Json(new
                {
                    success,
                    message = success ? $"Disconnected from {address}" : "Disconnect failed"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        group.MapDelete("/bluetooth/device/{address}", async (string address) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(address))
                    return Results.Json(new { success = false, error = "Device address required" });

                var success = await bluetoothService.RemoveDeviceAsync(address);

                return Results.Json(new
                {
                    success,
                    message = success ? $"Removed {address}" : "Remove failed"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // ========================================================================
        // SERVER-SENT EVENTS (SSE)
        // ========================================================================

        group.MapGet("/events",
            async (HttpContext context, PulseAudioEventService eventService, CancellationToken ct) =>
            {
                var syncIOFeature = context.Features.Get<IHttpBodyControlFeature>();
                if (syncIOFeature != null) syncIOFeature.AllowSynchronousIO = true;

                var bufferingFeature = context.Features.Get<IHttpResponseBodyFeature>();
                bufferingFeature?.DisableBuffering();

                context.Response.ContentType = "text/event-stream";
                context.Response.Headers["Cache-Control"] = "no-cache, no-store";
                context.Response.Headers["Connection"] = "keep-alive";
                context.Response.Headers["X-Accel-Buffering"] = "no";

                var writer = new StreamWriter(context.Response.Body) { AutoFlush = true };

                await writer.WriteLineAsync("event: connected");
                await writer.WriteLineAsync("data: {\"status\":\"connected\"}");
                await writer.WriteLineAsync();
                await writer.FlushAsync();

                eventService.RegisterClient(writer);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(30000, ct);
                        await writer.WriteLineAsync(": heartbeat");
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();
                    }
                }
                catch (OperationCanceledException) { }
                catch { /* best effort, nothing to recover */ }
                finally
                {
                    eventService.UnregisterClient(writer);
                }
            });
    }
}
