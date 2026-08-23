/* ============================================================================
   LAYER EDITOR - drag & drop / resize canvases live over the display preview
   ============================================================================ */

const layerEditor = {
  open: false,
  dispW: 128,
  dispH: 128,
  scale: 4,
  canvases: [],
  contentMap: {},
  selected: null,
  drag: null,          // { name, mode, startX, startY, orig:{x,y,w,h} }
  lastSent: 0,
  sending: false,
  grid: 4,
  snap: true,
  drawMode: false,     // when true, dragging on the stage draws a NEW canvas
  draw: null           // { sx, sy, cx, cy } in display pixels while drawing
};

// Unified pointer position for mouse + touch.
function lePoint(ev) {
  if (ev.touches && ev.touches.length) return { x: ev.touches[0].clientX, y: ev.touches[0].clientY };
  if (ev.changedTouches && ev.changedTouches.length) return { x: ev.changedTouches[0].clientX, y: ev.changedTouches[0].clientY };
  return { x: ev.clientX, y: ev.clientY };
}

const LE_SYS = ['HaToast', 'VoiceFeedback', 'CameraAlert'];
const LE_STD = ['Main', 'Header', 'Content', 'Footer', 'Left', 'Right',
  'TopLeft', 'TopRight', 'BottomLeft', 'BottomRight'];

let _studioMounted = false;
let _studioPoll = null;
let _studioFp = '';

function buildStudioBodyHtml() {
  return `
    <div class="le-studio">
      <div class="le-studio-header">
        <h2 style="margin:0;font-size:1rem;">🎛️ Studio</h2>
        <span class="le-scene" id="le-scene"></span>
        <span class="le-rot-note" id="le-rot-note"></span>
        <span style="flex:1"></span>
        <span class="le-menu">
          <button class="btn btn-small btn-secondary" onclick="leToggleScenes(event)">📋 Saved Scenes ▾</button>
          <div class="le-dropdown le-dropdown-right" id="le-scenes-menu" hidden></div>
        </span>
        <button class="btn btn-small btn-secondary" onclick="leSaveScene()">💾 Save Scene</button>
        <button class="btn btn-small btn-secondary" onclick="openPlaylistDialog()">🔁 Automation</button>
      </div>
      <p class="le-hint" id="le-first-run" style="margin:0 0 10px;">Pick a canvas on the left, assign content on the right, save as a Scene above. Automation rotates scenes; clock times are under Schedule.</p>
      <div class="le-studio-body">
        <aside class="le-pane le-layers" id="le-layers"></aside>
        <div class="le-center">
          <div class="le-toolbar">
            <button class="btn btn-small btn-primary" onclick="layerEditorAdd()">+ Add</button>
            <span class="le-menu">
              <button class="btn btn-small btn-secondary" onclick="leToggleNew(event)">✨ New ▾</button>
              <div class="le-dropdown" id="le-new-menu" hidden>
                <button onclick="leApplyProfile('fullscreen')">Blank (Full Screen)</button>
                <button onclick="leApplyProfile('headercontent')">Header + Content</button>
                <button onclick="leApplyProfile('threepanel')">Three Panel</button>
                <button onclick="leApplyProfile('splitview')">Split View</button>
                <button onclick="leApplyProfile('dashboard')">Dashboard 2×2</button>
              </div>
            </span>
            <button class="btn btn-small btn-secondary" id="le-draw-btn" onclick="layerEditorToggleDraw()">Draw</button>
            <label class="le-snap"><input type="checkbox" id="le-snap" ${layerEditor.snap ? 'checked' : ''} onchange="layerEditor.snap=this.checked"> snap ${layerEditor.grid}px</label>
            <span class="le-readout" id="le-readout"></span>
          </div>
          <div class="le-stage-wrap">
            <div class="le-stage" id="le-stage">
              <img id="le-stream" class="le-stream" alt="preview" draggable="false" />
            </div>
          </div>
        </div>
        <aside class="le-pane le-inspector" id="le-inspector">
          <p class="le-hint">Select a canvas to edit it.</p>
        </aside>
      </div>
    </div>`;
}

// Mount the Studio into its tab panel (called when the Studio tab is shown).
async function mountStudio() {
  if (_studioMounted) return;
  const panel = document.getElementById('tab-studio');
  if (!panel) return;
  ensureLayerEditorStyles();
  try {
    const disp = await window.api.get('/api/canvas/display');
    if (disp?.data?.width > 0) { layerEditor.dispW = disp.data.width; layerEditor.dispH = disp.data.height; }
  } catch (e) { /* defaults */ }

  panel.innerHTML = buildStudioBodyHtml();
  _studioMounted = true;
  layerEditor.open = true;

  // Fit the stage between the Layers (left) and Inspector (right) panes.
  const avail = Math.max(160, (panel.clientWidth || 900) - 580);
  const maxH = Math.min(window.innerHeight - 260, 640);
  layerEditor.scale = Math.max(1, Math.floor(Math.min(avail / layerEditor.dispW, maxH / layerEditor.dispH)));
  const stage = document.getElementById('le-stage');
  if (stage) {
    stage.style.width = (layerEditor.dispW * layerEditor.scale) + 'px';
    stage.style.height = (layerEditor.dispH * layerEditor.scale) + 'px';
  }

  const img = document.getElementById('le-stream');
  if (img) img.src = `${typeof API_BASE !== 'undefined' ? API_BASE : ''}/api/preview/stream?t=${Date.now()}`;

  // Pause any running rotation so timers don't wipe edits while the Studio is open.
  leSuspendRotation();
  await leLoadSchemas();
  leLoadScene();

  await refreshLayerEditor();
  leFitStage();
  if (_studioPoll) clearInterval(_studioPoll);
  _studioPoll = setInterval(lePollLayout, 1500);
  document.addEventListener('mousemove', leOnMove);
  document.addEventListener('mouseup', leOnUp);
  document.addEventListener('touchmove', leOnMove, { passive: false });
  document.addEventListener('touchend', leOnUp);
  document.addEventListener('touchcancel', leOnUp);
  document.addEventListener('click', leCloseMenus);
  window.addEventListener('resize', leFitStage);
}

function leFitStage() {
  const panel = document.getElementById('tab-studio');
  const stage = document.getElementById('le-stage');
  if (!panel || !stage || !layerEditor.open) return;
  const avail = Math.max(160, (panel.clientWidth || 900) - 580);
  const maxH = Math.min(window.innerHeight - 260, 720);
  const next = Math.max(1, Math.floor(Math.min(avail / layerEditor.dispW, maxH / layerEditor.dispH)));
  if (next === layerEditor.scale && stage.style.width) return;
  layerEditor.scale = next;
  stage.style.width = (layerEditor.dispW * layerEditor.scale) + 'px';
  stage.style.height = (layerEditor.dispH * layerEditor.scale) + 'px';
  renderLayerBoxes();
}

function leLayoutFingerprint(canvases, contentMap) {
  return (canvases || []).map(c =>
    [c.name, c.x, c.y, c.width, c.height, c.zOrder, c.isVisible === false ? 0 : 1,
      (contentMap && contentMap[c.name] && contentMap[c.name].extensionName) || ''].join('|')
  ).join(';');
}

async function lePollLayout() {
  if (!layerEditor.open) return;
  try {
    const [stack, content] = await Promise.all([
      window.api.get('/api/canvas/stack'),
      window.api.get('/api/layout/content')
    ]);
    const canvases = stack?.data || [];
    const contentMap = {};
    (content?.data?.contents || []).forEach(c => { contentMap[c.canvasName] = c; });
    const fp = leLayoutFingerprint(canvases, contentMap);
    if (fp === _studioFp) return;
    await refreshLayerEditor({ skipInspector: true });
  } catch (e) { /* intro / not ready yet */ }
}

function unmountStudio() {
  if (!_studioMounted) return;
  if (_studioPoll) { clearInterval(_studioPoll); _studioPoll = null; }
  _studioFp = '';
  document.removeEventListener('mousemove', leOnMove);
  document.removeEventListener('mouseup', leOnUp);
  document.removeEventListener('touchmove', leOnMove);
  document.removeEventListener('touchend', leOnUp);
  document.removeEventListener('touchcancel', leOnUp);
  document.removeEventListener('click', leCloseMenus);
  window.removeEventListener('resize', leFitStage);
  layerEditor.drawMode = false;
  layerEditor.draw = null;
  leResumeRotation();
  const img = document.getElementById('le-stream');
  if (img) img.src = '';
  const panel = document.getElementById('tab-studio');
  if (panel) panel.innerHTML = '';
  _studioMounted = false;
  layerEditor.open = false;
  if (typeof loadCanvasStack === 'function') loadCanvasStack();
}

// Studio is a real tab: mount on enter, unmount on leave.
window.addEventListener('tabChanged', (e) => {
  const t = e.detail && e.detail.tab;
  if (t === 'studio') mountStudio();
  else unmountStudio();
});

// Back-compat: any old caller just navigates to the Studio tab.
function openLayerEditor() {
  if (typeof switchTab === 'function') switchTab('studio');
}

// Suspend rotation while editing; show a badge if something was actually running.
async function leSuspendRotation() {
  try {
    const [pl, rot] = await Promise.all([
      window.api.post('/api/playlist/suspend', {}),
      window.api.post('/api/rotations/suspend-all', {})
    ]);
    const running = (pl?.data?.wasRunning) || (rot?.data?.wasRunning);
    const note = document.getElementById('le-rot-note');
    if (note && running) note.textContent = '⏸ Rotation paused while editing (resumes on close)';
  } catch (e) { /* best-effort */ }
}

function leResumeRotation() {
  // Fire-and-forget; the editor is closing.
  try { window.api.post('/api/playlist/resume', {}); } catch (e) { /* ignore */ }
  try { window.api.post('/api/rotations/resume-all', {}); } catch (e) { /* ignore */ }
}

function closeLayerEditor() {
  // Back-compat alias — the Studio now lives in a tab; leave by switching away.
  if (typeof switchTab === 'function') switchTab('studio');
  else unmountStudio();
}

