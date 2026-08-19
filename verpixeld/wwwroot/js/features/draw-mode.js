/* ============================================================================
   DRAW MODE - Full drawing functionality for LED matrix
   Includes: Pencil, Eraser, Shapes, Live Mode, Save/Load/Export
   ============================================================================ */

// ============================================================================
// DRAW STATE
// ============================================================================

const drawState = {
  canvas: null,
  ctx: null,
  isDrawing: false,
  lastX: 0,
  lastY: 0,
  startX: 0,              // Shape start point
  startY: 0,
  tool: 'pencil',
  fillMode: false,        // Fill shapes or just outline
  color: '#ffffff',
  alpha: 1.0,
  brushSize: 3,
  canvasWidth: 384,
  canvasHeight: 192,
  liveMode: false,
  pendingStrokes: [],      // Buffer for batching live strokes
  liveSendTimeout: null,   // Timeout for batched sending
  liveSendInterval: 50,    // Send batch every 50ms for smooth live drawing
  canvasSnapshot: null,    // For shape preview
  clientId: null,          // Unique client ID for collaborative drawing
  eventSource: null        // SSE connection for receiving others' drawings
};

function isShapeTool(tool) {
  return ['line', 'rect', 'ellipse'].includes(tool);
}

// ============================================================================
// INITIALIZATION
// ============================================================================

function initDrawMode() {
  const canvas = document.getElementById('draw-canvas');
  if (!canvas) return;

  drawState.canvas = canvas;
  drawState.ctx = canvas.getContext('2d', { willReadFrequently: true });
  
  // Set canvas size based on target (will be updated when canvas list loads)
  resizeDrawCanvas(drawState.canvasWidth, drawState.canvasHeight);
  
  // Clear canvas to black (LED matrix background)
  clearDrawingCanvas();
  
  // Mouse events
  canvas.addEventListener('mousedown', handleDrawStart);
  canvas.addEventListener('mousemove', handleDrawMove);
  canvas.addEventListener('mouseup', handleDrawEnd);
  canvas.addEventListener('mouseleave', handleDrawEnd);
  
  // Touch events for mobile
  canvas.addEventListener('touchstart', handleTouchStart, { passive: false });
  canvas.addEventListener('touchmove', handleTouchMove, { passive: false });
  canvas.addEventListener('touchend', handleDrawEnd);
  canvas.addEventListener('touchcancel', handleDrawEnd);
  
  // Control event listeners
  document.getElementById('draw-color')?.addEventListener('input', (e) => {
    drawState.color = e.target.value;
  });
  
  document.getElementById('draw-alpha')?.addEventListener('input', (e) => {
    drawState.alpha = parseInt(e.target.value) / 100;
    document.getElementById('draw-alpha-value').textContent = e.target.value + '%';
  });
  
  document.getElementById('draw-brush-size')?.addEventListener('input', (e) => {
    drawState.brushSize = parseInt(e.target.value);
    document.getElementById('draw-size-value').textContent = e.target.value + 'px';
  });
  
  // Update target canvas dropdown when layout changes
  updateDrawTargetCanvases();
  
  // Set up canvas selection change handler (using onchange to avoid duplicates)
  const targetSelect = document.getElementById('draw-target-canvas');
  if (targetSelect) {
    targetSelect.onchange = onDrawTargetCanvasChange;
  }
  
  // Handle window resize / orientation change
  let resizeTimeout;
  window.addEventListener('resize', () => {
    clearTimeout(resizeTimeout);
    resizeTimeout = setTimeout(() => {
      updateDrawCanvasDisplaySize();
    }, 100);
  });
  
  // Also handle orientation change specifically
  window.addEventListener('orientationchange', () => {
    setTimeout(() => {
      updateDrawCanvasDisplaySize();
    }, 200);
  });
  
  console.log('[DRAW] Draw mode initialized');
}

// ============================================================================
// CANVAS SIZING
// ============================================================================

function resizeDrawCanvas(width, height, preserveContent = false) {
  if (!drawState.canvas) return;
  
  // Save current content if preserving
  let imageData = null;
  if (preserveContent && drawState.ctx) {
    imageData = drawState.ctx.getImageData(0, 0, drawState.canvasWidth, drawState.canvasHeight);
  }
  
  drawState.canvasWidth = width;
  drawState.canvasHeight = height;
  drawState.canvas.width = width;
  drawState.canvas.height = height;
  
  // Restore content if we saved it
  if (imageData && drawState.ctx) {
    drawState.ctx.putImageData(imageData, 0, 0);
  }
  
  // Update display scale
  updateDrawCanvasDisplaySize();
}

