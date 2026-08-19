using System.Diagnostics;
using Microsoft.CognitiveServices.Speech;

namespace verpixeld.Services;

/// <summary>
///     Azure Text-to-Speech service with audio ducking.
///     Streams synthesised audio to PulseAudio via paplay,
///     optionally ducking background music during speech.
/// </summary>
public class TextToSpeechService
{
    // ── Configuration (kept in sync by VoiceCommandService) ──
    public string? SpeechKey { get; set; }
    public string? SpeechRegion { get; set; }
    public string TtsVoiceName { get; set; } = "de-DE-ConradNeural";
    public bool TtsEnabled { get; set; } = true;
    public int TtsDuckVolumePercent { get; set; } = 15;
    public bool TtsDuckingEnabled { get; set; } = true;

    /// <summary>
    ///     Speak text via Azure TTS streamed to paplay.
    ///     Ducks background audio if enabled.
    /// </summary>
    public async Task SpeakAsync(string text)
    {
        if (!TtsEnabled || string.IsNullOrEmpty(text)) return;

        if (string.IsNullOrEmpty(SpeechKey) || string.IsNullOrEmpty(SpeechRegion))
        {
            Console.WriteLine("[VOICE/TTS] Speech not configured, skipping TTS");
            return;
        }

        Process? paplay = null;
        List<(int Index, int Volume)>? duckedStreams = null;
        try
        {
            var speechConfig = SpeechConfig.FromSubscription(SpeechKey, SpeechRegion);
            speechConfig.SpeechSynthesisVoiceName = TtsVoiceName;
            speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);

            Console.WriteLine($"[VOICE/TTS] Speaking (streaming): \"{text}\" (voice={TtsVoiceName})");

            // Duck other audio streams BEFORE starting paplay (so TTS isn't affected)
            if (TtsDuckingEnabled)
                duckedStreams = await DuckAudioAsync();

            // Start paplay BEFORE synthesis so we can stream chunks as they arrive
            var psi = new ProcessStartInfo("paplay")
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--raw");
            psi.ArgumentList.Add("--format=s16le");
            psi.ArgumentList.Add("--rate=16000");
            psi.ArgumentList.Add("--channels=1");

            paplay = Process.Start(psi);
            if (paplay == null)
            {
                Console.WriteLine("[VOICE/TTS] Failed to start paplay");
                return;
            }

            // Log stderr asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = await paplay.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(stderr) && !stderr.Contains("Permission denied"))
                        Console.WriteLine($"[VOICE/TTS] paplay stderr: {stderr}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VOICE/TTS] paplay stderr read error: {ex.Message}");
                }
            });

            var paplayStream = paplay.StandardInput.BaseStream;

            using var synthesizer = new SpeechSynthesizer(speechConfig, null);

            synthesizer.Synthesizing += (_, e) =>
            {
                if (e.Result.AudioData.Length > 0)
                {
                    try { paplayStream.Write(e.Result.AudioData, 0, e.Result.AudioData.Length); }
                    catch (Exception ex) { Console.WriteLine($"[VOICE/TTS] paplay write error: {ex.Message}"); }
                }
            };

            var result = await synthesizer.SpeakTextAsync(text);

            try { paplayStream.Close(); } catch (Exception ex) { Console.WriteLine($"[VOICE/TTS] paplay stdin close error: {ex.Message}"); }

            if (result.Reason == ResultReason.Canceled)
            {
                var details = SpeechSynthesisCancellationDetails.FromResult(result);
                Console.WriteLine($"[VOICE/TTS] Canceled: {details.Reason} — {details.ErrorDetails}");
            }

            await paplay.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE/TTS] Error: {ex.Message}");
        }
        finally
        {
            try { paplay?.Kill(); } catch (Exception ex) { Console.WriteLine($"[VOICE/TTS] paplay kill error: {ex.Message}"); }
            paplay?.Dispose();

            if (duckedStreams != null && duckedStreams.Count > 0)
            {
                try { await RestoreAudioAsync(duckedStreams); }
                catch (Exception ex) { Console.WriteLine($"[VOICE/TTS] Audio restore error: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    ///     Fire-and-forget speak. Logs errors but doesn't propagate them.
    /// </summary>
    public void SpeakFireAndForget(string? text)
    {
        if (!TtsEnabled || string.IsNullOrEmpty(text)) return;
        _ = Task.Run(async () =>
        {
            try { await SpeakAsync(text); }
            catch (Exception ex) { Console.WriteLine($"[VOICE/TTS] Background TTS error: {ex.Message}"); }
        });
    }

    // ── Audio Ducking ──

    private async Task<List<(int Index, int Volume)>> DuckAudioAsync()
    {
        var originals = await PulseAudioHelper.GetSinkInputsAsync();
        if (originals.Count == 0) return originals;

        Console.WriteLine($"[VOICE/TTS] Ducking {originals.Count} audio stream(s) to {TtsDuckVolumePercent}%");
        foreach (var (index, _) in originals)
            await PulseAudioHelper.SetSinkInputVolumeAsync(index, TtsDuckVolumePercent);

        return originals;
    }

    private static async Task RestoreAudioAsync(List<(int Index, int Volume)> originals)
    {
        if (originals.Count == 0) return;

        Console.WriteLine($"[VOICE/TTS] Restoring {originals.Count} audio stream(s)");
        foreach (var (index, volume) in originals)
            await PulseAudioHelper.SetSinkInputVolumeAsync(index, volume);
    }
}
