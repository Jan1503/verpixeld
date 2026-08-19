/* ============================================================================
   CONSTANTS & GLOBALS
   ============================================================================ */

const API_BASE = '';

// Unified Icon Set - Use these constants throughout the application
const ICONS = {
  // Actions
  ADD: '➕',
  EDIT: '✏️',
  DELETE: '🗑️',
  SAVE: '💾',
  CLOSE: '✕',
  REFRESH: '🔄',
  SEARCH: '🔍',

  // Status
  SUCCESS: '✓',
  ERROR: '✗',
  WARNING: '⚠',
  INFO: 'ℹ',
  ACTIVE: '✅',
  INACTIVE: '⏸️',

  // Navigation
  UP: '⬆️',
  DOWN: '⬇️',
  LEFT: '⬅️',
  RIGHT: '➡️',

  // Content
  LAYOUT: '📐',
  CANVAS: '🖼️',
  FILTER: '🎨',
  SCHEDULE: '📅',
  BRIGHTNESS: '💡',
  NIGHT_MODE: '🌙',
  THEME: '☀️',
  SETTINGS: '⚙️',
  PLAY: '▶️',
  PAUSE: '⏸️',
  STOP: '⏹️',

  // Misc
  PIN: '📌',
  CLOCK: '⏰',
  DETAILS: '📋'
};

// Global state
let currentLoadedLayoutName = null;
let availableFilters = [];
let extensionUpdateTimer = null;

// Expose globally
window.ICONS = ICONS;
window.API_BASE = API_BASE;