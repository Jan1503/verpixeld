/* ============================================================================
   VISUAL CANVAS EDITOR - Overlay Canvas Creation
   ============================================================================ */

// Actual display size — resolved from the live canvas stack each time the dialog opens (no longer
// hardcoded to the 384x192 panel), so overlay canvases are sized for whatever display is connected.
let DISPLAY_WIDTH = 384;
let DISPLAY_HEIGHT = 192;

let canvasEditorState = {
  isDrawing: false,
  startX: 0,
  startY: 0,
  currentX: 0,
  currentY: 0,
  scale: 1,
  snapToGrid: true,
  gridSize: 16,
  showExisting: true,
  existingCanvases: [],
  maxZOrder: 0,
  selection: null
};

/**
 * Show dialog to add new overlapping canvas with visual editor
 */
async function showAddCanvasDialog() {
  try {
    // Authoritative physical display size from the matrix (not derived from canvases, which may include
    // stale oversized overlays from a saved layout made for a different display).
    try {
      const disp = await window.api.get('/api/canvas/display');
      if (disp && disp.data && disp.data.width > 0 && disp.data.height > 0) {
        DISPLAY_WIDTH = disp.data.width;
        DISPLAY_HEIGHT = disp.data.height;
      }
    } catch (e) { /* fall back to defaults below */ }

    const result = await window.api.get('/api/canvas/stack');

    canvasEditorState.existingCanvases = [];
    canvasEditorState.maxZOrder = 0;
    canvasEditorState.selection = null;
    
    let maxZOrder = 0;
    if (result.data && result.data.length > 0) {
      canvasEditorState.existingCanvases = result.data;
      maxZOrder = Math.max(...result.data.map(c => c.zOrder));
      canvasEditorState.maxZOrder = maxZOrder;
    }
    console.log('[CanvasEditor] display size', DISPLAY_WIDTH + 'x' + DISPLAY_HEIGHT);

    const html = `
    <div class="modal-overlay" id="add-canvas-modal">
      <div class="modal-content canvas-editor-modal">
        
        <div class="modal-header">
          <h2>${ICONS.ADD} Create Overlay Canvas</h2>
          <button class="modal-close" onclick="closeAddCanvasModal()">${ICONS.CLOSE}</button>
        </div>
        
        <div class="modal-body">
          <p class="text-muted canvas-editor-hint">
            Draw a rectangle on the display preview to define your canvas area.
          </p>
          
          <!-- Visual Canvas Editor -->
          <div class="canvas-editor-container">
            <canvas id="canvas-editor" class="canvas-editor-canvas"></canvas>
            <div class="canvas-editor-coords">
              <span>X: <strong id="coord-x">-</strong></span>
              <span>Y: <strong id="coord-y">-</strong></span>
              <span>W: <strong id="coord-w">-</strong></span>
              <span>H: <strong id="coord-h">-</strong></span>
            </div>
          </div>
          
          <!-- Editor Options -->
          <div class="canvas-editor-options">
            <label class="checkbox-label">
              <input type="checkbox" id="snap-to-grid" checked onchange="updateCanvasEditorOption('snap', this.checked)">
              <span>Snap to grid</span>
              <select id="grid-size" onchange="updateCanvasEditorOption('gridSize', parseInt(this.value))">
                <option value="8">8px</option>
                <option value="16" selected>16px</option>
                <option value="32">32px</option>
              </select>
            </label>
            <label class="checkbox-label">
              <input type="checkbox" id="show-existing" checked onchange="updateCanvasEditorOption('showExisting', this.checked)">
              <span>Show existing</span>
            </label>
          </div>
          
          <!-- Quick Presets -->
          <div class="canvas-editor-presets">
            <span class="presets-label">Presets:</span>
            <button type="button" class="btn-preset" onclick="applyCanvasPreset('fullscreen')">Full</button>
            <button type="button" class="btn-preset" onclick="applyCanvasPreset('topbar')">Top</button>
            <button type="button" class="btn-preset" onclick="applyCanvasPreset('bottombar')">Bottom</button>
            <button type="button" class="btn-preset" onclick="applyCanvasPreset('center')">Center</button>
            <button type="button" class="btn-preset" onclick="applyCanvasPreset('corner')">Corner</button>
          </div>
          
          <!-- Canvas Settings -->
          <div class="canvas-editor-settings">
            <div class="setting-row">
              <label for="new-canvas-name">Name</label>
              <input type="text" id="new-canvas-name" placeholder="Overlay" value="Overlay${maxZOrder + 1}">
            </div>
            <div class="setting-row">
              <label for="new-canvas-zorder">Z-Order</label>
              <input type="number" id="new-canvas-zorder" value="${maxZOrder + 1}" min="0" style="width: 80px;">
              <span class="setting-hint">Higher = on top</span>
            </div>
            <div class="setting-row">
              <label>Opacity</label>
              <div class="slider-compact">
                <input type="range" id="new-canvas-opacity" min="0" max="100" value="100" 
                       oninput="document.getElementById('new-canvas-opacity-value').textContent = this.value + '%'">
                <span id="new-canvas-opacity-value">100%</span>
              </div>
            </div>
          </div>
          
          <!-- Hidden inputs for coordinates -->
          <input type="hidden" id="new-canvas-x" value="0">
          <input type="hidden" id="new-canvas-y" value="0">
          <input type="hidden" id="new-canvas-width" value="0">
          <input type="hidden" id="new-canvas-height" value="0">
        </div>
        
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="closeAddCanvasModal()">Cancel</button>
          <button class="btn btn-primary" id="create-canvas-btn" onclick="confirmAddCanvas()" disabled>
            Create Canvas
          </button>
        </div>
        
      </div>
    </div>
  `;
  
    document.body.insertAdjacentHTML('beforeend', html);
    setTimeout(() => initCanvasEditor(), 50);
    
  } catch (error) {
    console.error('Error showing add canvas dialog:', error);
    showMessage('Failed to show add canvas dialog', 'error');
  }
}