function updateDrawCanvasDisplaySize() {
  if (!drawState.canvas) return;
  
  const width = drawState.canvasWidth;
  const height = drawState.canvasHeight;
  
  // Get container width for responsive sizing
  const container = document.querySelector('.draw-canvas-container');
  const containerWidth = container ? container.clientWidth - 20 : window.innerWidth - 40;
  
  // Calculate scale to fit container while maintaining aspect ratio
  // Desktop: larger scale, Mobile: fit to container
  const isMobile = window.innerWidth < 768;
  const maxWidth = isMobile ? containerWidth : Math.min(containerWidth, 950);
  
  // Calculate scale that fits within maxWidth
  let scale = Math.floor(maxWidth / width);
  scale = Math.max(1, Math.min(scale, isMobile ? 3 : 4)); // Cap scale
  
  const displayWidth = width * scale;
  const displayHeight = height * scale;
  
  // Store scale for other uses
  drawState.displayScale = scale;
  
  drawState.canvas.style.width = displayWidth + 'px';
  drawState.canvas.style.height = displayHeight + 'px';
  
  // Update info display
  const sizeInfo = document.getElementById('draw-canvas-size');
  if (sizeInfo) {
    sizeInfo.textContent = `${width} × ${height}`;
  }
  
  // Update grid size and cell size (1 pixel = scale px on screen)
  const grid = document.getElementById('draw-canvas-grid');
  if (grid) {
    grid.style.width = displayWidth + 'px';
    grid.style.height = displayHeight + 'px';
    // Set CSS variable for grid cell size (each cell = 1 LED pixel)
    grid.style.setProperty('--grid-cell-size', scale + 'px');
  }
}

// ============================================================================
// EVENT HANDLERS
// ============================================================================

function handleDrawStart(e) {
  e.preventDefault();
  drawState.isDrawing = true;
  const pos = getCanvasPosition(e);
  drawState.lastX = pos.x;
  drawState.lastY = pos.y;
  drawState.startX = pos.x;
  drawState.startY = pos.y;
  
  if (isShapeTool(drawState.tool)) {
    // Take snapshot for shape preview
    drawState.canvasSnapshot = drawState.ctx.getImageData(
      0, 0, drawState.canvasWidth, drawState.canvasHeight
    );
  } else {
    // Draw a single point for click without drag
    drawPoint(pos.x, pos.y);
    
    // Queue for live mode
    if (drawState.liveMode) {
      queueLiveStroke(pos.x, pos.y, pos.x, pos.y);
    }
  }
}

function handleDrawMove(e) {
  if (!drawState.isDrawing) return;
  e.preventDefault();
  
  const pos = getCanvasPosition(e);
  
  if (isShapeTool(drawState.tool)) {
    // Restore snapshot and draw shape preview
    if (drawState.canvasSnapshot) {
      drawState.ctx.putImageData(drawState.canvasSnapshot, 0, 0);
    }
    drawShapePreview(drawState.startX, drawState.startY, pos.x, pos.y);
    // Update lastX/lastY so handleDrawEnd knows the final position
    drawState.lastX = pos.x;
    drawState.lastY = pos.y;
  } else {
    const prevX = drawState.lastX;
    const prevY = drawState.lastY;
    
    drawLine(prevX, prevY, pos.x, pos.y);
    drawState.lastX = pos.x;
    drawState.lastY = pos.y;
    
    // Queue for live mode
    if (drawState.liveMode) {
      queueLiveStroke(prevX, prevY, pos.x, pos.y);
    }
  }
}

function handleDrawEnd(e) {
  if (drawState.isDrawing && isShapeTool(drawState.tool)) {
    // Get final position - use lastX/lastY (updated during move) or try to get from event
    let endX = drawState.lastX;
    let endY = drawState.lastY;
    
    // Try to get position from event if it's a mouse event
    if (e && e.clientX !== undefined) {
      const pos = getCanvasPosition(e);
      endX = pos.x;
      endY = pos.y;
    }
    
    // Restore and finalize shape
    if (drawState.canvasSnapshot) {
      drawState.ctx.putImageData(drawState.canvasSnapshot, 0, 0);
    }
    drawShapeFinal(drawState.startX, drawState.startY, endX, endY);
    drawState.canvasSnapshot = null;
  }
  
  drawState.isDrawing = false;
  
  // Flush any remaining strokes in live mode
  if (drawState.liveMode && drawState.pendingStrokes.length > 0) {
    sendLiveStrokes();
  }
}

function handleTouchStart(e) {
  e.preventDefault();
  if (e.touches.length === 1) {
    const touch = e.touches[0];
    drawState.isDrawing = true;
    const pos = getCanvasPosition(touch);
    drawState.lastX = pos.x;
    drawState.lastY = pos.y;
    drawState.startX = pos.x;
    drawState.startY = pos.y;
    
    if (isShapeTool(drawState.tool)) {
      drawState.canvasSnapshot = drawState.ctx.getImageData(
        0, 0, drawState.canvasWidth, drawState.canvasHeight
      );
    } else {
      drawPoint(pos.x, pos.y);
      if (drawState.liveMode) {
        queueLiveStroke(pos.x, pos.y, pos.x, pos.y);
      }
    }
  }
}

