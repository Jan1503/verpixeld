/* ============================================================================
   CANVAS SELECTORS - every "target canvas" dropdown of the page, refreshed together
   when canvases are added, removed or renamed.
   ============================================================================ */

import { api } from '../core/api.js';
import { contentTargetCanvases, escapeHtml } from '../utils/helpers.js';
import { updateCameraTargetCanvases } from './camera-stream.js';
import { updateDrawTargetCanvases } from './draw-mode.js';
import { populateMediaCanvasSelector } from './media-player.js';
import { refreshVisualizerStatus } from './visualizer.js';

/**
 * Refresh all canvas selectors across the application
 * Called when canvases are added or removed
 */
export async function refreshAllCanvasSelectors() {
  try {
    // Fetch current canvas list
    const result = await api.get('/api/canvas/stack');
    if (!result.data) return;
    
    const canvases = contentTargetCanvases(result.data);
    
    const optionsHtml = canvases.map(c => 
      `<option value="${escapeHtml(c.name)}">${escapeHtml(c.name)} (${c.width}\u00D7${c.height})</option>`
    ).join('');

    const fillSelect = (id) => {
      const el = document.getElementById(id);
      if (!el) return;
      const currentValue = el.value;
      el.innerHTML = optionsHtml;
      if (canvases.some(c => c.name === currentValue))
        el.value = currentValue;
    };

    // Draw and camera size themselves to the chosen canvas: their own modules fill those lists
    // (with the canvas sizes), a plain refill here would drop the sizes and leave a stale size.
    updateDrawTargetCanvases();
    updateCameraTargetCanvases();
    fillSelect('ai-target-canvas');
    fillSelect('ai-schedule-canvas');
    fillSelect('gallery-slideshow-canvas');
    fillSelect('upload-target-canvas');
    
    // The media target list has the media layer first; media-player.js builds it.
    populateMediaCanvasSelector();

    // Update visualizer canvas selector
    await refreshVisualizerStatus();

  } catch (error) {
    console.error('Failed to refresh canvas selectors:', error);
  }
}
