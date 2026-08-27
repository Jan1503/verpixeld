/* ============================================================================
   GAME INPUT — Studio keyboard → extension Control methods
   The LED wall has no keyboard; keys in this page invoke Flap/Jump/GoLeft/…
   on the selected (or first game) canvas. Category "Controls" only, so audio
   volume shortcuts never steal arrow keys from a game.
   ============================================================================ */

(function () {
  const ALIAS = {
    space: 'Space',
    up: 'ArrowUp',
    down: 'ArrowDown',
    left: 'ArrowLeft',
    right: 'ArrowRight'
  };

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
      if (e.key === 'ArrowUp' || e.key === 'ArrowDown' || e.key === 'ArrowLeft' || e.key === 'ArrowRight')
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
  setInterval(refresh, 4000);
  setTimeout(refresh, 800);

  window.studioGameKeys = function (canvasName) {
    return !!(canvasName && (maps[canvasName] || []).length);
  };
})();