/**
 * Close the add canvas modal
 */
function closeAddCanvasModal() {
  const modal = document.getElementById('add-canvas-modal');
  if (modal) modal.remove();
}

/**
 * Initialize the canvas editor
 */
function initCanvasEditor() {
  const canvas = document.getElementById('canvas-editor');
  if (!canvas) return;
  
  const container = canvas.parentElement;
  const containerWidth = container.clientWidth - 4;
  
  canvasEditorState.scale = Math.min(containerWidth / DISPLAY_WIDTH, 1.5);
  
  canvas.width = DISPLAY_WIDTH * canvasEditorState.scale;
  canvas.height = DISPLAY_HEIGHT * canvasEditorState.scale;
  
  drawCanvasEditor();
  
  canvas.addEventListener('mousedown', handleCanvasMouseDown);
  canvas.addEventListener('mousemove', handleCanvasMouseMove);
  canvas.addEventListener('mouseup', handleCanvasMouseUp);
  canvas.addEventListener('mouseleave', handleCanvasMouseUp);
  
  canvas.addEventListener('touchstart', handleCanvasTouchStart, { passive: false });
  canvas.addEventListener('touchmove', handleCanvasTouchMove, { passive: false });
  canvas.addEventListener('touchend', handleCanvasTouchEnd);
}

/**
 * Draw the canvas editor preview
 */
