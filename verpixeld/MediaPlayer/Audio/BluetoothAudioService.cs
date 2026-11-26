using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     Service for managing Bluetooth audio devices.
///     Uses BlueZ (bluetoothctl / D-Bus) for discovery, pairing, and connection,
///     and delegates PulseAudio sink management to <see cref="AudioOutputService"/>.
/// </summary>
[LogTag("BLUETOOTH")]
public class BluetoothAudioService
{
    private readonly ILogger<BluetoothAudioService> _log;

    private readonly AudioOutputService _audio;

    // Regex to strip ANSI escape codes from terminal output
    private static readonly Regex AnsiEscapeRegex =
        new(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07", RegexOptions.Compiled);

    // Store discovered devices from last scan
    private List<BluetoothDevice> _lastDiscoveredDevices = new();

    public BluetoothAudioService(AudioOutputService audioOutputService, ILogger<BluetoothAudioService> log)
    {
        _log = log;
        _audio = audioOutputService;
    }

    // ========================================================================
    // ADAPTER STATUS
    // ========================================================================

    /// <summary>Check if Bluetooth adapter exists on the system.</summary>
    public bool IsAdapterPresent()
    {
        try
        {
            var psi = new ProcessStartInfo("bluetoothctl", "show")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            return proc.ExitCode == 0 && output.Contains("Controller");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Check if Bluetooth adapter is powered on.</summary>
    public bool IsPoweredOn()
    {
        try
        {
            var psi = new ProcessStartInfo("bluetoothctl", "show")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            return proc.ExitCode == 0 && output.Contains("Powered: yes");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Check if Bluetooth is available and powered on.</summary>
    public bool IsAvailable() => IsAdapterPresent() && IsPoweredOn();

    // ========================================================================
    // POWER ON / OFF
    // ========================================================================

    /// <summary>
    ///     Power on the Bluetooth adapter.
    ///     Handles rfkill, systemd service, and PulseAudio module loading.
    /// </summary>
    public async Task<bool> PowerOnAsync()
    {
        _log.LogInformation("Powering on Bluetooth adapter...");

        // Step 1: Check rfkill
        try
        {
            _log.LogInformation("Checking rfkill status...");
            var rfkillCheck = await RunCommandAsync("rfkill", "list bluetooth");
            _log.LogInformation("rfkill status: {RfkillCheck}", rfkillCheck);

            if (rfkillCheck.Contains("Soft blocked: yes") || rfkillCheck.Contains("Hard blocked: yes"))
            {
                _log.LogInformation("Bluetooth is blocked by rfkill, unblocking...");
                var unblockResult = await RunCommandAsync("rfkill", "unblock bluetooth");
                _log.LogInformation("rfkill unblock result: {UnblockResult}", unblockResult);
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("rfkill check failed: {Message}", ex.Message);
        }

        // Step 2: Ensure bluetooth service is running
        try
        {
            _log.LogInformation("Checking bluetooth service...");
            var serviceStatus = await RunCommandAsync("systemctl", "is-active bluetooth");
            _log.LogInformation("Service status: {Trim}", serviceStatus.Trim());

            if (!serviceStatus.Contains("active"))
            {
                _log.LogInformation("Starting bluetooth service...");
                await RunCommandAsync("sudo", "systemctl start bluetooth");
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("Service check failed: {Message}", ex.Message);
        }

        // Step 3: Power on via bluetoothctl
        _log.LogInformation("Sending power on command...");
        var success = await RunBluetoothCtlCommandAsync("power on");

        // Wait and verify
        await Task.Delay(1000);
        var actuallyPoweredOn = IsPoweredOn();

        if (actuallyPoweredOn)
        {
            _log.LogInformation("Bluetooth adapter powered on successfully");
            await EnsurePulseAudioBluetoothModuleAsync();
            return true;
        }

        _log.LogError("Failed to power on Bluetooth adapter");
        _log.LogInformation("Try running manually: sudo rfkill unblock bluetooth && sudo systemctl restart bluetooth");
        return false;
    }

    /// <summary>Power off the Bluetooth adapter.</summary>
    public async Task<bool> PowerOffAsync()
    {
        _log.LogInformation("Powering off Bluetooth adapter...");
        return await RunBluetoothCtlCommandAsync("power off");
    }

    // ========================================================================
    // DEVICE MANAGEMENT
    // ========================================================================

    /// <summary>List paired/known Bluetooth devices.</summary>
    public async Task<List<BluetoothDevice>> GetPairedDevicesAsync()
    {
        var devices = new List<BluetoothDevice>();

        try
        {
            var devicesOutput = await RunBluetoothCtlAsync("devices");

            foreach (var line in devicesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var device = ParseBluetoothDeviceLine(line);
                if (device != null) devices.Add(device);
            }

            foreach (var device in devices.ToList())
            {
                var info = await GetDeviceInfoAsync(device.Address);
                if (info != null)
                {
                    device.IsPaired = info.IsPaired;
                    device.IsConnected = info.IsConnected;
                    device.IsTrusted = info.IsTrusted;
                    device.Icon = info.Icon;
                    if (!string.IsNullOrEmpty(info.Name)) device.Name = info.Name;
                }
            }

            return devices.Where(d => d.IsPaired).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError("Error listing devices: {Message}", ex.Message);
        }

        return devices;
    }

    /// <summary>Get discovered (not yet paired) devices from last scan.</summary>
    public async Task<List<BluetoothDevice>> GetDiscoveredDevicesAsync()
    {
        if (_lastDiscoveredDevices.Count > 0) return _lastDiscoveredDevices;

        var devices = new List<BluetoothDevice>();
        try
        {
            var output = await RunBluetoothCtlAsync("devices");
            var paired = await GetPairedDevicesAsync();
            var pairedAddresses = paired.Select(d => d.Address).ToHashSet();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var device = ParseBluetoothDeviceLine(line);
                if (device != null && !pairedAddresses.Contains(device.Address)) devices.Add(device);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("Error getting discovered devices: {Message}", ex.Message);
        }

        return devices;
    }

    /// <summary>Start scanning for Bluetooth devices.</summary>
    public async Task<List<BluetoothDevice>> StartScanAsync(int durationSeconds = 10)
    {
        _log.LogInformation("Starting scan for {DurationSeconds} seconds...", durationSeconds);
        _lastDiscoveredDevices.Clear();

        var discoveredDevices = new Dictionary<string, BluetoothDevice>();

        try
        {
            var psi = new ProcessStartInfo("bluetoothctl")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment = { ["TERM"] = "dumb" }
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _log.LogError("Failed to start bluetoothctl");
                return new List<BluetoothDevice>();
            }

            var outputTask = Task.Run(async () =>
            {
                try
                {
                    var reader = proc.StandardOutput;
                    while (!proc.HasExited)
                    {
                        var rawLine = await reader.ReadLineAsync();
                        if (rawLine == null) break;

                        var line = StripAnsiCodes(rawLine);
                        _log.LogInformation("{Line}", line);

                        var newMatch = Regex.Match(line, @"\[NEW\]\s+Device\s+([0-9A-F:]{17})\s+(.+)",
                            RegexOptions.IgnoreCase);
                        if (newMatch.Success)
                        {
                            var address = newMatch.Groups[1].Value;
                            var name = newMatch.Groups[2].Value.Trim();

                            if (!discoveredDevices.ContainsKey(address))
                            {
                                discoveredDevices[address] = new BluetoothDevice
                                {
                                    Address = address,
                                    Name = name,
                                    IsPaired = false
                                };
                                _log.LogInformation(">>> Discovered: {Name} ({Address})", name, address);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError("Output read error: {Message}", ex.Message);
                }
            });

            await proc.StandardInput.WriteLineAsync("scan on");
            await proc.StandardInput.FlushAsync();

            _log.LogInformation("Scanning...");
            await Task.Delay(durationSeconds * 1000);

            await proc.StandardInput.WriteLineAsync("scan off");
            await proc.StandardInput.WriteLineAsync("quit");
            await proc.StandardInput.FlushAsync();

            var exitTask = Task.Run(() => proc.WaitForExit(5000));
            await Task.WhenAny(exitTask, Task.Delay(6000));

            if (!proc.HasExited)
                try { proc.Kill(); } catch { /* best effort, nothing to recover */ }

            _log.LogInformation("Scan complete. Found {Count} new devices", discoveredDevices.Count);
        }
        catch (Exception ex)
        {
            _log.LogError("Scan error: {Message}", ex.Message);
        }

        var paired = await GetPairedDevicesAsync();
        var pairedAddresses = paired.Select(d => d.Address).ToHashSet();

        _lastDiscoveredDevices = discoveredDevices.Values
            .Where(d => !pairedAddresses.Contains(d.Address))
            .ToList();

        return _lastDiscoveredDevices;
    }

    /// <summary>Pair with a Bluetooth device (and automatically connect for audio devices).</summary>
    public async Task<(bool Success, string Message)> PairDeviceAsync(string address)
    {
        if (!IsDeviceAddress(address)) return (false, "Not a Bluetooth address");
        _log.LogInformation("Starting pairing process for {Address}...", address);

        try
        {
            var psi = new ProcessStartInfo("bluetoothctl")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment = { ["TERM"] = "dumb" }
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start bluetoothctl");

            var outputBuilder = new StringBuilder();
            var deviceConnected = false;

            var readTask = Task.Run(async () =>
            {
                try
                {
                    while (!proc.HasExited)
                    {
                        var line = await proc.StandardOutput.ReadLineAsync();
                        if (line == null) break;
                        var cleanLine = StripAnsiCodes(line);
                        _log.LogInformation("{CleanLine}", cleanLine);
                        outputBuilder.AppendLine(cleanLine);

                        if (cleanLine.Contains($"Device {address}") && cleanLine.Contains("Connected: yes"))
                            deviceConnected = true;
                    }
                }
                catch { /* best effort, nothing to recover */ }
            });

            _log.LogInformation("Trusting device...");
            await proc.StandardInput.WriteLineAsync($"trust {address}");
            await proc.StandardInput.FlushAsync();
            await Task.Delay(1500);

            _log.LogInformation("Pairing...");
            await proc.StandardInput.WriteLineAsync($"pair {address}");
            await proc.StandardInput.FlushAsync();
            await Task.Delay(6000);

            if (!deviceConnected)
            {
                _log.LogInformation("Connecting...");
                await proc.StandardInput.WriteLineAsync($"connect {address}");
                await proc.StandardInput.FlushAsync();
                await Task.Delay(4000);
            }
            else
            {
                _log.LogInformation("Device already connected during pairing");
            }

            await proc.StandardInput.WriteLineAsync("quit");
            await proc.StandardInput.FlushAsync();

            var exitTask = Task.Run(() => proc.WaitForExit(5000));
            await Task.WhenAny(exitTask, Task.Delay(6000));

            if (!proc.HasExited)
                try { proc.Kill(); } catch { /* best effort, nothing to recover */ }

            var output = outputBuilder.ToString();

            if (output.Contains("Failed to pair") ||
                (output.Contains("org.bluez.Error") && !output.Contains("InProgress")))
            {
                _log.LogError("Pairing failed");
                return (false, "Pairing failed. Make sure the device is in pairing mode.");
            }

            await Task.Delay(2000);
            var deviceInfo = await GetDeviceInfoAsync(address);

            if (deviceInfo?.IsConnected == true)
            {
                _log.LogInformation("Successfully paired and connected to {Address}", address);
                await Task.Delay(2000);

                var sinks = await _audio.GetAudioSinksAsync();
                var btSink = sinks.FirstOrDefault(s =>
                    s.Type == AudioOutputService.AudioOutputType.Bluetooth &&
                    (s.BluetoothAddress?.Equals(address, StringComparison.OrdinalIgnoreCase) == true ||
                     s.Name.Contains("bluez", StringComparison.OrdinalIgnoreCase)));

                if (btSink != null)
                {
                    await _audio.SetDefaultSinkAsync(btSink.Name);
                    _log.LogInformation("Set {Description} as default audio output", btSink.Description);
                    return (true, $"Connected and set as audio output: {btSink.Description}");
                }

                return (true, "Connected successfully");
            }

            if (deviceInfo?.IsPaired == true)
            {
                _log.LogInformation("Device paired but not connected");
                return (false,
                    "Device paired but connection failed. The device may not support audio or is out of range.");
            }

            return (false, "Pairing did not complete. Make sure the device is in pairing mode and try again.");
        }
        catch (Exception ex)
        {
            _log.LogError("Pairing error: {Message}", ex.Message);
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>Connect to a paired Bluetooth device.</summary>
    public async Task<bool> ConnectDeviceAsync(string address)
    {
        if (!IsDeviceAddress(address)) return false;
        _log.LogInformation("Connecting to {Address}...", address);

        await EnsurePulseAudioBluetoothModuleAsync();

        var deviceInfo = await GetDeviceInfoAsync(address);
        if (deviceInfo?.IsConnected == true)
        {
            _log.LogInformation("Device already connected, checking for audio sink...");
            var existingSinks = await _audio.GetAudioSinksAsync();
            var existingBtSink = existingSinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
            if (existingBtSink != null)
            {
                _log.LogInformation("Already connected with audio sink: {Description}", existingBtSink.Description);
                await _audio.SetDefaultSinkAsync(existingBtSink.Name);
                return true;
            }

            _log.LogInformation("Connected but no audio sink, disconnecting...");
            await RunBluetoothCtlCommandAsync($"disconnect {address}", 5000);
            await Task.Delay(3000);
        }

        _log.LogInformation("Now connecting (waiting for A2DP transport)...");
        var success = await ConnectBluetoothInteractiveAsync(address);

        if (!success)
        {
            _log.LogError("First connection attempt failed, retrying in 3 seconds...");
            await Task.Delay(3000);
            success = await ConnectBluetoothInteractiveAsync(address);
        }

        if (success)
        {
            _log.LogInformation("Connected to {Address}", address);

            _log.LogInformation("Waiting for PulseAudio to detect audio sink...");
            await Task.Delay(3000);

            var sinks = await _audio.GetAudioSinksAsync();
            var addressFormatted = address.Replace(":", "_");
            var btSink = sinks.FirstOrDefault(s =>
                s.Type == AudioOutputService.AudioOutputType.Bluetooth &&
                (s.BluetoothAddress?.Equals(address, StringComparison.OrdinalIgnoreCase) == true ||
                 s.Name.Contains(addressFormatted, StringComparison.OrdinalIgnoreCase)));

            if (btSink == null) btSink = sinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);

            if (btSink != null)
            {
                await _audio.SetDefaultSinkAsync(btSink.Name);
                _log.LogInformation("Set {Description} as default audio output", btSink.Description);
            }
            else
            {
                _log.LogWarning("Warning: No Bluetooth audio sink found in PulseAudio");
                _log.LogInformation("The Bluetooth device connected but PulseAudio can't see it.");
                _log.LogInformation("FIX: Run these commands:");
                _log.LogInformation("sudo usermod -aG bluetooth pulse");
                _log.LogInformation("sudo systemctl restart bluetooth");
                _log.LogInformation("sudo systemctl restart pulseaudio");

                var activated = await TryActivateBluetoothA2dpProfileAsync(address);

                if (!activated)
                {
                    _log.LogWarning("Could not activate A2DP profile.");

                    if (_audio.IsPulseAudioSystemMode)
                    {
                        _log.LogInformation("Trying to restart PulseAudio service to rescan Bluetooth...");
                        await RunCommandAsync("sudo", "systemctl restart pulseaudio");
                        await Task.Delay(3000);

                        sinks = await _audio.GetAudioSinksAsync();
                        btSink = sinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                        if (btSink != null)
                        {
                            await _audio.SetDefaultSinkAsync(btSink.Name);
                            _log.LogInformation("Found sink after service restart: {Description}", btSink.Description);
                        }
                        else
                        {
                            _log.LogInformation("Still no sink after restart.");
                            _log.LogInformation("The 'pulse' user is likely not in the 'bluetooth' group.");
                        }
                    }
                    else
                    {
                        _log.LogInformation("Trying to trigger PulseAudio Bluetooth rescan...");
                        await RunPactlAsync("unload-module module-bluetooth-discover");
                        await RunPactlAsync("unload-module module-bluetooth-policy");
                        await Task.Delay(1000);
                        await RunPactlAsync("load-module module-bluetooth-discover");
                        await RunPactlAsync("load-module module-bluetooth-policy");
                        await Task.Delay(2000);

                        sinks = await _audio.GetAudioSinksAsync();
                        btSink = sinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                        if (btSink != null)
                        {
                            await _audio.SetDefaultSinkAsync(btSink.Name);
                            _log.LogInformation("Found sink after reload: {Description}", btSink.Description);
                        }
                        else
                        {
                            _log.LogInformation("Still no sink.");
                        }
                    }
                }
            }
        }
        else
        {
            _log.LogError("Failed to connect to {Address}", address);
        }

        return success;
    }

    /// <summary>Disconnect from a Bluetooth device.</summary>
    public async Task<bool> DisconnectDeviceAsync(string address)
    {
        if (!IsDeviceAddress(address)) return false;
        _log.LogInformation("Disconnecting from {Address}...", address);
        var success = await RunBluetoothCtlCommandAsync($"disconnect {address}");
        if (success) _log.LogInformation("Disconnected from {Address}", address);
        return success;
    }

    /// <summary>Remove (unpair) a Bluetooth device.</summary>
    public async Task<bool> RemoveDeviceAsync(string address)
    {
        if (!IsDeviceAddress(address)) return false;
        _log.LogInformation("Removing {Address}...", address);
        await DisconnectDeviceAsync(address);
        var success = await RunBluetoothCtlCommandAsync($"remove {address}");
        if (success) _log.LogInformation("Removed {Address}", address);
        return success;
    }

    // ========================================================================
    // PULSE AUDIO BLUETOOTH MODULE MANAGEMENT
    // ========================================================================

    private async Task CheckPulseUserBluetoothGroupAsync()
    {
        if (!_audio.IsPulseAudioSystemMode) return;

        try
        {
            var groupsOutput = await RunCommandAsync("groups", "pulse");
            _log.LogInformation("Pulse user groups: {Trim}", groupsOutput.Trim());

            if (!groupsOutput.Contains("bluetooth"))
            {
                _log.LogWarning("'pulse' user is NOT in 'bluetooth' group!");
                _log.LogInformation("This will prevent Bluetooth audio from working.");
                _log.LogInformation("Fix with: sudo usermod -aG bluetooth pulse && sudo systemctl restart pulseaudio");
            }
            else
            {
                _log.LogInformation("OK: 'pulse' user is in 'bluetooth' group");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not check pulse user groups: {Message}", ex.Message);
        }
    }

    private async Task EnsurePulseAudioBluetoothModuleAsync()
    {
        await CheckPulseUserBluetoothGroupAsync();

        try
        {
            var output = await RunPactlAsync("list modules short");
            _log.LogInformation("Current PulseAudio modules:\n{Output}", output);

            var hasDiscover = output.Contains("module-bluetooth-discover");
            var hasPolicy = output.Contains("module-bluetooth-policy");

            if (_audio.IsPulseAudioSystemMode)
            {
                _log.LogInformation("PulseAudio running in system mode");

                if (hasDiscover)
                    _log.LogInformation("module-bluetooth-discover already loaded (system mode)");
                else
                {
                    _log.LogWarning("Warning: module-bluetooth-discover NOT loaded in system mode");
                    _log.LogInformation("Add 'load-module module-bluetooth-discover' to /etc/pulse/system.pa");
                }

                if (hasPolicy)
                    _log.LogInformation("module-bluetooth-policy already loaded (system mode)");
                else
                {
                    _log.LogWarning("Warning: module-bluetooth-policy NOT loaded in system mode");
                    _log.LogInformation("Add 'load-module module-bluetooth-policy' to /etc/pulse/system.pa");
                }

                return;
            }

            // User mode: try to load modules if not present
            if (!hasDiscover)
            {
                _log.LogInformation("Loading module-bluetooth-discover...");
                var result = await RunPactlAsync("load-module module-bluetooth-discover");
                if (!result.Contains("Failure") && !result.Contains("AlreadyExists"))
                    _log.LogInformation("module-bluetooth-discover loaded");
                else
                {
                    _log.LogWarning("Warning: Could not load module-bluetooth-discover: {Result}", result);
                    _log.LogInformation("Try: sudo apt install pulseaudio-module-bluetooth");
                }
            }
            else
            {
                _log.LogInformation("module-bluetooth-discover already loaded");
            }

            if (!hasPolicy)
            {
                _log.LogInformation("Loading module-bluetooth-policy...");
                var result = await RunPactlAsync("load-module module-bluetooth-policy");
                if (!result.Contains("Failure") && !result.Contains("AlreadyExists"))
                    _log.LogInformation("module-bluetooth-policy loaded");
                else
                    _log.LogWarning("Warning: Could not load module-bluetooth-policy: {Result}", result);
            }
            else
            {
                _log.LogInformation("module-bluetooth-policy already loaded");
            }
        }
        catch (Exception ex)
        {
            _log.LogError("Error checking PulseAudio modules: {Message}", ex.Message);
        }
    }

    private async Task<bool> TryActivateBluetoothA2dpProfileAsync(string address)
    {
        try
        {
            _log.LogInformation("Attempting to manually activate A2DP profile for {Address}...", address);

            var cardsOutput = await RunPactlAsync("list cards short");
            _log.LogInformation("Available cards:\n{CardsOutput}", cardsOutput);

            var addressFormatted = address.Replace(":", "_");

            var lines = cardsOutput.Split('\n');
            foreach (var line in lines)
                if (line.Contains("bluez", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(addressFormatted, StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var cardName = parts[1].Trim();
                        _log.LogInformation("Found Bluetooth card: {CardName}", cardName);

                        await RunPactlAsync("list cards");

                        var profiles = new[] { "a2dp_sink", "a2dp-sink", "headset_head_unit", "off" };

                        foreach (var profile in profiles)
                        {
                            if (profile == "off") continue;

                            _log.LogInformation("Trying profile: {Profile}", profile);
                            await RunPactlAsync($"set-card-profile {cardName} {profile}");

                            await Task.Delay(1000);

                            var sinks = await _audio.GetAudioSinksAsync();
                            var btSink = sinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                            if (btSink != null)
                            {
                                _log.LogInformation("Profile {Profile} activated! Sink: {Description}", profile, btSink.Description);
                                await _audio.SetDefaultSinkAsync(btSink.Name);
                                return true;
                            }
                        }
                    }
                }

            _log.LogInformation("No Bluetooth card found in PulseAudio");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError("Error activating A2DP profile: {Message}", ex.Message);
            return false;
        }
    }

    // ========================================================================
    // CONNECTION INTERNALS
    // ========================================================================

    /// <summary>Connect to Bluetooth device using D-Bus directly (more reliable than bluetoothctl).</summary>
    private async Task<bool> ConnectBluetoothInteractiveAsync(string address)
    {
        try
        {
            _log.LogInformation("Connecting via D-Bus...");

            var devPath = $"/org/bluez/hci0/dev_{address.Replace(":", "_")}";
            var isRoot = Environment.UserName == "root" || GetEffectiveUserId() == 0;
            ProcessStartInfo psi;

            if (isRoot)
            {
                var uid = GetUserUid("pi");
                var envCmd =
                    $"export XDG_RUNTIME_DIR=/run/user/{uid} DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{uid}/bus && busctl --system call org.bluez {devPath} org.bluez.Device1 Connect";
                psi = new ProcessStartInfo("sudo", $"-u pi bash -c \"{envCmd}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                _log.LogInformation("D-Bus call (in user session context for uid {Uid})", uid);
            }
            else
            {
                psi = new ProcessStartInfo("busctl",
                    $"--system call org.bluez {devPath} org.bluez.Device1 Connect")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                _log.LogInformation("D-Bus call: busctl --system call org.bluez {DevPath} org.bluez.Device1 Connect", devPath);
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _log.LogError("Failed to start busctl");
                return false;
            }

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();

            var exited = await Task.Run(() => proc.WaitForExit(30000));
            if (!exited)
            {
                _log.LogInformation("D-Bus call timed out");
                try { proc.Kill(); } catch { /* best effort, nothing to recover */ }
                return false;
            }

            if (!string.IsNullOrEmpty(error) && !error.Contains("warning"))
                _log.LogError("D-Bus error: {Trim}", error.Trim());

            if (proc.ExitCode == 0)
            {
                _log.LogInformation("D-Bus Connect succeeded, waiting for A2DP sink...");

                for (var i = 0; i < 8; i++)
                {
                    await Task.Delay(1000);
                    var sinks = await _audio.GetAudioSinksAsync();
                    var btSink = sinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                    if (btSink != null)
                    {
                        _log.LogInformation("Success! A2DP sink detected: {Name}", btSink.Name);
                        return true;
                    }
                    if (i % 2 == 0) _log.LogInformation("Waiting for A2DP sink... ({Value}/8)", i + 1);
                }

                _log.LogInformation("No sink yet, trying to activate A2DP profile via BlueZ...");

                _log.LogInformation("Checking device status via D-Bus...");
                var uuidOutput = await RunBusctlAsync($"get-property org.bluez {devPath} org.bluez.Device1 UUIDs");
                _log.LogInformation("Device UUIDs: {Trim}", uuidOutput.Trim());

                _log.LogInformation("Attempting to connect A2DP sink profile...");
                var profileResult =
                    await RunBusctlAsync(
                        $"call org.bluez {devPath} org.bluez.Device1 ConnectProfile s 0000110b-0000-1000-8000-00805f9b34fb");
                if (!string.IsNullOrEmpty(profileResult))
                    _log.LogInformation("ConnectProfile result: {Trim}", profileResult.Trim());
                await Task.Delay(2000);

                var quickCheck = await _audio.GetAudioSinksAsync();
                var btSinkQuick = quickCheck.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                if (btSinkQuick != null)
                {
                    _log.LogInformation("Success! A2DP sink detected: {Name}", btSinkQuick.Name);
                    return true;
                }

                _log.LogInformation("Trying to load bluez5 device directly...");
                await RunPactlAsync($"load-module module-bluez5-device path={devPath}");
                await Task.Delay(2000);

                quickCheck = await _audio.GetAudioSinksAsync();
                btSinkQuick = quickCheck.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                if (btSinkQuick != null)
                {
                    _log.LogInformation("Success with direct device load! A2DP sink detected: {Name}", btSinkQuick.Name);
                    return true;
                }

                _log.LogInformation("Trying minimal module reload...");
                await RunPactlAsync("unload-module module-bluez5-discover");
                await Task.Delay(1000);
                await RunPactlAsync("load-module module-bluez5-discover");
                await Task.Delay(3000);

                for (var i = 0; i < 5; i++)
                {
                    await Task.Delay(1000);
                    var sinks = await _audio.GetAudioSinksAsync();
                    var btSink = sinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                    if (btSink != null)
                    {
                        _log.LogInformation("Success after rescan! A2DP sink detected: {Name}", btSink.Name);
                        return true;
                    }
                }

                _log.LogInformation("D-Bus Connect succeeded but no A2DP sink appeared");
                return true;
            }

            _log.LogError("D-Bus Connect failed (exit code {ExitCode})", proc.ExitCode);
            _log.LogInformation("Falling back to bluetoothctl...");
            return await ConnectBluetoothCtlFallbackAsync(address);
        }
        catch (Exception ex)
        {
            _log.LogError("D-Bus connect error: {Message}", ex.Message);
            return await ConnectBluetoothCtlFallbackAsync(address);
        }
    }

    private async Task<bool> ConnectBluetoothCtlFallbackAsync(string address)
    {
        try
        {
            _log.LogInformation("Using interactive bluetoothctl connection...");

            var psi = new ProcessStartInfo("bluetoothctl")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["TERM"] = "dumb";

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = new StringBuilder();
            var readTask = Task.Run(async () =>
            {
                var buffer = new char[256];
                while (true)
                {
                    var count = await proc.StandardOutput.ReadAsync(buffer.AsMemory());
                    if (count == 0) break;
                    output.Append(buffer, 0, count);
                }
            });

            await proc.StandardInput.WriteLineAsync($"connect {address}");
            await proc.StandardInput.FlushAsync();

            _log.LogInformation("Waiting for A2DP handshake (up to 15s)...");
            var startTime = DateTime.Now;
            var connected = false;
            var transportReady = false;

            while ((DateTime.Now - startTime).TotalSeconds < 15)
            {
                await Task.Delay(500);
                var currentOutput = output.ToString();

                if (currentOutput.Contains("Connection successful"))
                {
                    connected = true;
                    _log.LogInformation("Connection successful, waiting for audio transport...");
                }

                if (currentOutput.Contains("A2DP") ||
                    currentOutput.Contains("Transport") ||
                    currentOutput.Contains("sep 0x") ||
                    currentOutput.Contains("MediaTransport"))
                {
                    transportReady = true;
                    _log.LogInformation("A2DP transport detected!");
                    break;
                }

                if (currentOutput.Contains("Failed to connect") ||
                    currentOutput.Contains("not available"))
                {
                    _log.LogError("Connection failed");
                    break;
                }
            }

            await proc.StandardInput.WriteLineAsync("quit");
            await Task.Run(() => proc.WaitForExit(3000));
            if (!proc.HasExited)
                try { proc.Kill(); } catch { /* best effort, nothing to recover */ }

            var fullOutput = StripAnsiCodes(output.ToString());
            _log.LogInformation("bluetoothctl: {Value}", (fullOutput.Length > 200 ? fullOutput[..200] + "..." : fullOutput).Replace("\n", " | "));

            if (connected || transportReady)
            {
                _log.LogInformation("Connection established, waiting for PulseAudio sink...");
                await Task.Delay(3000);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.LogError("bluetoothctl interactive error: {Message}", ex.Message);
            return false;
        }
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    private static BluetoothDevice? ParseBluetoothDeviceLine(string line)
    {
        var match = Regex.Match(line, @"Device\s+([0-9A-F:]{17})\s+(.+)", RegexOptions.IgnoreCase);
        if (match.Success)
            return new BluetoothDevice
            {
                Address = match.Groups[1].Value,
                Name = match.Groups[2].Value.Trim()
            };
        return null;
    }

    private async Task<BluetoothDevice?> GetDeviceInfoAsync(string address)
    {
        try
        {
            var output = await RunBluetoothCtlAsync($"info {address}");

            var device = new BluetoothDevice { Address = address };

            var nameMatch = Regex.Match(output, @"Name:\s*(.+)");
            if (nameMatch.Success) device.Name = nameMatch.Groups[1].Value.Trim();

            device.IsPaired = output.Contains("Paired: yes");
            device.IsConnected = output.Contains("Connected: yes");
            device.IsTrusted = output.Contains("Trusted: yes");

            var iconMatch = Regex.Match(output, @"Icon:\s*(.+)");
            if (iconMatch.Success) device.Icon = iconMatch.Groups[1].Value.Trim();

            return device;
        }
        catch
        {
            return null;
        }
    }

    private static string StripAnsiCodes(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return AnsiEscapeRegex.Replace(input, "");
    }

    /// <summary>Run a general command and return its output.</summary>
    private async Task<string> RunCommandAsync(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "";

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return output + error;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>Run a pactl command through AudioOutputService's PulseAudio session context.</summary>
    private async Task<string> RunPactlAsync(string args)
    {
        try
        {
            var psi = _audio.CreatePactlProcessStartInfo(args);

            using var proc = Process.Start(psi);
            if (proc == null) return "";

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return output + error;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> RunBusctlAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("busctl", $"--system {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "";

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();

            var exited = await Task.Run(() => proc.WaitForExit(10000));
            if (!exited)
            {
                try { proc.Kill(); } catch { /* best effort, nothing to recover */ }
                return "Error: timeout";
            }

            return output + (proc.ExitCode != 0 ? $" (exit {proc.ExitCode}: {error})" : "");
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> RunBluetoothCtlAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("bluetoothctl", command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "";

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return output;
        }
        catch (Exception ex)
        {
            _log.LogError("Command error: {Message}", ex.Message);
            return "";
        }
    }

    /// <summary>
    ///     Runs one bluetoothctl command. The command goes to bluetoothctl's stdin — no shell, so nothing in
    ///     it is interpreted. As root it runs as the desktop user (pi), who owns the audio session.
    /// </summary>
    private async Task<bool> RunBluetoothCtlCommandAsync(string command, int timeoutMs = 10000)
    {
        try
        {
            var isRoot = Environment.UserName == "root" || GetEffectiveUserId() == 0;
            var psi = new ProcessStartInfo(isRoot ? "sudo" : "bluetoothctl")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (isRoot)
            {
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add("pi");
                psi.ArgumentList.Add("bluetoothctl");
            }

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // Read while it runs, so a chatty bluetoothctl cannot block on a full pipe.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            await proc.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
            await proc.StandardInput.WriteLineAsync("quit").ConfigureAwait(false);
            proc.StandardInput.Close();

            using var timeout = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* already gone */ }
                return false;
            }

            var output = await outputTask.ConfigureAwait(false);
            return !output.Contains("Failed") && !output.Contains("not available");
        }
        catch (Exception ex)
        {
            _log.LogError("Command error: {Message}", ex.Message);
            return false;
        }
    }

    private static readonly Regex AddressPattern = new("^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$", RegexOptions.Compiled);

    /// <summary>A Bluetooth device address (AA:BB:CC:DD:EE:FF); anything else is refused before bluetoothctl sees it.</summary>
    public static bool IsDeviceAddress(string? address) => address != null && AddressPattern.IsMatch(address);

    // P/Invoke for root detection
    [DllImport("libc")]
    private static extern uint geteuid();

    private static uint GetEffectiveUserId()
    {
        try { return geteuid(); }
        catch { return uint.MaxValue; }
    }

    private static int GetUserUid(string username)
    {
        try
        {
            var psi = new ProcessStartInfo("id", $"-u {username}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return 1000;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return int.TryParse(output, out var uid) ? uid : 1000;
        }
        catch
        {
            return 1000;
        }
    }

    // ========================================================================
    // MODEL
    // ========================================================================

    /// <summary>Represents a Bluetooth device.</summary>
    public class BluetoothDevice
    {
        public string Address { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsPaired { get; set; }
        public bool IsConnected { get; set; }
        public bool IsTrusted { get; set; }
        public string? Icon { get; set; }
    }
}
