# Raspberry Pi Setup Guide

This guide covers the configuration required to run the RGB Display application on a Raspberry Pi.

## Table of Contents
- [System Requirements](#system-requirements)
- [Systemd Service Setup](#systemd-service-setup)
- [Enabling Web-Based Reboot](#enabling-web-based-reboot)
- [HTTPS Certificate](#https-certificate)
- [Troubleshooting](#troubleshooting)

---

## System Requirements

- Raspberry Pi (tested on Pi 4)
- Raspberry Pi OS (Bookworm or newer)
- .NET 10.0 ASP.NET Core Runtime
- RGB LED Matrix hardware connected via GPIO

---

## Systemd Service Setup

Create a systemd service to run the application at boot:

```bash
sudo nano /etc/systemd/system/rgb-display.service
```

Add the following content:

```ini
[Unit]
Description=RGB-Display
Wants=network-online.target
After=network-online.target

[Service]
ExecStart=sudo dotnet verpixeld.dll
WorkingDirectory=/home/pi/verpixeld/
Environment=DOTNET_ROOT=/usr/share/dotnet
User=pi
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Enable and start the service:

```bash
sudo systemctl daemon-reload
sudo systemctl enable rgb-display
sudo systemctl start rgb-display
```

Check status:

```bash
sudo systemctl status rgb-display
```

---

## Enabling Web-Based Reboot

The web interface includes a "Reboot System" button. By default, this won't work because modern Linux requires authentication for system actions like reboot.

### The Problem

When the application tries to reboot the system via `systemctl reboot`, Linux's polkit (PolicyKit) requires interactive authentication:

```
Call to Reboot failed: Interactive authentication required.
```

This happens even when:
- The app runs as root (via `sudo dotnet`)
- The user has sudo privileges
- Sudoers is configured for passwordless reboot

### The Solution: Polkit Rule

Create a polkit rule that allows reboot without authentication:

```bash
sudo nano /etc/polkit-1/rules.d/10-allow-reboot.rules
```

Add this content:

```javascript
polkit.addRule(function(action, subject) {
    if (action.id == "org.freedesktop.login1.reboot" ||
        action.id == "org.freedesktop.login1.reboot-multiple-sessions") {
        return polkit.Result.YES;
    }
});
```

Save the file (`Ctrl+X`, `Y`, `Enter`).

### Verify It Works

Test from the command line:

```bash
systemctl reboot
```

The system should reboot immediately without asking for a password.

### Security Note

This rule allows **any user** on the system to reboot without authentication. For a dedicated display device, this is acceptable. For multi-user systems, you can restrict it to specific users:

```javascript
polkit.addRule(function(action, subject) {
    if ((action.id == "org.freedesktop.login1.reboot" ||
         action.id == "org.freedesktop.login1.reboot-multiple-sessions") &&
        subject.user == "pi") {
        return polkit.Result.YES;
    }
});
```

---

## HTTPS Certificate

The application automatically generates a self-signed HTTPS certificate on first run. This is required for:

- Camera streaming (browsers require HTTPS for `getUserMedia`)
- Secure remote access

The certificate is stored at:
```
/home/pi/verpixeld/server.pfx
```

### Browser Warning

When accessing the web interface via HTTPS, browsers will show a security warning because the certificate is self-signed. This is normal:

1. Click "Advanced" or "Show Details"
2. Click "Proceed to [IP address]" or "Accept the Risk"

### Regenerating the Certificate

If you need to regenerate the certificate (e.g., IP address changed):

```bash
rm /home/pi/verpixeld/server.pfx
sudo systemctl restart rgb-display
```

---

## Troubleshooting

### Service Won't Start

Check the logs:
```bash
sudo journalctl -u rgb-display -f
```

### Permission Denied for GPIO

Ensure the service runs with sudo or as root:
```ini
ExecStart=sudo dotnet verpixeld.dll
```

Or run as root directly:
```ini
User=root
ExecStart=/usr/bin/dotnet verpixeld.dll
```

### Reboot Button Doesn't Work

1. Verify the polkit rule exists:
   ```bash
   cat /etc/polkit-1/rules.d/10-allow-reboot.rules
   ```

2. Test from command line:
   ```bash
   systemctl reboot
   ```

3. Check the application logs for error messages

### Web Interface Not Accessible

1. Check if the service is running:
   ```bash
   sudo systemctl status rgb-display
   ```

2. Check if ports are open:
   ```bash
   sudo netstat -tlnp | grep -E "5000|5001"
   ```

3. Verify firewall allows connections:
   ```bash
   sudo ufw status
   ```

### Certificate Issues

If HTTPS doesn't work:

1. Check if certificate exists:
   ```bash
   ls -la /home/pi/verpixeld/server.pfx
   ```

2. Check logs for certificate errors:
   ```bash
   sudo journalctl -u rgb-display | grep -i cert
   ```

3. Regenerate:
   ```bash
   rm /home/pi/verpixeld/server.pfx
   sudo systemctl restart rgb-display
   ```

---

## Quick Reference

| Task | Command |
|------|---------|
| Start service | `sudo systemctl start rgb-display` |
| Stop service | `sudo systemctl stop rgb-display` |
| Restart service | `sudo systemctl restart rgb-display` |
| View logs | `sudo journalctl -u rgb-display -f` |
| Check status | `sudo systemctl status rgb-display` |
| Web interface (HTTP) | `http://<pi-ip>:5000` |
| Web interface (HTTPS) | `https://<pi-ip>:5001` |

---

## Files Reference

| File | Purpose |
|------|---------|
| `/etc/systemd/system/rgb-display.service` | Systemd service definition |
| `/etc/polkit-1/rules.d/10-allow-reboot.rules` | Allows passwordless reboot |
| `/home/pi/verpixeld/server.pfx` | HTTPS certificate |
| `/home/pi/verpixeld/appsettings.json` | Application configuration |

---

*Last updated: June 2026 (.NET 10)*
