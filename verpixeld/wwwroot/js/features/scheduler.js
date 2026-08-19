/* ============================================================================
   LAYOUT SCHEDULER - Time-based Layout Automation
   ============================================================================ */

// Entry counter for unique IDs
let scheduleEntryCounter = 0;

/**
 * Fetch all schedules from server
 */
async function fetchSchedules() {
  try {
    const result = await api.get('/api/schedule/list');
    displaySchedules(result.data);
    await updateScheduleStatus();
  } catch (error) {
    console.error('Failed to fetch schedules:', error);
    showMessage('Failed to load schedules', 'error');
  }
}

/**
 * Display schedules in the UI
 */
function displaySchedules(schedules) {
  const container = document.getElementById('schedules-list');

  if (!schedules || schedules.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <div class="empty-state-icon">📅</div>
        <div class="empty-state-text">No schedules yet</div>
        <div class="empty-state-hint">Create schedules to automate layout changes</div>
      </div>`;
    return;
  }

  const html = schedules.map(schedule => {
    const activeClass = schedule.enabled ? 'active' : 'inactive';
    const defaultBadge = schedule.isDefault ? `<span class="badge-default">Default</span>` : '';

    return `
    <div class="card ${schedule.enabled ? 'card-success' : 'card-inactive'} card-interactive schedule-card ${activeClass}">
      <div class="card-header">
        <div>
          <div class="card-title">
            ${escapeHtml(schedule.name)} ${defaultBadge}
          </div>
        </div>
        <span class="badge ${schedule.enabled ? 'active' : 'inactive'}">
          ${schedule.enabled ? 'Enabled' : 'Disabled'}
        </span>
      </div>
      
      <div class="card-body">
        <div>${schedule.entries.length} schedule entry(s)</div>
        <div class="text-muted" style="margin-top: var(--spacing-xs); font-size: 0.75rem;">
          Modified: ${new Date(schedule.lastModified).toLocaleString()}
        </div>
      </div>
      
      <div class="card-footer">
        <button class="btn btn-small" onclick="viewSchedule('${escapeHtml(schedule.name)}')">View</button>
        <button class="btn btn-small btn-secondary" onclick="editSchedule('${escapeHtml(schedule.name)}')">Edit</button>
        <button class="btn btn-small ${schedule.enabled ? 'btn-warning' : 'btn-primary'}" 
                onclick="toggleSchedule('${escapeHtml(schedule.name)}', ${schedule.enabled})">
          ${schedule.enabled ? 'Disable' : 'Enable'}
        </button>
        ${!schedule.isDefault ? `
        <button class="btn btn-small" onclick="setDefaultSchedule('${escapeHtml(schedule.name)}')">Set Default</button>` : ''}
        <button class="btn btn-small btn-danger" onclick="deleteSchedule('${escapeHtml(schedule.name)}')">Delete</button>
      </div>
    </div>
  `;
  }).join('');

  container.innerHTML = html;
}

/**
 * Update schedule status display
 */
async function updateScheduleStatus() {
  try {
    const [activeResult, nextResult] = await Promise.all([
      api.get('/api/schedule/active'),
      api.get('/api/schedule/next')
    ]);

    const activeInfo = document.getElementById('active-schedule-info');
    const nextInfo = document.getElementById('next-change-info');

    if (activeResult.success && activeResult.data) {
      activeInfo.textContent = `${ICONS.SCHEDULE} Active: ${activeResult.data.name} (${activeResult.data.entries.length} entries)`;
    } else {
      activeInfo.textContent = 'No active schedule';
    }

    if (nextResult.success && nextResult.data) {
      const next = nextResult.data;
      nextInfo.textContent = `${ICONS.CLOCK} Next: "${next.layoutName}" at ${next.time} (in ${next.timeUntil.formatted})`;
    } else {
      nextInfo.textContent = 'No upcoming scheduled changes';
    }
  } catch (error) {
    console.error('Failed to update schedule status:', error);
  }
}

/**
 * Set a schedule as default
 */
async function setDefaultSchedule(scheduleName) {
  try {
    const result = await api.post(`/api/schedule/${encodeURIComponent(scheduleName)}/set-default`);
    showMessage(result.data, 'success');
    await fetchSchedules();
  } catch (error) {
    showMessage('Failed to set default schedule', 'error');
  }
}

/**
 * Delete a schedule
 */
async function deleteSchedule(scheduleName) {
  const confirmed = await showConfirm({
    title: 'Delete schedule',
    message: `Delete schedule "${scheduleName}"?\n\nThis action cannot be undone.`,
    confirmText: 'Delete',
    cancelText: 'Keep',
    type: 'danger',
    icon: `${ICONS.DELETE}`
  });

  if (!confirmed) {
    return;
  }

  try {
    const result = await api.del(`/api/schedule/${encodeURIComponent(scheduleName)}`);
    showMessage(result.data, 'success');
    await fetchSchedules();
  } catch (error) {
    showMessage('Failed to delete schedule', 'error');
  }
}

/**
 * Show dialog to create new schedule
 */
async function showNewScheduleDialog() {
  const modalHTML = `
    <div class="modal-overlay" id="new-schedule-modal">
      <div class="modal-content" style="max-width: 800px;">
        <div class="modal-header">
          <h2>${ICONS.SCHEDULE} Create New Schedule</h2>
          <button class="modal-close" onclick="closeNewScheduleDialog()">${ICONS.CLOSE}</button>
        </div>
        
        <div class="modal-body">
          <div class="form-group">
            <label for="schedule-name">Schedule Name *</label>
            <input type="text" id="schedule-name" class="form-control" 
                   placeholder="e.g., Business Hours, Daily Routine" required>
          </div>
          
          <div class="form-group">
            <label>
              <input type="checkbox" id="schedule-enabled" checked>
              Enable this schedule immediately
            </label>
          </div>
          
          <div class="form-group">
            <label>
              <input type="checkbox" id="schedule-default">
              Set as default schedule (loads on startup)
            </label>
          </div>
          
          <hr>
          
          <h3>${ICONS.SCHEDULE} Schedule Entries</h3>
          <p class="text-muted">Add time-based layout switches below:</p>
          
          <div id="schedule-entries-container">
            <!-- Entries will be added here -->
          </div>
          
          <button class="btn btn-secondary" onclick="addScheduleEntry()">
            ${ICONS.ADD} Add Entry
          </button>
        </div>
        
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="closeNewScheduleDialog()">Cancel</button>
          <button class="btn btn-primary" onclick="saveNewSchedule()">
            ${ICONS.SAVE} Create Schedule
          </button>
        </div>
      </div>
    </div>
  `;

  document.body.insertAdjacentHTML('beforeend', modalHTML);
  addScheduleEntry();
  document.getElementById('schedule-name').focus();
}

/**
 * Close new schedule dialog
 */
function closeNewScheduleDialog() {
  const modal = document.getElementById('new-schedule-modal');
  if (modal) modal.remove();
}

/**
 * Add a schedule entry to the form
 */
function addScheduleEntry() {
  const container = document.getElementById('schedule-entries-container');
  const entryId = `entry-${scheduleEntryCounter++}`;

  const entryHTML = `
    <div class="schedule-entry-card" id="${entryId}">
      <div class="schedule-entry-header">
        <span>Entry #${scheduleEntryCounter}</span>
        <button class="btn btn-icon btn-danger" onclick="removeScheduleEntry('${entryId}')">${ICONS.DELETE}</button>
      </div>
      
      <div class="schedule-entry-body">
        <div class="form-row">
          <div class="form-group">
            <label>Layout to Load *</label>
            <select class="form-control entry-layout" required>
              <option value="">-- Select Layout --</option>
            </select>
          </div>
          
          <div class="form-group">
            <label>Time (24-hour format) *</label>
            <input type="time" class="form-control entry-time" required>
          </div>
        </div>
        
        <div class="form-group">
          <label>Description (optional)</label>
          <input type="text" class="form-control entry-description" 
                 placeholder="e.g., Morning display, Evening mode">
        </div>
        
        <div class="form-group">
          <label>Active Days (leave empty for every day)</label>
          <div class="day-checkboxes">
            <label><input type="checkbox" class="entry-day" value="0"> Sun</label>
            <label><input type="checkbox" class="entry-day" value="1"> Mon</label>
            <label><input type="checkbox" class="entry-day" value="2"> Tue</label>
            <label><input type="checkbox" class="entry-day" value="3"> Wed</label>
            <label><input type="checkbox" class="entry-day" value="4"> Thu</label>
            <label><input type="checkbox" class="entry-day" value="5"> Fri</label>
            <label><input type="checkbox" class="entry-day" value="6"> Sat</label>
          </div>
        </div>
        
        <div class="form-group">
          <label>
            <input type="checkbox" class="entry-enabled" checked>
            Enable this entry
          </label>
        </div>
      </div>
    </div>
  `;

  container.insertAdjacentHTML('beforeend', entryHTML);
  loadLayoutOptionsForEntry(entryId);
}

/**
 * Remove a schedule entry
 */
function removeScheduleEntry(entryId) {
  const entry = document.getElementById(entryId);
  if (entry) entry.remove();
}

/**
 * Load layout options for a schedule entry dropdown
 */
async function loadLayoutOptionsForEntry(entryId) {
  try {
    const result = await api.get('/api/layout/saved');
    if (result.data) {
      const select = document.querySelector(`#${entryId} .entry-layout`);
      if (select) {
        result.data.forEach(layout => {
          const option = document.createElement('option');
          option.value = layout.name;
          option.textContent = `${layout.name} (${layout.profile})`;
          select.appendChild(option);
        });
      }
    }
  } catch (error) {
    console.error('Failed to load layout options:', error);
  }
}

