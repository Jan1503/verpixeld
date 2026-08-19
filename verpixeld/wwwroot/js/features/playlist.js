/**
 * Scene Playlist — rotate saved layouts on an interval with optional fade transitions.
 * Complements the time-of-day scheduler (which picks the active layout by time).
 */

window.playlistState = {
  config: { enabled: false, intervalSeconds: 15, loop: true, transition: 'Fade', layouts: [] },
  isRunning: false,
  currentLayout: null,
  available: [] // all saved layout names
};

async function openPlaylistDialog() {
  try {
    const [statusRes, savedRes] = await Promise.all([
      api.get('/api/playlist'),
      api.get('/api/layout/saved')
    ]);

    const data = statusRes.data || {};
    window.playlistState.config = Object.assign(
      { enabled: false, intervalSeconds: 15, loop: true, transition: 'Fade', layouts: [] },
      data.config || {}
    );
    window.playlistState.isRunning = !!data.isRunning;
    window.playlistState.currentLayout = data.currentLayout || null;
    window.playlistState.available = (savedRes.data || []).map(l => l.name).filter(Boolean);

    const html = `
  <div class="modal-overlay" id="playlist-modal">
    <div class="modal-content" style="max-width: 640px;">
      <div class="modal-header">
        <h2>🔁 Automation — Rotate Scenes</h2>
        <button class="modal-close" onclick="closePlaylistDialog()">✕</button>
      </div>
      <div class="modal-body">
        <p class="help-text" style="margin-top:0;">
          Cycles through the chosen scenes on a timer — great for rotating Home Assistant dashboards.
          For time-of-day switching instead, use the <strong>Schedule</strong> tab.
        </p>

        <div id="playlist-status" style="margin-bottom:14px;"></div>

        <div style="display:flex;gap:18px;align-items:flex-start;flex-wrap:wrap;margin-bottom:14px;">
          <label style="display:flex;flex-direction:column;gap:4px;font-size:13px;">
            Interval (seconds)
            <input type="number" id="playlist-interval" min="2" max="3600" style="width:110px;"
                   value="${window.playlistState.config.intervalSeconds}">
          </label>
          <label style="display:flex;flex-direction:column;gap:4px;font-size:13px;">
            Transition
            <select id="playlist-transition" style="width:140px;">
              <option value="Fade">Fade through black</option>
              <option value="Instant">Instant</option>
            </select>
          </label>
          <label class="checkbox-label" style="margin-top:22px;">
            <input type="checkbox" id="playlist-loop" ${window.playlistState.config.loop ? 'checked' : ''}>
            <span>Loop</span>
          </label>
        </div>

        <div style="display:flex;gap:16px;flex-wrap:wrap;">
          <div style="flex:1;min-width:240px;">
            <strong style="font-size:13px;">In playlist (in order)</strong>
            <div id="playlist-included" class="playlist-list"></div>
          </div>
          <div style="flex:1;min-width:240px;">
            <strong style="font-size:13px;">Available layouts</strong>
            <div id="playlist-available" class="playlist-list"></div>
          </div>
        </div>
      </div>
      <div class="modal-footer" style="justify-content:space-between;">
        <div style="display:flex;gap:8px;">
          <button class="btn btn-small btn-secondary" onclick="playlistControl('previous')">⏮ Prev</button>
          <button class="btn btn-small btn-secondary" onclick="playlistControl('next')">Next ⏭</button>
        </div>
        <div style="display:flex;gap:8px;">
          <button class="btn btn-secondary" onclick="playlistControl('stop')">Stop</button>
          <button class="btn btn-primary" onclick="savePlaylist(true)">Save &amp; Start</button>
          <button class="btn btn-secondary" onclick="savePlaylist(false)">Save (don't start)</button>
        </div>
      </div>
    </div>
  </div>`;

    document.body.insertAdjacentHTML('beforeend', html);
    ensurePlaylistStyles();
    document.getElementById('playlist-transition').value = window.playlistState.config.transition || 'Fade';
    renderPlaylistLists();
    renderPlaylistStatus();
  } catch (err) {
    console.error('openPlaylistDialog failed', err);
    showMessage('Failed to open playlist', 'error');
  }
}

function closePlaylistDialog() {
  const m = document.getElementById('playlist-modal');
  if (m) m.remove();
}

function renderPlaylistStatus() {
  const el = document.getElementById('playlist-status');
  if (!el) return;
  const s = window.playlistState;
  const running = s.isRunning
    ? `<span style="color:#2ecc71;">● Running</span>` + (s.currentLayout ? ` — showing <strong>${s.currentLayout}</strong>` : '')
    : `<span style="color:#888;">○ Stopped</span>`;
  el.innerHTML = `<div style="font-size:13px;">${running}</div>`;
}

