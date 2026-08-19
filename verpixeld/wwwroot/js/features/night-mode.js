/* ============================================================================
   NIGHT MODE - Automatic Brightness Scheduling
   ============================================================================ */

/**
 * Fetch night mode configuration from server
 */
async function fetchNightModeConfig() {
  try {
    const result = await window.api.get('/api/nightmode/config');
    return result.data;
  } catch {
    return null;
  }
}

/**
 * Refresh the night mode status display
 */
async function refreshNightModeStatus() {
  try {
    const statusResult = await window.api.get('/api/nightmode/status');
    const configResult = await window.api.get('/api/nightmode/config');

    {
      const status = statusResult.data;
      const config = configResult.data;

      const badge = document.getElementById('night-mode-badge');
      const info = document.getElementById('night-mode-info');

      if (config.enabled) {
        badge.textContent = status.isActive ? `${ICONS.NIGHT_MODE} ${status.mode.toUpperCase()} MODE` : `${ICONS.THEME} ${status.mode.toUpperCase()} MODE`;
        badge.className = `badge ${status.isActive ? 'night-mode' : 'active'}`;        

        const scheduleInfo = config.startTime && config.endTime
          ? `Schedule: ${config.startTime} - ${config.endTime}`
          : 'No schedule set';

        info.textContent = `${scheduleInfo} | Current: ${status.currentPercentage}% | Target: ${status.targetPercentage}%`;
      } else {
        badge.textContent = 'Disabled';
        badge.className = 'badge inactive';
        info.textContent = 'Configure night mode to enable automatic brightness adjustment';
      }
    }
  } catch (error) {
    console.error('Failed to refresh night mode status:', error);
  }
}

/**
 * Show the night mode configuration modal
 */
async function showNightModeConfig() {
  try {
    const config = await fetchNightModeConfig();

    if (!config) {
      showMessage('Failed to load night mode configuration', 'error');
      return;
    }

    const daysOfWeek = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
    const activeDaysChecks = daysOfWeek.map((day, index) => {
      const checked = config.activeDays.length === 0 || config.activeDays.includes(index);
      return `        
        <label class="checkbox-label">
          <input type="checkbox" class="day-checkbox" value="${index}" ${checked ? 'checked' : ''}>
          <span>${day}</span>
        </label>
      `;
    }).join('');

    const html = `
    <div class="modal-overlay" id="night-mode-config-modal">
      <div class="modal-content" style="max-width: 600px;">
        
        <div class="modal-header">
          <h2>${ICONS.NIGHT_MODE} Night Mode Configuration</h2>
          <button class="modal-close" onclick="closeNightModeConfig()">${ICONS.CLOSE}</button>
        </div>
        
        <div class="modal-body">
          <div class="config-section">
            <label class="checkbox-label">
              <input type="checkbox" id="nightmode-enabled" ${config.enabled ? 'checked' : ''}>
              <span><strong>Enable Night Mode</strong></span>
            </label>
            <p class="help-text">Automatically adjust brightness based on time schedule to save energy</p>
          </div>

          <div class="config-section">
            <h3>${ICONS.CLOCK} Schedule</h3>
            <div class="time-inputs">
              <div class="time-input-group">
                <label for="nightmode-start">Night Start Time</label>
                <input type="time" id="nightmode-start" value="${config.startTime}" required>
              </div>
              <div class="time-input-group">
                <label for="nightmode-end">Night End Time</label>
                <input type="time" id="nightmode-end" value="${config.endTime}" required>
              </div>
            </div>
            <p class="help-text">Times are in 24-hour format. Night mode spans from start to end time (can be overnight)</p>
          </div>

          <div class="config-section">
            <h3>${ICONS.BRIGHTNESS} Brightness Levels</h3>
            <div class="brightness-inputs">
              <div class="brightness-input-group">
                <label for="nightmode-day-brightness">Day Brightness</label>
                <div class="slider-with-value">
                  <input type="range" id="nightmode-day-brightness" min="0" max="100" 
                         value="${Math.round(config.dayBrightness * 100)}"
                         oninput="document.getElementById('nightmode-day-value').textContent = this.value + '%'">
                  <span id="nightmode-day-value">${Math.round(config.dayBrightness * 100)}%</span>
                </div>
              </div>
              <div class="brightness-input-group">
                <label for="nightmode-night-brightness">Night Brightness</label>
                <div class="slider-with-value">
                  <input type="range" id="nightmode-night-brightness" min="0" max="100" 
                         value="${Math.round(config.nightBrightness * 100)}"
                         oninput="document.getElementById('nightmode-night-value').textContent = this.value + '%'">
                  <span id="nightmode-night-value">${Math.round(config.nightBrightness * 100)}%</span>
                </div>
              </div>
            </div>
          </div>

          <div class="config-section">
            <h3>${ICONS.SETTINGS} Transition</h3>
            <div class="transition-input">
              <label for="nightmode-transition">Transition Duration (minutes)</label>
              <input type="number" id="nightmode-transition" min="0" max="60" 
                     value="${config.transitionMinutes}">
              <p class="help-text">Gradual fade time when switching modes (0 = instant)</p>
            </div>
          </div>

          <div class="config-section">
            <h3>${ICONS.SCHEDULE} Active Days</h3>
            <div class="days-selector">
              ${activeDaysChecks}
            </div>
            <p class="help-text">Select days when night mode is active (uncheck all for every day)</p>
          </div>
        </div>
        
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="closeNightModeConfig()">Cancel</button>
          <button class="btn btn-primary" onclick="saveNightModeConfig()">${ICONS.SAVE} Save & Apply</button>
        </div>
        
      </div>
    </div>
  `;

    document.body.insertAdjacentHTML('beforeend', html);
  } catch (error) {
    console.error('Error showing night mode config:', error);
    showMessage('Failed to show night mode configuration', 'error');
  }
}

