/* ============================================================================
   GAME INPUT — Studio keyboard → extension Control methods
   The LED wall has no keyboard; keys in this page invoke Flap/Jump/GoLeft/…
   on the selected (or first game) canvas. Category "Controls" only, so audio
   volume shortcuts never steal arrow keys from a game.
   ============================================================================ */

import { api } from '../core/api.js';
import { layerEditor } from './layer-editor.js';

(function () {
  const ALIAS = {
    space: 'Space',
    up: 'ArrowUp',
    down: 'ArrowDown',
    left: 'ArrowLeft',
    right: 'ArrowRight'
  };

  const NAMED_KEYS = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Enter', 'Escape', 'Backspace']);

  let maps = {};
  let lastRefresh = 0;
  let refreshing = false;
  const pending = new Set();

  function isTyping(el) {
    if (!el) return false;
    const tag = (el.tagName || '').toUpperCase();
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
    return !!el.isContentEditable;
  }

  function normalizeKey(raw) {
    const t = (raw || '').trim();
    if (!t) return '';
    const mapped = ALIAS[t.toLowerCase()];
    if (mapped) return mapped;
    if (t.length === 1) return t.toUpperCase();
    return t;
  }

  function parseShortcut(spec) {
    if (!spec) return [];
    return spec.split('|').map(part => {
      const [key, phase] = part.trim().split(':');
      return {
        key: normalizeKey(key),
        phase: (phase || 'down').toLowerCase() === 'up' ? 'up' : 'down'
      };
    }).filter(b => b.key);
  }

  function eventKeys(e) {
    const keys = new Set();
    if (e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar') keys.add('Space');
    if (e.key) {
      if (NAMED_KEYS.has(e.key))
        keys.add(e.key);
      else if (e.key.length === 1) keys.add(e.key.toUpperCase());
    }
    if (e.code && e.code.startsWith('Key') && e.code.length === 4)
      keys.add(e.code.slice(3));
    return keys;
  }

  function canvasNames() {
    const names = [];
    if (typeof layerEditor !== 'undefined') {
      if (Array.isArray(layerEditor.canvases))
        layerEditor.canvases.forEach(c => { if (c && c.name) names.push(c.name); });
      if (layerEditor.contentMap)
        Object.keys(layerEditor.contentMap).forEach(n => names.push(n));
    }
    if (window.currentEditingExtension && window.currentEditingExtension.canvasName)
      names.push(window.currentEditingExtension.canvasName);
    return [...new Set(names)];
  }

  function preferredCanvas() {
    if (typeof layerEditor !== 'undefined' && layerEditor.selected && (maps[layerEditor.selected] || []).length)
      return layerEditor.selected;
    if (window.currentEditingExtension && window.currentEditingExtension.canvasName) {
      const n = window.currentEditingExtension.canvasName;
      if ((maps[n] || []).length) return n;
    }
    for (const name of canvasNames())
      if ((maps[name] || []).length) return name;
    return null;
  }

  function parseControls(list) {
    const bindings = [];
    (list || []).forEach(m => {
      if (!m || !m.keyboardShortcut || !m.name) return;
      if (!/^controls$/i.test(m.category || '')) return;
      parseShortcut(m.keyboardShortcut).forEach(b => {
        bindings.push({ key: b.key, phase: b.phase, name: m.name });
      });
    });
    return bindings;
  }

  async function refresh() {
    if (refreshing) return;
    const names = canvasNames();
    if (!names.length) return;
    if (Date.now() - lastRefresh < 2000) return;
    refreshing = true;
    lastRefresh = Date.now();
    try {
      const next = {};
      await Promise.all(names.map(async name => {
        try {
          const r = await api.get(`/api/layout/methods/${encodeURIComponent(name)}`);
          next[name] = parseControls(r.data || []);
        } catch {
          next[name] = [];
        }
      }));
      maps = next;
    } finally {
      refreshing = false;
    }
  }

  async function invoke(canvas, methodName) {
    const id = canvas + ':' + methodName;
    if (pending.has(id)) return;
    pending.add(id);
    try {
      await api.post(`/api/layout/invoke/${encodeURIComponent(canvas)}`, { methodName, args: [] });
    } catch (err) {
      console.warn('[game-input]', methodName, err.message || err);
    } finally {
      pending.delete(id);
    }
  }

  function onKey(e, phase) {
    if (e.repeat && phase === 'down') return;
    if (isTyping(e.target)) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;

    refresh();

    const canvas = preferredCanvas();
    if (!canvas) return;
    const bindings = maps[canvas] || [];
    if (!bindings.length) return;

    const keys = eventKeys(e);
    let matched = false;
    const fired = new Set();
    for (const b of bindings) {
      if (b.phase !== phase) continue;
      if (!keys.has(b.key)) continue;
      if (fired.has(b.name)) continue;
      fired.add(b.name);
      matched = true;
      invoke(canvas, b.name);
    }
    if (matched) {
      e.preventDefault();
      e.stopPropagation();
    }
  }

  document.addEventListener('keydown', e => onKey(e, 'down'), true);
  document.addEventListener('keyup', e => onKey(e, 'up'), true);

  /* --------------------------------------------------------------------------
     GAMEPAD — standard-mapping pads become Pad* keys (PadA, PadB, PadX, PadY,
     PadLB, PadRB, PadLT, PadRT, PadBack, PadStart, PadUp/Down/Left/Right).
     D-pad and left stick share the PadUp… names. If the game binds no Pad*
     key, the pad falls back to the keyboard keys every game already knows.
     -------------------------------------------------------------------------- */
  const PAD_BUTTONS = {
    0: 'PadA', 1: 'PadB', 2: 'PadX', 3: 'PadY', 4: 'PadLB', 5: 'PadRB', 6: 'PadLT', 7: 'PadRT',
    8: 'PadBack', 9: 'PadStart', 12: 'PadUp', 13: 'PadDown', 14: 'PadLeft', 15: 'PadRight'
  };
  const PAD_FALLBACK = {
    PadA: 'Space', PadB: 'Escape', PadX: 'Space', PadStart: 'Enter', PadBack: 'Escape',
    PadUp: 'ArrowUp', PadDown: 'ArrowDown', PadLeft: 'ArrowLeft', PadRight: 'ArrowRight'
  };
  const PAD_KEYS = Object.values(PAD_BUTTONS);
  const padState = {};
  let padLoop = false;

  function padKey(key, phase) {
    refresh();
    const canvas = preferredCanvas();
    if (!canvas) return;
    const bindings = maps[canvas] || [];
    if (!bindings.length) return;
    const target = bindings.some(b => b.key === key) ? key : (PAD_FALLBACK[key] || key);
    const fired = new Set();
    for (const b of bindings) {
      if (b.phase !== phase || b.key !== target || fired.has(b.name)) continue;
      fired.add(b.name);
      invoke(canvas, b.name);
    }
  }

  function setPad(key, down) {
    if (!!padState[key] === down) return;
    padState[key] = down;
    padKey(key, down ? 'down' : 'up');
  }

  function pollPads() {
    const pads = navigator.getGamepads ? navigator.getGamepads() : [];
    const now = {};
    let any = false;
    for (const p of pads) {
      if (!p || !p.connected) continue;
      any = true;
      p.buttons.forEach((b, i) => {
        const k = PAD_BUTTONS[i];
        if (k && (b.pressed || b.value > 0.5)) now[k] = true;
      });
      const ax = p.axes[0] || 0;
      const ay = p.axes[1] || 0;
      // Hysteresis; vertical needs a firmer push so diagonal steering does not brake.
      const h = (k, on, off) => (padState[k] ? off : on);
      if (ax < -h('PadLeft', 0.5, 0.35)) now.PadLeft = true;
      if (ax > h('PadRight', 0.5, 0.35)) now.PadRight = true;
      if (ay < -h('PadUp', 0.75, 0.55)) now.PadUp = true;
      if (ay > h('PadDown', 0.75, 0.55)) now.PadDown = true;
    }
    if (!document.hidden) PAD_KEYS.forEach(k => setPad(k, !!now[k]));
    if (any) requestAnimationFrame(pollPads);
    else {
      PAD_KEYS.forEach(k => setPad(k, false));
      padLoop = false;
    }
  }

  function startPads() {
    if (padLoop) return;
    padLoop = true;
    requestAnimationFrame(pollPads);
  }

  window.addEventListener('gamepadconnected', startPads);
  if (navigator.getGamepads && Array.from(navigator.getGamepads()).some(p => p)) startPads();
  setInterval(refresh, 4000);
  setTimeout(refresh, 800);

  window.studioGameKeys = function (canvasName) {
    return !!(canvasName && (maps[canvasName] || []).length);
  };
})();