function renderPlaylistLists() {
  const inc = window.playlistState.config.layouts;
  const incEl = document.getElementById('playlist-included');
  const availEl = document.getElementById('playlist-available');
  if (!incEl || !availEl) return;

  if (inc.length === 0) {
    incEl.innerHTML = `<div class="playlist-empty">No layouts yet — add some →</div>`;
  } else {
    incEl.innerHTML = inc.map((name, i) => `
      <div class="playlist-row">
        <span class="playlist-name" title="${name}">${i + 1}. ${name}</span>
        <span class="playlist-actions">
          <button class="btn-icon" title="Up" onclick="playlistMove(${i},-1)" ${i === 0 ? 'disabled' : ''}>▲</button>
          <button class="btn-icon" title="Down" onclick="playlistMove(${i},1)" ${i === inc.length - 1 ? 'disabled' : ''}>▼</button>
          <button class="btn-icon" title="Remove" onclick="playlistRemove(${i})">✕</button>
        </span>
      </div>`).join('');
  }

  const avail = window.playlistState.available.filter(n => !inc.includes(n));
  if (avail.length === 0) {
    availEl.innerHTML = `<div class="playlist-empty">All scenes are in the rotation.</div>`;
  } else {
    availEl.innerHTML = avail.map(name => `
      <div class="playlist-row">
        <span class="playlist-name" title="${name}">${name}</span>
        <span class="playlist-actions">
          <button class="btn-icon" title="Add" onclick="playlistAdd('${encodeURIComponent(name)}')">＋</button>
        </span>
      </div>`).join('');
  }
}

function playlistAdd(encName) {
  const name = decodeURIComponent(encName);
  if (!window.playlistState.config.layouts.includes(name)) {
    window.playlistState.config.layouts.push(name);
    renderPlaylistLists();
  }
}

function playlistRemove(i) {
  window.playlistState.config.layouts.splice(i, 1);
  renderPlaylistLists();
}

function playlistMove(i, dir) {
  const list = window.playlistState.config.layouts;
  const j = i + dir;
  if (j < 0 || j >= list.length) return;
  [list[i], list[j]] = [list[j], list[i]];
  renderPlaylistLists();
}

function gatherPlaylistConfig(enabled) {
  const interval = parseInt(document.getElementById('playlist-interval').value, 10);
  return {
    enabled: !!enabled,
    intervalSeconds: isNaN(interval) ? 15 : Math.max(2, interval),
    loop: document.getElementById('playlist-loop').checked,
    transition: document.getElementById('playlist-transition').value || 'Fade',
    layouts: window.playlistState.config.layouts.slice()
  };
}

async function savePlaylist(start) {
  const cfg = gatherPlaylistConfig(start);
  if (start && cfg.layouts.length === 0) {
    showMessage('Add at least one layout first', 'error');
    return;
  }
  try {
    const res = await api.post('/api/playlist/configure', cfg);
    window.playlistState.config = (res.data && res.data.config) || cfg;
    window.playlistState.isRunning = !!(res.data && res.data.isRunning);
    renderPlaylistStatus();
    showMessage(start ? 'Playlist started' : 'Playlist saved', 'success');
  } catch (err) {
    console.error('savePlaylist failed', err);
    showMessage('Failed to save playlist', 'error');
  }
}

async function playlistControl(action) {
  try {
    await api.post('/api/playlist/' + action, {});
    if (action === 'stop') window.playlistState.isRunning = false;
    if (action === 'next' || action === 'previous') window.playlistState.isRunning = true;
    // Refresh status shortly after (the swap is async on the server).
    setTimeout(async () => {
      try {
        const r = await api.get('/api/playlist');
        window.playlistState.isRunning = !!r.data.isRunning;
        window.playlistState.currentLayout = r.data.currentLayout || null;
        renderPlaylistStatus();
      } catch (_) { /* ignore */ }
    }, 600);
    renderPlaylistStatus();
  } catch (err) {
    console.error('playlistControl failed', err);
    showMessage('Playlist action failed', 'error');
  }
}

function ensurePlaylistStyles() {
  if (document.getElementById('playlist-styles')) return;
  const css = `
    .playlist-list { margin-top:6px; border:1px solid var(--border-color,#333); border-radius:8px;
      max-height:240px; overflow-y:auto; background:var(--bg-secondary,rgba(0,0,0,0.2)); }
    .playlist-row { display:flex; align-items:center; justify-content:space-between; gap:8px;
      padding:6px 10px; border-bottom:1px solid var(--border-color,#2a2a2a); font-size:13px; }
    .playlist-row:last-child { border-bottom:none; }
    .playlist-name { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
    .playlist-actions { display:flex; gap:4px; flex-shrink:0; }
    .playlist-actions .btn-icon { background:transparent; border:1px solid var(--border-color,#444);
      color:inherit; border-radius:6px; width:26px; height:26px; cursor:pointer; line-height:1; }
    .playlist-actions .btn-icon:hover:not(:disabled) { background:rgba(255,255,255,0.08); }
    .playlist-actions .btn-icon:disabled { opacity:0.3; cursor:default; }
    .playlist-empty { padding:14px 10px; font-size:12px; color:#888; }
  `;
  const style = document.createElement('style');
  style.id = 'playlist-styles';
  style.textContent = css;
  document.head.appendChild(style);
}

window.openPlaylistDialog = openPlaylistDialog;
window.closePlaylistDialog = closePlaylistDialog;
window.playlistAdd = playlistAdd;
window.playlistRemove = playlistRemove;
window.playlistMove = playlistMove;
window.savePlaylist = savePlaylist;
window.playlistControl = playlistControl;
