/* ============================================================================
   BRIGHTNESS CONTROL - Global and Per-Canvas Brightness Management
   ============================================================================ */

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
async function updateCanvasBrightness(canvasName, value) {
  const brightness = parseInt(value) / 100.0;
  document.getElementById(`${canvasName}-brightness-value`).textContent = `${value}%`;

  // Debounce API calls
  clearTimeout(window[`${canvasName}_brightnessTimer`]);
  window[`${canvasName}_brightnessTimer`] = setTimeout(async () => {
    try {
      await window.api.post(`/api/brightness/canvas/${encodeURIComponent(canvasName)}`, { brightness });
    } catch {
      return;
    }
  }, 150);
}

/**
 * Fetch current brightness levels
 */
async function fetchBrightnessLevels() {
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

  const html = canvases.map(canvas => `
    <div class="canvas-brightness-item">
      <div class="brightness-control">
        <label for="${canvas.name}-brightness">${canvas.name}</label>
        <div class="slider-with-value">
          <input type="range" id="${canvas.name}-brightness" 
                 min="0" max="100" value="${canvas.percentage}"
                 oninput="updateCanvasBrightness('${canvas.name}', this.value)">
          <span id="${canvas.name}-brightness-value">${canvas.percentage}%</span>
        </div>
      </div>
    </div>
  `).join('');

  container.innerHTML = html;
}

// Expose globally
window.updateGlobalBrightness = updateGlobalBrightness;
window.updateCanvasBrightness = updateCanvasBrightness;
window.fetchBrightnessLevels = fetchBrightnessLevels;
window.displayCanvasBrightnessControls = displayCanvasBrightnessControls;