/**
 * Save new schedule
 */
async function saveNewSchedule() {
  try {
    const scheduleName = document.getElementById('schedule-name').value.trim();
    if (!scheduleName) {
      showMessage('Please enter a schedule name', 'error');
      return;
    }

    const enabled = document.getElementById('schedule-enabled').checked;
    const isDefault = document.getElementById('schedule-default').checked;

    const entries = [];
    const entryCards = document.querySelectorAll('.schedule-entry-card');

    for (const card of entryCards) {
      const layoutName = card.querySelector('.entry-layout').value;
      const time = card.querySelector('.entry-time').value;
      const description = card.querySelector('.entry-description').value;
      const entryEnabled = card.querySelector('.entry-enabled').checked;

      if (!layoutName || !time) {
        showMessage('Please fill in all required fields (Layout and Time)', 'error');
        return;
      }

      const activeDays = [];
      card.querySelectorAll('.entry-day:checked').forEach(checkbox => {
        activeDays.push(parseInt(checkbox.value));
      });

      entries.push({
        id: generateGuid(),
        layoutName,
        time,
        activeDays,
        enabled: entryEnabled,
        description,
        lastTriggered: null,
        createdAt: new Date().toISOString()
      });
    }

    if (entries.length === 0) {
      showMessage('Please add at least one schedule entry', 'error');
      return;
    }

    const schedule = {
      name: scheduleName,
      enabled,
      isDefault,
      entries,
      createdAt: new Date().toISOString(),
      lastModified: new Date().toISOString()
    };

    const result = await api.post('/api/schedule/save', schedule);
    showMessage(result.data, 'success');
    closeNewScheduleDialog();
    await fetchSchedules();
  } catch (error) {
    console.error('Error saving schedule:', error);
    showMessage('Error: ' + error.message, 'error');
  }
}