function handleTouchMove(e) {
  e.preventDefault();
  if (!drawState.isDrawing || e.touches.length !== 1) return;
  
  const touch = e.touches[0];
  const pos = getCanvasPosition(touch);
  
  if (isShapeTool(drawState.tool)) {
    if (drawState.canvasSnapshot) {
      drawState.ctx.putImageData(drawState.canvasSnapshot, 0, 0);
    }
    drawShapePreview(drawState.startX, drawState.startY, pos.x, pos.y);
    drawState.lastX = pos.x;
    drawState.lastY = pos.y;
  } else {
    const prevX = drawState.lastX;
    const prevY = drawState.lastY;
    
    drawLine(prevX, prevY, pos.x, pos.y);
    drawState.lastX = pos.x;
    drawState.lastY = pos.y;
    
    if (drawState.liveMode) {
      queueLiveStroke(prevX, prevY, pos.x, pos.y);
    }
  }
}

function getCanvasPosition(e) {
  const canvas = drawState.canvas;
  const rect = canvas.getBoundingClientRect();
  const scaleX = canvas.width / rect.width;
  const scaleY = canvas.height / rect.height;
  
  return {
    x: Math.floor((e.clientX - rect.left) * scaleX),
    y: Math.floor((e.clientY - rect.top) * scaleY)
  };
}

// ============================================================================
// DRAWING PRIMITIVES
// ============================================================================

function drawPoint(x, y) {
  const ctx = drawState.ctx;
  if (!ctx) return;
  
  if (drawState.tool === 'eraser') {
    // Eraser - draw black (transparent on LED)
    ctx.fillStyle = '#000000';
  } else {
    // Pencil - use selected color with alpha
    ctx.fillStyle = hexToRgba(drawState.color, drawState.alpha);
  }
  
  const size = drawState.brushSize;
  const halfSize = Math.floor(size / 2);
  
  // Draw square pixels for crisp LED-style appearance
  ctx.fillRect(x - halfSize, y - halfSize, size, size);
}

function drawLine(x1, y1, x2, y2) {
  const ctx = drawState.ctx;
  if (!ctx) return;
  
  // Bresenham's line algorithm for pixel-perfect lines
  const dx = Math.abs(x2 - x1);
  const dy = Math.abs(y2 - y1);
  const sx = x1 < x2 ? 1 : -1;
  const sy = y1 < y2 ? 1 : -1;
  let err = dx - dy;
  
  while (true) {
    drawPoint(x1, y1);
    
    if (x1 === x2 && y1 === y2) break;
    
    const e2 = 2 * err;
    if (e2 > -dy) {
      err -= dy;
      x1 += sx;
    }
    if (e2 < dx) {
      err += dx;
      y1 += sy;
    }
  }
}

// ============================================================================
// SHAPE DRAWING
// ============================================================================

function drawShapePreview(x1, y1, x2, y2) {
  drawShapeInternal(x1, y1, x2, y2);
}

function drawShapeFinal(x1, y1, x2, y2) {
  drawShapeInternal(x1, y1, x2, y2);
  
  // Send to live canvas if in live mode
  if (drawState.liveMode) {
    sendLiveShape(drawState.tool, x1, y1, x2, y2, drawState.fillMode);
  }
}

function drawShapeInternal(x1, y1, x2, y2) {
  const ctx = drawState.ctx;
  if (!ctx) return;
  
  const color = hexToRgba(drawState.color, drawState.alpha);
  const size = drawState.brushSize;
  
  switch (drawState.tool) {
    case 'line':
      drawShapeLine(ctx, x1, y1, x2, y2, color, size);
      break;
    case 'rect':
      drawShapeRect(ctx, x1, y1, x2, y2, color, size, drawState.fillMode);
      break;
    case 'ellipse':
      drawShapeEllipse(ctx, x1, y1, x2, y2, color, size, drawState.fillMode);
      break;
  }
}

function drawShapeLine(ctx, x1, y1, x2, y2, color, size) {
  ctx.fillStyle = color;
  const halfSize = Math.floor(size / 2);
  
  // Bresenham's line algorithm
  const dx = Math.abs(x2 - x1);
  const dy = Math.abs(y2 - y1);
  const sx = x1 < x2 ? 1 : -1;
  const sy = y1 < y2 ? 1 : -1;
  let err = dx - dy;
  let cx = x1, cy = y1;
  
  while (true) {
    ctx.fillRect(cx - halfSize, cy - halfSize, size, size);
    if (cx === x2 && cy === y2) break;
    const e2 = 2 * err;
    if (e2 > -dy) { err -= dy; cx += sx; }
    if (e2 < dx) { err += dx; cy += sy; }
  }
}

function drawShapeRect(ctx, x1, y1, x2, y2, color, size, filled) {
  const left = Math.min(x1, x2);
  const top = Math.min(y1, y2);
  const right = Math.max(x1, x2);
  const bottom = Math.max(y1, y2);
  const width = right - left;
  const height = bottom - top;
  
  ctx.fillStyle = color;
  
  if (filled) {
    ctx.fillRect(left, top, width, height);
  } else {
    const halfSize = Math.floor(size / 2);
    // Top
    ctx.fillRect(left - halfSize, top - halfSize, width + size, size);
    // Bottom
    ctx.fillRect(left - halfSize, bottom - halfSize, width + size, size);
    // Left
    ctx.fillRect(left - halfSize, top, size, height);
    // Right
    ctx.fillRect(right - halfSize, top, size, height);
  }
}

