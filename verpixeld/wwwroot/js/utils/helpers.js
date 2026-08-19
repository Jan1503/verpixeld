/* ============================================================================
   UTILITY FUNCTIONS
   Common helpers used throughout the application
   ============================================================================ */

/**
 * Escape HTML to prevent XSS
 */
function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

/**
 * Format uptime in seconds to human readable string
 */
function formatUptime(seconds) {
  if (typeof seconds !== 'number' || isNaN(seconds) || seconds < 0) {
    return '0s';
  }

  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = Math.floor(seconds % 60);

  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  } else if (minutes > 0) {
    return `${minutes}m ${secs}s`;
  } else {
    return `${secs}s`;
  }
}

/**
 * Set button loading state with spinner
 */
function setButtonLoading(button, loading = true) {
  if (!button) return;
  
  if (loading) {
    // Store original text
    if (!button.dataset.originalText) {
      button.dataset.originalText = button.innerHTML;
    }
    button.innerHTML = `<span class="btn-text">${button.dataset.originalText}</span>`;
    button.classList.add('loading');
    button.disabled = true;
  } else {
    button.classList.remove('loading');
    button.disabled = false;
    if (button.dataset.originalText) {
      button.innerHTML = button.dataset.originalText;
    }
  }
}

/**
 * Update slider value display
 */
function updateSlider(id) {
  const slider = document.getElementById(id);
  const valueDisplay = document.getElementById(id + '-value');
  if (slider && valueDisplay) {
    valueDisplay.textContent = slider.value;
  }
}

/**
 * Create color picker with alpha support
 */
function createColorInputWithAlpha(paramName, currentValue, onChangeCallback) {
  // Parse current color value
  let hexColor = '#000000';
  let alpha = 1.0;

  // Normalize the color value
  let colorStr = '';
  if (typeof currentValue === 'string') {
    colorStr = currentValue.trim();
    // Add # if missing
    if (colorStr && !colorStr.startsWith('#')) {
      colorStr = '#' + colorStr;
    }
  } else if (typeof currentValue === 'number') {
    // Handle numeric color values (e.g., from SKColor which uses ARGB as uint32)
    colorStr = '#' + (currentValue >>> 0).toString(16).padStart(8, '0');
  }

  if (colorStr.startsWith('#')) {
    if (colorStr.length === 9) {
      // 8-digit hex: #AARRGGBB (ARGB format)
      const alphaHex = colorStr.substring(1, 3);
      const colorHex = colorStr.substring(3, 9);
      
      if (/^[0-9A-Fa-f]{6}$/.test(colorHex)) {
        hexColor = '#' + colorHex;
        alpha = parseInt(alphaHex, 16) / 255;
      }
    } else if (colorStr.length === 7) {
      // 6-digit hex: #RRGGBB (no alpha)
      const colorHex = colorStr.substring(1, 7);
      if (/^[0-9A-Fa-f]{6}$/.test(colorHex)) {
        hexColor = colorStr;
        alpha = 1.0;
      }
    }
  }

  const alphaPercent = Math.round(alpha * 100);

  return `
    <div class="color-alpha-picker">
      <input type="color" id="${paramName}-color" value="${hexColor}" 
             onchange="${onChangeCallback}">
      <div class="alpha-slider-group">
        <label class="alpha-label">Alpha:</label>
        <input type="range" id="${paramName}-alpha" min="0" max="100" 
               value="${alphaPercent}" step="1"
               oninput="document.getElementById('${paramName}-alpha-value').textContent = this.value + '%'; ${onChangeCallback}">
        <span id="${paramName}-alpha-value" class="alpha-value">${alphaPercent}%</span>
      </div>
    </div>
  `;
}

/**
 * Get combined color+alpha value from inputs (ARGB format)
 */
function getColorWithAlpha(paramName) {
  const colorInput = document.getElementById(`${paramName}-color`);
  const alphaInput = document.getElementById(`${paramName}-alpha`);

  if (!colorInput || !alphaInput) {
    return '#FF000000';
  }

  const hex = colorInput.value;
  const alpha = parseInt(alphaInput.value);
  const alphaHex = Math.round((alpha / 100) * 255).toString(16).padStart(2, '0');

  // Return ARGB format: #AARRGGBB
  return `#${alphaHex}${hex.substring(1)}`;
}

/**
 * Compare values for change detection (handles type coercion)
 */
function valuesEqual(a, b) {
  // Handle undefined/null
  if (a === undefined || a === null) {
    return b === undefined || b === null;
  }
  if (b === undefined || b === null) {
    return false;
  }
  
  // Handle numbers (compare with tolerance for floats)
  if (typeof a === 'number' && typeof b === 'number') {
    return Math.abs(a - b) < 0.0001;
  }
  
  // Handle number vs string comparison
  if (typeof a === 'number' || typeof b === 'number') {
    const numA = parseFloat(a);
    const numB = parseFloat(b);
    if (!isNaN(numA) && !isNaN(numB)) {
      return Math.abs(numA - numB) < 0.0001;
    }
  }
  
  // Handle booleans
  if (typeof a === 'boolean' || typeof b === 'boolean') {
    const boolA = String(a).toLowerCase() === 'true';
    const boolB = String(b).toLowerCase() === 'true';
    return boolA === boolB;
  }
  
  // String comparison
  return String(a) === String(b);
}

