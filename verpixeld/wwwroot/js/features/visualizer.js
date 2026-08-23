/* ============================================================================
   AUDIO VISUALIZER - Real-time music visualization with FFT analysis
   ============================================================================ */

let visualizerStatus = {
  isRunning: false,
  targetCanvasId: null,
  mode: 'SpectrumBars',
  colorScheme: 'Rainbow',
  sensitivity: 1.0,
  smoothing: 0.7,
  availableCanvases: [],
  modes: [],
  colorSchemes: []
};

/**
 * Initialize visualizer controls
 */
async function initVisualizer() {
  await refreshVisualizerStatus();
}

/**
 * Refresh visualizer status from server
 */
async function refreshVisualizerStatus() {
  try {
    const result = await window.api.get('/api/visualizer/status');
    if (result.data) {
      visualizerStatus = result.data;
      updateVisualizerUI();
    }
  } catch {
    return;
  }
}

/**
 * Update the visualizer UI based on current status
 */
function updateVisualizerUI() {
  // Update canvas selector
  const canvasSelect = document.getElementById('visualizer-canvas');
  if (canvasSelect && visualizerStatus.availableCanvases) {
    const currentValue = canvasSelect.value;
    const list = (typeof contentTargetCanvases === 'function'
      ? contentTargetCanvases(visualizerStatus.availableCanvases.map(c => ({ name: c.id || c.name, id: c.id, isSystem: c.isSystem })))
      : visualizerStatus.availableCanvases);
    canvasSelect.innerHTML = list.map(c =>
      `<option value="${c.id || c.name}">${c.name || c.id}</option>`
    ).join('');
    
    // Restore selection or use current target
    if (visualizerStatus.targetCanvasId) {
      canvasSelect.value = visualizerStatus.targetCanvasId;
    } else if (currentValue) {
      canvasSelect.value = currentValue;
    }
  }
  
  // Update mode selector
  const modeSelect = document.getElementById('visualizer-mode');
  if (modeSelect && visualizerStatus.modes && modeSelect.options.length === 0) {
    modeSelect.innerHTML = visualizerStatus.modes.map(m => {
      const label = m.replace(/([A-Z])/g, ' $1').trim(); // CamelCase to spaces
      return `<option value="${m}">${label}</option>`;
    }).join('');
  }
  if (modeSelect) {
    modeSelect.value = visualizerStatus.mode;
  }
  
  // Update color scheme selector
  const colorSelect = document.getElementById('visualizer-color');
  if (colorSelect && visualizerStatus.colorSchemes && colorSelect.options.length === 0) {
    colorSelect.innerHTML = visualizerStatus.colorSchemes.map(c => 
      `<option value="${c}">${c}</option>`
    ).join('');
  }
  if (colorSelect) {
    colorSelect.value = visualizerStatus.colorScheme;
  }
  
  // Update sensitivity slider
  const sensitivitySlider = document.getElementById('visualizer-sensitivity');
  const sensitivityValue = document.getElementById('visualizer-sensitivity-value');
  if (sensitivitySlider) {
    sensitivitySlider.value = visualizerStatus.sensitivity;
  }
  if (sensitivityValue) {
    sensitivityValue.textContent = visualizerStatus.sensitivity.toFixed(1) + 'x';
  }
  
  // Update smoothing slider
  const smoothingSlider = document.getElementById('visualizer-smoothing');
  const smoothingValue = document.getElementById('visualizer-smoothing-value');
  if (smoothingSlider) {
    smoothingSlider.value = visualizerStatus.smoothing;
  }
  if (smoothingValue) {
    smoothingValue.textContent = Math.round(visualizerStatus.smoothing * 100) + '%';
  }
  
  // Update button states
  const startBtn = document.getElementById('visualizer-start-btn');
  const stopBtn = document.getElementById('visualizer-stop-btn');
  const statusIndicator = document.getElementById('visualizer-status');
  
  if (startBtn) {
    startBtn.disabled = visualizerStatus.isRunning;
    startBtn.textContent = visualizerStatus.isRunning ? 'Running...' : 'Start Visualizer';
  }
  
  if (stopBtn) {
    stopBtn.disabled = !visualizerStatus.isRunning;
  }
  
  if (statusIndicator) {
    if (visualizerStatus.isRunning) {
      statusIndicator.innerHTML = `<span class="status-active">\u{1F3B5} Active on: ${getCanvasName(visualizerStatus.targetCanvasId)}</span>`;
    } else {
      statusIndicator.innerHTML = '<span class="status-inactive">Not running</span>';
    }
  }
}