function drawShapeEllipse(ctx, x1, y1, x2, y2, color, size, filled) {
  const cx = Math.round((x1 + x2) / 2);
  const cy = Math.round((y1 + y2) / 2);
  const rx = Math.abs(Math.round((x2 - x1) / 2));
  const ry = Math.abs(Math.round((y2 - y1) / 2));
  
  if (rx === 0 || ry === 0) return;
  
  ctx.fillStyle = color;
  
  if (filled) {
    // Fill ellipse with horizontal lines
    for (let y = -ry; y <= ry; y++) {
      const x = Math.round(rx * Math.sqrt(1 - (y * y) / (ry * ry)));
      ctx.fillRect(cx - x, cy + y, x * 2 + 1, 1);
    }
  } else {
    // Outline using midpoint ellipse algorithm
    const halfSize = Math.floor(size / 2);
    let x = 0, y = ry;
    let d1 = (ry * ry) - (rx * rx * ry) + (0.25 * rx * rx);
    let dx = 2 * ry * ry * x;
    let dy = 2 * rx * rx * y;
    
    while (dx < dy) {
      ctx.fillRect(cx + x - halfSize, cy + y - halfSize, size, size);
      ctx.fillRect(cx - x - halfSize, cy + y - halfSize, size, size);
      ctx.fillRect(cx + x - halfSize, cy - y - halfSize, size, size);
      ctx.fillRect(cx - x - halfSize, cy - y - halfSize, size, size);
      
      if (d1 < 0) {
        x++; dx += 2 * ry * ry;
        d1 += dx + ry * ry;
      } else {
        x++; y--;
        dx += 2 * ry * ry; dy -= 2 * rx * rx;
        d1 += dx - dy + ry * ry;
      }
    }
    
    let d2 = ((ry * ry) * ((x + 0.5) * (x + 0.5))) + 
             ((rx * rx) * ((y - 1) * (y - 1))) - 
             (rx * rx * ry * ry);
    
    while (y >= 0) {
      ctx.fillRect(cx + x - halfSize, cy + y - halfSize, size, size);
      ctx.fillRect(cx - x - halfSize, cy + y - halfSize, size, size);
      ctx.fillRect(cx + x - halfSize, cy - y - halfSize, size, size);
      ctx.fillRect(cx - x - halfSize, cy - y - halfSize, size, size);
      
      if (d2 > 0) {
        y--; dy -= 2 * rx * rx;
        d2 += rx * rx - dy;
      } else {
        y--; x++;
        dx += 2 * ry * ry; dy -= 2 * rx * rx;
        d2 += dx - dy + rx * rx;
      }
    }
  }
}

// ============================================================================
// TOOL CONTROLS
// ============================================================================

function toggleFillMode() {
  const checkbox = document.getElementById('draw-fill-mode');
  drawState.fillMode = checkbox?.checked || false;
}

function selectDrawTool(tool) {
  drawState.tool = tool;
  
  // Update UI - tool buttons
  document.querySelectorAll('.draw-tool-btn').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.tool === tool);
  });
  
  // Show/hide fill toggle for shape tools
  const fillToggle = document.getElementById('draw-shape-fill-toggle');
  if (fillToggle) {
    fillToggle.style.display = isShapeTool(tool) ? 'block' : 'none';
  }
  
  // Update cursor
  const canvas = drawState.canvas;
  if (canvas) {
    canvas.style.cursor = tool === 'eraser' ? 'cell' : 'crosshair';
  }
}

function clearDrawingCanvas() {
  const ctx = drawState.ctx;
  if (!ctx) return;
  
  // Fill with black (LED off state)
  ctx.fillStyle = '#000000';
  ctx.fillRect(0, 0, drawState.canvasWidth, drawState.canvasHeight);
  
  // Also clear the live canvas if in live mode
  if (drawState.liveMode) {
    const targetSelect = document.getElementById('draw-target-canvas');
    const targetCanvas = targetSelect?.value;
    if (targetCanvas) {
      sendLiveClear(targetCanvas);
    }
  }
}

function toggleDrawGrid() {
  const grid = document.getElementById('draw-canvas-grid');
  const checkbox = document.getElementById('draw-show-grid');
  if (grid && checkbox) {
    grid.classList.toggle('visible', checkbox.checked);
  }
}

async function updateDrawTargetCanvases() {
  const select = document.getElementById('draw-target-canvas');
  if (!select) return;
  
  try {
    const result = await api.get('/api/layout/canvases');
    if (result.data) {
      const currentSelection = select.value;
      select.innerHTML = result.data.map(canvas =>
        `<option value="${canvas.name}" data-width="${canvas.width}" data-height="${canvas.height}">${canvas.name} (${canvas.width}×${canvas.height})</option>`
      ).join('');
      let selectedCanvas = result.data[0];
      if (currentSelection) {
        const found = result.data.find(c => c.name === currentSelection);
        if (found) {
          select.value = currentSelection;
          selectedCanvas = found;
        }
      }
      if (selectedCanvas) {
        resizeDrawCanvas(selectedCanvas.width, selectedCanvas.height);
      }
    }
  } catch (err) {
    console.error('[DRAW] Failed to load canvases:', err);
  }
}

