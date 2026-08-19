/* ============================================================================
   SAVED LAYOUTS - Layout Persistence and Management
   ============================================================================ */

/**
 * Fetch and display saved layouts
 */
async function fetchSavedLayouts() {
  const container = document.getElementById('saved-layouts-list');

  // Show loading skeleton
  container.innerHTML = '<div class="skeleton-card"></div><div class="skeleton-card"></div>';

  try {
    const result = await api.get('/api/layout/saved');
    await displaySavedLayouts(result.data);
  } catch (error) {
    console.error('Failed to fetch saved layouts:', error);
    container.innerHTML =
      '<p class="text-danger">Error loading saved layouts</p>';
  }
}

/**
 * Display saved layouts in the UI
 */
async function displaySavedLayouts(layouts) {
  const container = document.getElementById('saved-layouts-list');

  if (!layouts || layouts.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <div class="empty-state-icon">📁</div>
        <div class="empty-state-text">No saved layouts yet</div>
        <div class="empty-state-hint">Save your current layout to quickly restore it later</div>
      </div>`;
    return;
  }

  // Get current active layout profile
  let currentProfile = null;
  try {
    const result = await api.get('/api/layout/current');
    currentProfile = result.data.profile;
  } catch {
    // continue with currentProfile = null
  }

  // Sort by last modified (newest first)
  layouts.sort((a, b) => new Date(b.lastModified) - new Date(a.lastModified));

  const html = layouts.map(layout => {
    const modifiedDate = new Date(layout.lastModified).toLocaleString();
    const isDefault = layout.isDefault === true;
    const isActive = currentLoadedLayoutName && layout.name === currentLoadedLayoutName;

    return `
  <div class="card ${isActive ? 'card-success' : isDefault ? 'card-info' : ''} card-interactive saved-layout-card${isDefault ? ' is-default' : ''}${isActive ? ' is-active' : ''}">
    <div class="card-header">
      <div>
        <div class="card-title">
          ${escapeHtml(layout.name)}
          ${isDefault ? `<span class="badge-default">${ICONS.PIN} Default</span>` : ''}
          ${isActive ? `<span class="badge active">${ICONS.ACTIVE} ACTIVE</span>` : ''}
        </div>
        <div class="card-subtitle">${layout.profile}</div>
      </div>
    </div>
    
    ${layout.description ? `
      <div class="card-body">
        ${escapeHtml(layout.description)}
      </div>
    ` : ''}
    
    <div class="card-meta" style="margin-top: ${layout.description ? 'var(--spacing-sm)' : '0'};">
      <div style="font-size: 0.75rem; color: var(--color-text-muted); display: flex; justify-content: space-between;">
        <span>${Object.keys(layout.canvases || {}).length} canvas(es)</span>
        <span>Modified: ${modifiedDate}</span>
      </div>
    </div>
    
    <div class="card-footer">
      <button class="btn btn-small btn-primary" onclick="loadSavedLayout('${escapeHtml(layout.name)}')">Load</button>
      ${!isDefault ?
      `<button class="btn btn-small btn-secondary" onclick="setAsDefaultLayout('${escapeHtml(layout.name)}')">Set Default</button>` :
      `<button class="btn btn-small btn-warning" onclick="clearDefaultLayout('${escapeHtml(layout.name)}')">Clear Default</button>`
      }
      <button class="btn btn-small" onclick="viewLayoutDetails('${escapeHtml(layout.name)}')">Details</button>
      <button class="btn btn-small btn-danger" onclick="deleteSavedLayout('${escapeHtml(layout.name)}')">Delete</button>
    </div>
  </div>
`;
  }).join('');

  container.innerHTML = html;
}

/**
 * Show save layout dialog
 */
async function showSaveLayoutDialog() {
  try {
    const layoutResult = await api.get('/api/layout/current');
    const contentResult = await api.get('/api/layout/content');

    const layout = layoutResult.data;
    const content = contentResult.data;

    const extensionCount = content.contents?.filter(c => c.extensionName).length || 0;

    // Check if night mode is enabled
    const nightModeResult = await api.get('/api/nightmode/config');
    const nightModeEnabled = nightModeResult.data.enabled;

    const html = `
  <div class="modal-overlay" id="save-layout-modal">
    <div class="modal-content" style="max-width: 600px;">
      
      <div class="modal-header">
        <h2>${ICONS.SAVE} Save Current Layout</h2>
        <button class="modal-close" onclick="closeSaveLayoutModal()">${ICONS.CLOSE}</button>
      </div>
      
      <div class="modal-body">
        <div style="margin-bottom: 15px;">
          <label for="save-layout-name">Layout Name *</label>
          <input type="text" id="save-layout-name" placeholder="My Custom Layout" required>
        </div>
        
        <div style="margin-bottom: 15px;">
          <label for="save-layout-description">Description (optional)</label>
          <textarea id="save-layout-description" placeholder="Describe this layout..." rows="3"></textarea>
        </div>
        
        <div class="save-layout-summary">
          <p><strong>Current Configuration:</strong></p>
          <p>Profile: ${layout.displayName}</p>
          <p>Canvases: ${layout.canvasCount}</p>
          <p>Extensions: ${extensionCount}</p>
        </div>
        
        <div class="save-layout-options">
          <label class="checkbox-label">
            <input type="checkbox" id="save-layout-as-default">
            <span>${ICONS.PIN} Set as default (loads on startup)</span>
          </label>
          
          <label class="checkbox-label">
            <input type="checkbox" id="save-layout-include-filters">
            <span>${ICONS.FILTER} Include active filters</span>
          </label>
          
          <label class="checkbox-label">
            <input type="checkbox" id="save-layout-override-brightness" checked>
            <span>${ICONS.BRIGHTNESS} Apply this layout's brightness when loading</span>
          </label>
          ${nightModeEnabled ?
            `<p class="help-text" style="margin-left: 28px; color: #e67e22;">${ICONS.WARNING} Will temporarily override night mode (night mode resumes on next check)</p>` :
            '<p class="help-text" style="margin-left: 28px;">When unchecked, the current brightness will be preserved</p>'
          }
        </div>
      </div>
      
      <div class="modal-footer">
        <button class="btn btn-secondary" onclick="closeSaveLayoutModal()">Cancel</button>
        <button class="btn btn-primary" onclick="confirmSaveLayout()">${ICONS.SAVE} Save Layout</button>
      </div>
      
    </div>
  </div>
`;

    document.body.insertAdjacentHTML('beforeend', html);

  } catch (error) {
    console.error('Error showing save dialog:', error);
    showMessage('Failed to show save dialog', 'error');
  }
}

/**
 * Close save layout modal
 */
function closeSaveLayoutModal() {
  const modal = document.getElementById('save-layout-modal');
  if (modal) modal.remove();
}

/**
 * Confirm and save layout
 */
async function confirmSaveLayout() {
  const name = document.getElementById('save-layout-name').value.trim();
  const description = document.getElementById('save-layout-description').value.trim();
  const isDefault = document.getElementById('save-layout-as-default').checked;
  const includeFilters = document.getElementById('save-layout-include-filters').checked;
  const overrideGlobalBrightness = document.getElementById('save-layout-override-brightness').checked;

  if (!name) {
    showMessage('Please enter a layout name', 'error');
    return;
  }

  try {
    const result = await api.post('/api/layout/save', {
      name,
      description,
      isDefault,
      includeFilters,
      overrideGlobalBrightness
    });

    showMessage(result.data.message, 'success');
    closeModal();
    await fetchSavedLayouts();
  } catch (error) {
    console.error('Error saving layout:', error);
    showMessage('Failed to save layout', 'error');
  }
}

/**
 * Load a saved layout
 */
async function loadSavedLayout(layoutName) {
  const confirmed = await showConfirm({
    title: 'Load Layout',
    message: `Load layout "${layoutName}"? This will stop all active content and apply the saved layout.`,
    confirmText: 'Load',
    cancelText: 'Cancel',
    type: 'warning'
  });

  if (!confirmed) {
    return;
  }

  showLoading(`Loading layout "${layoutName}"...`);

  try {
    // Stop video playback before loading new layout (but keep audio playing)
    const mediaState = window.mediaState || {};
    if (mediaState.isRunning && !mediaState.isAudioPlayback) {
      console.log('[LAYOUT] Stopping video playback before loading new layout');
      if (typeof stopDemoPlayback === 'function') {
        await stopDemoPlayback();
      }
    }
    
    const result = await api.post(`/api/layout/load/${encodeURIComponent(layoutName)}`);

    toast.success('Layout Loaded', result.data.message);

      // Track the loaded layout name
      currentLoadedLayoutName = layoutName;
      localStorage.setItem('activeLayoutName', layoutName);

      // Refresh all UI
      await refreshLayoutInfo();
      await fetchSavedLayouts();
      
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
    console.error('Error loading layout:', error);
    toast.error('Error', 'Failed to load layout: ' + error.message);
  } finally {
    hideLoading();
  }
}

/**
 * Delete a saved layout
 */
async function deleteSavedLayout(layoutName) {
  const confirmed = await showConfirm({
    title: 'Delete Layout',
    message: `Delete layout "${layoutName}"? This action cannot be undone.`,
    confirmText: 'Delete',
    cancelText: 'Keep',
    type: 'danger',
    icon: `${ICONS.DELETE}`
  });

  if (!confirmed) {
    return;
  }

  try {
    const result = await api.del(`/api/layout/saved/${encodeURIComponent(layoutName)}`);

    showMessage(result.data, 'success');
    await fetchSavedLayouts();
  } catch (error) {
    console.error('Error deleting layout:', error);
    showMessage('Failed to delete layout', 'error');
  }
}

/**
 * Set a layout as default
 */
async function setAsDefaultLayout(layoutName) {
  try {
    const result = await api.post(`/api/layout/saved/${encodeURIComponent(layoutName)}/set-default`);

    showMessage(result.data, 'success');
    await fetchSavedLayouts();
  } catch (error) {
    console.error('Error setting default layout:', error);
    showMessage('Failed to set default layout', 'error');
  }
}

/**
 * Clear default status from a layout
 */
async function clearDefaultLayout(layoutName) {
  try {
    const result = await api.post(`/api/layout/saved/${encodeURIComponent(layoutName)}/clear-default`);

    showMessage(result.data, 'success');
    await fetchSavedLayouts();
  } catch (error) {
    console.error('Error clearing default layout:', error);
    showMessage('Failed to clear default layout', 'error');
  }
}

/**
 * View layout details
 */
async function viewLayoutDetails(layoutName) {
  try {
    const result = await api.get(`/api/layout/saved/${encodeURIComponent(layoutName)}`);

    const layout = result.data;
    const createdDate = new Date(layout.createdAt).toLocaleString();
    const modifiedDate = new Date(layout.lastModified).toLocaleString();

    const canvasesHtml = Object.entries(layout.canvases || {}).map(([name, config]) => `
      <div style="padding: 10px; border-radius: 6px; margin: 8px 0;">
        <strong>${name}:</strong> ${config.extensionName || '(empty)'}
        ${config.extensionName ? `
        <br>
        <small>${Object.keys(config.configuration || {}).length} settings configured</small>
        ${config.brightness !== undefined ? `<br><small>Brightness: ${(config.brightness * 100).toFixed(0)}%</small>` : ''}` : ''}
      </div>
    `).join('');

    const html = `
  <div class="modal-overlay" id="layout-details-modal">
    <div class="modal-content" style="max-width: 700px;">
      
      <div class="modal-header">
        <h2>${ICONS.DETAILS} Layout Details</h2>
        <button class="modal-close" onclick="closeLayoutDetailsModal()">${ICONS.CLOSE}</button>
      </div>
      
      <div class="modal-body">
        <div style="margin-bottom: 15px;">
          <label><strong>Name</strong></label>
          <p>${escapeHtml(layout.name)} ${layout.isDefault ? `<span class="badge-default">${ICONS.PIN} Default</span>` : ''}</p>
        </div>
        
        ${layout.description ? `
        <div style="margin-bottom: 15px;">
          <label><strong>Description</strong></label>
          <p>${escapeHtml(layout.description)}</p>
        </div>` : ''}

        <div style="margin-bottom: 15px;">
          <label><strong>Profile</strong></label>
          <p>${layout.profile}</p>
        </div>
        
        ${layout.globalBrightness !== undefined ? `
        <div style="margin-bottom: 15px;">
          <label><strong>Global Brightness</strong></label>
          <p>${(layout.globalBrightness * 100).toFixed(0)}% ${layout.overrideGlobalBrightness === false ? '<span style="color: #95a5a6;">(will not override on load)</span>' : ''}</p>
        </div>` : ''}
        
        <div style="margin-bottom: 15px;">
          <label><strong>Brightness Behavior</strong></label>
          <p>${layout.overrideGlobalBrightness !== false ? `${ICONS.BRIGHTNESS} Will restore saved brightness when loaded` : `${ICONS.INFO} Will preserve current brightness when loaded`}</p>
        </div>
        
        ${layout.filters && layout.filters.length > 0 ? `
        <div style="margin-bottom: 15px;">
          <label><strong>Filters</strong></label>
          <p>${ICONS.FILTER} ${layout.filters.length} filter(s) saved</p>
        </div>` : ''}
        
        <div style="margin-bottom: 15px;">
          <label><strong>Created</strong></label>
          <p>${createdDate}</p>
        </div>
        
        <div style="margin-bottom: 15px;">
          <label><strong>Last Modified</strong></label>
          <p>${modifiedDate}</p>
        </div>
        
        <div style="margin-bottom: 15px;">
          <label><strong>Canvas Configurations</strong></label>
          ${canvasesHtml || '<p class="text-muted">No canvases configured</p>'}
        </div>
      </div>
      
      <div class="modal-footer">
        <button class="btn btn-secondary" onclick="closeLayoutDetailsModal()">Close</button>
        <button class="btn btn-primary" onclick="closeLayoutDetailsModal(); loadSavedLayout('${escapeHtml(layout.name)}')">
          Load This Layout
        </button>
      </div>
      
    </div>
  </div>
`;

    document.body.insertAdjacentHTML('beforeend', html);

  } catch (error) {
    console.error('Error viewing layout details:', error);
    showMessage('Failed to get layout details', 'error');
  }
}

/**
 * Close layout details modal
 */
function closeLayoutDetailsModal() {
  const modal = document.getElementById('layout-details-modal');
  if (modal) modal.remove();
}

// Expose globally
window.fetchSavedLayouts = fetchSavedLayouts;
window.displaySavedLayouts = displaySavedLayouts;
window.showSaveLayoutDialog = showSaveLayoutDialog;
window.closeSaveLayoutModal = closeSaveLayoutModal;
window.confirmSaveLayout = confirmSaveLayout;
window.loadSavedLayout = loadSavedLayout;
window.deleteSavedLayout = deleteSavedLayout;
window.setAsDefaultLayout = setAsDefaultLayout;
window.clearDefaultLayout = clearDefaultLayout;
window.viewLayoutDetails = viewLayoutDetails;
window.closeLayoutDetailsModal = closeLayoutDetailsModal;
