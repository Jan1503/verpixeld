# Multi-network-panel notes (future)

Saved 2026-08-22 so we do not re-derive this when a second panel exists.
**Not implemented.** One 256×128 panel is the current product.

PixPlane today is 1:1 unicast UDP 7777 (`frameId` / `fragIdx` / `nThis` / flags), discovery UDP 7778 (`VPXD`) already returns a **list**, HTTP `/cmd` + `/status` per panel. No timestamps, ACK, or network vsync. Geometry and packer are hardcoded 256×128. Seam correction is host-side, per wall, not in the protocol.

## Do not rebuild the protocol

Multi-panel is **host orchestration** plus optional small header fields later. Firmware can stay 256×128.

| Goal | Approach |
|------|----------|
| **Clone** | Same 256×128 on N panels. N unicast streamers or one multicast of the same packets. |
| **Tiled wall** | Host composes a virtual size, crops 256×128 per tile, packs bit-planes, sends **different** streams. Optional `panelId` / tile offset in the datagram is cosmetic. |

Discovery, `livemode` 8/14, identify, bind-by-chip-id stay as they are (per panel).

## Sync is staged — “absolute” is not UDP

Each panel free-runs `acquireFront` / `swapBuffers`. The ICND `send_vsync()` is chip-local. Two controllers will drift.

1. **Same frame, loose** — send frame *N* to everyone; each swaps when its last commit chunk arrives. Fine for clocks/dashboards. Video tears at the joint (14-bit double-buffer RX stall already jitters a single panel).
2. **Shared present time** (best software step) — send all tiles without display swap, then multicast `COMMIT frameId, t_present`. Panels hold the fill buffer and swap at *t*. Shared clock: NTP is ~1–5 ms (a few scan frames). Hardware PTP/802.1AS is unlikely on W6300. Stops mixed *N* / *N+1*.
3. **Hardware genlock** — master vsync GPIO or last-microsecond “swap now”. Scanlines phase-locked. Sender-card territory; only if the seam bothers on video.

`frameId` is 8-bit and not a clock. Without (2) or (3) the panels are independent clocks.

## Host work when we actually need it

- Panel list instead of one `Host` / `PanelId`: chip-id, IP, tile `(x,y)`, optional per-panel seam curve.
- One composer at virtual size → N crops → N `PanelStreamer`s (or one with fan-out).
- **One** pacing budget for the sum: ~19 Mbit/s × N. Two 14-bit keyframes at once saturate a Pi uplink — serialize tiles, share COMMIT, or stagger keyframes.
- Do not change PixPlane until a second panel is on the desk.

## Suggested build order

1. Two streamers, identical full frames (clone) — proves discovery and network.
2. Virtual canvas + crop (real tiles); swap still “when complete”.
3. COMMIT + coarse clock if motion shows the joint.
4. Genlock only for a seamless video wall.

Scan-home columns 63/127/191/255 remain a **single-controller ICND** artefact, not the gap between two MCUs. Seam LUTs stay host-side **per panel**.