async function refreshLayerEditor(opts) {
  try {
    const [stack, content] = await Promise.all([
      window.api.get('/api/canvas/stack'),
      window.api.get('/api/layout/content')
    ]);
    const prevSel = layerEditor.selected;
    layerEditor.canvases = stack?.data || [];
    layerEditor.contentMap = {};
    (content?.data?.contents || []).forEach(c => { layerEditor.contentMap[c.canvasName] = c; });
    const selGone = !layerEditor.canvases.some(c => c.name === layerEditor.selected);
    if (selGone)
      layerEditor.selected = layerEditor.canvases[0]?.name || null;
    _studioFp = leLayoutFingerprint(layerEditor.canvases, layerEditor.contentMap);
    const skipInspector = !!(opts && opts.skipInspector) && !selGone && prevSel === layerEditor.selected;
    renderLayerBoxes(skipInspector);
    // Let the rest of the UI (Canvas/Media/AI/Draw/Visualizer selectors) pick up added/removed/renamed/
    // resized canvases without a page refresh.
    window.dispatchEvent(new CustomEvent('layoutChanged'));
  } catch (e) {
    console.error('Layer editor refresh failed:', e);
  }
}

function renderLayerBoxes(skipInspector) {
  const stage = document.getElementById('le-stage');
  if (!stage) return;
  stage.querySelectorAll('.le-box').forEach(b => b.remove());

  const s = layerEditor.scale;
  const sorted = [...layerEditor.canvases].sort((a, b) => a.zOrder - b.zOrder);
  for (const c of sorted) {
    const content = layerEditor.contentMap[c.name];
    const label = content ? content.extensionName : '(empty)';
    const sel = layerEditor.selected === c.name;
    const hidden = c.isVisible === false;
    const box = document.createElement('div');
    box.className = 'le-box' + (sel ? ' selected' : '') + (hidden ? ' hidden' : '');
    box.dataset.name = c.name;
    box.style.left = (c.x * s) + 'px';
    box.style.top = (c.y * s) + 'px';
    box.style.width = (c.width * s) + 'px';
    box.style.height = (c.height * s) + 'px';
    // The SELECTED box is raised to the top for interaction only (does NOT change the real display
    // z-order) so a fully-covered canvas can still be grabbed once selected via the chip bar.
    box.style.zIndex = sel ? 1000 : (10 + c.zOrder);
    box.innerHTML =
      `<div class="le-box-label">${c.name} · ${label}<br><span class="le-box-dims">${c.width}×${c.height}</span></div>` +
      ['nw', 'ne', 'sw', 'se', 'n', 's', 'e', 'w'].map(h => `<div class="le-h le-h-${h}" data-h="${h}"></div>`).join('');
    box.addEventListener('mousedown', (ev) => leOnDown(ev, c));
    box.addEventListener('touchstart', (ev) => leOnDown(ev, c), { passive: false });
    stage.appendChild(box);
  }
  renderLayers();
  if (!skipInspector) renderInspector();
  updateReadout();
}