/**
 * Handle canvas selection change in draw mode
 */
function onDrawTargetCanvasChange() {
  const select = document.getElementById('draw-target-canvas');
  if (!select) return;
  
  const option = select.selectedOptions[0];
  if (option) {
    const width = parseInt(option.dataset.width);
    const height = parseInt(option.dataset.height);
    
    if (width && height && (width !== drawState.canvasWidth || height !== drawState.canvasHeight)) {
      console.log(`[DRAW] Resizing canvas to ${width}×${height}`);
      resizeDrawCanvas(width, height);
      clearDrawingCanvas();
    }
  }
}

// ============================================================================
// APPLY DRAWING TO CANVAS
// ============================================================================

async function applyDrawingToCanvas() {
  const canvas = drawState.canvas;
  const targetSelect = document.getElementById('draw-target-canvas');
  
  if (!canvas || !targetSelect) {
    toast.error('Error', 'Drawing canvas not initialized');
    return;
  }
  
  const targetCanvas = targetSelect.value;
  if (!targetCanvas) {
    toast.error('Error', 'Please select a target canvas');
    return;
  }
  
  // Get the image data as base64 PNG
  const imageData = canvas.toDataURL('image/png');
  
  // Find the apply button and show loading
  const applyBtn = document.querySelector('#section-draw .section-header-actions .btn-primary');
  if (applyBtn) setButtonLoading(applyBtn, true);
  
  try {
    await api.post('/api/draw/apply/' + encodeURIComponent(targetCanvas), { imageData });
    toast.success('Drawing Applied', `Your drawing has been sent to "${targetCanvas}"`);
  } catch (error) {
    console.error('[DRAW] Failed to apply drawing:', error);
    toast.error('Error', error.message || 'Failed to send drawing');
  } finally {
    if (applyBtn) setButtonLoading(applyBtn, false);
  }
}

// ============================================================================
// LIVE DRAW MODE (COLLABORATIVE)
// ============================================================================

function toggleLiveDrawMode() {
  const checkbox = document.getElementById('draw-live-mode');
  drawState.liveMode = checkbox?.checked || false;
  
  if (drawState.liveMode) {
    // Connect to SSE for collaborative drawing
    connectToLiveDrawingEvents();
    
    // Clear the display canvas when entering live mode
    const targetSelect = document.getElementById('draw-target-canvas');
    const targetCanvas = targetSelect?.value;
    
    if (targetCanvas) {
      // Send a clear command to start fresh
      sendLiveClear(targetCanvas);
      toast.info('Live Mode', `Collaborative drawing enabled - others can see your strokes!`);
    }
    
    console.log('[DRAW] Live mode enabled (collaborative)');
  } else {
    // Disconnect from SSE
    disconnectFromLiveDrawingEvents();
    
    // Clear pending strokes when disabling
    drawState.pendingStrokes = [];
    if (drawState.liveSendTimeout) {
      clearTimeout(drawState.liveSendTimeout);
      drawState.liveSendTimeout = null;
    }
    console.log('[DRAW] Live mode disabled');
  }
}

/**
 * Connect to Server-Sent Events for collaborative live drawing
 */
function connectToLiveDrawingEvents() {
  if (drawState.eventSource) {
    drawState.eventSource.close();
  }
  
  drawState.eventSource = new EventSource(API_BASE + '/api/draw/live/events');
  
  drawState.eventSource.addEventListener('connected', (e) => {
    try {
      const data = JSON.parse(e.data);
      drawState.clientId = data.clientId;
      console.log('[DRAW] Connected to collaborative drawing, clientId:', drawState.clientId);
    } catch (err) {
      console.error('[DRAW] Error parsing connected event:', err);
    }
  });
  
  drawState.eventSource.addEventListener('draw', (e) => {
    try {
      const data = JSON.parse(e.data);
      handleRemoteDrawEvent(data);
    } catch (err) {
      console.error('[DRAW] Error parsing draw event:', err);
    }
  });
  
  drawState.eventSource.onerror = (err) => {
    console.warn('[DRAW] SSE connection error, will reconnect...');
    // EventSource will automatically reconnect
  };
}

/**
 * Disconnect from live drawing events
 */
function disconnectFromLiveDrawingEvents() {
  if (drawState.eventSource) {
    drawState.eventSource.close();
    drawState.eventSource = null;
    drawState.clientId = null;
    console.log('[DRAW] Disconnected from collaborative drawing');
  }
}

/**
 * Handle drawing events received from other users
 */