/**
 * Edit existing schedule
 */
async function editSchedule(scheduleName) {
  try {
    const result = await api.get(`/api/schedule/${encodeURIComponent(scheduleName)}`);
    if (!result.data) {
      showMessage('Failed to load schedule', 'error');
      return;
    }

    const schedule = result.data;

    const modalHTML = `
      <div class="modal-overlay" id="edit-schedule-modal">
        <div class="modal-content" style="max-width: 800px;">
          <div class="modal-header">
            <h2>${ICONS.EDIT} Edit Schedule: ${escapeHtml(schedule.name)}</h2>
            <button class="modal-close" onclick="closeEditScheduleDialog()">${ICONS.CLOSE}</button>
          </div>
          
          <div class="modal-body">
            <div class="form-group">
              <label for="edit-schedule-name">Schedule Name *</label>
              <input type="text" id="edit-schedule-name" class="form-control" 
                     value="${escapeHtml(schedule.name)}" required>
            </div>
            
            <div class="form-group">
              <label>
                <input type="checkbox" id="edit-schedule-enabled" ${schedule.enabled ? 'checked' : ''}>
                Enable this schedule
              </label>
            </div>
            
            <div class="form-group">
              <label>
                <input type="checkbox" id="edit-schedule-default" ${schedule.isDefault ? 'checked' : ''}>
                Set as default schedule
              </label>
            </div>
            
            <hr>
            
            <h3>Schedule Entries</h3>
            <div id="edit-schedule-entries-container">
              <!-- Entries will be loaded here -->
            </div>
            
            <button class="btn btn-secondary" onclick="addEditScheduleEntry()">
              ${ICONS.ADD} Add Entry
            </button>
          </div>
          
          <div class="modal-footer">
            <button class="btn btn-secondary" onclick="closeEditScheduleDialog()">Cancel</button>
            <button class="btn btn-primary" onclick="saveEditedSchedule('${escapeHtml(schedule.name)}')">
              ${ICONS.SAVE} Save Changes
            </button>
          </div>
        </div>
      </div>
    `;

    document.body.insertAdjacentHTML('beforeend', modalHTML);

    for (const entry of schedule.entries) {
      await addEditScheduleEntry(entry);
    }

  } catch (error) {
    console.error('Error loading schedule for edit:', error);
    showMessage('Failed to load schedule', 'error');
  }
}

