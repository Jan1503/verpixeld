/* ============================================================================
   APPLICATION INITIALIZATION - Core startup and global function exposure
   ============================================================================ */

import { fetchBrightnessLevels } from '../features/brightness.js';
import { loadCanvasStack } from '../features/canvas-stack.js';
import { initDrawMode } from '../features/draw-mode.js';
import { clearAllFilters, displayFilters, fetchAvailableFilters, showFilterPicker } from '../features/filter-management.js';
import { applyLayout, refreshLayoutInfo } from '../features/layout-management.js';
import { refreshNightModeStatus } from '../features/night-mode.js';
import { fetchSavedLayouts, showSaveLayoutDialog } from '../features/saved-layouts.js';
import { fetchSchedules } from '../features/scheduler.js';
import { applyHostRestartButton, confirmRestartRender, confirmSystemReboot, startReconnectionCheck, updateConnectionStatus } from '../features/system-control.js';
import { initVisualizer } from '../features/visualizer.js';
import { restoreCollapsedStates } from '../ui/navigation.js';
import { initTabs } from '../ui/tabs.js';
import { initTheme } from '../ui/theme.js';
import { isBusy, onReady, trackAction } from '../utils/helpers.js';
import { refreshAllCanvasSelectors } from '../features/canvas-selectors.js';

/**
 * Format filter type name for display
 */
export function formatFilterName(typeName) {
  return typeName
    .replace(/Filter$/, '')
    .replace(/([A-Z])/g, ' $1')
    .trim();
}

/**
 * Format filter parameters for display
 */
export function formatParameters(params) {
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
export async function fetchStatus() {
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
 * Fetch active filters from server
 */
export async function fetchFilters() {
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
  refreshAllCanvasSelectors();
  
  // Listen for layout changes to refresh canvas selectors
  window.addEventListener('layoutChanged', () => {
    refreshAllCanvasSelectors();
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

// DOMContentLoaded handler
onReady(initializeApplication);

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
onReady(() => {
  initEnhancedUI();
  initDrawMode();
  initVisualizer();
});