function handleRemoteDrawEvent(data) {
  const ctx = drawState.ctx;
  if (!ctx) return;
  
  // Check if this is for the current canvas
  const targetSelect = document.getElementById('draw-target-canvas');
  const currentCanvas = targetSelect?.value;
  if (data.canvas !== currentCanvas) return;
  
  if (data.type === 'strokes') {
    // Draw strokes from another user
    for (const stroke of data.strokes) {
      const color = hexToRgba(stroke.color, stroke.alpha);
      ctx.strokeStyle = color;
      ctx.fillStyle = color;
      ctx.lineWidth = stroke.size;
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      
      if (stroke.x1 === stroke.x2 && stroke.y1 === stroke.y2) {
        // Single point
        ctx.beginPath();
        ctx.arc(stroke.x1, stroke.y1, stroke.size / 2, 0, Math.PI * 2);
        ctx.fill();
      } else {
        // Line
        ctx.beginPath();
        ctx.moveTo(stroke.x1, stroke.y1);
        ctx.lineTo(stroke.x2, stroke.y2);
        ctx.stroke();
      }
    }
  } else if (data.type === 'shape') {
    // Draw shape from another user
    const color = hexToRgba(data.color, data.alpha);
    drawRemoteShape(ctx, data.tool, data.x1, data.y1, data.x2, data.y2, color, data.size, data.filled);
  } else if (data.type === 'clear') {
    // Clear canvas (another user cleared it)
    ctx.fillStyle = '#000000';
    ctx.fillRect(0, 0, drawState.canvasWidth, drawState.canvasHeight);
    console.log('[DRAW] Canvas cleared by another user');
  }
}

/**
 * Draw a shape received from another user
 */
function drawRemoteShape(ctx, tool, x1, y1, x2, y2, color, size, filled) {
  ctx.strokeStyle = color;
  ctx.fillStyle = color;
  ctx.lineWidth = size;
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  
  if (tool === 'line') {
    ctx.beginPath();
    ctx.moveTo(x1, y1);
    ctx.lineTo(x2, y2);
    ctx.stroke();
  } else if (tool === 'rect') {
    const left = Math.min(x1, x2);
    const top = Math.min(y1, y2);
    const width = Math.abs(x2 - x1);
    const height = Math.abs(y2 - y1);
    
    if (filled) {
      ctx.fillRect(left, top, width, height);
    } else {
      ctx.strokeRect(left, top, width, height);
    }
  } else if (tool === 'ellipse') {
    const cx = (x1 + x2) / 2;
    const cy = (y1 + y2) / 2;
    const rx = Math.abs(x2 - x1) / 2;
    const ry = Math.abs(y2 - y1) / 2;
    
    ctx.beginPath();
    ctx.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
    if (filled) {
      ctx.fill();
    } else {
      ctx.stroke();
    }
  }
}

function queueLiveStroke(x1, y1, x2, y2) {
  // Get current drawing parameters
  const stroke = {
    x1, y1, x2, y2,
    color: drawState.tool === 'eraser' ? '#000000' : drawState.color,
    alpha: drawState.tool === 'eraser' ? 1.0 : drawState.alpha,
    size: drawState.brushSize
  };
  
  drawState.pendingStrokes.push(stroke);
  
  // Schedule batch send if not already scheduled
  if (!drawState.liveSendTimeout) {
    drawState.liveSendTimeout = setTimeout(() => {
      sendLiveStrokes();
    }, drawState.liveSendInterval);
  }
}

async function sendLiveStrokes() {
  if (drawState.pendingStrokes.length === 0) {
    drawState.liveSendTimeout = null;
    return;
  }
  
  const targetSelect = document.getElementById('draw-target-canvas');
  const targetCanvas = targetSelect?.value;
  
  if (!targetCanvas) {
    drawState.pendingStrokes = [];
    drawState.liveSendTimeout = null;
    return;
  }
  
  // Copy and clear pending strokes
  const strokes = [...drawState.pendingStrokes];
  drawState.pendingStrokes = [];
  drawState.liveSendTimeout = null;
  
  try {
    await api.post('/api/draw/live/' + encodeURIComponent(targetCanvas), { strokes, clientId: drawState.clientId });
  } catch {
    console.error('[DRAW] Live send failed');
  }
}

async function sendLiveClear(canvasName) {
  try {
    await api.post('/api/draw/live/clear/' + encodeURIComponent(canvasName), { clientId: drawState.clientId });
  } catch {
    console.error('[DRAW] Live clear failed');
  }
}

function sendLiveShape(tool, x1, y1, x2, y2, filled) {
  const targetSelect = document.getElementById('draw-target-canvas');
  const targetCanvas = targetSelect?.value;
  if (!targetCanvas) return;
  
  api.post('/api/draw/live/shape/' + encodeURIComponent(targetCanvas), {
    tool, x1, y1, x2, y2, filled,
    color: drawState.color,
    alpha: drawState.alpha,
    size: drawState.brushSize,
    clientId: drawState.clientId
  }).catch(() => {});
}

// ============================================================================
// DRAWING SAVE / LOAD / EXPORT (SHARED SERVER STORAGE)
// ============================================================================

/**
 * Get all saved drawings from server
 */
async function getSavedDrawings() {
  try {
    const result = await api.get('/api/drawings');
    return result.data || [];
  } catch (e) {
    console.error('[DRAW] Failed to load saved drawings:', e);
    return [];
  }
}

/**
 * Save a drawing to server
 */
async function saveDrawingToServer(drawing) {
  try {
    return await api.post('/api/drawings', drawing);
  } catch (e) {
    console.error('[DRAW] Failed to save drawing:', e);
    return { success: false, error: e.message };
  }
}

/**
 * Delete a drawing from server
 */