/**
 * Convert hex color to RGBA string
 */
function hexToRgba(hex, alpha = 1) {
  const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
  if (result) {
    return `rgba(${parseInt(result[1], 16)}, ${parseInt(result[2], 16)}, ${parseInt(result[3], 16)}, ${alpha})`;
  }
  return hex;
}

/**
 * Generate a unique GUID
 */
function generateGuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
    const r = Math.random() * 16 | 0;
    const v = c === 'x' ? r : (r & 0x3 | 0x8);
    return v.toString(16);
  });
}

/**
 * Debounce function execution
 */
function debounce(func, wait) {
  let timeout;
  return function executedFunction(...args) {
    const later = () => {
      clearTimeout(timeout);
      func(...args);
    };
    clearTimeout(timeout);
    timeout = setTimeout(later, wait);
  };
}

/**
 * Throttle function execution
 */
function throttle(func, limit) {
  let inThrottle;
  return function(...args) {
    if (!inThrottle) {
      func.apply(this, args);
      inThrottle = true;
      setTimeout(() => inThrottle = false, limit);
    }
  };
}

/**
 * Refresh all canvas selectors across the application
 * Called when canvases are added or removed
 */
async function refreshAllCanvasSelectors() {
  try {
    // Fetch current canvas list
    const result = await window.api.get('/api/canvas/stack');
    if (!result.data) return;
    
    const canvases = result.data;
    
    // Build options HTML
    const optionsHtml = canvases.map(c => 
      `<option value="${c.name}">${c.name} (${c.width}\u00D7${c.height})</option>`
    ).join('');
    
    // Update draw target canvas selector
    const drawSelect = document.getElementById('draw-target-canvas');
    if (drawSelect) {
      const currentValue = drawSelect.value;
      drawSelect.innerHTML = optionsHtml;
      // Restore selection if still exists
      if (canvases.some(c => c.name === currentValue)) {
        drawSelect.value = currentValue;
      }
    }
    
    // Update camera target canvas selector
    const cameraSelect = document.getElementById('camera-target-canvas');
    if (cameraSelect) {
      const currentValue = cameraSelect.value;
      cameraSelect.innerHTML = optionsHtml;
      if (canvases.some(c => c.name === currentValue)) {
        cameraSelect.value = currentValue;
      }
    }
    
    // Update media/video target canvas selector
    const mediaSelect = document.getElementById('media-target-canvas');
    if (mediaSelect) {
      const currentValue = mediaSelect.value;
      mediaSelect.innerHTML = canvases.map(c => 
        `<option value="${c.name}">${c.name}${c.name === 'Main' ? ' (Default)' : ''}</option>`
      ).join('');
      if (canvases.some(c => c.name === currentValue)) {
        mediaSelect.value = currentValue;
      }
    }
    
    // Update AI target canvas selector
    const aiSelect = document.getElementById('ai-target-canvas');
    if (aiSelect) {
      const currentValue = aiSelect.value;
      aiSelect.innerHTML = optionsHtml;
      if (canvases.some(c => c.name === currentValue)) {
        aiSelect.value = currentValue;
      }
    }

    // Update visualizer canvas selector
    if (typeof refreshVisualizerStatus === 'function') {
      await refreshVisualizerStatus();
    }
    
  } catch (error) {
    console.error('Failed to refresh canvas selectors:', error);
  }
}

// Aliases for backward compatibility
function updateDrawTargetCanvases() { refreshAllCanvasSelectors(); }
function updateCameraTargetCanvases() { refreshAllCanvasSelectors(); }
function populateDemoCanvasSelector() { refreshAllCanvasSelectors(); }

// Expose globally
window.escapeHtml = escapeHtml;
window.formatUptime = formatUptime;
window.setButtonLoading = setButtonLoading;
window.updateSlider = updateSlider;
window.createColorInputWithAlpha = createColorInputWithAlpha;
window.getColorWithAlpha = getColorWithAlpha;
window.valuesEqual = valuesEqual;
window.hexToRgba = hexToRgba;
window.generateGuid = generateGuid;
window.debounce = debounce;
window.throttle = throttle;
window.refreshAllCanvasSelectors = refreshAllCanvasSelectors;
window.updateDrawTargetCanvases = updateDrawTargetCanvases;
window.updateCameraTargetCanvases = updateCameraTargetCanvases;
window.populateDemoCanvasSelector = populateDemoCanvasSelector;