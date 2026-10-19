# verpixeld — agent notes

.NET 10 LED-wall host. Sibling checkouts (same parent folder):

```
verpixeld/           this host (web UI, media, outputs, docker/)
CanvasManagement/    engine, extensions, filters, deploy.ps1
pixplane/            BGRA → bit-plane UDP (256×128 datagrams)
verpixeld-panel/     RP2350 + W6300 firmware
```

Do not flash firmware unless the user asks. Do not commit `docker/verpixeld-nas.tar`.

## Hosts

| Host | Output | Persist | Audio |
|------|--------|---------|-------|
| Raspberry Pi | GPIO HUB75, network, HDMI `/dev/fb0`, SPI | next to `verpixeld.dll` | Pulse/ALSA/Bluetooth |
| NAS Docker (`verpixeld:nas`) | **network only** | `/app/Config` overlay, plugins/fonts on `/app/Data` | none — never open ALSA `default` |
| Windows | simulation + preview | next to the DLL | none required |

Do not run the Pi host and the NAS container at the same time (both send UDP 7777).

Docker: after any change to the host (C#, wwwroot, docker) or plugins that ship in the image:

```powershell
D:\Development\RGB-Display\verpixeld\docker\build.ps1 -Tar
```

Import `docker/verpixeld-nas.tar` in Portainer (Images → Import), then recreate the container. Skip the rebuild only if the user says so. `-SkipVlc` leaves LibVLC out.

## Display / network wall

- One process, one `CanvasManager`, one `OutputRuntime`.
- A physical panel is always 256×128 PixPlane. The virtual wall is the bounding box of placed panels; the host crops 256×128 per panel.
- Same origin on every panel = clone. Gaps stay black. Overlap is allowed.
- `CanvasManager.Resize` can change the composite live. Overlay canvases (MediaPlayer, HA toast, voice, camera, startup) follow the wall. User canvases keep their size — stretch them in Studio.
- Settings → Network: type IPs (Docker cannot scan), drag tiles or Side by side / Stack. Saving writes `WallWidth`/`WallHeight` and `App.DisplayWidth`/`DisplayHeight`.

## Web UI

Plain ES in `verpixeld/wwwroot`. Bump `?v=` on every edited `js`/`css` in `index.html`. Studio is `layer-editor.js`. Media tab plays on a dedicated MediaPlayer overlay (not Main).

## Tests / deploy

```powershell
dotnet test D:\Development\RGB-Display\verpixeld\verpixeld.Tests\verpixeld.Tests.csproj
```

Pi bundle: `CanvasManagement/deploy.ps1` (linux-arm64). Host-only: `dotnet publish` from `verpixeld/verpixeld`.

## Do not

- Rewrite DI / introduce Serilog / split god classes unless the task is that file.
- Change PixPlane packing geometry.
- Auto-stretch existing Studio canvases when the wall grows.
