/* ============================================================================
   CONSOLE LOG - Streams backend logs to the GUI
   ============================================================================ */

const consoleState = {
  latestSequence: 0,
  autoScroll: true,
  paused: false,
  pollInterval: null,
  filterText: '',
  entries: []
};

/**
 * Initialize console log
 */
function initConsoleLog() {
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
 * Start polling for new log entries
 */
function startLogPolling() {
  if (consoleState.pollInterval) return;
  
  consoleState.pollInterval = setInterval(async () => {
    if (consoleState.paused) return;
    
    try {
      const result = await window.api.get(`/api/logs/poll?since=${consoleState.latestSequence}`);
      if (result.entries && result.entries.length > 0) {
        consoleState.latestSequence = result.latestSequence;
        consoleState.entries.push(...result.entries);
        
        // Trim in-memory entries
        if (consoleState.entries.length > 500) {
          consoleState.entries = consoleState.entries.slice(-500);
        }
        
        appendLogs(result.entries);
      }
    } catch {
      return;
    }
  }, 1000);
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
  line.className = 'console-line ' + getLogLevel(entry.msg);
  
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
function getLogLevel(msg) {
  if (msg.includes('ERROR') || msg.includes('Error') || msg.includes('error:') || msg.includes('Failed')) return 'level-error';
  if (msg.includes('WARN') || msg.includes('Warning')) return 'level-warn';
  if (msg.includes('[YOUTUBE]') || msg.includes('[MEDIA]') || msg.includes('[VIDEO]') || msg.includes('[AUDIO]')) return 'level-media';
  if (msg.includes('[INIT]') || msg.includes('[STARTUP]') || msg.includes('[CONFIG]')) return 'level-init';
  if (msg.includes('[FAVORITES]') || msg.includes('[HISTORY]')) return 'level-info';
  return '';
}

/**
 * Check if entry matches current filter
 */
function matchesFilter(entry) {
  if (!consoleState.filterText) return true;
  return entry.msg.toLowerCase().includes(consoleState.filterText.toLowerCase());
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
    el.textContent = `${container.children.length} lines`;
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

// Expose globally
window.initConsoleLog = initConsoleLog;
window.filterConsoleLogs = filterConsoleLogs;
window.toggleConsolePause = toggleConsolePause;
window.clearConsoleLog = clearConsoleLog;
window.scrollConsoleToBottom = scrollConsoleToBottom;

// Initialize when DOM is ready
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initConsoleLog);
} else {
  setTimeout(initConsoleLog, 100);
}