/**
 * Close the night mode configuration modal
 */
function closeNightModeConfig() {
  const modal = document.getElementById('night-mode-config-modal');
  if (modal) modal.remove();
}

/**
 * Save night mode configuration
 */
async function saveNightModeConfig() {
  try {
    const enabled = document.getElementById('nightmode-enabled').checked;
    const startTime = document.getElementById('nightmode-start').value;
    const endTime = document.getElementById('nightmode-end').value;
    const dayBrightness = parseInt(document.getElementById('nightmode-day-brightness').value) / 100;
    const nightBrightness = parseInt(document.getElementById('nightmode-night-brightness').value) / 100;
    const transitionMinutes = parseInt(document.getElementById('nightmode-transition').value);

    const dayCheckboxes = document.querySelectorAll('.day-checkbox:checked');
    const activeDays = dayCheckboxes.length === 7 ? [] : Array.from(dayCheckboxes).map(cb => parseInt(cb.value));

    const config = {
      enabled,
      startTime,
      endTime,
      dayBrightness,
      nightBrightness,
      transitionMinutes,
      activeDays
    };

    await window.api.post('/api/nightmode/config', config);
    showMessage('Night mode configuration saved successfully', 'success');
    closeModal();
    await refreshNightModeStatus();
  } catch (error) {
    console.error('Error saving night mode config:', error);
    showMessage(error.message || 'Failed to save night mode configuration', 'error');
  }
}

/**
 * Force test night mode settings
 */
async function testNightModeNow() {
  try {
    const result = await window.api.post('/api/nightmode/force-update');
    showMessage(`Test applied! Mode: ${result.data.mode}, Brightness: ${Math.round(result.data.brightness * 100)}%`, 'success');
    await refreshNightModeStatus();
  } catch (error) {
    console.error('Error testing night mode:', error);
    showMessage(error.message || 'Failed to test night mode', 'error');
  }
}

// Expose globally
window.fetchNightModeConfig = fetchNightModeConfig;
window.refreshNightModeStatus = refreshNightModeStatus;
window.showNightModeConfig = showNightModeConfig;
window.closeNightModeConfig = closeNightModeConfig;
window.saveNightModeConfig = saveNightModeConfig;
window.testNightModeNow = testNightModeNow;
