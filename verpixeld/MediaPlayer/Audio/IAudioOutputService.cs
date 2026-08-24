namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     Interface for audio output management via PulseAudio.
///     Bluetooth management is handled separately by <see cref="BluetoothAudioService"/>.
/// </summary>
public interface IAudioOutputService
{
    /// <summary>Current default sink name.</summary>
    string CurrentSinkName { get; }

    /// <summary>Check if PulseAudio is available.</summary>
    bool IsPulseAudioAvailable();

    /// <summary>
    ///     FFmpeg audio output args, e.g. <c>-f pulse default</c> or <c>-f alsa default</c>.
    ///     Empty when there is no usable device (typical in Docker) so video can still play.
    /// </summary>
    string GetFFmpegAudioOutput();

    /// <summary>Get list of available audio output sinks.</summary>
    Task<List<AudioOutputService.AudioSink>> GetAudioSinksAsync();

    /// <summary>Get the current default audio sink.</summary>
    Task<string> GetDefaultSinkAsync();

    /// <summary>Set the default audio output sink.</summary>
    Task<bool> SetDefaultSinkAsync(string sinkName);

    /// <summary>Set volume for the default sink (0-150).</summary>
    Task<bool> SetVolumeAsync(int volumePercent);

    /// <summary>Toggle mute for the default sink.</summary>
    Task<bool> ToggleMuteAsync();
}