async function deleteDrawingFromServer(id) {
  try {
    return await api.del('/api/drawings/' + id);
  } catch (e) {
    console.error('[DRAW] Failed to delete drawing:', e);
    return { success: false, error: e.message };
  }
}

/**
 * Clear all drawings from server
 */
async function clearAllDrawingsFromServer() {
  try {
    return await api.del('/api/drawings');
  } catch (e) {
    console.error('[DRAW] Failed to clear drawings:', e);
    return { success: false, error: e.message };
  }
}

/**
 * Show save drawing dialog
 */
function showSaveDrawingDialog() {
  const canvas = drawState.canvas;
  if (!canvas) {
    toast.error('Error', 'No drawing canvas available');
    return;
  }

  // Get thumbnail preview
  const thumbnailUrl = canvas.toDataURL('image/png');
  
  const modalHTML = `
    <div class="modal-overlay" id="save-drawing-modal">
      <div class="modal-content" style="max-width: 450px;">
        <div class="modal-header">
          <h3>Save Drawing</h3>
          <button class="modal-close" onclick="closeModal()">&times;</button>
        </div>
        <div class="modal-body">
          <div class="save-drawing-preview">
            <img src="${thumbnailUrl}" alt="Drawing preview" class="drawing-thumbnail-large">
          </div>
          <div class="form-group">
            <label for="drawing-name">Drawing Name</label>
            <input type="text" id="drawing-name" class="form-control" 
                   placeholder="My Drawing" maxlength="50" autofocus>
          </div>
          <div class="form-group">
            <label class="text-muted">Size: ${drawState.canvasWidth} × ${drawState.canvasHeight}</label>
          </div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="closeModal()">Cancel</button>
          <button class="btn btn-primary" onclick="confirmSaveDrawing()">Save</button>
        </div>
      </div>
    </div>
  `;
  
  document.body.insertAdjacentHTML('beforeend', modalHTML);
  
  // Focus input and select on Enter
  const nameInput = document.getElementById('drawing-name');
  nameInput.focus();
  nameInput.addEventListener('keypress', (e) => {
    if (e.key === 'Enter') confirmSaveDrawing();
  });
}

/**
 * Confirm and save the drawing
 */
async function confirmSaveDrawing() {
  const nameInput = document.getElementById('drawing-name');
  const saveBtn = document.querySelector('#save-drawing-modal .btn-primary');
  let name = nameInput.value.trim();
  
  if (!name) {
    name = `Drawing ${new Date().toLocaleString()}`;
  }
  
  const canvas = drawState.canvas;
  const imageData = canvas.toDataURL('image/png');
  
  const drawing = {
    name: name,
    width: drawState.canvasWidth,
    height: drawState.canvasHeight,
    imageData: imageData
  };
  
  // Show loading state
  if (saveBtn) setButtonLoading(saveBtn, true);
  
  const result = await saveDrawingToServer(drawing);
  
  if (saveBtn) setButtonLoading(saveBtn, false);
  
  if (result.success) {
    closeModal();
    toast.success('Saved', `Drawing "${name}" saved and shared with all users`);
  } else {
    toast.error('Save Failed', result.error || 'Could not save drawing');
  }
}

/**
 * Show drawing gallery to load a saved drawing
 */
async function showDrawingGallery() {
  // Show loading modal first
  const loadingHTML = `
    <div class="modal-overlay" id="drawing-gallery-modal">
      <div class="modal-content" style="max-width: 700px;">
        <div class="modal-header">
          <h3>Shared Drawings</h3>
          <button class="modal-close" onclick="closeModal()">&times;</button>
        </div>
        <div class="modal-body">
          <div class="skeleton-container">
            <div class="skeleton-card"></div>
            <div class="skeleton-card"></div>
          </div>
        </div>
      </div>
    </div>
  `;
  document.body.insertAdjacentHTML('beforeend', loadingHTML);
  
  // Fetch drawings from server
  const drawings = await getSavedDrawings();
  
  let galleryHTML = '';
  
  if (drawings.length === 0) {
    galleryHTML = `
      <div class="empty-state">
        <div class="empty-state-icon">🖼️</div>
        <div class="empty-state-message">No saved drawings yet</div>
        <div class="empty-state-hint">Create and save a drawing to share with everyone</div>
      </div>
    `;
  } else {
    galleryHTML = `
      <div class="drawing-gallery">
        ${drawings.map(d => `
          <div class="drawing-gallery-item" data-id="${d.id}">
            <img src="${d.imageData}" alt="${escapeHtml(d.name)}" class="drawing-thumbnail" 
                 onclick="loadDrawing('${d.id}')">
            <div class="drawing-gallery-info">
              <span class="drawing-gallery-name" title="${escapeHtml(d.name)}">${escapeHtml(d.name)}</span>
              <span class="drawing-gallery-size">${d.width}×${d.height}</span>
            </div>
            <div class="drawing-gallery-actions">
              <button class="btn-icon btn-icon-sm" onclick="loadDrawing('${d.id}')" title="Load">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
                  <polyline points="7 10 12 15 17 10"></polyline>
                  <line x1="12" y1="15" x2="12" y2="3"></line>
                </svg>
              </button>
              <button class="btn-icon btn-icon-sm btn-icon-danger" onclick="deleteDrawing('${d.id}')" title="Delete">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <polyline points="3 6 5 6 21 6"></polyline>
                  <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
                </svg>
              </button>
            </div>
          </div>
        `).join('')}
      </div>
    `;
  }
  
  // Update modal content
  const modal = document.getElementById('drawing-gallery-modal');
  if (modal) {
    modal.innerHTML = `
      <div class="modal-content" style="max-width: 700px;">
        <div class="modal-header">
          <h3>Shared Drawings</h3>
          <button class="modal-close" onclick="closeModal()">&times;</button>
        </div>
        <div class="modal-body">
          ${galleryHTML}
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="refreshDrawingGallery()">Refresh</button>
          <button class="btn btn-secondary" onclick="closeModal()">Close</button>
          ${drawings.length > 0 ? `<button class="btn btn-danger" onclick="clearAllDrawings()">Delete All</button>` : ''}
        </div>
      </div>
    `;
  }
}

