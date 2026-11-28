/* ============================================================================
   CONSOLE LOG - Streams backend logs to the GUI
   ============================================================================ */

import { onReady, wire } from '../utils/helpers.js';

const consoleState = {
  latestSequence: 0,
  autoScroll: true,
  paused: false,
  pollTimer: null,
  polling: false,
  filterText: '',
  minLevel: 'all', // all | warn | error
  entries: []
};

/**
 * Initialize console log
 */
function initConsoleLog() {
  wire(document.getElementById('tab-console'), {
    click: { pause: toggleConsolePause, clear: clearConsoleLog, bottom: scrollConsoleToBottom },
    input: { filter: el => filterConsoleLogs(el.value) },
    change: { level: el => filterConsoleLevel(el.value) }
  });
  fetchInitialLogs();
  startLogPolling();
  
  // Detect scroll position to toggle auto-scroll
  const container = document.getElementById('console-output');
  if (container) {
    container.addEventListener('scroll', () => {
      const atBottom = container.scrollHeight - container.scrollTop - container.clientHeight < 40;
      consoleState.autoScroll = atBottom;
      updateAutoScrollIndicator();
    });
  }
}

/**
 * Fetch initial batch of logs
 */
async function fetchInitialLogs() {
  try {
    const result = await window.api.get('/api/logs?count=300');
    consoleState.latestSequence = result.latestSequence;
    consoleState.entries = result.entries;
    renderAllLogs();
  } catch (error) {
    console.error('[CONSOLE] Failed to fetch initial logs:', error);
  }
}

/**
 * Poll for new log entries, one request at a time. Each request gets a time limit: a request the
 * browser never answered (lost connection, NAS asleep) used to leave the log standing until reload.
 */
function startLogPolling() {
  if (consoleState.polling) return;
  consoleState.polling = true;
  scheduleLogPoll(1000);
  // Hidden tabs poll rarely; catch up as soon as the page is visible again.
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) scheduleLogPoll(0);
  });
}

function scheduleLogPoll(delay) {
  clearTimeout(consoleState.pollTimer);
  consoleState.pollTimer = setTimeout(pollLogs, delay);
}

async function pollLogs() {
  if (consoleState.paused) {
    scheduleLogPoll(1000);
    return;
  }
  let delay = 1000;
  const ctrl = new AbortController();
  const limit = setTimeout(() => ctrl.abort(), 8000);
  try {
    const res = await fetch(`${window.API_BASE || ''}/api/logs/poll?since=${consoleState.latestSequence}`,
      { signal: ctrl.signal, cache: 'no-store' });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const result = await res.json();
    setConsoleOffline(false);
    if (result.latestSequence < consoleState.latestSequence) {
      // The host restarted and counts from zero again: start over.
      await fetchInitialLogs();
    } else if (result.entries && result.entries.length > 0) {
      consoleState.latestSequence = result.latestSequence;
      consoleState.entries.push(...result.entries);

      // Trim in-memory entries
      if (consoleState.entries.length > 500) {
        consoleState.entries = consoleState.entries.slice(-500);
      }

      appendLogs(result.entries);
    }
  } catch {
    setConsoleOffline(true);
    delay = 3000;
  } finally {
    clearTimeout(limit);
    scheduleLogPoll(delay);
  }
}

/** Shows in the line count that the host does not answer, instead of a log that just stands still. */
function setConsoleOffline(offline) {
  if (consoleState.offline === offline) return;
  consoleState.offline = offline;
  updateLineCount();
}

/**
 * Render all logs (initial load)
 */
function renderAllLogs() {
  const container = document.getElementById('console-output');
  if (!container) return;
  
  const fragment = document.createDocumentFragment();
  
  consoleState.entries.forEach(entry => {
    if (matchesFilter(entry)) {
      fragment.appendChild(createLogLine(entry));
    }
  });
  
  container.innerHTML = '';
  container.appendChild(fragment);
  
  if (consoleState.autoScroll) {
    container.scrollTop = container.scrollHeight;
  }
  
  updateLineCount();
}

/**
 * Append new log entries (incremental)
 */
