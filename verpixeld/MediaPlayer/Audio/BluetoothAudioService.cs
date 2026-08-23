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
public class BluetoothAudioService
{
    private readonly AudioOutputService _audio;

    // Regex to strip ANSI escape codes from terminal output
    private static readonly Regex AnsiEscapeRegex =
        new(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07", RegexOptions.Compiled);

    // Store discovered devices from last scan
    private List<BluetoothDevice> _lastDiscoveredDevices = new();

    public BluetoothAudioService(AudioOutputService audioOutputService)
    {
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
        Console.WriteLine("[BLUETOOTH] Powering on Bluetooth adapter...");

        // Step 1: Check rfkill
        try
        {
            Console.WriteLine("[BLUETOOTH] Checking rfkill status...");
            var rfkillCheck = await RunCommandAsync("rfkill", "list bluetooth");
            Console.WriteLine($"[BLUETOOTH] rfkill status: {rfkillCheck}");

            if (rfkillCheck.Contains("Soft blocked: yes") || rfkillCheck.Contains("Hard blocked: yes"))
            {
                Console.WriteLine("[BLUETOOTH] Bluetooth is blocked by rfkill, unblocking...");
                var unblockResult = await RunCommandAsync("rfkill", "unblock bluetooth");
                Console.WriteLine($"[BLUETOOTH] rfkill unblock result: {unblockResult}");
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] rfkill check failed: {ex.Message}");
        }

        // Step 2: Ensure bluetooth service is running
        try
        {
            Console.WriteLine("[BLUETOOTH] Checking bluetooth service...");
            var serviceStatus = await RunCommandAsync("systemctl", "is-active bluetooth");
            Console.WriteLine($"[BLUETOOTH] Service status: {serviceStatus.Trim()}");

            if (!serviceStatus.Contains("active"))
            {
                Console.WriteLine("[BLUETOOTH] Starting bluetooth service...");
                await RunCommandAsync("sudo", "systemctl start bluetooth");
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] Service check failed: {ex.Message}");
        }

        // Step 3: Power on via bluetoothctl
        Console.WriteLine("[BLUETOOTH] Sending power on command...");
        var success = await RunBluetoothCtlCommandAsync("power on");

        // Wait and verify
        await Task.Delay(1000);
        var actuallyPoweredOn = IsPoweredOn();

        if (actuallyPoweredOn)
        {
            Console.WriteLine("[BLUETOOTH] Bluetooth adapter powered on successfully");
            await EnsurePulseAudioBluetoothModuleAsync();
            return true;
        }

        Console.WriteLine("[BLUETOOTH] Failed to power on Bluetooth adapter");
        Console.WriteLine(
            "[BLUETOOTH] Try running manually: sudo rfkill unblock bluetooth && sudo systemctl restart bluetooth");
        return false;
    }

    /// <summary>Power off the Bluetooth adapter.</summary>
    public async Task<bool> PowerOffAsync()
    {
        Console.WriteLine("[BLUETOOTH] Powering off Bluetooth adapter...");
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
            Console.WriteLine($"[BLUETOOTH] Error listing devices: {ex.Message}");
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
            Console.WriteLine($"[BLUETOOTH] Error getting discovered devices: {ex.Message}");
        }

        return devices;
    }