/**
 * Close edit schedule dialog
 */
function closeEditScheduleDialog() {
  const modal = document.getElementById('edit-schedule-modal');
  if (modal) modal.remove();
}

/**
 * Add entry to edit schedule form
 */
async function addEditScheduleEntry(existingEntry = null) {
  const container = document.getElementById('edit-schedule-entries-container');
  const entryId = `edit-entry-${scheduleEntryCounter++}`;

  const entryHTML = `
    <div class="schedule-entry-card" id="${entryId}">
      <div class="schedule-entry-header">
        <span>Entry #${scheduleEntryCounter}</span>
        <button class="btn btn-icon btn-danger" onclick="removeScheduleEntry('${entryId}')">${ICONS.DELETE}</button>
      </div>
      
      <div class="schedule-entry-body">
        <div class="form-row">
          <div class="form-group">
            <label>Layout to Load *</label>
            <select class="form-control entry-layout" required>
              <option value="">-- Select Layout --</option>
            </select>
          </div>
          
          <div class="form-group">
            <label>Time (24-hour format) *</label>
            <input type="time" class="form-control entry-time" 
                   value="${existingEntry ? existingEntry.time : ''}" required>
          </div>
        </div>
        
        <div class="form-group">
          <label>Description (optional)</label>
          <input type="text" class="form-control entry-description" 
                 value="${existingEntry ? escapeHtml(existingEntry.description || '') : ''}"
                 placeholder="e.g., Morning display, Evening mode">
        </div>
        
        <div class="form-group">
          <label>Active Days (leave empty for every day)</label>
          <div class="day-checkboxes">
            <label><input type="checkbox" class="entry-day" value="0"> Sun</label>
            <label><input type="checkbox" class="entry-day" value="1"> Mon</label>
            <label><input type="checkbox" class="entry-day" value="2"> Tue</label>
            <label><input type="checkbox" class="entry-day" value="3"> Wed</label>
            <label><input type="checkbox" class="entry-day" value="4"> Thu</label>
            <label><input type="checkbox" class="entry-day" value="5"> Fri</label>
            <label><input type="checkbox" class="entry-day" value="6"> Sat</label>
          </div>
        </div>
        
        <div class="form-group">
          <label>
            <input type="checkbox" class="entry-enabled" 
                   ${existingEntry && existingEntry.enabled !== false ? 'checked' : ''}>
            Enable this entry
          </label>
        </div>
      </div>
    </div>
  `;

  container.insertAdjacentHTML('beforeend', entryHTML);
  await loadLayoutOptionsForEntry(entryId);

  if (existingEntry) {
    const select = document.querySelector(`#${entryId} .entry-layout`);
    if (select) select.value = existingEntry.layoutName;

    if (existingEntry.activeDays && existingEntry.activeDays.length > 0) {
      existingEntry.activeDays.forEach(day => {
        const checkbox = document.querySelector(`#${entryId} .entry-day[value="${day}"]`);
        if (checkbox) checkbox.checked = true;
      });
    }
  }
}