function drawCanvasEditor() {
  const canvas = document.getElementById('canvas-editor');
  if (!canvas) return;
  
  const ctx = canvas.getContext('2d');
  const scale = canvasEditorState.scale;
  
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  
  // Background
  ctx.fillStyle = '#1e293b';
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  
  // Grid
  if (canvasEditorState.snapToGrid) {
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.08)';
    ctx.lineWidth = 0.5;
    
    const gridSize = canvasEditorState.gridSize * scale;
    
    for (let x = gridSize; x < canvas.width; x += gridSize) {
      ctx.beginPath();
      ctx.moveTo(x, 0);
      ctx.lineTo(x, canvas.height);
      ctx.stroke();
    }
    
    for (let y = gridSize; y < canvas.height; y += gridSize) {
      ctx.beginPath();
      ctx.moveTo(0, y);
      ctx.lineTo(canvas.width, y);
      ctx.stroke();
    }
  }
  
  // Existing canvases
  if (canvasEditorState.showExisting && canvasEditorState.existingCanvases.length > 0) {
    const colors = ['#3b82f6', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899'];
    
    canvasEditorState.existingCanvases.forEach((c, i) => {
      const color = colors[i % colors.length];
      
      ctx.fillStyle = color + '40';
      ctx.fillRect(c.x * scale, c.y * scale, c.width * scale, c.height * scale);
      
      ctx.strokeStyle = color;
      ctx.lineWidth = 2;
      ctx.strokeRect(c.x * scale, c.y * scale, c.width * scale, c.height * scale);
      
      ctx.fillStyle = color;
      ctx.font = `${10 * Math.min(scale, 1.2)}px sans-serif`;
      ctx.fillText(c.name, c.x * scale + 4, c.y * scale + 12 * Math.min(scale, 1.2));
    });
  }
  
  // Drawing selection
  if (canvasEditorState.isDrawing) {
    const x = Math.min(canvasEditorState.startX, canvasEditorState.currentX);
    const y = Math.min(canvasEditorState.startY, canvasEditorState.currentY);
    const w = Math.abs(canvasEditorState.currentX - canvasEditorState.startX);
    const h = Math.abs(canvasEditorState.currentY - canvasEditorState.startY);
    
    ctx.fillStyle = 'rgba(6, 182, 212, 0.3)';
    ctx.fillRect(x * scale, y * scale, w * scale, h * scale);
    
    ctx.strokeStyle = '#06b6d4';
    ctx.lineWidth = 2;
    ctx.setLineDash([5, 5]);
    ctx.strokeRect(x * scale, y * scale, w * scale, h * scale);
    ctx.setLineDash([]);
  } else if (canvasEditorState.selection) {
    const sel = canvasEditorState.selection;
    
    ctx.fillStyle = 'rgba(6, 182, 212, 0.4)';
    ctx.fillRect(sel.x * scale, sel.y * scale, sel.width * scale, sel.height * scale);
    
    ctx.strokeStyle = '#06b6d4';
    ctx.lineWidth = 3;
    ctx.strokeRect(sel.x * scale, sel.y * scale, sel.width * scale, sel.height * scale);
    
    ctx.fillStyle = '#06b6d4';
    ctx.font = `bold ${12 * Math.min(scale, 1.2)}px sans-serif`;
    const label = `${sel.width} × ${sel.height}`;
    const labelX = sel.x * scale + (sel.width * scale - ctx.measureText(label).width) / 2;
    const labelY = sel.y * scale + sel.height * scale / 2 + 4;
    ctx.fillText(label, labelX, labelY);
  }
  
  // Border
  ctx.strokeStyle = 'rgba(255, 255, 255, 0.3)';
  ctx.lineWidth = 2;
  ctx.strokeRect(0, 0, canvas.width, canvas.height);
}

/**
 * Get canvas coordinates from mouse event
 */
function getCanvasCoords(e) {
  const canvas = document.getElementById('canvas-editor');
  const rect = canvas.getBoundingClientRect();

  // Map from the canvas's actual on-screen size to display pixels. Using the rendered rect (instead of an
  // assumed scale) keeps the cursor aligned even when CSS stretches the canvas (e.g. on small panels).
  const sx = DISPLAY_WIDTH / rect.width;
  const sy = DISPLAY_HEIGHT / rect.height;

  let x = Math.round((e.clientX - rect.left) * sx);
  let y = Math.round((e.clientY - rect.top) * sy);
  
  if (canvasEditorState.snapToGrid) {
    const grid = canvasEditorState.gridSize;
    x = Math.round(x / grid) * grid;
    y = Math.round(y / grid) * grid;
  }
  
  x = Math.max(0, Math.min(DISPLAY_WIDTH, x));
  y = Math.max(0, Math.min(DISPLAY_HEIGHT, y));
  
  return { x, y };
}

function handleCanvasMouseDown(e) {
  e.preventDefault();
  const coords = getCanvasCoords(e);
  
  canvasEditorState.isDrawing = true;
  canvasEditorState.startX = coords.x;
  canvasEditorState.startY = coords.y;
  canvasEditorState.currentX = coords.x;
  canvasEditorState.currentY = coords.y;
  
  drawCanvasEditor();
}