/**
 * Refresh the drawing gallery
 */
async function refreshDrawingGallery() {
  closeModal();
  await showDrawingGallery();
}

/**
 * Load a drawing from the gallery
 */
async function loadDrawing(drawingId) {
  const drawings = await getSavedDrawings();
  const drawing = drawings.find(d => d.id === drawingId);
  
  if (!drawing) {
    toast.error('Error', 'Drawing not found');
    return;
  }
  
  // Resize canvas if needed
  if (drawing.width !== drawState.canvasWidth || drawing.height !== drawState.canvasHeight) {
    resizeDrawCanvas(drawing.width, drawing.height);
  }
  
  // Load the image onto the canvas
  const img = new Image();
  img.onload = () => {
    const ctx = drawState.ctx;
    ctx.clearRect(0, 0, drawState.canvasWidth, drawState.canvasHeight);
    ctx.drawImage(img, 0, 0);
    closeModal();
    toast.success('Loaded', `Drawing "${drawing.name}" loaded`);
  };
  img.onerror = () => {
    toast.error('Error', 'Failed to load drawing image');
  };
  img.src = drawing.imageData;
}

/**
 * Delete a single drawing
 */
async function deleteDrawing(drawingId) {
  const confirmed = await showConfirm({
    title: 'Delete Drawing',
    message: 'Are you sure you want to delete this drawing? This will remove it for all users.',
    confirmText: 'Delete',
    cancelText: 'Keep',
    type: 'danger'
  });
  
  if (!confirmed) return;
  
  const result = await deleteDrawingFromServer(drawingId);
  
  if (result.success) {
    // Refresh gallery
    closeModal();
    await showDrawingGallery();
    toast.success('Deleted', 'Drawing deleted');
  } else {
    toast.error('Delete Failed', result.error || 'Could not delete drawing');
  }
}

/**
 * Clear all saved drawings
 */
async function clearAllDrawings() {
  const confirmed = await showConfirm({
    title: 'Delete All Drawings',
    message: 'Are you sure you want to delete ALL saved drawings? This will remove them for all users and cannot be undone.',
    confirmText: 'Delete All',
    cancelText: 'Cancel',
    type: 'danger'
  });
  
  if (!confirmed) return;
  
  const result = await clearAllDrawingsFromServer();
  
  if (result.success) {
    closeModal();
    toast.success('Cleared', 'All drawings deleted');
  } else {
    toast.error('Clear Failed', result.error || 'Could not clear drawings');
  }
}

/**
 * Export drawing as PNG download
 */
function exportDrawingAsPng() {
  const canvas = drawState.canvas;
  if (!canvas) {
    toast.error('Error', 'No drawing canvas available');
    return;
  }
  
  // Create download link
  const link = document.createElement('a');
  link.download = `ledmatrix-drawing-${Date.now()}.png`;
  link.href = canvas.toDataURL('image/png');
  link.click();
  
  toast.success('Exported', 'Drawing exported as PNG');
}

// ============================================================================
// EXPOSE GLOBALLY
// ============================================================================

window.drawState = drawState;
window.initDrawMode = initDrawMode;
window.updateDrawTargetCanvases = updateDrawTargetCanvases;
window.onDrawTargetCanvasChange = onDrawTargetCanvasChange;
window.selectDrawTool = selectDrawTool;
window.toggleFillMode = toggleFillMode;
window.clearDrawingCanvas = clearDrawingCanvas;
window.toggleLiveDrawMode = toggleLiveDrawMode;
window.toggleDrawGrid = toggleDrawGrid;
window.applyDrawingToCanvas = applyDrawingToCanvas;
window.showSaveDrawingDialog = showSaveDrawingDialog;
window.confirmSaveDrawing = confirmSaveDrawing;
window.showDrawingGallery = showDrawingGallery;
window.refreshDrawingGallery = refreshDrawingGallery;
window.loadDrawing = loadDrawing;
window.deleteDrawing = deleteDrawing;
window.clearAllDrawings = clearAllDrawings;
window.exportDrawingAsPng = exportDrawingAsPng;