/**
 * Get canvas name by ID
 */
function getCanvasName(canvasId) {
  const canvas = visualizerStatus.availableCanvases?.find(c => c.id === canvasId);
  return canvas?.name || canvasId || 'Unknown';
}

/**
 * Start the visualizer
 */
async function startVisualizer() {
  const canvasId = document.getElementById('visualizer-canvas')?.value;
  const mode = document.getElementById('visualizer-mode')?.value;
  const colorScheme = document.getElementById('visualizer-color')?.value;
  const sensitivity = parseFloat(document.getElementById('visualizer-sensitivity')?.value) || 1.0;
  const smoothing = parseFloat(document.getElementById('visualizer-smoothing')?.value) || 0.7;
  
  if (!canvasId) {
    window.toast.error('Visualizer', 'Please select a canvas');
    return;
  }
  
  try {
    window.toast.info('Visualizer', 'Starting visualizer...');
    
    const result = await window.api.post('/api/visualizer/start', {
      canvasId,
      mode,
      colorScheme,
      sensitivity,
      smoothing
    });
    
    window.toast.success('Visualizer', result.message || 'Visualizer started');
    await refreshVisualizerStatus();
  } catch (error) {
    window.toast.error('Visualizer', error.message || 'Failed to start visualizer');
  }
}

/**
 * Stop the visualizer
 */
async function stopVisualizer() {
  try {
    window.toast.info('Visualizer', 'Stopping visualizer...');
    
    await window.api.post('/api/visualizer/stop');
    
    window.toast.success('Visualizer', 'Visualizer stopped');
    await refreshVisualizerStatus();
  } catch (error) {
    window.toast.error('Visualizer', error.message || 'Failed to stop visualizer');
  }
}

/**
 * Update visualizer settings (called when sliders/selects change)
 */
async function updateVisualizerSettings() {
  // Only send if visualizer is running
  if (!visualizerStatus.isRunning) return;
  
  const mode = document.getElementById('visualizer-mode')?.value;
  const colorScheme = document.getElementById('visualizer-color')?.value;
  const sensitivity = parseFloat(document.getElementById('visualizer-sensitivity')?.value);
  const smoothing = parseFloat(document.getElementById('visualizer-smoothing')?.value);
  
  try {
    const result = await window.api.put('/api/visualizer/settings', {
      mode,
      colorScheme,
      sensitivity,
      smoothing
    });
    
    if (result.data) {
      visualizerStatus = { ...visualizerStatus, ...result.data };
      // Don't call full updateVisualizerUI to avoid resetting dropdowns while user is selecting
    }
  } catch {
    return;
  }
}

/**
 * Handle sensitivity slider change
 */
function onSensitivityChange(value) {
  const display = document.getElementById('visualizer-sensitivity-value');
  if (display) {
    display.textContent = parseFloat(value).toFixed(1) + 'x';
  }
  // Debounced update
  clearTimeout(window._visualizerSettingsTimeout);
  window._visualizerSettingsTimeout = setTimeout(updateVisualizerSettings, 200);
}

/**
 * Handle smoothing slider change
 */
function onSmoothingChange(value) {
  const display = document.getElementById('visualizer-smoothing-value');
  if (display) {
    display.textContent = Math.round(parseFloat(value) * 100) + '%';
  }
  // Debounced update
  clearTimeout(window._visualizerSettingsTimeout);
  window._visualizerSettingsTimeout = setTimeout(updateVisualizerSettings, 200);
}

/**
 * Handle mode/color change
 */
function onVisualizerModeChange() {
  updateVisualizerSettings();
}

// Expose functions globally
window.initVisualizer = initVisualizer;
window.refreshVisualizerStatus = refreshVisualizerStatus;
window.startVisualizer = startVisualizer;
window.stopVisualizer = stopVisualizer;
window.onSensitivityChange = onSensitivityChange;
window.onSmoothingChange = onSmoothingChange;
window.onVisualizerModeChange = onVisualizerModeChange;