function handleCanvasMouseMove(e) {
  const coords = getCanvasCoords(e);
  
  if (canvasEditorState.isDrawing) {
    canvasEditorState.currentX = coords.x;
    canvasEditorState.currentY = coords.y;
    updateCoordsDisplay();
    drawCanvasEditor();
  }
}

function handleCanvasMouseUp(e) {
  if (!canvasEditorState.isDrawing) return;
  
  canvasEditorState.isDrawing = false;
  
  const x = Math.min(canvasEditorState.startX, canvasEditorState.currentX);
  const y = Math.min(canvasEditorState.startY, canvasEditorState.currentY);
  const w = Math.abs(canvasEditorState.currentX - canvasEditorState.startX);
  const h = Math.abs(canvasEditorState.currentY - canvasEditorState.startY);
  
  if (w >= 16 && h >= 16) {
    canvasEditorState.selection = { x, y, width: w, height: h };
    applyEditorSelection();
  } else {
    canvasEditorState.selection = null;
    updateCoordsDisplay();
  }
  
  drawCanvasEditor();
}

function handleCanvasTouchStart(e) {
  e.preventDefault();
  const touch = e.touches[0];
  handleCanvasMouseDown({ clientX: touch.clientX, clientY: touch.clientY, preventDefault: () => {} });
}

function handleCanvasTouchMove(e) {
  e.preventDefault();
  const touch = e.touches[0];
  handleCanvasMouseMove({ clientX: touch.clientX, clientY: touch.clientY });
}

function handleCanvasTouchEnd(e) {
  handleCanvasMouseUp(e);
}

/**
 * Update coordinate display
 */
function updateCoordsDisplay() {
  const sel = canvasEditorState.selection;
  
  if (canvasEditorState.isDrawing) {
    const x = Math.min(canvasEditorState.startX, canvasEditorState.currentX);
    const y = Math.min(canvasEditorState.startY, canvasEditorState.currentY);
    const w = Math.abs(canvasEditorState.currentX - canvasEditorState.startX);
    const h = Math.abs(canvasEditorState.currentY - canvasEditorState.startY);
    
    document.getElementById('coord-x').textContent = x;
    document.getElementById('coord-y').textContent = y;
    document.getElementById('coord-w').textContent = w;
    document.getElementById('coord-h').textContent = h;
  } else if (sel) {
    document.getElementById('coord-x').textContent = sel.x;
    document.getElementById('coord-y').textContent = sel.y;
    document.getElementById('coord-w').textContent = sel.width;
    document.getElementById('coord-h').textContent = sel.height;
  } else {
    document.getElementById('coord-x').textContent = '-';
    document.getElementById('coord-y').textContent = '-';
    document.getElementById('coord-w').textContent = '-';
    document.getElementById('coord-h').textContent = '-';
  }
}

/**
 * Apply editor selection to form inputs
 */
function applyEditorSelection() {
  const sel = canvasEditorState.selection;
  if (!sel) return;
  
  document.getElementById('new-canvas-x').value = sel.x;
  document.getElementById('new-canvas-y').value = sel.y;
  document.getElementById('new-canvas-width').value = sel.width;
  document.getElementById('new-canvas-height').value = sel.height;
  
  document.getElementById('create-canvas-btn').disabled = false;
  updateCoordsDisplay();
}

/**
 * Update canvas editor options
 */
function updateCanvasEditorOption(option, value) {
  switch (option) {
    case 'snap':
      canvasEditorState.snapToGrid = value;
      break;
    case 'gridSize':
      canvasEditorState.gridSize = value;
      break;
    case 'showExisting':
      canvasEditorState.showExisting = value;
      break;
  }
  drawCanvasEditor();
}

/**
 * Apply a canvas preset
 */