function appendLogs(entries) {
  const container = document.getElementById('console-output');
  if (!container) return;
  
  let added = false;
  entries.forEach(entry => {
    if (matchesFilter(entry)) {
      container.appendChild(createLogLine(entry));
      added = true;
    }
  });
  
  if (added && consoleState.autoScroll) {
    container.scrollTop = container.scrollHeight;
  }
  
  // Trim displayed lines
  while (container.children.length > 500) {
    container.removeChild(container.firstChild);
  }
  
  updateLineCount();
}

/**
 * Create a single log line element
 */
function createLogLine(entry) {
  const line = document.createElement('div');
  line.className = 'console-line ' + getLogLevel(entry.msg, entry.level);
  
  const time = document.createElement('span');
  time.className = 'console-time';
  time.textContent = entry.time;
  
  const msg = document.createElement('span');
  msg.className = 'console-msg';
  msg.textContent = entry.msg;
  
  line.appendChild(time);
  line.appendChild(msg);
  return line;
}

/**
 * Get log level class from message content
 */
function getLogLevel(msg, level) {
  // The host sends the level (from ILogger, or guessed for plain console lines); the colour groups below
  // only add a tint for some sources.
  if (level === 'error') return 'level-error';
  if (level === 'warn') return 'level-warn';
  if (!level && (msg.includes('ERROR') || msg.includes('Error') || msg.includes('error:') || msg.includes('Failed'))) return 'level-error';
  if (!level && (msg.includes('WARN') || msg.includes('Warning'))) return 'level-warn';
  if (msg.includes('[YOUTUBE]') || msg.includes('[MEDIA]') || msg.includes('[VIDEO]') || msg.includes('[AUDIO]')) return 'level-media';
  if (msg.includes('[INIT]') || msg.includes('[STARTUP]') || msg.includes('[CONFIG]')) return 'level-init';
  if (msg.includes('[FAVORITES]') || msg.includes('[HISTORY]')) return 'level-info';
  return '';
}

/**
 * Check if entry matches current filter
 */
function matchesFilter(entry) {
  if (consoleState.minLevel === 'error' && entry.level !== 'error') return false;
  if (consoleState.minLevel === 'warn' && entry.level !== 'warn' && entry.level !== 'error') return false;
  if (!consoleState.filterText) return true;
  // Every word must appear; "-word" hides lines that have it (e.g. "vlc -ffmpeg", "-night -health").
  const msg = entry.msg.toLowerCase();
  for (const word of consoleState.filterText.toLowerCase().split(/\s+/)) {
    if (!word || word === '-') continue;
    if (word.startsWith('-')) {
      if (msg.includes(word.slice(1))) return false;
    } else if (!msg.includes(word)) {
      return false;
    }
  }
  return true;
}

/**
 * Show all lines, warnings and errors, or errors only
 */
function filterConsoleLevel(level) {
  consoleState.minLevel = level;
  renderAllLogs();
}

/**
 * Filter logs by text
 */
function filterConsoleLogs(text) {
  consoleState.filterText = text;
  renderAllLogs();
}

/**
 * Toggle pause/resume polling
 */
function toggleConsolePause() {
  consoleState.paused = !consoleState.paused;
  const btn = document.getElementById('console-pause-btn');
  if (btn) {
    btn.textContent = consoleState.paused ? '▶ Resume' : '⏸ Pause';
    btn.classList.toggle('active', consoleState.paused);
  }
}

/**
 * Clear the console display
 */
function clearConsoleLog() {
  const container = document.getElementById('console-output');
  if (container) container.innerHTML = '';
  consoleState.entries = [];
  updateLineCount();
}

/**
 * Scroll to bottom
 */
function scrollConsoleToBottom() {
  const container = document.getElementById('console-output');
  if (container) {
    container.scrollTop = container.scrollHeight;
    consoleState.autoScroll = true;
    updateAutoScrollIndicator();
  }
}

/**
 * Update line count display
 */
function updateLineCount() {
  const el = document.getElementById('console-line-count');
  const container = document.getElementById('console-output');
  if (el && container) {
    el.textContent = `${container.children.length} lines` + (consoleState.offline ? ' · no connection, retrying' : '');
  }
}

/**
 * Update auto-scroll indicator
 */
function updateAutoScrollIndicator() {
  const el = document.getElementById('console-autoscroll');
  if (el) {
    el.classList.toggle('active', consoleState.autoScroll);
  }
}

// Initialize when DOM is ready
onReady(initConsoleLog);
