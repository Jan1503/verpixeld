using System.Diagnostics;
using Microsoft.CognitiveServices.Speech.Audio;

namespace verpixeld.Services;

/// <summary>
///     Wraps a parec process to capture microphone audio from PulseAudio
///     and pipe it into a <see cref="PushAudioInputStream"/> for the Azure Speech SDK.
///     
///     Why parec instead of arecord or direct SDK mic access:
///     - arecord can't access ALSA devices when running as root with system-mode PA
///     - The Speech SDK's AudioConfig.FromMicrophoneInput() also fails (same PA issues)
///     - parec connects to PulseAudio successfully and outputs raw PCM to stdout
/// </summary>
[LogTag("VOICE")]
public class ParecAudioSession : IDisposable
{
    private readonly ILogger<ParecAudioSession> _log;

    public PushAudioInputStream PushStream { get; }
    public AudioConfig AudioConfig { get; }

    private Process? _parecProcess;
    private Task? _pumpTask;
    private volatile bool _disposed;

    /// <summary>
    ///     When false, audio from parec is read but NOT pushed into the stream.
    ///     This prevents stale audio from accumulating during non-listening periods
    ///     (e.g. during LLM classification, image generation, TTS playback).
    /// </summary>
    private volatile bool _pushing = true;

    /// <summary>True if the parec process is still running and capturing audio.</summary>
    public bool IsAlive => !_disposed && _parecProcess != null && !_parecProcess.HasExited;

    /// <summary>Pause pushing audio to the stream (discard incoming audio from parec).</summary>
    public void PausePush() => _pushing = false;

    /// <summary>Resume pushing audio to the stream (feed fresh audio to recognizer).</summary>
    public void ResumePush() => _pushing = true;

    public ParecAudioSession(string? paSourceName, ILogger<ParecAudioSession> log)
    {
        _log = log;
        // Speech SDK expects: 16kHz, 16-bit, mono PCM
        var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        PushStream = AudioInputStream.CreatePushStream(format);
        AudioConfig = Microsoft.CognitiveServices.Speech.Audio.AudioConfig.FromStreamInput(PushStream);

        var args = new List<string>
        {
            "--format=s16le",
            "--rate=16000",
            "--channels=1",
            "--raw",
            "--latency-msec=50"
        };

        if (!string.IsNullOrEmpty(paSourceName) && paSourceName != "default")
        {
            args.Insert(0, $"--device={paSourceName}");
            _log.LogInformation("Starting parec capture from PA source: {PaSourceName}", paSourceName);
        }
        else
        {
            _log.LogInformation("Starting parec capture from default source");
        }

        var psi = new ProcessStartInfo("parec")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        _log.LogInformation("Running: parec {Value}", string.Join(" ", args));

        _parecProcess = Process.Start(psi);
        if (_parecProcess == null)
            throw new InvalidOperationException("Failed to start parec");

        // Pump stdout (raw PCM bytes) into the PushAudioInputStream
        _pumpTask = Task.Run(async () =>
        {
            try
            {
                var buffer = new byte[3200]; // 100ms of 16kHz 16-bit mono
                var stream = _parecProcess.StandardOutput.BaseStream;
                while (!_disposed)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead <= 0) break;

                    if (_pushing)
                        PushStream.Write(buffer, bytesRead);
                }
            }
            catch (Exception ex) when (!_disposed)
            {
                _log.LogError("parec pump error: {Message}", ex.Message);
            }
            finally
            {
                PushStream.Close();
            }
        });

        // Log stderr in background
        Task.Run(async () =>
        {
            try
            {
                var stderr = await _parecProcess.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                    _log.LogInformation("parec stderr: {Trim}", stderr.Trim());
            }
            catch (Exception ex)
            {
                _log.LogError("parec stderr read error: {Message}", ex.Message);
            }
        });

        _log.LogInformation("parec started (PID: {Id})", _parecProcess.Id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_parecProcess != null && !_parecProcess.HasExited)
            {
                _parecProcess.Kill();
                _parecProcess.WaitForExit(2000);
                _log.LogInformation("parec stopped");
            }
            _parecProcess?.Dispose();
            _parecProcess = null;
        }
        catch (Exception ex)
        {
            _log.LogError("parec cleanup error: {Message}", ex.Message);
        }

        try { PushStream.Close(); } catch (Exception ex) { _log.LogError("PushStream close error: {Message}", ex.Message); }
        try { AudioConfig.Dispose(); } catch (Exception ex) { _log.LogError("AudioConfig dispose error: {Message}", ex.Message); }
    }
}