// LEFT PANE — every canvas as a selectable layer row (replaces the old chip bar), top = front.
function renderLayers() {
  const pane = document.getElementById('le-layers');
  if (!pane) return;
  const byZ = [...layerEditor.canvases].sort((a, b) => b.zOrder - a.zOrder);
  pane.innerHTML =
    `<div class="le-pane-title">Layers</div>` +
    byZ.map(c => {
      const sel = layerEditor.selected === c.name ? ' selected' : '';
      const hidden = c.isVisible === false;
      const content = layerEditor.contentMap[c.name];
      const label = (LE_SYS.includes(c.name) || c.isSystem)
        ? 'host overlay'
        : (content ? content.extensionName : '(empty)');
      const nm = c.name.replace(/'/g, "\\'");
      const icon = leExtIcon(label === '(empty)' ? '' : label);
      return `<div class="le-layer${sel}${hidden ? ' is-hidden' : ''}" onclick="selectLayerCanvas('${nm}')" title="z:${c.zOrder} · ${c.width}×${c.height}">
        ${icon}
        <div class="le-layer-main"><span class="le-layer-name">${c.name}</span><span class="le-layer-ext">${label}</span></div>
        <button class="le-eye" title="${hidden ? 'Show' : 'Hide'}" onclick="layerEditorToggleVisible('${nm}', event)">${hidden ? '○' : '●'}</button>
        <span class="le-layer-z">z${c.zOrder}</span>
      </div>`;
    }).join('');
}

// RIGHT PANE — inspector for the selected canvas: Transform, Appearance, Content.
function renderInspector() {
  const pane = document.getElementById('le-inspector');
  if (!pane) return;
  const c = layerEditor.canvases.find(x => x.name === layerEditor.selected);
  if (!c) {
    pane.innerHTML = '<p class="le-hint">Select a canvas to edit it.</p>';
    return;
  }
  const pct = Math.round((c.opacity ?? 1) * 100);
  const isStd = LE_STD.includes(c.name);
  const isSys = LE_SYS.includes(c.name) || c.isSystem;
  pane.innerHTML = `
    <div class="le-insp-head">
      <strong class="le-insp-name">${c.name}</strong>
      <span class="le-insp-actions">
        ${isStd || isSys ? '' : `<button class="le-ico" title="Rename" onclick="layerEditorRename()">✎</button>`}
        ${isStd || isSys ? '' : `<button class="le-ico le-ico-danger" title="Remove canvas" onclick="layerEditorRemove()">🗑</button>`}
      </span>
    </div>

    <div class="le-section">
      <div class="le-section-title">Transform</div>
      <div class="le-grid4">
        <label><span class="le-axis">X</span><input type="number" id="le-x" value="${c.x}" onchange="leApplyTransform()"></label>
        <label><span class="le-axis">Y</span><input type="number" id="le-y" value="${c.y}" onchange="leApplyTransform()"></label>
        <label><span class="le-axis">W</span><input type="number" id="le-w" value="${c.width}" onchange="leApplyTransform()"></label>
        <label><span class="le-axis">H</span><input type="number" id="le-h" value="${c.height}" onchange="leApplyTransform()"></label>
      </div>
      <p class="le-hint">Or drag / resize on the stage.</p>
      <div class="le-align">
        <span class="le-sel-lbl">Align</span>
        <button class="le-preset" title="Left" onclick="layerEditorAlign('left')">⟸</button>
        <button class="le-preset" title="Center H" onclick="layerEditorAlign('hcenter')">⬌</button>
        <button class="le-preset" title="Right" onclick="layerEditorAlign('right')">⟹</button>
        <button class="le-preset" title="Top" onclick="layerEditorAlign('top')">⇑</button>
        <button class="le-preset" title="Center V" onclick="layerEditorAlign('vcenter')">⬍</button>
        <button class="le-preset" title="Bottom" onclick="layerEditorAlign('bottom')">⇓</button>
      </div>
      <div class="le-align">
        <span class="le-sel-lbl">Fit</span>
        <button class="le-preset" onclick="layerEditorFit('full')">Full</button>
        <button class="le-preset" onclick="layerEditorFit('top')">Top</button>
        <button class="le-preset" onclick="layerEditorFit('bottom')">Bottom</button>
        <button class="le-preset" onclick="layerEditorFit('center')">Center</button>
        <button class="le-preset" onclick="layerEditorFit('corner')">Corner</button>
      </div>
    </div>

    <div class="le-section">
      <div class="le-section-title">Appearance</div>
      <div class="le-row">
        <span class="le-sel-lbl">Opacity</span>
        <input type="range" id="le-opacity" min="0" max="100" value="${pct}" oninput="layerEditorSetOpacity(this.value)">
        <span id="le-opacity-val" class="le-sel-lbl">${pct}%</span>
      </div>
      <div class="le-row">
        <span class="le-sel-lbl">Panel depth</span>
        <select id="le-bits" onchange="layerEditorSetPanelBits(this.value)">
          <option value="14" ${(c.panelColorBits ?? 14) !== 8 ? 'selected' : ''}>14-bit (video)</option>
          <option value="8" ${(c.panelColorBits ?? 14) === 8 ? 'selected' : ''}>8-bit (fast)</option>
        </select>
      </div>
      <p class="le-hint">Network wall only. The panel uses the highest depth of visible canvases; HDMI / SPI / GPIO / simulation ignore this.</p>
      <div class="le-row">
        <span class="le-sel-lbl">Z-order ${c.zOrder}</span>
        <button class="le-ico" onclick="layerEditorZ('up')" title="Bring forward">▲</button>
        <button class="le-ico" onclick="layerEditorZ('down')" title="Send back">▼</button>
      </div>
      <div class="le-row">
        <label class="le-sel-lbl" title="Transparent background reveals the layer beneath (the extension must use a transparent background colour)">
          <input type="checkbox" ${c.transparentBackground ? 'checked' : ''} onchange="layerEditorSetTransparent(this.checked)"> Transparent background
        </label>
      </div>
    </div>

    <div class="le-section">
      <div class="le-section-title">Content</div>
      <div id="le-content">${isSys
        ? '<p class="le-hint">Host overlay — you can move it on the stage. Assign clocks, media, draw or AI to another canvas.</p>'
        : '<p class="le-hint">Loading…</p>'}</div>
    </div>`;
  if (!isSys) loadContentSection(c.name);
}

function selectLayerCanvas(name) {
  layerEditor.selected = name;
  renderLayerBoxes();
}

function updateReadout() {
  const r = document.getElementById('le-readout');
  if (!r) return;
  const c = layerEditor.canvases.find(x => x.name === layerEditor.selected);
  r.textContent = c ? `${c.x},${c.y} · ${c.width}×${c.height}` : `${layerEditor.dispW}×${layerEditor.dispH} display`;
}

function leOnDown(ev, c) {
  ev.preventDefault();
  ev.stopPropagation();
  const pt = lePoint(ev);
  layerEditor.selected = c.name;
  const mode = (ev.target.classList && ev.target.classList.contains('le-h')) ? ev.target.dataset.h : 'move';
  layerEditor.drag = {
    name: c.name,
    mode,
    startX: pt.x,
    startY: pt.y,
    orig: { x: c.x, y: c.y, w: c.width, h: c.height }
  };
  renderLayerBoxes();
}

function leSnap(v) {
  if (!layerEditor.snap) return Math.round(v);
  return Math.round(v / layerEditor.grid) * layerEditor.grid;
}

function leComputeGeometry(d, dx, dy) {
  const minS = layerEditor.snap ? layerEditor.grid : 4;
  const dispW = layerEditor.dispW, dispH = layerEditor.dispH;
  const m = d.mode;

  if (m === 'move') {
    // Size is fixed; only the position shifts (clamped to keep the canvas fully on-screen).
    let x = leSnap(d.orig.x + dx);
    let y = leSnap(d.orig.y + dy);
    x = Math.max(0, Math.min(x, dispW - d.orig.w));
    y = Math.max(0, Math.min(y, dispH - d.orig.h));
    return { x, y, width: d.orig.w, height: d.orig.h };
  }

  // Resize: move ONLY the dragged edges; the opposite edges stay anchored (fixes the box growing the
  // wrong way when a dragged edge hits the display border).
  let left = d.orig.x, top = d.orig.y;
  let right = d.orig.x + d.orig.w, bottom = d.orig.y + d.orig.h;
  if (m.includes('e')) right = d.orig.x + d.orig.w + dx;
  if (m.includes('w')) left = d.orig.x + dx;
  if (m.includes('s')) bottom = d.orig.y + d.orig.h + dy;
  if (m.includes('n')) top = d.orig.y + dy;

  left = leSnap(left); right = leSnap(right);
  top = leSnap(top); bottom = leSnap(bottom);

  left = Math.max(0, left); top = Math.max(0, top);
  right = Math.min(dispW, right); bottom = Math.min(dispH, bottom);

  if (right - left < minS) { if (m.includes('w')) left = Math.max(0, right - minS); else right = Math.min(dispW, left + minS); }
  if (bottom - top < minS) { if (m.includes('n')) top = Math.max(0, bottom - minS); else bottom = Math.min(dispH, top + minS); }

  return { x: left, y: top, width: Math.max(minS, right - left), height: Math.max(minS, bottom - top) };
}

function leOnMove(ev) {
  // Draw-new-canvas mode takes precedence over box dragging.
  if (layerEditor.draw) {
    if (ev.type === 'touchmove' && ev.cancelable) ev.preventDefault();
    const p = leStageCoords(lePoint(ev));
    layerEditor.draw.cx = p.x;
    layerEditor.draw.cy = p.y;
    renderDrawBox();
    return;
  }

  const d = layerEditor.drag;
  if (!d) return;
  if (ev.type === 'touchmove' && ev.cancelable) ev.preventDefault();
  const s = layerEditor.scale;
  const pt = lePoint(ev);
  const dx = Math.round((pt.x - d.startX) / s);
  const dy = Math.round((pt.y - d.startY) / s);
  const g = leComputeGeometry(d, dx, dy);

  // Live visual update.
  const box = document.querySelector(`.le-box[data-name="${CSS.escape(d.name)}"]`);
  if (box) {
    box.style.left = (g.x * s) + 'px';
    box.style.top = (g.y * s) + 'px';
    box.style.width = (g.width * s) + 'px';
    box.style.height = (g.height * s) + 'px';
    const dimsEl = box.querySelector('.le-box-dims');
    if (dimsEl) dimsEl.textContent = `${g.width}×${g.height}`;
  }
  const rec = layerEditor.canvases.find(c => c.name === d.name);
  if (rec) { rec.x = g.x; rec.y = g.y; rec.width = g.width; rec.height = g.height; }
  updateReadout();

  // Live-send only MOVES (cheap reposition, no restart). Resizes recreate the canvas and restart the
  // extension, so commit them ONCE on release to avoid churn, flicker and overlapping requests.
  if (d.mode === 'move') {
    const now = performance.now();
    if (now - layerEditor.lastSent > 90) {
      layerEditor.lastSent = now;
      leSendBounds(d.name, g, false);
    }
  }
}

async function leOnUp() {
  // Finalize a draw-to-create gesture.
  if (layerEditor.draw) {
    const dr = layerEditor.draw;
    layerEditor.draw = null;
    let x = Math.min(dr.sx, dr.cx), y = Math.min(dr.sy, dr.cy);
    let w = Math.abs(dr.cx - dr.sx), h = Math.abs(dr.cy - dr.sy);
    if (layerEditor.snap) {
      const g = layerEditor.grid;
      x = Math.round(x / g) * g; y = Math.round(y / g) * g;
      w = Math.round(w / g) * g; h = Math.round(h / g) * g;
    }
    removeDrawBox();
    layerEditorToggleDraw(false);
    if (w >= layerEditor.grid && h >= layerEditor.grid) {
      await leCreateCanvas({ x, y, width: w, height: h });
    }
    return;
  }

  const d = layerEditor.drag;
  if (!d) return;
  layerEditor.drag = null;
  const rec = layerEditor.canvases.find(c => c.name === d.name);
  if (rec) await leSendBounds(d.name, { x: rec.x, y: rec.y, width: rec.width, height: rec.height }, true);
  // A resize recreates content; pull fresh truth so labels/dims are accurate.
  await refreshLayerEditor();
}

/* ----- Draw-to-create / presets (absorbed from the old "Create Overlay Canvas" control) ----- */

// Map a client point to display pixels within the stage.
function leStageCoords(pt) {
  const stage = document.getElementById('le-stage');
  if (!stage) return { x: 0, y: 0 };
  const r = stage.getBoundingClientRect();
  let x = (pt.x - r.left) / layerEditor.scale;
  let y = (pt.y - r.top) / layerEditor.scale;
  x = Math.max(0, Math.min(layerEditor.dispW, x));
  y = Math.max(0, Math.min(layerEditor.dispH, y));
  return { x, y };
}

function layerEditorToggleDraw(force) {
  const want = (typeof force === 'boolean') ? force : !layerEditor.drawMode;
  layerEditor.drawMode = want;
  const btn = document.getElementById('le-draw-btn');
  const stage = document.getElementById('le-stage');
  let ov = document.getElementById('le-drawlayer');
  if (want) {
    if (stage && !ov) {
      ov = document.createElement('div');
      ov.id = 'le-drawlayer';
      ov.className = 'le-drawlayer';
      ov.addEventListener('mousedown', leDrawStart);
      ov.addEventListener('touchstart', leDrawStart, { passive: false });
      stage.appendChild(ov);
    }
    if (btn) btn.classList.add('active');
  } else {
    if (ov) ov.remove();
    removeDrawBox();
    if (btn) btn.classList.remove('active');
  }
}

function leDrawStart(ev) {
  ev.preventDefault();
  const p = leStageCoords(lePoint(ev));
  layerEditor.draw = { sx: p.x, sy: p.y, cx: p.x, cy: p.y };
  renderDrawBox();
}

function renderDrawBox() {
  const stage = document.getElementById('le-stage');
  if (!stage || !layerEditor.draw) return;
  let box = document.getElementById('le-drawbox');
  if (!box) {
    box = document.createElement('div');
    box.id = 'le-drawbox';
    box.className = 'le-drawbox';
    stage.appendChild(box);
  }
  const d = layerEditor.draw, s = layerEditor.scale;
  const x = Math.min(d.sx, d.cx), y = Math.min(d.sy, d.cy);
  const w = Math.abs(d.cx - d.sx), h = Math.abs(d.cy - d.sy);
  box.style.left = (x * s) + 'px';
  box.style.top = (y * s) + 'px';
  box.style.width = (w * s) + 'px';
  box.style.height = (h * s) + 'px';
  box.textContent = `${Math.round(w)}×${Math.round(h)}`;
}

function removeDrawBox() {
  document.getElementById('le-drawbox')?.remove();
}

async function leCreateCanvas(geom, name) {
  const existing = layerEditor.canvases.map(c => c.name);
  if (!name) { let n = 1; do { name = 'Overlay' + n++; } while (existing.includes(name)); }
  const maxZ = layerEditor.canvases.reduce((m, c) => Math.max(m, c.zOrder), 0);
  try {
    await window.api.post('/api/canvas/create', {
      name, x: geom.x, y: geom.y, width: geom.width, height: geom.height, zOrder: maxZ + 1
    });
    layerEditor.selected = name;
    await refreshLayerEditor();
  } catch (e) {
    if (typeof showMessage === 'function') showMessage('Create failed: ' + e.message, 'error');
  }
}

function lePresetGeom(kind) {
  const W = layerEditor.dispW, H = layerEditor.dispH;
  const bar = Math.max(layerEditor.grid * 2, Math.round(H / 4));
  switch (kind) {
    case 'full': return { x: 0, y: 0, width: W, height: H };
    case 'top': return { x: 0, y: 0, width: W, height: bar };
    case 'bottom': return { x: 0, y: H - bar, width: W, height: bar };
    case 'center': {
      const cw = Math.round(W / 2), ch = Math.round(H / 2);
      return { x: Math.round((W - cw) / 2), y: Math.round((H - ch) / 2), width: cw, height: ch };
    }
    case 'corner': {
      const cw = Math.min(W, Math.max(layerEditor.grid * 3, Math.round(W / 3)));
      const ch = Math.min(H, Math.max(layerEditor.grid * 2, Math.round(H / 4)));
      return { x: W - cw, y: 0, width: cw, height: ch };
    }
    default: return null;
  }
}

async function layerEditorPreset(kind) {
  const g = lePresetGeom(kind);
  if (g) await leCreateCanvas(g);
}

async function layerEditorFit(kind) {
  const c = layerEditor.canvases.find(x => x.name === layerEditor.selected);
  if (!c) {
    if (typeof showMessage === 'function') showMessage('Select a canvas first', 'info');
    return;
  }
  const g = lePresetGeom(kind);
  if (!g) return;
  await leSendBounds(c.name, g, true);
  await refreshLayerEditor();
}

async function layerEditorAlign(kind) {
  const c = layerEditor.canvases.find(x => x.name === layerEditor.selected);
  if (!c) {
    if (typeof showMessage === 'function') showMessage('Select a canvas first', 'info');
    return;
  }
  const W = layerEditor.dispW, H = layerEditor.dispH;
  let x = c.x, y = c.y;
  if (kind === 'left') x = 0;
  else if (kind === 'right') x = W - c.width;
  else if (kind === 'hcenter') x = Math.round((W - c.width) / 2);
  else if (kind === 'top') y = 0;
  else if (kind === 'bottom') y = H - c.height;
  else if (kind === 'vcenter') y = Math.round((H - c.height) / 2);
  x = Math.max(0, Math.min(x, Math.max(0, W - c.width)));
  y = Math.max(0, Math.min(y, Math.max(0, H - c.height)));
  await leSendBounds(c.name, { x, y, width: c.width, height: c.height }, true);
  await refreshLayerEditor();
}

function leExtIcon(extName) {
  if (!extName) return '';
  const s = leSchemaFor(extName);
  if (!s?.iconData) return '';
  let mime = 'image/svg+xml';
  try { if (!atob(s.iconData).includes('<svg')) mime = 'image/png'; } catch (e) { }
  return `<img class="le-layer-icon" src="data:${mime};base64,${s.iconData}" alt="">`;
}

async function layerEditorToggleVisible(name, ev) {
  if (ev) ev.stopPropagation();
  const c = layerEditor.canvases.find(x => x.name === name);
  if (!c) return;
  const next = c.isVisible === false;
  try {
    await window.api.put('/api/canvas/' + encodeURIComponent(name) + '/visible', { visible: next });
    await refreshLayerEditor();
  } catch (e) {
    if (typeof showMessage === 'function') showMessage('Visibility failed: ' + (e.message || e), 'error');
  }
}

async function leSendBounds(name, g, isFinal) {
  // Never let two bounds requests overlap (a resize recreates the canvas). Live moves are dropped while
  // one is in flight; the final commit (isFinal) always goes through.
  if (layerEditor.sending && !isFinal) return;
  layerEditor.sending = true;
  try {
    await window.api.put('/api/canvas/' + encodeURIComponent(name) + '/bounds', g);
  } catch (e) {
    if (isFinal) console.error('Failed to set bounds:', e);
  } finally {
    layerEditor.sending = false;
  }
}

async function layerEditorAdd() {
  const existing = layerEditor.canvases.map(c => c.name);
  let n = 1, name;
  do { name = 'Overlay' + n++; } while (existing.includes(name));
  const maxZ = layerEditor.canvases.reduce((m, c) => Math.max(m, c.zOrder), 0);
  const w = Math.max(layerEditor.grid * 2, Math.round(layerEditor.dispW / 2));
  const h = Math.max(layerEditor.grid * 2, Math.round(layerEditor.dispH / 2));
  try {
    await window.api.post('/api/canvas/create', { name, x: 0, y: 0, width: w, height: h, zOrder: maxZ + 1 });
    layerEditor.selected = name;
    await refreshLayerEditor();
  } catch (e) {
    if (typeof showMessage === 'function') showMessage('Create failed: ' + e.message, 'error');
  }
}

function layerEditorAssign() {
  if (!layerEditor.selected) {
    if (typeof showMessage === 'function') showMessage('Select a canvas first', 'info');
    return;
  }
  if (LE_SYS.includes(layerEditor.selected)) {
    if (typeof showMessage === 'function')
      showMessage('Host overlay — pick another canvas for content', 'info');
    return;
  }
  if (typeof assignExtensionToCanvas === 'function') {
    assignExtensionToCanvas(layerEditor.selected);
    // Pull fresh state once the picker has likely applied the assignment.
    setTimeout(() => { if (layerEditor.open) refreshLayerEditor(); }, 1500);
  }
}

async function layerEditorRemove() {
  const name = layerEditor.selected;
  if (!name) { if (typeof showMessage === 'function') showMessage('Select a canvas first', 'info'); return; }
  if (LE_STD.includes(name) || LE_SYS.includes(name)) {
    if (typeof showMessage === 'function') showMessage(`'${name}' can't be removed`, 'info');
    return;
  }
  // NOTE: don't delegate to removeOverlayCanvas() — its confirm dialog calls the global closeModal(),
  // which removes ALL .modal-overlay elements (including this editor). Do it inline with a native confirm.
  if (!window.confirm(`Remove overlay canvas "${name}"?\nThis deletes the canvas and any content on it.`)) return;
  try {
    await window.api.post('/api/canvas/' + encodeURIComponent(name) + '/remove');
    if (typeof showMessage === 'function') showMessage(`Canvas '${name}' removed`, 'success');
    layerEditor.selected = null;
    await refreshLayerEditor();
    if (typeof updateDrawTargetCanvases === 'function') updateDrawTargetCanvases();
    if (typeof updateCameraTargetCanvases === 'function') updateCameraTargetCanvases();
    if (typeof populateDemoCanvasSelector === 'function') populateDemoCanvasSelector();
    window.dispatchEvent(new CustomEvent('layoutChanged'));
  } catch (e) {
    if (typeof showMessage === 'function') showMessage('Remove failed: ' + (e.message || e), 'error');
  }
}

async function layerEditorSetOpacity(value) {
  const name = layerEditor.selected;
  const opv = document.getElementById('le-opacity-val');
  if (opv) opv.textContent = value + '%';
  if (!name) return;
  const opacity = parseInt(value, 10) / 100;
  const rec = layerEditor.canvases.find(c => c.name === name);
  if (rec) rec.opacity = opacity;
  try {
    await window.api.put('/api/canvas/' + encodeURIComponent(name) + '/opacity', { opacity });
  } catch (e) {
    console.error('Opacity update failed:', e);
  }
}

async function layerEditorSetPanelBits(value) {
  const name = layerEditor.selected;
  if (!name) return;
  const panelColorBits = parseInt(value, 10) >= 14 ? 14 : 8;
  const rec = layerEditor.canvases.find(c => c.name === name);
  if (rec) rec.panelColorBits = panelColorBits;
  try {
    await window.api.put('/api/canvas/' + encodeURIComponent(name) + '/colorbits', { panelColorBits });
  } catch (e) {
    console.error('Panel depth update failed:', e);
  }
}

async function layerEditorZ(dir) {
  const name = layerEditor.selected;
  if (!name) { if (typeof showMessage === 'function') showMessage('Select a canvas first', 'info'); return; }
  const ep = dir === 'up' ? 'move-up' : 'move-down';
  try {
    await window.api.post('/api/canvas/' + encodeURIComponent(name) + '/' + ep);
    await refreshLayerEditor();
  } catch (e) {
    if (typeof showMessage === 'function') showMessage('Z-order change failed: ' + e.message, 'error');
  }
}

async function layerEditorSetTransparent(on) {
  const name = layerEditor.selected;
  if (!name) return;
  const rec = layerEditor.canvases.find(c => c.name === name);
  if (rec) rec.transparentBackground = on;
  try {
    await window.api.put('/api/canvas/' + encodeURIComponent(name) + '/transparent', { transparent: on });
  } catch (e) {
    if (typeof showMessage === 'function') showMessage('Transparent toggle failed: ' + e.message, 'error');
  }
}

function layerEditorEditParams() {
  if (!layerEditor.selected) {
    if (typeof showMessage === 'function') showMessage('Select a canvas first', 'info');
    return;
  }
  if (typeof editExtensionParameters === 'function') {
    editExtensionParameters(layerEditor.selected);
  }
}

async function layerEditorRename() {
  const name = layerEditor.selected;
  if (!name) { if (typeof showMessage === 'function') showMessage('Select a canvas first', 'info'); return; }
  if (LE_STD.includes(name) || LE_SYS.includes(name)) {
    if (typeof showMessage === 'function') showMessage(`'${name}' can't be renamed`, 'info');
    return;
  }
  const newName = window.prompt('Rename canvas', name);
  if (!newName || newName.trim() === '' || newName === name) return;
  try {
    const res = await window.api.put('/api/canvas/' + encodeURIComponent(name) + '/rename', { newName: newName.trim() });
    if (res && res.success === false) {
      if (typeof showMessage === 'function') showMessage(res.error || 'Rename failed', 'error');
      return;
    }
    layerEditor.selected = newName.trim();
    await refreshLayerEditor();
  } catch (e) {
    if (typeof showMessage === 'function') showMessage('Rename failed: ' + e.message, 'error');
  }
}

/* ----- Schemas / scene header / transform ----- */

async function leLoadSchemas() {
  if (window.__leSchemas) return;
  try {
    const r = await window.api.get('/api/extensions/available');
    window.__leSchemas = {};
    (r.data || []).forEach(e => { window.__leSchemas[e.displayName] = e; });
  } catch (e) { window.__leSchemas = {}; }
}
function leSchemaFor(ext) { return (window.__leSchemas || {})[ext]; }

async function leLoadScene() {
  try {
    const r = await window.api.get('/api/layout/current');
    const el = document.getElementById('le-scene');
    if (el) el.textContent = r?.data?.displayName || r?.data?.profile || '';
  } catch (e) { /* ignore */ }
}

function leSaveScene() {
  const cur = (document.getElementById('le-scene')?.textContent || 'Scene').trim();
  document.getElementById('le-savescene-modal')?.remove();
  const html = `
  <div class="modal-overlay" id="le-savescene-modal" style="z-index:10010;">
    <div class="modal-content" style="max-width:480px;">
      <div class="modal-header">
        <h2>💾 Save Scene</h2>
        <button class="modal-close" onclick="leSaveSceneClose()">${typeof ICONS !== 'undefined' ? ICONS.CLOSE : '✕'}</button>
      </div>
      <div class="modal-body">
        <div style="margin-bottom:12px;">
          <label for="le-scene-name">Scene name</label>
          <input type="text" id="le-scene-name" value="${cur.replace(/"/g, '&quot;')}" placeholder="My Scene">
        </div>
        <label class="checkbox-label"><input type="checkbox" id="le-scene-default"> <span>📌 Set as default (loads on startup)</span></label>
        <label class="checkbox-label"><input type="checkbox" id="le-scene-filters"> <span>🎨 Include active filters</span></label>
        <label class="checkbox-label"><input type="checkbox" id="le-scene-brightness" checked> <span>🔆 Apply this scene's brightness when loading</span></label>
      </div>
      <div class="modal-footer">
        <button class="btn btn-secondary" onclick="leSaveSceneClose()">Cancel</button>
        <button class="btn btn-primary" onclick="leSaveSceneConfirm()">Save Scene</button>
      </div>
    </div>
  </div>`;
  document.body.insertAdjacentHTML('beforeend', html);
  setTimeout(() => document.getElementById('le-scene-name')?.focus(), 50);
}

function leSaveSceneClose() {
  document.getElementById('le-savescene-modal')?.remove();
}

async function leSaveSceneConfirm() {
  const name = (document.getElementById('le-scene-name')?.value || '').trim();
  if (!name) { if (typeof showMessage === 'function') showMessage('Enter a scene name', 'error'); return; }
  const body = {
    name,
    description: '',
    isDefault: document.getElementById('le-scene-default')?.checked || false,
    includeFilters: document.getElementById('le-scene-filters')?.checked || false,
    overrideGlobalBrightness: document.getElementById('le-scene-brightness')?.checked !== false
  };
  try {
    await window.api.post('/api/layout/save', body);
    if (typeof showMessage === 'function') showMessage(`Scene '${name}' saved`, 'success');
    leSaveSceneClose();
    const el = document.getElementById('le-scene');
    if (el) el.textContent = name;
    if (typeof fetchSavedLayouts === 'function') fetchSavedLayouts();
  } catch (e) {
    if (typeof showMessage === 'function') showMessage('Save failed: ' + (e.message || e), 'error');
  }
}

async function leApplyTransform() {
  const name = layerEditor.selected;
  if (!name) return;
  const x = parseInt(document.getElementById('le-x').value, 10);
  const y = parseInt(document.getElementById('le-y').value, 10);
  const w = parseInt(document.getElementById('le-w').value, 10);
  const h = parseInt(document.getElementById('le-h').value, 10);
  if ([x, y, w, h].some(v => isNaN(v))) return;
  const g = {
    x: Math.max(0, Math.min(x, layerEditor.dispW - 1)),
    y: Math.max(0, Math.min(y, layerEditor.dispH - 1)),
    width: Math.max(4, Math.min(w, layerEditor.dispW)),
    height: Math.max(4, Math.min(h, layerEditor.dispH))
  };
  await leSendBounds(name, g, true);
  await refreshLayerEditor();
}

/* ----- Scenes dropdown + New (profile) menu in the Studio ----- */

function leCloseMenus(ev) {
  if (ev && ev.target && ev.target.closest && ev.target.closest('.le-menu')) return;
  const a = document.getElementById('le-scenes-menu'); if (a) a.hidden = true;
  const b = document.getElementById('le-new-menu'); if (b) b.hidden = true;
}

function leToggleNew(ev) {
  if (ev) ev.stopPropagation();
  const m = document.getElementById('le-new-menu');
  const sc = document.getElementById('le-scenes-menu'); if (sc) sc.hidden = true;
  if (m) m.hidden = !m.hidden;
}

function leToggleScenes(ev) {
  if (ev) ev.stopPropagation();
  const m = document.getElementById('le-scenes-menu');
  const nw = document.getElementById('le-new-menu'); if (nw) nw.hidden = true;
  if (!m) return;
  if (m.hidden) { leRenderScenes(); m.hidden = false; } else m.hidden = true;
}

async function leRenderScenes() {
  const m = document.getElementById('le-scenes-menu');
  if (!m) return;
  m.innerHTML = '<div class="le-hint" style="padding:6px">Loading…</div>';
  try {
    const r = await window.api.get('/api/layout/saved');
    const list = r.data || [];
    if (!list.length) { m.innerHTML = '<div class="le-hint" style="padding:6px">No scenes saved yet. Use 💾 Save Scene.</div>'; return; }
    m.innerHTML = list.map(l => {
      const nm = (l.name || '').replace(/'/g, "\\'");
      return `<div class="le-scene-row">
        <button class="le-scene-load" title="Load scene" onclick="leOpenScene('${nm}')">${l.isDefault ? '★ ' : ''}${l.name}</button>
        <span class="le-scene-acts">
          <button class="le-ico" title="Set as default" onclick="leSceneDefault('${nm}')">★</button>
          <button class="le-ico le-ico-danger" title="Delete scene" onclick="leSceneDelete('${nm}')">🗑</button>
        </span>
      </div>`;
    }).join('');
  } catch (e) {
    m.innerHTML = '<div class="le-hint" style="padding:6px">Failed to load scenes.</div>';
  }
}

async function leOpenScene(name) {
  const m = document.getElementById('le-scenes-menu'); if (m) m.hidden = true;
  if (typeof loadSavedLayout === 'function') await loadSavedLayout(name);
  await refreshLayerEditor();
  leLoadScene();
}

async function leSceneDefault(name) {
  if (typeof setAsDefaultLayout === 'function') await setAsDefaultLayout(name);
  leRenderScenes();
}

async function leSceneDelete(name) {
  if (typeof deleteSavedLayout === 'function') await deleteSavedLayout(name);
  leRenderScenes();
}

async function leApplyProfile(profile) {
  const m = document.getElementById('le-new-menu'); if (m) m.hidden = true;
  try {
    await window.api.post('/api/layout/apply/' + profile, {});
    await refreshLayerEditor();
    leLoadScene();
    if (typeof showMessage === 'function') showMessage('New ' + profile + ' layout', 'success');
  } catch (e) {
    if (typeof showMessage === 'function') showMessage('Failed: ' + (e.message || e), 'error');
  }
}

/* ----- Content list (1 item = static, 2+ = rotation), edited inline in the inspector ----- */

async function loadContentSection(name) {
  let data = {};
  let live = null;
  try {
    const [rot, all] = await Promise.all([
      window.api.get('/api/canvas/' + encodeURIComponent(name) + '/rotation'),
      window.api.get('/api/layout/content')
    ]);
    data = rot.data || {};
    const list = all?.data?.contents || [];
    layerEditor.contentMap = {};
    list.forEach(c => { layerEditor.contentMap[c.canvasName] = c; });
    live = layerEditor.contentMap[name] || null;
    renderLayerBoxes(true);
  } catch (e) { /* ignore */ }
  if (layerEditor.selected !== name) return; // selection changed while loading
  window.leContent = {
    name,
    steps: data.steps || [],
    interval: data.intervalSeconds || 12,
    transition: data.transition || 'Fade',
    loop: data.loop !== false,
    isRunning: !!data.isRunning,
    activeIndex: (typeof data.activeIndex === 'number') ? data.activeIndex : -1,
    single: live
  };
  renderContentSection();
}

function renderContentSection() {
  const host = document.getElementById('le-content');
  if (!host) return;
  const s = window.leContent;
  const steps = s.steps;

  if (steps.length === 0) {
    if (s.single && s.single.extensionName) {
      host.innerHTML =
        `<div class="le-content-row">
           <span class="le-content-name">${s.single.extensionName}</span>
           <span class="playlist-actions">
             <button class="btn-icon" title="Quick parameters" onclick="contentToggleParams(0)">⚙</button>
             <button class="btn-icon" title="Full editor (lists, tables, actions)" onclick="contentFullParams(0)">⊞</button>
             <button class="btn-icon" title="Replace content" onclick="contentChangeSingle()">⇄</button>
             <button class="btn-icon" title="Remove" onclick="contentClear()">✕</button>
           </span>
         </div>
         <div id="le-params-0" class="le-params"></div>
         <div class="le-add-row">
           <button class="btn btn-small btn-secondary" onclick="contentAdd()">+ Add</button>
           <button class="btn btn-small btn-secondary" onclick="contentAddMedia()">🎬 Media</button>
           <button class="btn btn-small btn-secondary" onclick="contentAddCamera()">📷 Camera</button>
         </div>`;
    } else {
      host.innerHTML =
        `<p class="le-hint">No content yet.</p>
         <div class="le-add-row">
           <button class="btn btn-small btn-primary" onclick="contentAdd()">+ Add</button>
           <button class="btn btn-small btn-secondary" onclick="contentAddMedia()">🎬 Media</button>
           <button class="btn btn-small btn-secondary" onclick="contentAddCamera()">📷 Camera</button>
         </div>`;
    }
    return;
  }

  const rotating = steps.length > 1;
  const head = rotating
    ? `<div class="le-rotate-head">
         <label class="le-sel-lbl"><input type="checkbox" id="le-rot-enabled" ${s.isRunning ? 'checked' : ''} onchange="contentRotationSettings()"> Rotate</label>
         <label class="le-sel-lbl">every <input type="number" id="le-rot-interval" min="2" max="3600" value="${s.interval}" style="width:54px" onchange="contentRotationSettings()">s</label>
         <select id="le-rot-transition" onchange="contentRotationSettings()"><option value="Fade">Fade</option><option value="Instant">Instant</option></select>
         <label class="le-sel-lbl"><input type="checkbox" id="le-rot-loop" ${s.loop ? 'checked' : ''} onchange="contentRotationSettings()"> loop</label>
       </div>`
    : `<p class="le-hint">Add another item below to rotate this canvas.</p>`;

  const rows = steps.map((st, i) => {
    const isStream = (st.type === 'media' || st.type === 'camera');
    const sicon = st.type === 'camera' ? '📷 ' : (st.type === 'media' ? '🎬 ' : '');
    const cfgBtn = isStream
      ? `<button class="btn-icon" title="Show on display" onclick="contentApply(${i})">▶</button>`
      : `<button class="btn-icon" title="Show on display & quick-edit parameters" onclick="contentToggleParams(${i})">⚙</button>
         <button class="btn-icon" title="Full editor (lists, tables, actions)" onclick="contentFullParams(${i})">⊞</button>`;
    return `
    <div class="playlist-row${i === s.activeIndex ? ' le-active' : ''}">
      <span class="playlist-name" title="${(st.detail || '').replace(/"/g, '&quot;')}">${i === s.activeIndex ? '▶ ' : ''}${i + 1}. ${sicon}${st.extension}${st.detail ? ` <span class="le-step-detail">${st.detail}</span>` : ''}</span>
      <span class="playlist-actions">
        ${cfgBtn}
        <button class="btn-icon" title="Duplicate" onclick="contentDuplicate(${i})">⧉</button>
        <button class="btn-icon" title="Up" onclick="contentMove(${i},-1)" ${i === 0 ? 'disabled' : ''}>▲</button>
        <button class="btn-icon" title="Down" onclick="contentMove(${i},1)" ${i === steps.length - 1 ? 'disabled' : ''}>▼</button>
        <button class="btn-icon" title="Remove" onclick="contentRemove(${i})">✕</button>
      </span>
    </div>
    <div id="le-params-${i}" class="le-params"></div>`;
  }).join('');

  host.innerHTML = head +
    `<div class="playlist-list">${rows}</div>
     <div class="le-add-row">
       <button class="btn btn-small btn-secondary" onclick="contentAdd()">+ Add</button>
       <button class="btn btn-small btn-secondary" onclick="contentAddMedia()">🎬 Media</button>
       <button class="btn btn-small btn-secondary" onclick="contentAddCamera()">📷 Camera</button>
     </div>`;
  const tsel = document.getElementById('le-rot-transition');
  if (tsel) tsel.value = s.transition;
}

function contentAdd() {
  const name = window.leContent?.name || layerEditor.selected;
  if (!name || typeof assignExtensionToCanvas !== 'function') return;
  if (LE_SYS.includes(name)) return;
  // Snapshot whether a single (non-list) content exists right now, so the picker callback (which runs
  // later) doesn't re-read stale state and double-capture it.
  const s = window.leContent || {};
  const promoteSingle = (s.steps || []).length === 0 && s.single && s.single.extensionName;
  assignExtensionToCanvas(name, async (ext) => {
    const base = '/api/canvas/' + encodeURIComponent(name) + '/rotation';
    if (promoteSingle) {
      // Move the existing single content into the list as item 1 so it isn't lost.
      await window.api.post(base + '/add-current', {});
    }
    await window.api.post(base + '/add-extension', { extension: ext });
    // Display the item we just added so it's visible and editable (a bare step otherwise stays blank
    // until rotation runs).
    try {
      const r = await window.api.get(base);
      const cnt = (r.data?.steps || []).length;
      if (cnt > 0) await window.api.post(base + '/apply-step?index=' + (cnt - 1), {});
    } catch (e) { /* ignore */ }
    await refreshLayerEditor();
    await loadContentSection(name);
  });
}

function contentChangeSingle() {
  const name = layerEditor.selected;
  if (!name || typeof assignExtensionToCanvas !== 'function') return;
  if (LE_SYS.includes(name)) return;
  assignExtensionToCanvas(name, async (ext) => {
    await window.api.post('/api/layout/assign', { canvasName: name, extensionName: ext });
    await refreshLayerEditor();
  });
}

// Add a Media (video) content item — opens a small picker of available videos.
async function contentAddMedia() {
  const name = window.leContent?.name || layerEditor.selected;
  if (!name || LE_SYS.includes(name)) return;
  let videos = [];
  try { const r = await window.api.get('/api/media/status'); videos = (r.data && r.data.availableVideos) || []; } catch (e) { /* ignore */ }
  document.getElementById('le-media-modal')?.remove();
  const opts = videos.map(v => `<option value="${String(v).replace(/"/g, '&quot;')}">${v}</option>`).join('');
  const body = videos.length
    ? `<label class="le-sel-lbl">Video file</label>
       <select id="le-media-file" style="width:100%;margin:4px 0 10px;">${opts}</select>
       <label class="checkbox-label"><input type="checkbox" id="le-media-loop" checked> <span>Loop while shown</span></label>`
    : `<p class="le-hint">No videos found. Upload videos in the Media tab first.</p>`;
  const html = `
  <div class="modal-overlay" id="le-media-modal" style="z-index:10010;">
    <div class="modal-content" style="max-width:460px;">
      <div class="modal-header"><h2>🎬 Add Media</h2><button class="modal-close" onclick="leMediaClose()">${typeof ICONS !== 'undefined' ? ICONS.CLOSE : '✕'}</button></div>
      <div class="modal-body">${body}<p class="le-hint" style="margin-top:10px;">Note: one media stream plays at a time across the display.</p></div>
      <div class="modal-footer">
        <button class="btn btn-secondary" onclick="leMediaClose()">Cancel</button>
        ${videos.length ? '<button class="btn btn-primary" onclick="leMediaAdd()">Add</button>' : ''}
      </div>
    </div>
  </div>`;
  document.body.insertAdjacentHTML('beforeend', html);
}

function leMediaClose() { document.getElementById('le-media-modal')?.remove(); }

async function leMediaAdd() {
  const name = window.leContent?.name || layerEditor.selected;
  const file = document.getElementById('le-media-file')?.value;
  const loop = document.getElementById('le-media-loop')?.checked !== false;
  if (!name || !file) return;
  const base = '/api/canvas/' + encodeURIComponent(name) + '/rotation';
  const s = window.leContent || {};
  if ((s.steps || []).length === 0 && s.single && s.single.extensionName) {
    await window.api.post(base + '/add-current', {});
  }
  await window.api.post(base + '/add-media', { file, loop });
  try {
    const r = await window.api.get(base);
    const cnt = (r.data && r.data.steps || []).length;
    if (cnt > 0) await window.api.post(base + '/apply-step?index=' + (cnt - 1), {});
  } catch (e) { /* ignore */ }
  leMediaClose();
  await refreshLayerEditor();
  await loadContentSection(name);
}

const LE_CAM_EFFECTS = ['none', 'edge', 'invert', 'sepia', 'nightvision', 'thermal', 'posterize', 'pixelate', 'rgbshift', 'emboss', 'blur'];

// Add a USB-camera content item — pick the device + visual effect for this step.
async function contentAddCamera() {
  const name = window.leContent?.name || layerEditor.selected;
  if (!name || LE_SYS.includes(name)) return;
  let devices = [];
  try { const r = await window.api.get('/api/localcam/devices'); devices = (r.data && r.data.videoDevices) || []; } catch (e) { /* ignore */ }
  document.getElementById('le-cam-modal')?.remove();
  const devOpts = devices.map(d => `<option value="${String(d.path).replace(/"/g, '&quot;')}">${d.name || d.path}</option>`).join('');
  const fxOpts = LE_CAM_EFFECTS.map(f => `<option value="${f}">${f}</option>`).join('');
  const body = devices.length
    ? `<label class="le-sel-lbl">Camera device</label>
       <select id="le-cam-device" style="width:100%;margin:4px 0 10px;">${devOpts}</select>
       <label class="le-sel-lbl">Effect</label>
       <select id="le-cam-effect" style="width:100%;margin:4px 0 0;">${fxOpts}</select>`
    : `<p class="le-hint">No USB cameras detected (looking for /dev/video*). Connect a camera, or set device and effects on the Create tab first.</p>`;
  const html = `
  <div class="modal-overlay" id="le-cam-modal" style="z-index:10010;">
    <div class="modal-content" style="max-width:460px;">
      <div class="modal-header"><h2>📷 Add Camera</h2><button class="modal-close" onclick="leCamClose()">${typeof ICONS !== 'undefined' ? ICONS.CLOSE : '✕'}</button></div>
      <div class="modal-body">${body}<p class="le-hint" style="margin-top:10px;">USB device and effects are configured on the Create tab. Assign the canvas here. One camera stream plays at a time.</p></div>
      <div class="modal-footer">
        <button class="btn btn-secondary" onclick="leCamClose()">Cancel</button>
        ${devices.length ? '<button class="btn btn-primary" onclick="leCamAdd()">Add</button>' : ''}
      </div>
    </div>
  </div>`;
  document.body.insertAdjacentHTML('beforeend', html);
}

function leCamClose() { document.getElementById('le-cam-modal')?.remove(); }

async function leCamAdd() {
  const name = window.leContent?.name || layerEditor.selected;
  const device = document.getElementById('le-cam-device')?.value;
  const effect = document.getElementById('le-cam-effect')?.value || 'none';
  if (!name) return;
  const base = '/api/canvas/' + encodeURIComponent(name) + '/rotation';
  const s = window.leContent || {};
  if ((s.steps || []).length === 0 && s.single && s.single.extensionName) {
    await window.api.post(base + '/add-current', {});
  }
  await window.api.post(base + '/add-camera', { device, effect });
  try {
    const r = await window.api.get(base);
    const cnt = (r.data && r.data.steps || []).length;
    if (cnt > 0) await window.api.post(base + '/apply-step?index=' + (cnt - 1), {});
  } catch (e) { /* ignore */ }
  leCamClose();
  await refreshLayerEditor();
  await loadContentSection(name);
}

// Preview a step on the display (used for media/camera steps, which have no inline params).
async function contentApply(i) {
  const name = window.leContent.name;
  await window.api.post('/api/canvas/' + encodeURIComponent(name) + '/rotation/apply-step?index=' + i, {});
  await loadContentSection(name);
}

async function contentRemove(i) {
  const name = window.leContent.name;
  await window.api.post('/api/canvas/' + encodeURIComponent(name) + '/rotation/remove-step?index=' + i, {});
  await loadContentSection(name);
}

async function contentClear() {
  const name = window.leContent?.name;
  if (!name) return;
  try { await window.api.post('/api/layout/stop/' + encodeURIComponent(name)); } catch (e) { /* already empty */ }
  await loadContentSection(name);
}

async function contentDuplicate(i) {
  const name = window.leContent.name;
  await window.api.post('/api/canvas/' + encodeURIComponent(name) + '/rotation/duplicate-step?index=' + i, {});
  await loadContentSection(name);
}

async function contentMove(i, dir) {
  const name = window.leContent.name;
  await window.api.post('/api/canvas/' + encodeURIComponent(name) + '/rotation/move-step?index=' + i + '&dir=' + dir, {});
  await loadContentSection(name);
}

async function contentRotationSettings() {
  const name = window.leContent.name;
  const enabled = document.getElementById('le-rot-enabled')?.checked || false;
  const interval = parseInt(document.getElementById('le-rot-interval')?.value, 10) || 12;
  const transition = document.getElementById('le-rot-transition')?.value || 'Fade';
  const loop = document.getElementById('le-rot-loop')?.checked !== false;
  await window.api.post('/api/canvas/' + encodeURIComponent(name) + '/rotation/settings',
    { enabled, intervalSeconds: Math.max(2, interval), transition, loop });
  window.leContent.isRunning = enabled;
}

// Toggle inline parameter editing for a content item (i = list index, or 0 for the single content).
async function contentToggleParams(i) {
  const host = document.getElementById('le-params-' + i);
  if (!host) return;
  if (host.dataset.open === '1') { host.innerHTML = ''; host.dataset.open = '0'; return; }
  // Only one params panel open at a time.
  document.querySelectorAll('#le-content .le-params').forEach(p => { p.innerHTML = ''; p.dataset.open = '0'; });
  await leLoadSchemas(); // ensure the schema is available before building the form

  const s = window.leContent;
  let ext, config = {};
  try {
    if (s.steps.length === 0) {
      ext = s.single && s.single.extensionName;
      const r = await window.api.get('/api/layout/content/' + encodeURIComponent(s.name));
      config = (r.data && (r.data.currentParameters || r.data.configuration)) || {};
    } else {
      ext = s.steps[i].extension;
      // Show this step on the display so edits preview live (it becomes the active step).
      try { await window.api.post('/api/canvas/' + encodeURIComponent(s.name) + '/rotation/apply-step?index=' + i, {}); } catch (e) { /* ignore */ }
      const r = await window.api.get('/api/canvas/' + encodeURIComponent(s.name) + '/rotation/step/' + i);
      config = (r.data && r.data.config) || {};
    }
  } catch (e) { console.error('[studio] load params failed', e); }

  host.dataset.ext = ext || '';
  host.dataset.open = '1';

  if (!ext) { host.innerHTML = '<p class="le-hint">No content on this item.</p>'; return; }

  try {
    host.innerHTML = leBuildParamForm(ext, config, i) +
      `<div class="le-params-actions"><button class="btn btn-small btn-primary" onclick="contentSaveParams(${i})">Save</button></div>`;
  } catch (e) {
    console.error('[studio] render params failed', e);
    host.innerHTML = `<p class="le-hint">Couldn't render parameters inline. <a href="#" onclick="editExtensionParameters('${(s.name || '').replace(/'/g, "\\'")}');return false;">Open full editor</a></p>`;
    return;
  }

  // Keep each slider's numeric readout in sync while dragging (renderField puts a "<id>-val"
  // span in the label). Without this the number above the slider never moves.
  host.querySelectorAll('input[type=range]').forEach(el => {
    const span = document.getElementById(el.id + '-val');
    if (span) el.addEventListener('input', () => { span.textContent = el.value; });
  });
  // Live-apply on change so edits react immediately on the display (no need to hunt for Save).
  host.querySelectorAll('input, select').forEach(el => {
    el.addEventListener('change', () => leApplyParams(i, false));
  });
}

function leBuildParamForm(ext, config, i) {
  const e = leSchemaFor(ext);
  if (!e || !e.parameters || !e.parameters.length) return '<p class="le-hint">No parameters.</p>';
  let structured = 0;
  const rows = e.parameters.map(p => {
    const kind = (p.kind || '').toLowerCase();
    if (kind === 'list' || kind === 'object') { structured++; return ''; }
    const fid = 'lep_' + i + '_' + p.name;
    const v = (config && (p.name in config)) ? config[p.name] : p.defaultValue;
    return renderField(p, v, fid, '');
  }).join('');
  return rows + (structured ? '<p class="le-hint">Advanced list/table parameters: use the full Params editor.</p>' : '');
}

// Collect the inline form values and apply them (live, no reload) or save (reload to refresh labels).
async function leApplyParams(i, reload) {
  const host = document.getElementById('le-params-' + i);
  const s = window.leContent;
  const ext = host && host.dataset.ext;
  const e = leSchemaFor(ext);
  if (!e) return;
  const config = {};
  (e.parameters || []).forEach(p => {
    const kind = (p.kind || '').toLowerCase();
    if (kind === 'list' || kind === 'object') return;
    const fid = 'lep_' + i + '_' + p.name;
    if (!document.getElementById(fid) && leafKind(p) !== 'color') return;
    config[p.name] = readFieldValue(p, fid);
  });
  try {
    if (s.steps.length === 0) {
      await window.api.post('/api/layout/configure/' + encodeURIComponent(s.name), config);
    } else {
      await window.api.put('/api/canvas/' + encodeURIComponent(s.name) + '/rotation/step/' + i + '/config', config);
    }
    if (reload && typeof showMessage === 'function') showMessage('Saved', 'success');
  } catch (e2) {
    if (typeof showMessage === 'function') showMessage('Apply failed: ' + (e2.message || e2), 'error');
  }
  if (reload) await loadContentSection(s.name);
}

function contentSaveParams(i) { return leApplyParams(i, true); }

// Open the FULL parameter editor (supports list/table params like HA Grid sensors, plus actions)
// for a content item. The inline form only covers scalar params, so structured extensions need this.
async function contentFullParams(i) {
  const s = window.leContent;
  if (!s || typeof editExtensionParameters !== 'function') return;
  const name = s.name;
  if ((s.steps || []).length === 0) {
    // Single content: the live canvas extension IS this content, so edit it directly.
    editExtensionParameters(name);
    return;
  }
  // Rotation step: show it on the display first so the live canvas carries this step's extension +
  // config, edit against the live canvas, then capture the result back into the step on close.
  try { await window.api.post('/api/canvas/' + encodeURIComponent(name) + '/rotation/apply-step?index=' + i, {}); } catch (e) { /* ignore */ }
  window.leFullParamStep = { name, index: i };
  editExtensionParameters(name);
}

// After the full editor closes for a rotation step, persist the live canvas's current parameters
// back into that step so the rotation keeps the edits (the full editor writes to the live canvas).
async function captureStepFromLiveCanvas(name, index) {
  try {
    await leLoadSchemas();
    const r = await window.api.get('/api/layout/content/' + encodeURIComponent(name));
    let cfg = (r.data && (r.data.currentParameters || r.data.configuration)) || {};
    // Keep only real, editable parameters for this extension. Never write back runtime flags like
    // IsRunning: re-applying a step with IsRunning=true makes the extension's Start() early-exit,
    // leaving the canvas blank (same class as the old "saved layout loads blank" bug).
    const ext = window.leContent?.steps?.[index]?.extension || '';
    const schema = leSchemaFor(ext);
    if (schema && schema.parameters && schema.parameters.length) {
      const allowed = new Set(schema.parameters.map(p => p.name));
      const filtered = {};
      Object.keys(cfg).forEach(k => { if (allowed.has(k)) filtered[k] = cfg[k]; });
      cfg = filtered;
    } else {
      delete cfg.IsRunning; delete cfg.isRunning;
    }
    if (cfg && Object.keys(cfg).length) {
      await window.api.put('/api/canvas/' + encodeURIComponent(name) + '/rotation/step/' + index + '/config', cfg);
    }
  } catch (e) { /* ignore */ }
  if (window.leContent && window.leContent.name === name) await loadContentSection(name);
}

function ensureLayerEditorStyles() {
  if (document.getElementById('layer-editor-styles')) return;
  const css = `
  .le-toolbar{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin:6px 0 10px;}
  .le-snap{font-size:.8rem;color:var(--text-muted,#aaa);display:flex;align-items:center;gap:4px;}
  .le-readout{margin-left:auto;font-size:.8rem;color:var(--text-muted,#aaa);font-variant-numeric:tabular-nums;}
  .le-chips{display:flex;flex-wrap:wrap;gap:6px;margin:0 0 10px;}
  .le-chip{display:flex;flex-direction:column;align-items:flex-start;line-height:1.1;padding:4px 8px;border:1px solid #3a3a3a;border-radius:6px;background:#1d1d1f;color:#ddd;cursor:pointer;font-size:.78rem;}
  .le-chip:hover{border-color:#5ab4ff;}
  .le-chip.selected{border-color:#ffcc33;background:rgba(255,204,51,.15);color:#fff;}
  .le-chip-sub{font-size:.66rem;color:var(--text-muted,#999);}
  .le-selbar{display:flex;align-items:center;flex-wrap:wrap;gap:10px;min-height:34px;margin:0 0 10px;padding:6px 10px;border:1px solid #2e2e30;border-radius:8px;background:#161618;}
  .le-hint{font-size:.8rem;color:var(--text-muted,#999);}
  .le-sel-title{display:flex;flex-direction:column;line-height:1.1;}
  .le-sel-title strong{color:#ffcc33;font-size:.9rem;}
  .le-sel-ext{font-size:.68rem;color:var(--text-muted,#999);}
  .le-sel-group{display:flex;align-items:center;gap:6px;padding-left:10px;border-left:1px solid #2e2e30;}
  .le-sel-lbl{font-size:.74rem;color:var(--text-muted,#aaa);font-variant-numeric:tabular-nums;}
  .le-sel-group input[type=range]{width:90px;}
  .le-ico{width:24px;height:22px;border:1px solid #3a3a3a;border-radius:5px;background:#1d1d1f;color:#ddd;cursor:pointer;line-height:1;}
  .le-ico:hover{border-color:#5ab4ff;}
  .le-sel-actions{display:flex;gap:6px;margin-left:auto;}
  .le-stage-wrap{display:flex;justify-content:center;}
  .le-stage{position:relative;background:#111;background-image:linear-gradient(45deg,#1a1a1a 25%,transparent 25%),linear-gradient(-45deg,#1a1a1a 25%,transparent 25%),linear-gradient(45deg,transparent 75%,#1a1a1a 75%),linear-gradient(-45deg,transparent 75%,#1a1a1a 75%);background-size:16px 16px;background-position:0 0,0 8px,8px -8px,-8px 0;border:1px solid #333;overflow:hidden;touch-action:none;user-select:none;}
  .le-stream{position:absolute;inset:0;width:100%;height:100%;image-rendering:pixelated;pointer-events:none;}
  .le-box{position:absolute;box-sizing:border-box;border:1px solid rgba(80,180,255,.9);background:rgba(80,180,255,.10);cursor:move;}
  .le-box.selected{border-color:#ffcc33;background:rgba(255,204,51,.14);box-shadow:0 0 0 1px rgba(255,204,51,.5);}
  .le-box-label{position:absolute;top:1px;left:2px;font-size:9px;line-height:1.05;color:#fff;text-shadow:0 1px 2px #000;pointer-events:none;white-space:nowrap;}
  .le-box-dims{color:#9fd;}
  .le-h{position:absolute;width:9px;height:9px;background:#ffcc33;border:1px solid #6b5400;box-sizing:border-box;}
  .le-h-nw{left:-5px;top:-5px;cursor:nwse-resize;} .le-h-ne{right:-5px;top:-5px;cursor:nesw-resize;}
  .le-h-sw{left:-5px;bottom:-5px;cursor:nesw-resize;} .le-h-se{right:-5px;bottom:-5px;cursor:nwse-resize;}
  .le-h-n{left:50%;top:-5px;transform:translateX(-50%);cursor:ns-resize;} .le-h-s{left:50%;bottom:-5px;transform:translateX(-50%);cursor:ns-resize;}
  .le-h-e{right:-5px;top:50%;transform:translateY(-50%);cursor:ew-resize;} .le-h-w{left:-5px;top:50%;transform:translateY(-50%);cursor:ew-resize;}
  .le-rot-note{font-size:.74rem;color:#ffcc33;}
  .le-presets{display:flex;gap:4px;align-items:center;flex-wrap:wrap;}
  .le-preset{font-size:.72rem;padding:3px 7px;border:1px solid #3a3a3a;border-radius:5px;background:#1d1d1f;color:#ddd;cursor:pointer;}
  .le-preset:hover{border-color:#5ab4ff;}
  #le-draw-btn.active{background:#06b6d4;color:#021;border-color:#06b6d4;}
  .le-drawlayer{position:absolute;inset:0;z-index:2000;cursor:crosshair;touch-action:none;}
  .le-drawbox{position:absolute;z-index:2001;box-sizing:border-box;border:2px dashed #06b6d4;background:rgba(6,182,212,.25);pointer-events:none;color:#cffafe;font-size:10px;display:flex;align-items:center;justify-content:center;text-shadow:0 1px 2px #000;}
  .playlist-list{margin-top:6px;border:1px solid #2e2e30;border-radius:8px;max-height:220px;overflow-y:auto;background:rgba(0,0,0,.2);}
  .playlist-row{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:6px 10px;border-bottom:1px solid #2a2a2a;font-size:13px;}
  .playlist-row:last-child{border-bottom:none;}
  .playlist-name{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
  .playlist-actions{display:flex;gap:4px;flex-shrink:0;}
  .playlist-actions .btn-icon{background:transparent;border:1px solid #444;color:inherit;border-radius:6px;width:26px;height:26px;cursor:pointer;line-height:1;}
  .playlist-actions .btn-icon:hover:not(:disabled){background:rgba(255,255,255,.08);}
  .playlist-actions .btn-icon:disabled{opacity:.3;cursor:default;}
  .playlist-empty{padding:12px 10px;font-size:12px;color:#888;}
  .le-step-detail{color:#7fb7ff;font-size:.82em;}
  .le-studio{width:100%;}
  .le-studio-header{display:flex;align-items:center;gap:10px;margin-bottom:10px;}
  .le-scene{font-size:.82rem;color:#7fb7ff;}
  .le-studio-body{display:flex;gap:12px;align-items:stretch;}
  .le-pane{flex:0 0 auto;background:#161618;border:1px solid #2e2e30;border-radius:8px;padding:8px;overflow:auto;max-height:72vh;}
  .le-layers{width:200px;}
  .le-inspector{width:330px;}
  .le-center{flex:1 1 auto;display:flex;flex-direction:column;align-items:center;gap:8px;min-width:0;}
  .le-pane-title{font-size:.72rem;text-transform:uppercase;letter-spacing:.04em;color:#888;margin-bottom:6px;}
  .le-box.hidden{border-style:dashed;opacity:.45;}
  .le-layer{display:flex;align-items:center;justify-content:space-between;gap:6px;padding:6px 8px;border:1px solid #2a2a2a;border-radius:6px;margin-bottom:4px;cursor:pointer;}
  .le-layer:hover{border-color:#5ab4ff;}
  .le-layer.selected{border-color:#ffcc33;background:rgba(255,204,51,.12);}
  .le-layer.is-hidden{opacity:.5;}
  .le-layer-icon{width:18px;height:18px;flex-shrink:0;object-fit:contain;border-radius:3px;}
  .le-eye{flex-shrink:0;width:22px;height:22px;border:1px solid #3a3a3a;border-radius:5px;background:#1d1d1f;color:#ddd;cursor:pointer;line-height:1;padding:0;}
  .le-eye:hover{border-color:#5ab4ff;}
  .le-align{display:flex;flex-wrap:wrap;align-items:center;gap:4px;margin-top:8px;}
  .le-layer-main{display:flex;flex-direction:column;line-height:1.15;overflow:hidden;}
  .le-layer-name{font-size:.82rem;color:#eee;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
  .le-layer-ext{font-size:.68rem;color:#999;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
  .le-layer-z{font-size:.66rem;color:#777;flex-shrink:0;}
  .le-insp-head{display:flex;align-items:center;justify-content:space-between;margin-bottom:8px;}
  .le-insp-name{color:#ffcc33;font-size:1rem;}
  .le-insp-actions{display:flex;gap:4px;}
  .le-ico-danger:hover{border-color:#e05555;color:#e05555;}
  .le-section{border-top:1px solid #2a2a2a;padding:8px 0;}
  .le-section-title{font-size:.72rem;text-transform:uppercase;letter-spacing:.04em;color:#888;margin-bottom:6px;}
  .le-grid4{display:grid;grid-template-columns:1fr 1fr;gap:6px;}
  .le-grid4 label{display:flex;align-items:center;gap:6px;font-size:.75rem;color:#aaa;}
  .le-axis{flex:0 0 14px;text-align:center;color:#888;}
  .le-grid4 input{flex:1 1 auto;width:100%;min-width:0;height:26px;box-sizing:border-box;}
  .le-row{display:flex;align-items:center;gap:8px;margin:5px 0;}
  .le-row input[type=range]{flex:1;}
  .le-content-row{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:6px 0;}
  .le-content-name{font-size:.85rem;color:#eee;}
  .le-add-btn{margin-top:8px;width:100%;}
  .le-add-row{display:flex;gap:6px;margin-top:8px;}
  .le-add-row > button{flex:1;}
  .le-rotate-head{display:flex;align-items:center;flex-wrap:wrap;gap:10px;margin-bottom:8px;font-size:.75rem;}
  .le-rotate-head label{display:inline-flex;align-items:center;gap:5px;margin:0;}
  .le-rotate-head input[type=number]{height:24px;box-sizing:border-box;}
  .le-rotate-head input[type=checkbox]{margin:0;}
  .le-rotate-head select{font-size:.75rem;height:24px;box-sizing:border-box;}
  .le-params:not(:empty){border:1px solid #2a2a2a;border-radius:6px;padding:8px;margin:0 0 8px;background:rgba(0,0,0,.2);}
  .le-params-actions{margin-top:8px;display:flex;justify-content:flex-end;}
  .playlist-row.le-active{background:rgba(46,204,113,.14);}
  .le-menu{position:relative;display:inline-block;}
  .le-dropdown{position:absolute;top:100%;left:0;z-index:5000;margin-top:4px;min-width:210px;max-height:320px;overflow:auto;background:#161618;border:1px solid #3a3a3a;border-radius:8px;padding:6px;box-shadow:0 8px 24px rgba(0,0,0,.45);}
  .le-dropdown-right{left:auto;right:0;}
  .le-dropdown[hidden]{display:none;}
  .le-dropdown > button{display:block;width:100%;text-align:left;background:transparent;border:0;color:#ddd;padding:6px 8px;border-radius:6px;cursor:pointer;font-size:.82rem;}
  .le-dropdown > button:hover{background:rgba(255,255,255,.08);}
  .le-scene-row{display:flex;align-items:center;justify-content:space-between;gap:6px;}
  .le-scene-load{flex:1;text-align:left;background:transparent;border:0;color:#ddd;padding:6px 8px;border-radius:6px;cursor:pointer;font-size:.82rem;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
  .le-scene-load:hover{background:rgba(255,255,255,.08);}
  .le-scene-acts{display:flex;gap:2px;flex-shrink:0;}
  @media (max-width:900px){ .le-studio-body{flex-direction:column;} .le-pane{width:auto;max-height:none;} }
  `;
  const el = document.createElement('style');
  el.id = 'layer-editor-styles';
  el.textContent = css;
  document.head.appendChild(el);
}

window.openLayerEditor = openLayerEditor;
window.closeLayerEditor = closeLayerEditor;
window.selectLayerCanvas = selectLayerCanvas;
window.layerEditorAdd = layerEditorAdd;
window.layerEditorRename = layerEditorRename;
window.layerEditorRemove = layerEditorRemove;
window.layerEditorZ = layerEditorZ;
window.layerEditorSetTransparent = layerEditorSetTransparent;
window.layerEditorSetOpacity = layerEditorSetOpacity;
window.layerEditorSetPanelBits = layerEditorSetPanelBits;
window.layerEditorToggleDraw = layerEditorToggleDraw;
window.layerEditorToggleVisible = layerEditorToggleVisible;
window.layerEditorAlign = layerEditorAlign;
window.layerEditorFit = layerEditorFit;
window.layerEditorPreset = layerEditorPreset;
window.leSaveScene = leSaveScene;
window.leSaveSceneClose = leSaveSceneClose;
window.leSaveSceneConfirm = leSaveSceneConfirm;
window.leApplyTransform = leApplyTransform;
window.leToggleScenes = leToggleScenes;
window.leToggleNew = leToggleNew;
window.leOpenScene = leOpenScene;
window.leSceneDefault = leSceneDefault;
window.leSceneDelete = leSceneDelete;
window.leApplyProfile = leApplyProfile;
window.contentAdd = contentAdd;
window.contentAddMedia = contentAddMedia;
window.contentAddCamera = contentAddCamera;
window.leCamClose = leCamClose;
window.leCamAdd = leCamAdd;
window.leMediaClose = leMediaClose;
window.leMediaAdd = leMediaAdd;
window.contentApply = contentApply;
window.contentChangeSingle = contentChangeSingle;
window.contentRemove = contentRemove;
window.contentClear = contentClear;
window.contentDuplicate = contentDuplicate;
window.contentMove = contentMove;
window.contentRotationSettings = contentRotationSettings;
window.contentToggleParams = contentToggleParams;
window.contentSaveParams = contentSaveParams;
window.contentFullParams = contentFullParams;
window.captureStepFromLiveCanvas = captureStepFromLiveCanvas;



