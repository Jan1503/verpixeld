/* ============================================================================
   CANVAS EFFECTS - the effect chain of the canvas selected in the Studio
   (its own filters, applied before it is layered; the Effects tab is the wall-wide chain).
   ============================================================================ */

import { api } from '../core/api.js';
import { ensureExtStyles, readFieldValue, renderField } from './extension-management.js';
import { dataAttrs, escapeHtml, notifyError, onReady, wire } from '../utils/helpers.js';

let catalog = null;            // effects the host offers, alphabetical
const chains = new Map();      // canvas name -> last known effect list (paint at once, refresh after)
const openSettings = new Set(); // "canvas|index" whose settings panel is open
let shown = null;              // canvas whose effects are on screen
const timers = new Map();      // debounce per effect setting

const path = name => `/api/canvas/${encodeURIComponent(name)}/effects`;

async function loadCatalog() {
  if (catalog) return catalog;
  const r = await api.get('/api/filters/available');
  catalog = (r.data || []).slice().sort((a, b) =>
    (a.displayName || a.name).localeCompare(b.displayName || b.name, undefined, { sensitivity: 'base' }));
  return catalog;
}

const schemaFor = type => (catalog || []).find(f => f.name === type);

/** Shows the effects of `canvasName` in #le-effects (cached list first, then the live one). */
export async function renderCanvasEffects(canvasName) {
  shown = canvasName;
  const host = document.getElementById('le-effects');
  if (!host) return;
  ensureExtStyles(); // the settings fields are the extension editor's fields
  if (chains.has(canvasName)) paint(host, canvasName, chains.get(canvasName));
  else host.innerHTML = '<p class="le-hint">Loading effects…</p>';
  try {
    await loadCatalog();
    const r = await api.get(path(canvasName));
    chains.set(canvasName, r.data || []);
    const now = document.getElementById('le-effects');
    if (now && shown === canvasName) paint(now, canvasName, r.data || []);
  } catch (error) {
    const now = document.getElementById('le-effects');
    if (now && shown === canvasName) now.innerHTML = '<p class="le-hint">Effects are not available.</p>';
    console.error('[effects]', error);
  }
}

function fieldId(canvas, index, param) {
  return 'lefx_' + (canvas + '_' + index + '_' + param).replace(/[^A-Za-z0-9_-]/g, '_');
}

function paint(host, canvas, effects) {
  const rows = effects.map((fx, i) => {
    const schema = schemaFor(fx.filterType);
    // Strength and on/off have their own controls in the row.
    const params = (schema?.parameters || []).filter(p => !p.isReadOnly && p.name !== 'Intensity' && p.name !== 'Enabled');
    const pct = Math.round((fx.intensity ?? 1) * 100);
    const open = openSettings.has(canvas + '|' + i);
    const fields = params.map(p => {
      const id = fieldId(canvas, i, p.name);
      const value = fx.parameters?.[p.name] ?? p.defaultValue;
      return renderField(p, value, id, { action: 'le-fx-param', index: i, param: p.name, field: id });
    }).join('');
    return `
      <div class="le-fx${fx.enabled ? '' : ' is-off'}">
        <div class="le-fx-head">
          <label class="le-check" title="On / off"><input type="checkbox" ${fx.enabled ? 'checked' : ''}${dataAttrs({ change: 'le-fx-enabled', index: i })}></label>
          <span class="le-fx-name" title="${escapeHtml(schema?.description || '')}">${escapeHtml(fx.displayName || fx.filterType)}</span>
          <span class="le-fx-acts">
            ${params.length ? `<button class="le-ico${open ? ' active' : ''}" title="Settings"${dataAttrs({ action: 'le-fx-settings', index: i })}>⚙</button>` : ''}
            <button class="le-ico" title="Earlier in the chain" ${i === 0 ? 'disabled' : ''}${dataAttrs({ action: 'le-fx-move', index: i, dir: -1 })}>▲</button>
            <button class="le-ico" title="Later in the chain" ${i === effects.length - 1 ? 'disabled' : ''}${dataAttrs({ action: 'le-fx-move', index: i, dir: 1 })}>▼</button>
            <button class="le-ico le-ico-danger" title="Remove effect"${dataAttrs({ action: 'le-fx-remove', index: i })}>✕</button>
          </span>
        </div>
        <div class="le-row le-fx-strength">
          <span class="le-sel-lbl">Strength</span>
          <input type="range" min="0" max="100" value="${pct}"${dataAttrs({ input: 'le-fx-intensity', index: i })}>
          <span class="le-sel-lbl le-fx-pct">${pct}%</span>
        </div>
        ${params.length ? `<div class="le-fx-params ext-group-body"${open ? '' : ' hidden'}>${fields}</div>` : ''}
      </div>`;
  }).join('');

  const groups = {};
  for (const f of catalog || []) (groups[f.category || 'Other'] ||= []).push(f);
  const options = Object.keys(groups).sort().map(cat =>
    `<optgroup label="${escapeHtml(cat)}">${groups[cat].map(f =>
      `<option value="${escapeHtml(f.name)}">${escapeHtml(f.displayName || f.name)}</option>`).join('')}</optgroup>`).join('');

  host.innerHTML = `
    ${rows || '<p class="le-hint">No effects on this canvas. Effects apply to this canvas only; the Effects tab works on the whole wall.</p>'}
    <div class="le-row le-fx-add">
      <select id="le-fx-type" aria-label="Effect to add">${options}</select>
      <button class="btn btn-small btn-secondary" data-action="le-fx-add">+ Effect</button>
    </div>`;
}