/**
 * Save edited schedule
 */
async function saveEditedSchedule(originalName) {
  try {
    const scheduleName = document.getElementById('edit-schedule-name').value.trim();
    if (!scheduleName) {
      showMessage('Please enter a schedule name', 'error');
      return;
    }

    const enabled = document.getElementById('edit-schedule-enabled').checked;
    const isDefault = document.getElementById('edit-schedule-default').checked;

    const entries = [];
    const entryCards = document.querySelectorAll('#edit-schedule-entries-container .schedule-entry-card');

    for (const card of entryCards) {
      const layoutName = card.querySelector('.entry-layout').value;
      const time = card.querySelector('.entry-time').value;
      const description = card.querySelector('.entry-description').value;
      const entryEnabled = card.querySelector('.entry-enabled').checked;

      if (!layoutName || !time) {
        showMessage('Please fill in all required fields (Layout and Time)', 'error');
        return;
      }

      const activeDays = [];
      card.querySelectorAll('.entry-day:checked').forEach(checkbox => {
        activeDays.push(parseInt(checkbox.value));
      });

      entries.push({
        id: generateGuid(),
        layoutName,
        time,
        activeDays,
        enabled: entryEnabled,
        description,
        lastTriggered: null,
        createdAt: new Date().toISOString()
      });
    }

    if (entries.length === 0) {
      showMessage('Please add at least one schedule entry', 'error');
      return;
    }

    // If name changed, delete old schedule
    if (originalName !== scheduleName) {
      await api.del(`/api/schedule/${encodeURIComponent(originalName)}`);
    }

    const schedule = {
      name: scheduleName,
      enabled,
      isDefault,
      entries,
      createdAt: new Date().toISOString(),
      lastModified: new Date().toISOString()
    };

    const result = await api.post('/api/schedule/save', schedule);
    showMessage('Schedule updated successfully', 'success');
    closeEditScheduleDialog();
    await fetchSchedules();
  } catch (error) {
    console.error('Error saving schedule:', error);
    showMessage('Error: ' + error.message, 'error');
  }
}

/**
 * View schedule details
 */