    /// <summary>Start scanning for Bluetooth devices.</summary>
    public async Task<List<BluetoothDevice>> StartScanAsync(int durationSeconds = 10)
    {
        Console.WriteLine($"[BLUETOOTH] Starting scan for {durationSeconds} seconds...");
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
                Console.WriteLine("[BLUETOOTH] Failed to start bluetoothctl");
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
                        Console.WriteLine($"[BLUETOOTH] {line}");

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
                                Console.WriteLine($"[BLUETOOTH] >>> Discovered: {name} ({address})");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BLUETOOTH] Output read error: {ex.Message}");
                }
            });

            await proc.StandardInput.WriteLineAsync("scan on");
            await proc.StandardInput.FlushAsync();

            Console.WriteLine("[BLUETOOTH] Scanning...");
            await Task.Delay(durationSeconds * 1000);

            await proc.StandardInput.WriteLineAsync("scan off");
            await proc.StandardInput.WriteLineAsync("quit");
            await proc.StandardInput.FlushAsync();

            var exitTask = Task.Run(() => proc.WaitForExit(5000));
            await Task.WhenAny(exitTask, Task.Delay(6000));

            if (!proc.HasExited)
                try { proc.Kill(); } catch { }

            Console.WriteLine($"[BLUETOOTH] Scan complete. Found {discoveredDevices.Count} new devices");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] Scan error: {ex.Message}");
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
        Console.WriteLine($"[BLUETOOTH] Starting pairing process for {address}...");

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
                        Console.WriteLine($"[BLUETOOTH] {cleanLine}");
                        outputBuilder.AppendLine(cleanLine);

                        if (cleanLine.Contains($"Device {address}") && cleanLine.Contains("Connected: yes"))
                            deviceConnected = true;
                    }
                }
                catch { }
            });

            Console.WriteLine("[BLUETOOTH] Trusting device...");
            await proc.StandardInput.WriteLineAsync($"trust {address}");
            await proc.StandardInput.FlushAsync();
            await Task.Delay(1500);

            Console.WriteLine("[BLUETOOTH] Pairing...");
            await proc.StandardInput.WriteLineAsync($"pair {address}");
            await proc.StandardInput.FlushAsync();
            await Task.Delay(6000);

            if (!deviceConnected)
            {
                Console.WriteLine("[BLUETOOTH] Connecting...");
                await proc.StandardInput.WriteLineAsync($"connect {address}");
                await proc.StandardInput.FlushAsync();
                await Task.Delay(4000);
            }
            else
            {
                Console.WriteLine("[BLUETOOTH] Device already connected during pairing");
            }

            await proc.StandardInput.WriteLineAsync("quit");
            await proc.StandardInput.FlushAsync();

            var exitTask = Task.Run(() => proc.WaitForExit(5000));
            await Task.WhenAny(exitTask, Task.Delay(6000));

            if (!proc.HasExited)
                try { proc.Kill(); } catch { }

            var output = outputBuilder.ToString();

            if (output.Contains("Failed to pair") ||
                (output.Contains("org.bluez.Error") && !output.Contains("InProgress")))
            {
                Console.WriteLine("[BLUETOOTH] Pairing failed");
                return (false, "Pairing failed. Make sure the device is in pairing mode.");
            }

            await Task.Delay(2000);
            var deviceInfo = await GetDeviceInfoAsync(address);

            if (deviceInfo?.IsConnected == true)
            {
                Console.WriteLine($"[BLUETOOTH] Successfully paired and connected to {address}");
                await Task.Delay(2000);

                var sinks = await _audio.GetAudioSinksAsync();
                var btSink = sinks.FirstOrDefault(s =>
                    s.Type == AudioOutputService.AudioOutputType.Bluetooth &&
                    (s.BluetoothAddress?.Equals(address, StringComparison.OrdinalIgnoreCase) == true ||
                     s.Name.Contains("bluez", StringComparison.OrdinalIgnoreCase)));

                if (btSink != null)
                {
                    await _audio.SetDefaultSinkAsync(btSink.Name);
                    Console.WriteLine($"[BLUETOOTH] Set {btSink.Description} as default audio output");
                    return (true, $"Connected and set as audio output: {btSink.Description}");
                }

                return (true, "Connected successfully");
            }

            if (deviceInfo?.IsPaired == true)
            {
                Console.WriteLine("[BLUETOOTH] Device paired but not connected");
                return (false,
                    "Device paired but connection failed. The device may not support audio or is out of range.");
            }

            return (false, "Pairing did not complete. Make sure the device is in pairing mode and try again.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] Pairing error: {ex.Message}");
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>Connect to a paired Bluetooth device.</summary>
    public async Task<bool> ConnectDeviceAsync(string address)
    {
        Console.WriteLine($"[BLUETOOTH] Connecting to {address}...");

        await EnsurePulseAudioBluetoothModuleAsync();

        var deviceInfo = await GetDeviceInfoAsync(address);
        if (deviceInfo?.IsConnected == true)
        {
            Console.WriteLine("[BLUETOOTH] Device already connected, checking for audio sink...");
            var existingSinks = await _audio.GetAudioSinksAsync();
            var existingBtSink = existingSinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
            if (existingBtSink != null)
            {
                Console.WriteLine($"[BLUETOOTH] Already connected with audio sink: {existingBtSink.Description}");
                await _audio.SetDefaultSinkAsync(existingBtSink.Name);
                return true;
            }

            Console.WriteLine("[BLUETOOTH] Connected but no audio sink, disconnecting...");
            await RunBluetoothCtlCommandAsync($"disconnect {address}", 5000);
            await Task.Delay(3000);
        }

        Console.WriteLine("[BLUETOOTH] Now connecting (waiting for A2DP transport)...");
        var success = await ConnectBluetoothInteractiveAsync(address);

        if (!success)
        {
            Console.WriteLine("[BLUETOOTH] First connection attempt failed, retrying in 3 seconds...");
            await Task.Delay(3000);
            success = await ConnectBluetoothInteractiveAsync(address);
        }

        if (success)
        {
            Console.WriteLine($"[BLUETOOTH] Connected to {address}");

            Console.WriteLine("[BLUETOOTH] Waiting for PulseAudio to detect audio sink...");
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
                Console.WriteLine($"[BLUETOOTH] Set {btSink.Description} as default audio output");
            }
            else
            {
                Console.WriteLine("[BLUETOOTH] Warning: No Bluetooth audio sink found in PulseAudio");
                Console.WriteLine("[BLUETOOTH] The Bluetooth device connected but PulseAudio can't see it.");
                Console.WriteLine("[BLUETOOTH] FIX: Run these commands:");
                Console.WriteLine("[BLUETOOTH]   sudo usermod -aG bluetooth pulse");
                Console.WriteLine("[BLUETOOTH]   sudo systemctl restart bluetooth");
                Console.WriteLine("[BLUETOOTH]   sudo systemctl restart pulseaudio");

                var activated = await TryActivateBluetoothA2dpProfileAsync(address);

                if (!activated)
                {
                    Console.WriteLine("[BLUETOOTH] Could not activate A2DP profile.");

                    if (_audio.IsPulseAudioSystemMode)
                    {
                        Console.WriteLine("[BLUETOOTH] Trying to restart PulseAudio service to rescan Bluetooth...");
                        await RunCommandAsync("sudo", "systemctl restart pulseaudio");
                        await Task.Delay(3000);

                        sinks = await _audio.GetAudioSinksAsync();
                        btSink = sinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                        if (btSink != null)
                        {
                            await _audio.SetDefaultSinkAsync(btSink.Name);
                            Console.WriteLine($"[BLUETOOTH] Found sink after service restart: {btSink.Description}");
                        }
                        else
                        {
                            Console.WriteLine("[BLUETOOTH] Still no sink after restart.");
                            Console.WriteLine("[BLUETOOTH] The 'pulse' user is likely not in the 'bluetooth' group.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[BLUETOOTH] Trying to trigger PulseAudio Bluetooth rescan...");
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
                            Console.WriteLine($"[BLUETOOTH] Found sink after reload: {btSink.Description}");
                        }
                        else
                        {
                            Console.WriteLine("[BLUETOOTH] Still no sink.");
                        }
                    }
                }
            }
        }
        else
        {
            Console.WriteLine($"[BLUETOOTH] Failed to connect to {address}");
        }

        return success;
    }

    /// <summary>Disconnect from a Bluetooth device.</summary>
    public async Task<bool> DisconnectDeviceAsync(string address)
    {
        Console.WriteLine($"[BLUETOOTH] Disconnecting from {address}...");
        var success = await RunBluetoothCtlCommandAsync($"disconnect {address}");
        if (success) Console.WriteLine($"[BLUETOOTH] Disconnected from {address}");
        return success;
    }

    /// <summary>Remove (unpair) a Bluetooth device.</summary>
    public async Task<bool> RemoveDeviceAsync(string address)
    {
        Console.WriteLine($"[BLUETOOTH] Removing {address}...");
        await DisconnectDeviceAsync(address);
        var success = await RunBluetoothCtlCommandAsync($"remove {address}");
        if (success) Console.WriteLine($"[BLUETOOTH] Removed {address}");
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
            Console.WriteLine($"[BLUETOOTH] Pulse user groups: {groupsOutput.Trim()}");

            if (!groupsOutput.Contains("bluetooth"))
            {
                Console.WriteLine("[BLUETOOTH] WARNING: 'pulse' user is NOT in 'bluetooth' group!");
                Console.WriteLine("[BLUETOOTH] This will prevent Bluetooth audio from working.");
                Console.WriteLine(
                    "[BLUETOOTH] Fix with: sudo usermod -aG bluetooth pulse && sudo systemctl restart pulseaudio");
            }
            else
            {
                Console.WriteLine("[BLUETOOTH] OK: 'pulse' user is in 'bluetooth' group");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] Could not check pulse user groups: {ex.Message}");
        }
    }

    private async Task EnsurePulseAudioBluetoothModuleAsync()
    {
        await CheckPulseUserBluetoothGroupAsync();

        try
        {
            var output = await RunPactlAsync("list modules short");
            Console.WriteLine($"[BLUETOOTH] Current PulseAudio modules:\n{output}");

            var hasDiscover = output.Contains("module-bluetooth-discover");
            var hasPolicy = output.Contains("module-bluetooth-policy");

            if (_audio.IsPulseAudioSystemMode)
            {
                Console.WriteLine("[BLUETOOTH] PulseAudio running in system mode");

                if (hasDiscover)
                    Console.WriteLine("[BLUETOOTH] module-bluetooth-discover already loaded (system mode)");
                else
                {
                    Console.WriteLine("[BLUETOOTH] Warning: module-bluetooth-discover NOT loaded in system mode");
                    Console.WriteLine("[BLUETOOTH] Add 'load-module module-bluetooth-discover' to /etc/pulse/system.pa");
                }

                if (hasPolicy)
                    Console.WriteLine("[BLUETOOTH] module-bluetooth-policy already loaded (system mode)");
                else
                {
                    Console.WriteLine("[BLUETOOTH] Warning: module-bluetooth-policy NOT loaded in system mode");
                    Console.WriteLine("[BLUETOOTH] Add 'load-module module-bluetooth-policy' to /etc/pulse/system.pa");
                }

                return;
            }

            // User mode: try to load modules if not present
            if (!hasDiscover)
            {
                Console.WriteLine("[BLUETOOTH] Loading module-bluetooth-discover...");
                var result = await RunPactlAsync("load-module module-bluetooth-discover");
                if (!result.Contains("Failure") && !result.Contains("AlreadyExists"))
                    Console.WriteLine("[BLUETOOTH] module-bluetooth-discover loaded");
                else
                {
                    Console.WriteLine($"[BLUETOOTH] Warning: Could not load module-bluetooth-discover: {result}");
                    Console.WriteLine("[BLUETOOTH] Try: sudo apt install pulseaudio-module-bluetooth");
                }
            }
            else
            {
                Console.WriteLine("[BLUETOOTH] module-bluetooth-discover already loaded");
            }

            if (!hasPolicy)
            {
                Console.WriteLine("[BLUETOOTH] Loading module-bluetooth-policy...");
                var result = await RunPactlAsync("load-module module-bluetooth-policy");
                if (!result.Contains("Failure") && !result.Contains("AlreadyExists"))
                    Console.WriteLine("[BLUETOOTH] module-bluetooth-policy loaded");
                else
                    Console.WriteLine($"[BLUETOOTH] Warning: Could not load module-bluetooth-policy: {result}");
            }
            else
            {
                Console.WriteLine("[BLUETOOTH] module-bluetooth-policy already loaded");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] Error checking PulseAudio modules: {ex.Message}");
        }
    }

    private async Task<bool> TryActivateBluetoothA2dpProfileAsync(string address)
    {
        try
        {
            Console.WriteLine($"[BLUETOOTH] Attempting to manually activate A2DP profile for {address}...");

            var cardsOutput = await RunPactlAsync("list cards short");
            Console.WriteLine($"[BLUETOOTH] Available cards:\n{cardsOutput}");

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
                        Console.WriteLine($"[BLUETOOTH] Found Bluetooth card: {cardName}");

                        await RunPactlAsync("list cards");

                        var profiles = new[] { "a2dp_sink", "a2dp-sink", "headset_head_unit", "off" };

                        foreach (var profile in profiles)
                        {
                            if (profile == "off") continue;

                            Console.WriteLine($"[BLUETOOTH] Trying profile: {profile}");
                            await RunPactlAsync($"set-card-profile {cardName} {profile}");

                            await Task.Delay(1000);

                            var sinks = await _audio.GetAudioSinksAsync();
                            var btSink = sinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                            if (btSink != null)
                            {
                                Console.WriteLine($"[BLUETOOTH] Profile {profile} activated! Sink: {btSink.Description}");
                                await _audio.SetDefaultSinkAsync(btSink.Name);
                                return true;
                            }
                        }
                    }
                }

            Console.WriteLine("[BLUETOOTH] No Bluetooth card found in PulseAudio");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] Error activating A2DP profile: {ex.Message}");
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
            Console.WriteLine("[BLUETOOTH] Connecting via D-Bus...");

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
                Console.WriteLine($"[BLUETOOTH] D-Bus call (in user session context for uid {uid})");
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
                Console.WriteLine(
                    $"[BLUETOOTH] D-Bus call: busctl --system call org.bluez {devPath} org.bluez.Device1 Connect");
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.WriteLine("[BLUETOOTH] Failed to start busctl");
                return false;
            }

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();

            var exited = await Task.Run(() => proc.WaitForExit(30000));
            if (!exited)
            {
                Console.WriteLine("[BLUETOOTH] D-Bus call timed out");
                try { proc.Kill(); } catch { }
                return false;
            }

            if (!string.IsNullOrEmpty(error) && !error.Contains("warning"))
                Console.WriteLine($"[BLUETOOTH] D-Bus error: {error.Trim()}");

            if (proc.ExitCode == 0)
            {
                Console.WriteLine("[BLUETOOTH] D-Bus Connect succeeded, waiting for A2DP sink...");

                for (var i = 0; i < 8; i++)
                {
                    await Task.Delay(1000);
                    var sinks = await _audio.GetAudioSinksAsync();
                    var btSink = sinks.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                    if (btSink != null)
                    {
                        Console.WriteLine($"[BLUETOOTH] Success! A2DP sink detected: {btSink.Name}");
                        return true;
                    }
                    if (i % 2 == 0) Console.WriteLine($"[BLUETOOTH] Waiting for A2DP sink... ({i + 1}/8)");
                }

                Console.WriteLine("[BLUETOOTH] No sink yet, trying to activate A2DP profile via BlueZ...");

                Console.WriteLine("[BLUETOOTH] Checking device status via D-Bus...");
                var uuidOutput = await RunBusctlAsync($"get-property org.bluez {devPath} org.bluez.Device1 UUIDs");
                Console.WriteLine($"[BLUETOOTH] Device UUIDs: {uuidOutput.Trim()}");

                Console.WriteLine("[BLUETOOTH] Attempting to connect A2DP sink profile...");
                var profileResult =
                    await RunBusctlAsync(
                        $"call org.bluez {devPath} org.bluez.Device1 ConnectProfile s 0000110b-0000-1000-8000-00805f9b34fb");
                if (!string.IsNullOrEmpty(profileResult))
                    Console.WriteLine($"[BLUETOOTH] ConnectProfile result: {profileResult.Trim()}");
                await Task.Delay(2000);

                var quickCheck = await _audio.GetAudioSinksAsync();
                var btSinkQuick = quickCheck.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                if (btSinkQuick != null)
                {
                    Console.WriteLine($"[BLUETOOTH] Success! A2DP sink detected: {btSinkQuick.Name}");
                    return true;
                }

                Console.WriteLine("[BLUETOOTH] Trying to load bluez5 device directly...");
                await RunPactlAsync($"load-module module-bluez5-device path={devPath}");
                await Task.Delay(2000);

                quickCheck = await _audio.GetAudioSinksAsync();
                btSinkQuick = quickCheck.FirstOrDefault(s => s.Type == AudioOutputService.AudioOutputType.Bluetooth);
                if (btSinkQuick != null)
                {
                    Console.WriteLine($"[BLUETOOTH] Success with direct device load! A2DP sink detected: {btSinkQuick.Name}");
                    return true;
                }

                Console.WriteLine("[BLUETOOTH] Trying minimal module reload...");
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
                        Console.WriteLine($"[BLUETOOTH] Success after rescan! A2DP sink detected: {btSink.Name}");
                        return true;
                    }
                }

                Console.WriteLine("[BLUETOOTH] D-Bus Connect succeeded but no A2DP sink appeared");
                return true;
            }

            Console.WriteLine($"[BLUETOOTH] D-Bus Connect failed (exit code {proc.ExitCode})");
            Console.WriteLine("[BLUETOOTH] Falling back to bluetoothctl...");
            return await ConnectBluetoothCtlFallbackAsync(address);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] D-Bus connect error: {ex.Message}");
            return await ConnectBluetoothCtlFallbackAsync(address);
        }
    }

    private async Task<bool> ConnectBluetoothCtlFallbackAsync(string address)
    {
        try
        {
            Console.WriteLine("[BLUETOOTH] Using interactive bluetoothctl connection...");

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

            Console.WriteLine("[BLUETOOTH] Waiting for A2DP handshake (up to 15s)...");
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
                    Console.WriteLine("[BLUETOOTH] Connection successful, waiting for audio transport...");
                }

                if (currentOutput.Contains("A2DP") ||
                    currentOutput.Contains("Transport") ||
                    currentOutput.Contains("sep 0x") ||
                    currentOutput.Contains("MediaTransport"))
                {
                    transportReady = true;
                    Console.WriteLine("[BLUETOOTH] A2DP transport detected!");
                    break;
                }

                if (currentOutput.Contains("Failed to connect") ||
                    currentOutput.Contains("not available"))
                {
                    Console.WriteLine("[BLUETOOTH] Connection failed");
                    break;
                }
            }

            await proc.StandardInput.WriteLineAsync("quit");
            await Task.Run(() => proc.WaitForExit(3000));
            if (!proc.HasExited)
                try { proc.Kill(); } catch { }

            var fullOutput = StripAnsiCodes(output.ToString());
            Console.WriteLine(
                $"[BLUETOOTH] bluetoothctl: {(fullOutput.Length > 200 ? fullOutput[..200] + "..." : fullOutput).Replace("\n", " | ")}");

            if (connected || transportReady)
            {
                Console.WriteLine("[BLUETOOTH] Connection established, waiting for PulseAudio sink...");
                await Task.Delay(3000);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] bluetoothctl interactive error: {ex.Message}");
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
                try { proc.Kill(); } catch { }
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
            Console.WriteLine($"[BLUETOOTH] Command error: {ex.Message}");
            return "";
        }
    }

    private async Task<bool> RunBluetoothCtlCommandAsync(string command, int timeoutMs = 10000)
    {
        try
        {
            var isRoot = Environment.UserName == "root" || GetEffectiveUserId() == 0;

            ProcessStartInfo psi;
            if (isRoot)
                psi = new ProcessStartInfo("sudo", $"-u pi bash -c \"echo -e '{command}\\nquit' | bluetoothctl\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            else
                psi = new ProcessStartInfo("bash", $"-c \"echo -e '{command}\\nquit' | bluetoothctl\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var completed = await Task.Run(() => proc.WaitForExit(timeoutMs));
            if (!completed)
            {
                try { proc.Kill(); } catch { }
                return false;
            }

            var output = await proc.StandardOutput.ReadToEndAsync();
            return !output.Contains("Failed") && !output.Contains("not available");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLUETOOTH] Command error: {ex.Message}");
            return false;
        }
    }

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
