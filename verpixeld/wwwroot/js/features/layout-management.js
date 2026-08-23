/* ============================================================================
   LAYOUT MANAGEMENT - Layout Profiles, Canvas Display & Extension Assignment
   ============================================================================ */

/**
 * Refresh current layout information
 */
async function refreshLayoutInfo() {
  try {
    const layoutResult = await api.get('/api/layout/current');
    const layout = layoutResult.data;

    const select = document.getElementById('layout-profile');
    if (select && document.activeElement !== select) {
      select.value = layout.profile;
    }

    const desc = document.getElementById('layout-description');
    if (desc) {
      desc.textContent = `Current: ${layout.displayName} - ${layout.description}`;
    }

    displayCanvases(layout.canvases);

    const contentResult = await api.get('/api/layout/content');
    window.canvasContent = contentResult.data.contents;
    displayCanvases(layoutResult.data.canvases);

    // Fetch brightness levels
    await fetchBrightnessLevels();
  } catch (error) {
    console.error('Failed to refresh layout info:', error);
  }
}

/**
 * Display canvases in the canvas grid
 */
function displayCanvases(canvases) {
  const container = document.getElementById('canvas-grid');
  if (!container) return; // section retired (Studio replaces it)

  if (!canvases || canvases.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <div class="empty-state-icon">🖼️</div>
        <div class="empty-state-text">No canvases available</div>
        <div class="empty-state-hint">Select a layout profile to create canvas areas</div>
      </div>`;
    return;
  }

  const contentMap = {};
  if (window.canvasContent) {
    window.canvasContent.forEach(c => {
      contentMap[c.canvasName] = c;
    });
  }

  const html = (typeof contentTargetCanvases === 'function' ? contentTargetCanvases(canvases) : canvases).map(canvas => {
    const content = contentMap[canvas.name];
    const hasContent = !!content;
    const uptime = content ? formatUptime(content.uptime) : null;

    return `
    <div class="card ${hasContent ? 'card-success' : ''} card-interactive">
      <div class="card-header">
        <div>
          <div class="card-title">${canvas.name}</div>
          <div class="card-subtitle">${canvas.width}×${canvas.height}</div>
        </div>
        <span class="badge ${hasContent ? 'active' : 'inactive'}">
          ${hasContent ? 'Active' : 'Empty'}
        </span>
      </div>
      
      ${hasContent ? `
        <div class="card-body">
          <strong>${content.extensionName}</strong>
          <div class="text-muted" style="margin-top: 4px;">Uptime: ${uptime}</div>
        </div>
      ` : '<div class="card-body text-muted">No extension assigned</div>'}
      
      <div class="card-footer">
        <button class="btn btn-small ${hasContent ? 'btn-secondary' : 'btn-primary'}" 
                onclick="assignExtensionToCanvas('${canvas.name}')">
          ${hasContent ? 'Change' : 'Assign'}
        </button>
        ${hasContent ? `
          <button class="btn btn-small btn-secondary" onclick="editExtensionParameters('${canvas.name}')">Edit</button>
          <button class="btn btn-small btn-danger" onclick="stopCanvasContent('${canvas.name}')">Stop</button>
        ` : ''}
      </div>
    </div>
  `;
  }).join('');

  container.innerHTML = html;
}

/**
 * Apply selected layout profile
 */
async function applyLayout() {
  const select = document.getElementById('layout-profile');
  const profile = select.value;
  const applyBtn = document.getElementById('apply-layout-btn');

  const confirmed = await showConfirm({
    title: 'Switch Layout',
    message: `Switch to "${profile}" layout? This will stop all active content.`,
    confirmText: 'Switch',
    cancelText: 'Cancel',
    type: 'warning'
  });

  if (confirmed) {
    setButtonLoading(applyBtn, true);

    try {
      await api.post(`/api/layout/apply/${profile}`);

      currentLoadedLayoutName = null;
      localStorage.removeItem('activeLayoutName');

      toast.success('Scene loaded', `Started from profile ${profile}`);
      await refreshLayoutInfo();
      await fetchSavedLayouts();
      if (typeof updateDrawTargetCanvases === 'function') {
        updateDrawTargetCanvases();
      }
      if (typeof updateCameraTargetCanvases === 'function') {
        updateCameraTargetCanvases();
      }
    } catch (error) {
      toast.error('Error', 'Failed to apply layout: ' + (error.message || 'Unknown error'));
    } finally {
      setButtonLoading(applyBtn, false);
    }
  }
}

/**
 * Show extension picker for canvas assignment
 */
async function assignExtensionToCanvas(canvasName, onPick) {
  if (typeof isSystemOverlayCanvas === 'function' && isSystemOverlayCanvas(canvasName)) {
    if (typeof showMessage === 'function')
      showMessage(`'${canvasName}' is a host overlay — pick another canvas for content`, 'info');
    return;
  }
  // Check if this canvas is actually showing video (Main remaps to MediaPlayer).
  const mediaCanvas = typeof mediaPlaybackCanvasName === 'function'
    ? mediaPlaybackCanvasName()
    : ((window.mediaState || {}).playbackCanvasName || null);
  if (mediaCanvas && mediaCanvas === canvasName) {
    const confirmed = await showConfirm({
      title: 'Canvas in Use',
      message: `The canvas "${canvasName}" is currently being used by the media player.\n\nAssigning an extension will interfere with media playback. Do you want to stop the media player first?`,
      confirmText: 'Stop Media & Continue',
      cancelText: 'Cancel',
      type: 'warning',
      icon: '⚠️'
    });
    
    if (!confirmed) {
      return;
    }
    
    // Stop media player
    if (typeof stopDemoPlayback === 'function') {
      await stopDemoPlayback();
    }
  }
  
  const html = `
    <div class="modal-overlay" id="extension-picker-modal">
      <div class="modal-content" style="max-width: 900px;">
        
        <div class="modal-header">
          <h2>${ICONS.CANVAS} Assign Extension to ${canvasName}</h2>
          <button class="modal-close" onclick="closeExtensionPicker()">${ICONS.CLOSE}</button>
        </div>
        
        <div class="modal-body">
          <div class="extension-selector" id="extension-selector">
            <!-- Extensions will be populated here -->
          </div>
        </div>
        
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="closeExtensionPicker()">Cancel</button>
        </div>
        
      </div>
    </div>
  `;
  
  document.body.insertAdjacentHTML('beforeend', html);
  
  try {
    const result = await api.get('/api/extensions/available');
    const container = document.getElementById('extension-selector');
    
    const extHtml = result.data.map(ext => {
        let iconHtml;
        if (ext.iconData) {
          let mimeType = 'image/png';
          try {
            const decoded = atob(ext.iconData);
            if (decoded.includes('<svg')) mimeType = 'image/svg+xml';
          } catch (e) { }

          iconHtml = `<div class="extension-icon-container">
                        <img src="data:${mimeType};base64,${ext.iconData}" 
                             alt="${ext.displayName}" class="extension-icon-img"
                             onerror="this.style.display='none'; this.nextElementSibling.style.display='block';" />
                        <div class="extension-icon" style="display:none;">🧩</div>
                    </div>`;
        }
        else {
          iconHtml = `<div class="extension-icon">🧩</div>`;
        }

        return `
                    <div class="extension-card" data-extension="${ext.displayName}" data-canvas="${canvasName}">
                        ${iconHtml}
                        <div class="extension-name">${ext.displayName}</div>
                    </div>
                `;
      }).join('');

      container.innerHTML = extHtml;
      
    container.querySelectorAll('.extension-card').forEach(card => {
      card.addEventListener('click', () => {
        const extensionName = card.getAttribute('data-extension');
        closeExtensionPicker();
        // When a pick-callback is supplied (e.g. the Studio Content list), hand the choice back instead of
        // assigning directly to the canvas.
        if (typeof onPick === 'function') onPick(extensionName);
        else confirmExtensionAssignment(canvasName, extensionName);
      });
    });
  } catch (error) {
    console.error('Failed to load extensions:', error);
    showMessage('Failed to load extensions: ' + (error.message || 'Unknown error'), true);
    closeExtensionPicker();
  }
}

/**
 * Close extension picker modal
 */
function closeExtensionPicker() {
  const modal = document.getElementById('extension-picker-modal');
  if (modal) modal.remove();
}

/**
 * Confirm and execute extension assignment
 */
async function confirmExtensionAssignment(canvasName, extensionName) {
  closeExtensionPicker();

  try {
    await api.post('/api/layout/assign', { canvasName, extensionName });
    showMessage(`${extensionName} assigned to ${canvasName}`, false);
    await refreshLayoutInfo();
  } catch (error) {
    showMessage('Failed to assign extension: ' + (error.message || 'Unknown error'), true);
  }
}

/**
 * Stop content on a canvas
 */
async function stopCanvasContent(canvasName) {
  const confirmed = await showConfirm({
    title: 'Stop Content',
    message: `Stop content on ${canvasName}?`,
    confirmText: 'Stop',
    cancelText: 'Cancel',
    type: 'warning'
  });

  if (confirmed) {
    try {
      await api.post(`/api/layout/stop/${canvasName}`);
      showMessage(`Stopped content on ${canvasName}`, false);
      await refreshLayoutInfo();
    } catch (error) {
      showMessage('Failed to stop content: ' + (error.message || 'Unknown error'), true);
    }
  }
}

// Expose globally
window.refreshLayoutInfo = refreshLayoutInfo;
window.displayCanvases = displayCanvases;
window.applyLayout = applyLayout;
window.assignExtensionToCanvas = assignExtensionToCanvas;
window.closeExtensionPicker = closeExtensionPicker;
window.confirmExtensionAssignment = confirmExtensionAssignment;
window.stopCanvasContent = stopCanvasContent;
window.refreshLayout = refreshLayoutInfo; // Alias
