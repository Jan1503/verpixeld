/* ============================================================================
   APPLICATION INITIALIZATION - Core startup and global function exposure
   ============================================================================ */

/**
 * Format filter type name for display
 */
function formatFilterName(typeName) {
  return typeName
    .replace(/Filter$/, '')
    .replace(/([A-Z])/g, ' $1')
    .trim();
}

/**
 * Format filter parameters for display
 */
function formatParameters(params) {
  return Object.entries(params)
    .filter(([key]) => !['FilterName', 'Name'].includes(key))
    .map(([key, value]) => {
      const displayValue = typeof value === 'number' ? value.toFixed(2) : value;
      return `${key}: ${displayValue}`;
    })
    .join(', ');
}

/**
 * Fetch application status from server
 */
async function fetchStatus() {
  try {
    const result = await window.api.get('/api/status');
    if (result.data) {
      const data = result.data;
      
      // Update stat cards
      const resolutionEl = document.getElementById('resolution');
      if (resolutionEl) resolutionEl.textContent = data.displayResolution.replace('x', '×');
      
      const fpsEl = document.getElementById('stat-fps');
      if (fpsEl) fpsEl.textContent = data.fps || '--';
      
      const uptimeEl = document.getElementById('stat-uptime');
      if (uptimeEl) uptimeEl.textContent = data.uptimeFormatted;

      window.__inContainer = !!data.inContainer;
      if (typeof applyHostRestartButton === 'function') applyHostRestartButton();
      
      updateConnectionStatus('connected');
    }
  } catch (error) {
    console.error('Failed to fetch status:', error);
    updateConnectionStatus('disconnected');
  }
}

/**
 * Fetch available filters from server
 */
async function fetchAvailableFilters() {
  try {
    const result = await window.api.get('/api/filters/available');
    if (result.data) {
      availableFilters = result.data;
    }
  } catch (error) {
    console.error('Failed to fetch available filters:', error);
  }
}

/**
 * Fetch active filters from server
 */
async function fetchFilters() {
  try {
    const result = await window.api.get('/api/filters');
    if (result.data) {
      displayFilters(result.data);
    }
  } catch (error) {
    console.error('Failed to fetch filters:', error);
  }
}

/**
 * Initialize application on DOM ready
 */
function initializeApplication() {
  console.log('DOM loaded, initializing application...');

  // Restore last loaded layout from localStorage
  currentLoadedLayoutName = localStorage.getItem('activeLayoutName') || null;
  console.log('📋 Active layout from localStorage:', currentLoadedLayoutName || 'none');

  // Initial data load
  fetchStatus();
  fetchAvailableFilters();
  fetchFilters();
  refreshLayoutInfo();
  fetchSavedLayouts();
  fetchBrightnessLevels();
  refreshNightModeStatus();
  loadCanvasStack();
  fetchSchedules();
  initTheme();
  
  // Initialize all canvas selectors
  if (typeof refreshAllCanvasSelectors === 'function') {
    refreshAllCanvasSelectors();
  }
  
  // Listen for layout changes to refresh canvas selectors
  window.addEventListener('layoutChanged', () => {
    if (typeof refreshAllCanvasSelectors === 'function') {
      refreshAllCanvasSelectors();
    }
  });

  // Static buttons with an id; trackAction gives them the same busy state and error toast as wired ones.
  const onClick = (id, fn) => {
    const btn = document.getElementById(id);
    btn?.addEventListener('click', event => { if (!isBusy(btn, event)) trackAction(btn, fn()); });
  };
  onClick('apply-layout-btn', () => applyLayout());
  onClick('refresh-layout-btn', () => refreshLayoutInfo());
  onClick('save-current-layout-btn', () => showSaveLayoutDialog());
  onClick('refresh-saved-layouts-btn', () => fetchSavedLayouts());
  onClick('start-local-mode-btn', () => startSelectedLocalMode());
  onClick('stop-local-mode-btn', () => setMode('stop'));
  onClick('configure-mode-btn', () => showModeConfig());
  onClick('add-filter-btn', () => showFilterPicker());
  onClick('clear-all-filters-btn', () => clearAllFilters());
  onClick('reboot-btn', () => confirmSystemReboot());
  onClick('restart-render-btn', () => confirmRestartRender());

  console.log('Application initialized');
}

/**
 * Initialize enhanced UI features
 */
function initEnhancedUI() {
  // Initialize tab navigation system
  if (typeof initTabs === 'function') {
    initTabs();
  }
  
  // Restore collapsed sections state
  if (typeof restoreCollapsedStates === 'function') {
    restoreCollapsedStates();
  }
  
  // Start connection status monitoring
  if (typeof startReconnectionCheck === 'function') {
    startReconnectionCheck();
  }
}

// Expose globally
window.formatFilterName = formatFilterName;
window.formatParameters = formatParameters;
window.fetchStatus = fetchStatus;
window.fetchAvailableFilters = fetchAvailableFilters;
window.fetchFilters = fetchFilters;
window.initializeApplication = initializeApplication;
window.initEnhancedUI = initEnhancedUI;

// DOMContentLoaded handler
document.addEventListener('DOMContentLoaded', initializeApplication);

// Auto-refresh interval
setInterval(() => {
  if (document.hidden) return;
  fetchStatus();
  fetchFilters();
  refreshLayoutInfo();
  refreshNightModeStatus();
  loadCanvasStack();
}, 5000);

// Initialize enhanced UI features when DOM is ready
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => {
    initEnhancedUI();
    if (typeof initDrawMode === 'function') {
      initDrawMode();
    }
    if (typeof initVisualizer === 'function') {
      initVisualizer();
    }
  });
} else {
  initEnhancedUI();
  if (typeof initDrawMode === 'function') {
    initDrawMode();
  }
  if (typeof initVisualizer === 'function') {
    initVisualizer();
  }
}