function applyCanvasPreset(preset) {
  let sel = null;
  
  switch (preset) {
    case 'fullscreen':
      sel = { x: 0, y: 0, width: DISPLAY_WIDTH, height: DISPLAY_HEIGHT };
      break;
    case 'topbar':
      sel = { x: 0, y: 0, width: DISPLAY_WIDTH, height: 32 };
      break;
    case 'bottombar':
      sel = { x: 0, y: DISPLAY_HEIGHT - 32, width: DISPLAY_WIDTH, height: 32 };
      break;
    case 'center':
      const cw = Math.round(DISPLAY_WIDTH / 2);
      const ch = Math.round(DISPLAY_HEIGHT / 2);
      sel = { x: Math.round((DISPLAY_WIDTH - cw) / 2), y: Math.round((DISPLAY_HEIGHT - ch) / 2), width: cw, height: ch };
      break;
    case 'corner':
      sel = { x: DISPLAY_WIDTH - 96, y: 0, width: 96, height: 48 };
      break;
  }
  
  if (sel) {
    if (canvasEditorState.snapToGrid) {
      const grid = canvasEditorState.gridSize;
      sel.x = Math.round(sel.x / grid) * grid;
      sel.y = Math.round(sel.y / grid) * grid;
      sel.width = Math.round(sel.width / grid) * grid;
      sel.height = Math.round(sel.height / grid) * grid;
    }
    
    canvasEditorState.selection = sel;
    applyEditorSelection();
    drawCanvasEditor();
  }
}

/**
 * Confirm and create the canvas
 */
async function confirmAddCanvas() {
  const name = document.getElementById('new-canvas-name').value.trim();
  const x = parseInt(document.getElementById('new-canvas-x').value);
  const y = parseInt(document.getElementById('new-canvas-y').value);
  const width = parseInt(document.getElementById('new-canvas-width').value);
  const height = parseInt(document.getElementById('new-canvas-height').value);
  const zOrder = parseInt(document.getElementById('new-canvas-zorder').value);
  const opacity = parseInt(document.getElementById('new-canvas-opacity').value) / 100.0;

  if (!name) {
    showMessage('Please enter a canvas name', 'error');
    return;
  }

  try {
    await window.api.post('/api/canvas/create', {
      name,
      x,
      y,
      width,
      height,
      zOrder,
      opacity
    });

    showMessage(`Canvas '${name}' created successfully!`, 'success');
    closeModal();

    // Refresh displays
    await loadCanvasStack();
    await refreshLayoutInfo();
    
    // Update all canvas selectors
    updateDrawTargetCanvases();
    if (typeof updateCameraTargetCanvases === 'function') {
      updateCameraTargetCanvases();
    }
    if (typeof populateDemoCanvasSelector === 'function') {
      populateDemoCanvasSelector();
    }
    
    // Dispatch event for other listeners
    window.dispatchEvent(new CustomEvent('layoutChanged'));
  } catch (error) {
    console.error('Error creating canvas:', error);
    showMessage('Failed to create canvas', 'error');
  }
}

/**
 * Remove overlay canvas
 */
async function removeOverlayCanvas(canvasName) {
  const confirmed = await showConfirm({
    title: 'Remove overlay canvas',
    message: `Remove overlay canvas "${canvasName}"?\n\nThis will permanently delete the canvas and any content on it.`,
    confirmText: 'Remove',
    cancelText: 'Keep',
    type: 'danger',
    icon: `${ICONS.DELETE}`
  });

  if (!confirmed) {
    return;
  }

  try {
    console.log(`Removing overlay canvas '${canvasName}'...`);

    await window.api.post(`/api/canvas/${encodeURIComponent(canvasName)}/remove`);

    showMessage(`Canvas '${canvasName}' removed successfully`, 'success');

      // Refresh displays
      await loadCanvasStack();
      await refreshLayoutInfo();
      
      // Update all canvas selectors
      updateDrawTargetCanvases();
      if (typeof updateCameraTargetCanvases === 'function') {
        updateCameraTargetCanvases();
      }
      if (typeof populateDemoCanvasSelector === 'function') {
        populateDemoCanvasSelector();
      }
      
      // Dispatch event for other listeners
      window.dispatchEvent(new CustomEvent('layoutChanged'));
  } catch (error) {
    console.error('Error removing overlay canvas:', error);
    showMessage(`Error: ${error.message}`, 'error');
  }
}

// Expose globally
window.showAddCanvasDialog = showAddCanvasDialog;
window.closeAddCanvasModal = closeAddCanvasModal;
window.confirmAddCanvas = confirmAddCanvas;
window.removeOverlayCanvas = removeOverlayCanvas;
window.updateCanvasEditorOption = updateCanvasEditorOption;
window.applyCanvasPreset = applyCanvasPreset;
