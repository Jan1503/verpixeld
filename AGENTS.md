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

Plain ES in `verpixeld/wwwroot`. Do not add `?v=` to script/style URLs: the host serves index.html with a content hash on every local `.js`/`.css` (`AssetVersioning`), so a changed file is fetched at once and unchanged ones stay cached. Text that is not ours (canvas, scene, file, device, station names; titles) goes into HTML only through `escapeHtml` (helpers.js; it also escapes quotes, so attributes are safe). No inline handlers (`onclick=`, `oninput=` …), neither in index.html nor in JS templates: controls carry a marker (`data-action`, `data-input`, `data-change`, `data-keydown`, `data-submit`, `data-pointerdown`) and values in `data-*`, and the owning feature file wires them with `wire(container, { click: { name: (el, event) => … }, … })` or, for a rendered list or dialog, `onAction(container, { name: (dataset, el, event) => … })` (helpers.js; both delegate, so re-rendered content needs no rebinding, and they bind once per container). `dataAttrs({...})` / `markerAttrs(event, { action, ...data })` build escaped marker attributes in templates. Under delegation `return false` does nothing (call `event.preventDefault()`), and a direct listener on an ancestor must not `stopPropagation()` clicks it does not own. Every script is an ES module: index.html loads `js/main.js` (plus the classic `ui/splash.js`, which must paint first), main.js imports the modules in start order, and each module imports what it uses (`import { api } from '../core/api.js'`). Never add another `<script>` for a module (a module loaded once with `?v=` and once through an import runs twice). Imports carry no hash: the host serves unversioned JS/CSS with `Cache-Control: no-cache`. Base modules (`constants.js`, `core/api.js`, `utils/helpers.js`, `ui/toast.js`, `ui/modal.js`) import nothing from features, so they are evaluated first; start-up code runs through `onReady(...)`, which waits for DOMContentLoaded, so no module touches another's constants while the graph is still being evaluated. Names stay off `window`; the few `window.X` left (`api`, `toast`, `mediaState` …) are read that way on purpose. Check with `deno lint` and the rules `no-undef`, `no-import-assign`, `no-redeclare` (browser globals excepted) after moving code between files. An element moved out of its container at runtime (the Studio dropdowns go to `<body>` while open) needs its own `wire`, the container's delegation no longer sees it. Never set `el.onclick = …` on a marked control (it runs in addition to the wired handler). No browser dialogs: `showConfirm({...})` and `showPrompt({...})` (modal.js) answer once and close only themselves, so they work on top of another dialog. Studio is `layer-editor.js`. Media tab plays on a dedicated MediaPlayer overlay (not Main).

## Logging

`ILogger<T>` by constructor injection. `HostLoggerProvider` prints `[TAG] message` (warnings/errors marked) and feeds the web log (level filter). The tag is the class name without Service/Endpoints/Manager; keep an established tag with `[LogTag("ROTATE")]`. Message templates, not interpolation: `_log.LogWarning("Seek failed: {Message}", ex.Message)`. Errors are `LogError`, recoverable trouble `LogWarning`. A class that creates logged children takes `ILoggerFactory` and passes `loggers.CreateLogger<Child>()`. Static endpoint classes set a `private static ILogger Log` in their `Map…` method. `Console.WriteLine` only where no logger exists yet (startup before the host is built, static helpers); the web log guesses its level from the wording.

## Tests / deploy

```powershell
dotnet test D:\Development\RGB-Display\verpixeld\verpixeld.Tests\verpixeld.Tests.csproj
```

Pi bundle: `CanvasManagement/deploy.ps1` (linux-arm64). Host-only: `dotnet publish` from `verpixeld/verpixeld`.

## Do not

- Rewrite DI / introduce Serilog / split god classes unless the task is that file.
- Change PixPlane packing geometry.
- Auto-stretch existing Studio canvases when the wall grows.
