/* ============================================================================
   BRIGHTNESS CONTROL - Global and Per-Canvas Brightness Management
   ============================================================================ */

import { escapeHtml, onReady, wire } from '../utils/helpers.js';

let brightnessUpdateTimer = null;

/**
 * Update global brightness
 */
async function updateGlobalBrightness(value) {
  const brightness = parseInt(value) / 100.0;
  document.getElementById('global-brightness-value').textContent = `${value}%`;

  // Debounce API calls
  clearTimeout(brightnessUpdateTimer);
  brightnessUpdateTimer = setTimeout(async () => {
    try {
      await window.api.post('/api/brightness/global', { brightness });
    } catch {
      return;
    }
  }, 150);
}

/**
 * Update per-canvas brightness
 */
const canvasBrightnessTimers = new Map();

async function updateCanvasBrightness(canvasName, value) {
  const brightness = parseInt(value) / 100.0;

  // Debounce API calls, per canvas
  clearTimeout(canvasBrightnessTimers.get(canvasName));
  canvasBrightnessTimers.set(canvasName, setTimeout(async () => {
    try {
      await window.api.post(`/api/brightness/canvas/${encodeURIComponent(canvasName)}`, { brightness });
    } catch {
      return;
    }
  }, 150));
}

/**
 * Fetch current brightness levels
 */
export async function fetchBrightnessLevels() {
  try {
    const globalResult = await window.api.get('/api/brightness/global');
    const percentage = globalResult.data.percentage;
    document.getElementById('global-brightness').value = percentage;
    document.getElementById('global-brightness-value').textContent = `${percentage}%`;

    const canvasesResult = await window.api.get('/api/brightness/canvases');
    displayCanvasBrightnessControls(canvasesResult.data);
  } catch (error) {
    console.error('Failed to fetch brightness levels:', error);
  }
}

/**
 * Display per-canvas brightness controls
 */
function displayCanvasBrightnessControls(canvases) {
  const container = document.getElementById('canvas-brightness-controls');

  if (!canvases || canvases.length === 0) {
    container.innerHTML = '';
    return;
  }

  // Canvas names are user text: escaped, and carried in data-name (not in ids or inline code).
  const html = canvases.map(canvas => `
    <div class="canvas-brightness-item">
      <label class="brightness-control">
        <span>${escapeHtml(canvas.name)}</span>
        <div class="slider-with-value">
          <input type="range" min="0" max="100" value="${canvas.percentage}" data-name="${escapeHtml(canvas.name)}">
          <span class="canvas-brightness-value">${canvas.percentage}%</span>
        </div>
      </label>
    </div>
  `).join('');

  container.innerHTML = html;
  if (!container._brightnessBound) {
    container._brightnessBound = true;
    container.addEventListener('input', event => {
      const input = event.target.closest('input[data-name]');
      if (!input) return;
      input.parentElement.querySelector('.canvas-brightness-value').textContent = `${input.value}%`;
      updateCanvasBrightness(input.dataset.name, input.value);
    });
  }
}

// Controls of #global-controls in index.html: wired here instead of inline handlers.
onReady(() => wire(document.getElementById('global-controls'), {
  input: {
    'update-global-brightness': (el, event) => updateGlobalBrightness(el.value)
  }
}));