/** Settings change on the live effect; sliders are debounced so dragging sends a few requests, not hundreds. */
function update(index, parameters, delay = 120) {
  const canvas = shown;
  if (!canvas) return;
  const key = canvas + '|' + index;
  clearTimeout(timers.get(key));
  timers.set(key, setTimeout(async () => {
    try {
      const r = await api.put(`${path(canvas)}/${index}`, { parameters });
      const list = chains.get(canvas);
      if (list && r.data) list[index] = r.data;
    } catch (error) {
      notifyError('Effects', error);
    }
  }, delay));
}

const index = el => Number(el.dataset.index);

onReady(() => wire(document.getElementById('tab-studio'), {
  click: {
    'le-fx-add': async () => {
      const type = document.getElementById('le-fx-type')?.value;
      if (!type || !shown) return;
      await api.post(path(shown), { filterType: type });
      await renderCanvasEffects(shown);
    },
    'le-fx-remove': async el => {
      await api.del(`${path(shown)}/${index(el)}`);
      openSettings.clear();
      await renderCanvasEffects(shown);
    },
    'le-fx-move': async el => {
      await api.post(`${path(shown)}/${index(el)}/move?delta=${el.dataset.dir}`);
      openSettings.clear();
      await renderCanvasEffects(shown);
    },
    'le-fx-settings': el => {
      const key = shown + '|' + index(el);
      const panel = el.closest('.le-fx')?.querySelector('.le-fx-params');
      if (!panel) return;
      panel.hidden = !panel.hidden;
      el.classList.toggle('active', !panel.hidden);
      if (panel.hidden) openSettings.delete(key); else openSettings.add(key);
    }
  },
  change: {
    'le-fx-enabled': el => {
      el.closest('.le-fx')?.classList.toggle('is-off', !el.checked);
      update(index(el), { Enabled: el.checked }, 0);
    },
    'le-fx-param': el => sendParam(el)
  },
  input: {
    'le-fx-intensity': el => {
      const pct = el.closest('.le-fx-strength')?.querySelector('.le-fx-pct');
      if (pct) pct.textContent = el.value + '%';
      update(index(el), { Intensity: Number(el.value) / 100 });
    },
    'le-fx-param': el => sendParam(el)
  }
}));

function sendParam(el) {
  const i = index(el);
  const fx = (chains.get(shown) || [])[i];
  const field = (schemaFor(fx?.filterType)?.parameters || []).find(p => p.name === el.dataset.param);
  if (!field) return;
  const value = readFieldValue(field, el.dataset.field);
  const span = document.getElementById(el.dataset.field + '-val');
  if (span && el.type === 'range') span.textContent = el.value;
  update(i, { [field.name]: value });
}
