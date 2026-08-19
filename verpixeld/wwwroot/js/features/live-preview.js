/* ============================================================================
   LIVE PREVIEW - MJPEG stream of the LED matrix output
   ============================================================================ */

let _previewActive = false;

// Base dimensions of the LED matrix output
const PREVIEW_BASE_WIDTH = 384;
const PREVIEW_BASE_HEIGHT = 192;

/**
 * Toggle the live preview stream on/off
 */
function togglePreview() {
  if (_previewActive) {
    stopPreview();
  } else {
    startPreview();
  }
}

/**
 * Start the MJPEG live preview stream
 */
function startPreview() {
  const img = document.getElementById('preview-stream');
  const placeholder = document.getElementById('preview-placeholder');
  const btn = document.getElementById('preview-toggle-btn');
  const statusText = document.getElementById('preview-status-text');

  if (!img || !placeholder) return;

  // Apply current scale and start the stream
  const scale = parseFloat(document.getElementById('preview-scale')?.value) || 1;
  applyPreviewScale(img, scale);

  img.src = `${API_BASE}/api/preview/stream`;
  img.style.display = 'block';
  placeholder.style.display = 'none';

  _previewActive = true;

  if (btn) {
    btn.textContent = 'Stop';
    btn.classList.remove('btn-primary');
    btn.classList.add('btn-danger');
  }

  if (statusText) statusText.textContent = 'Streaming...';

  // Handle stream errors (server restart, network issue)
  img.onerror = () => {
    if (_previewActive) {
      if (statusText) statusText.textContent = 'Connection lost — retrying...';
      // Retry after a short delay
      setTimeout(() => {
        if (_previewActive) {
          img.src = `${API_BASE}/api/preview/stream?t=${Date.now()}`;
        }
      }, 2000);
    }
  };

  img.onload = () => {
    if (statusText) statusText.textContent = 'Streaming';
  };
}

/**
 * Stop the MJPEG live preview stream
 */
function stopPreview() {
  const img = document.getElementById('preview-stream');
  const placeholder = document.getElementById('preview-placeholder');
  const btn = document.getElementById('preview-toggle-btn');
  const statusText = document.getElementById('preview-status-text');

  if (img) {
    img.onerror = null;
    img.onload = null;
    img.src = '';           // Disconnect the MJPEG stream
    img.style.display = 'none';
  }

  if (placeholder) placeholder.style.display = 'flex';

  _previewActive = false;

  if (btn) {
    btn.textContent = 'Start';
    btn.classList.remove('btn-danger');
    btn.classList.add('btn-primary');
  }

  if (statusText) statusText.textContent = 'Stopped';
}

/**
 * Set the preview scale (called by the range slider)
 */
function setPreviewScale(value) {
  const scale = parseFloat(value) || 1;
  const label = document.getElementById('preview-scale-label');
  if (label) label.textContent = `${scale}x`;

  const img = document.getElementById('preview-stream');
  if (img) applyPreviewScale(img, scale);

  const placeholder = document.getElementById('preview-placeholder');
  if (placeholder) {
    placeholder.style.width = `${PREVIEW_BASE_WIDTH * scale}px`;
    placeholder.style.height = `${PREVIEW_BASE_HEIGHT * scale}px`;
  }
}

/**
 * Apply pixel-exact dimensions to the preview image
 */
function applyPreviewScale(img, scale) {
  img.style.width = `${PREVIEW_BASE_WIDTH * scale}px`;
  img.style.height = `${PREVIEW_BASE_HEIGHT * scale}px`;
}

/**
 * Check and show simulation mode badge
 */
async function checkSimulationMode() {
  try {
    const result = await window.api.get('/api/settings');
    if (result.data?.systemInfo?.simulationMode) {
      const badge = document.getElementById('preview-sim-badge');
      if (badge) badge.style.display = 'inline-block';
    }
  } catch (error) {
    // Ignore — non-critical
  }
}

// Auto-stop when leaving the settings tab to free the connection
window.addEventListener('tabChanged', (e) => {
  if (e.detail?.tab !== 'settings' && _previewActive) {
    stopPreview();
  }

  if (e.detail?.tab === 'settings') {
    checkSimulationMode();
  }
});

// Expose globally
window.togglePreview = togglePreview;
window.startPreview = startPreview;
window.stopPreview = stopPreview;
window.setPreviewScale = setPreviewScale;
