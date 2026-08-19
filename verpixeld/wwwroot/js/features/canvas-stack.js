/* ============================================================================
   CANVAS STACKING - Layer Management UI
   ============================================================================ */

/**
 * Toggle the canvas stack help/info section
 */
function toggleStackInfo() {
  const help = document.getElementById('canvas-stack-help');
  const btn = document.querySelector('.info-hint-toggle');
  if (help && btn) {
    help.classList.toggle('collapsed');
    btn.classList.toggle('active');
  }
}

/**
 * Load and display the canvas stack
 */
async function loadCanvasStack() {
  try {
    if (!window.canvasContent) {
      const contentResult = await api.get('/api/layout/content');
      if (contentResult.data) {
        window.canvasContent = contentResult.data.contents;
      }
    }

    const result = await api.get('/api/canvas/stack');
    displayCanvasStack(result.data);
  } catch (error) {
    console.error('Failed to load canvas stack:', error);
  }
}

/**
 * Display the canvas stack in the UI
 */
function displayCanvasStack(canvases) {
  const container = document.getElementById('canvas-stack');
  if (!container) return; // section retired (Studio replaces it)

  if (!canvases || canvases.length === 0) {
    container.innerHTML = '<p class="text-muted">No canvases in current layout</p>';
    return;
  }

  // Sort by z-order (highest first for visual representation - top to bottom)
  const sorted = [...canvases].sort((a, b) => b.zOrder - a.zOrder);

  // Get content info for each canvas
  const contentMap = {};
  if (window.canvasContent) {
    window.canvasContent.forEach(c => {
      contentMap[c.canvasName] = c;
    });
  }

  // Check if media player is using any canvas
  const mediaState = window.mediaState || {};
  const mediaCanvasName = mediaState.isRunning ? mediaState.targetCanvasName : null;
  
  const html = sorted.map(canvas => {
    const content = contentMap[canvas.name];
    const hasContent = !!content;
    const hasMediaPlayer = mediaCanvasName === canvas.name;

    // Check if this is a custom overlay canvas (not part of standard layout)
    const isOverlay = !['Main', 'Header', 'Content', 'Footer', 'Left', 'Right',
      'TopLeft', 'TopRight', 'BottomLeft', 'BottomRight'].includes(canvas.name);

    // Determine content display
    let contentLabel = '';
    if (hasMediaPlayer) {
      const mediaType = mediaState.isAudioPlayback ? '🎵 Audio' : '🎬 Video';
      contentLabel = `<span class="stack-item-media">${mediaType}</span>`;
    } else if (hasContent) {
      contentLabel = `<span class="stack-item-extension">${content.extensionName}</span>`;
    }

    return `
  <div class="canvas-stack-item-compact ${hasContent ? 'has-content' : ''} ${hasMediaPlayer ? 'has-media' : ''}" data-canvas="${canvas.name}" data-zorder="${canvas.zOrder}">
    <div class="stack-item-zorder">${canvas.zOrder}</div>
    <div class="stack-item-info">
      <span class="stack-item-name">${canvas.name}</span>
      ${contentLabel}
      <span class="stack-item-dims">${canvas.width}×${canvas.height}</span>
      ${!canvas.isVisible ? '<span class="stack-item-hidden">Hidden</span>' : ''}
    </div>
    <div class="stack-item-opacity">
      <input type="range" 
             id="stack-opacity-${canvas.name}"
             min="0" max="100" 
             value="${Math.round(canvas.opacity * 100)}"
             oninput="updateCanvasStackOpacity('${canvas.name}', this.value)"
             title="Opacity">
      <span id="stack-opacity-value-${canvas.name}" class="stack-opacity-value">${Math.round(canvas.opacity * 100)}%</span>
    </div>
    <div class="stack-item-actions">
      <button class="btn-stack" onclick="moveStackCanvasUp('${canvas.name}')" title="Move up">▲</button>
      <button class="btn-stack" onclick="moveStackCanvasDown('${canvas.name}')" title="Move down">▼</button>
      ${isOverlay ? `<button class="btn-stack btn-stack-danger" onclick="removeOverlayCanvas('${canvas.name}')" title="Remove">✕</button>` : ''}
    </div>
  </div>
`;
}).join('');

  container.innerHTML = html;
}

/**
 * Update canvas opacity via slider
 */
async function updateCanvasStackOpacity(canvasName, value) {
  const opacity = parseInt(value) / 100.0;
  const valueSpan = document.getElementById(`stack-opacity-value-${canvasName}`);
  if (valueSpan) {
    valueSpan.textContent = `${value}%`;
  }

  try {
    await api.put('/api/canvas/' + encodeURIComponent(canvasName) + '/opacity', { opacity });
    console.log(`✅ Updated opacity for '${canvasName}' to ${value}%`);
  } catch (error) {
    console.error('Failed to update canvas opacity:', error);
    showMessage(`Failed to update opacity: ${error.message}`, 'error');
  }
}

/**
 * Move canvas up in the stack (increase z-order)
 */
async function moveStackCanvasUp(canvasName) {
  try {
    console.log(`Moving canvas '${canvasName}' up...`);
    const result = await api.post('/api/canvas/' + encodeURIComponent(canvasName) + '/move-up');
    console.log(`✅ ${result.data}`);
    await loadCanvasStack();
  } catch (error) {
    console.error('Error moving canvas up:', error);
    showMessage(`Error: ${error.message}`, 'error');
  }
}

/**
 * Move canvas down in the stack (decrease z-order)
 */
async function moveStackCanvasDown(canvasName) {
  try {
    console.log(`Moving canvas '${canvasName}' down...`);
    const result = await api.post('/api/canvas/' + encodeURIComponent(canvasName) + '/move-down');
    console.log(`✅ ${result.data}`);
    await loadCanvasStack();
  } catch (error) {
    console.error('Error moving canvas down:', error);
    showMessage(`Error: ${error.message}`, 'error');
  }
}

/**
 * Bring canvas to front (highest z-order)
 */
async function bringCanvasToFront(canvasName) {
  try {
    const result = await api.post('/api/canvas/' + encodeURIComponent(canvasName) + '/bring-to-front');
    showMessage(result.data, 'success');
    await loadCanvasStack();
  } catch (error) {
    console.error('Error bringing canvas to front:', error);
    showMessage(`Error: ${error.message}`, 'error');
  }
}

/**
 * Send canvas to back (lowest z-order)
 */
async function sendCanvasToBack(canvasName) {
  try {
    const result = await api.post('/api/canvas/' + encodeURIComponent(canvasName) + '/send-to-back');
    showMessage(result.data, 'success');
    await loadCanvasStack();
  } catch (error) {
    console.error('Error sending canvas to back:', error);
    showMessage(`Error: ${error.message}`, 'error');
  }
}

// Expose globally
window.toggleStackInfo = toggleStackInfo;
window.loadCanvasStack = loadCanvasStack;
window.displayCanvasStack = displayCanvasStack;
window.updateCanvasStackOpacity = updateCanvasStackOpacity;
window.moveStackCanvasUp = moveStackCanvasUp;
window.moveStackCanvasDown = moveStackCanvasDown;
window.bringCanvasToFront = bringCanvasToFront;
window.sendCanvasToBack = sendCanvasToBack;