async function viewSchedule(scheduleName) {
  try {
    const result = await api.get(`/api/schedule/${encodeURIComponent(scheduleName)}`);
    if (!result.data) {
      showMessage('Failed to load schedule', 'error');
      return;
    }

    const schedule = result.data;

    const entriesHTML = schedule.entries.map((entry, index) => {
      const daysText = entry.activeDays && entry.activeDays.length > 0
        ? ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
          .filter((_, i) => entry.activeDays.includes(i))
          .join(', ')
        : 'Every day';

      const statusBadge = entry.enabled
        ? '<span style="color: #28a745;">✅ Enabled</span>'
        : '<span style="color: #dc3545;">⏸️ Disabled</span>';

      return `
        <div class="schedule-entry-view">
          <div class="entry-number">Entry #${index + 1} ${statusBadge}</div>
          <div class="entry-details">
            <p><strong>Layout:</strong> ${escapeHtml(entry.layoutName)}</p>
            <p><strong>Time:</strong> ${entry.time}</p>
            <p><strong>Active Days:</strong> ${daysText}</p>
            ${entry.description ? `<p><strong>Description:</strong> ${escapeHtml(entry.description)}</p>` : ''}
            ${entry.lastTriggered ? `<p class="text-muted">Last triggered: ${new Date(entry.lastTriggered).toLocaleString()}</p>` : ''}
          </div>
        </div>
      `;
    }).join('');

    const modalHTML = `
      <div class="modal-overlay" id="view-schedule-modal">
        <div class="modal-content" style="max-width: 700px;">
          <div class="modal-header">
            <h2>${ICONS.DETAILS} Schedule: ${escapeHtml(schedule.name)}</h2>
            <button class="modal-close" onclick="closeViewScheduleDialog()">×</button>
          </div>
          
          <div class="modal-body">
            <div class="schedule-info">
              <p><strong>Status:</strong> ${schedule.enabled ? '✅ Enabled' : '⏸️ Disabled'}</p>
              ${schedule.isDefault ? '<p><strong>📌 Default Schedule</strong> (loads on startup)</p>' : ''}
              <p><strong>Entries:</strong> ${schedule.entries.length}</p>
              <p class="text-muted">Created: ${new Date(schedule.createdAt).toLocaleString()}</p>
              <p class="text-muted">Modified: ${new Date(schedule.lastModified).toLocaleString()}</p>
            </div>
            
            <hr>
            
            <h3>Schedule Entries</h3>
            ${entriesHTML}
          </div>
          
          <div class="modal-footer">
            <button class="btn btn-secondary" onclick="closeViewScheduleDialog()">Close</button>
            <button class="btn btn-primary" onclick="closeViewScheduleDialog(); editSchedule('${escapeHtml(schedule.name)}')">
              ${ICONS.EDIT} Edit Schedule
            </button>
          </div>
        </div>
      </div>
    `;

    document.body.insertAdjacentHTML('beforeend', modalHTML);
  } catch (error) {
    console.error('Error viewing schedule:', error);
    showMessage('Failed to load schedule', 'error');
  }
}

/**
 * Close view schedule dialog
 */
function closeViewScheduleDialog() {
  const modal = document.getElementById('view-schedule-modal');
  if (modal) modal.remove();
}

/**
 * Toggle schedule enabled/disabled
 */
async function toggleSchedule(scheduleName, currentlyEnabled) {
  try {
    const result = await api.get(`/api/schedule/${encodeURIComponent(scheduleName)}`);
    if (!result.data) {
      showMessage('Failed to load schedule', 'error');
      return;
    }

    const schedule = result.data;
    schedule.enabled = !currentlyEnabled;
    schedule.lastModified = new Date().toISOString();

    await api.post('/api/schedule/save', schedule);
    showMessage(
      schedule.enabled ? `Schedule '${scheduleName}' enabled` : `Schedule '${scheduleName}' disabled`,
      'success'
    );

    if (schedule.enabled) {
      await api.post(`/api/schedule/activate/${encodeURIComponent(scheduleName)}`);
    }

    await fetchSchedules();
  } catch (error) {
    console.error('Error toggling schedule:', error);
    showMessage('Failed to toggle schedule', 'error');
  }
}

// Auto-refresh schedule status every minute
setInterval(updateScheduleStatus, 60000);

// Expose globally
window.fetchSchedules = fetchSchedules;
window.displaySchedules = displaySchedules;
window.updateScheduleStatus = updateScheduleStatus;
window.setDefaultSchedule = setDefaultSchedule;
window.deleteSchedule = deleteSchedule;
window.showNewScheduleDialog = showNewScheduleDialog;
window.closeNewScheduleDialog = closeNewScheduleDialog;
window.addScheduleEntry = addScheduleEntry;
window.removeScheduleEntry = removeScheduleEntry;
window.loadLayoutOptionsForEntry = loadLayoutOptionsForEntry;
window.saveNewSchedule = saveNewSchedule;
window.editSchedule = editSchedule;
window.closeEditScheduleDialog = closeEditScheduleDialog;
window.addEditScheduleEntry = addEditScheduleEntry;
window.saveEditedSchedule = saveEditedSchedule;
window.viewSchedule = viewSchedule;
window.closeViewScheduleDialog = closeViewScheduleDialog;
window.toggleSchedule = toggleSchedule;
