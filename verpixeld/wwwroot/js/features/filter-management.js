/* ============================================================================
   FILTER MANAGEMENT - Active Filters & Filter Configuration
   ============================================================================ */

// Live update timer for filter editing
let liveUpdateTimer = null;

/**
 * Display active filters in the UI
 */
function displayFilters(filters) {
  const container = document.getElementById('active-filters');

  if (filters.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <div class="empty-state-icon">🎨</div>
        <div class="empty-state-text">No active filters</div>
        <div class="empty-state-hint">Add filters to adjust brightness, colors, and effects</div>
      </div>`;
    return;
  }

  const html = filters.map((filter, index) => `
  <div class="card card-interactive filter-item" data-filter-index="${index}" data-filter-type="${filter.type}">
    <div class="card-header">
      <div class="card-title">${formatFilterName(filter.type)}</div>
    </div>
    
    <div class="card-body">
      <div style="font-size: 0.875rem; color: var(--color-text-secondary);">
        ${formatParameters(filter.parameters)}
      </div>
    </div>
    
    <div class="card-footer">
      <button class="btn btn-small btn-secondary btn-edit-filter" data-index="${index}">Edit</button>
      <button class="btn btn-small btn-danger btn-remove-filter" data-index="${index}">Remove</button>
    </div>
  </div>
`).join('');

  container.innerHTML = html;
  window.activeFilterParams = filters.map(f => f.parameters);

  document.querySelectorAll('.btn-edit-filter').forEach(btn => {
    btn.addEventListener('click', (e) => {
      const index = parseInt(e.target.getAttribute('data-index'));
      const filterItem = e.target.closest('.filter-item');
      const filterType = filterItem.getAttribute('data-filter-type');
      const params = window.activeFilterParams[index];
      editFilter(index, filterType, params);
    });
  });

  document.querySelectorAll('.btn-remove-filter').forEach(btn => {
    btn.addEventListener('click', (e) => {
      const index = parseInt(e.target.getAttribute('data-index'));
      removeFilter(index);
    });
  });
}

/**
 * Show filter picker modal
 */
async function showFilterPicker() {
  const html = `
    <div class="modal-overlay" id="filter-picker-modal">
      <div class="modal-content" style="max-width: 900px;">
        
        <div class="modal-header">
          <h2>${ICONS.FILTER} Add Filter</h2>
          <button class="modal-close" onclick="closeFilterPicker()">${ICONS.CLOSE}</button>
        </div>
        
        <div class="modal-body">
          <div class="filter-selector" id="filter-selector"></div>
        </div>
        
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="closeFilterPicker()">Cancel</button>
        </div>
        
      </div>
    </div>
  `;
  
  document.body.insertAdjacentHTML('beforeend', html);
  
  if (availableFilters.length === 0) {
    await fetchAvailableFilters();
  }

  const container = document.getElementById('filter-selector');
  const categories = {};
  availableFilters.forEach(filter => {
    const category = filter.category || 'Other';
    if (!categories[category]) categories[category] = [];
    categories[category].push(filter);
  });

  let filterHtml = '';
  for (const [category, filters] of Object.entries(categories)) {
    filterHtml += `<div class="filter-category-section">
            <h3 class="category-title">${category}</h3>
            <div class="filter-grid">`;

    filters.forEach(filter => {
      let iconHtml;
      if (filter.iconData) {
        let mimeType = 'image/png';
        try {
          const decoded = atob(filter.iconData);
          if (decoded.includes('<svg')) mimeType = 'image/svg+xml';
        } catch (e) { }

        iconHtml = `<div class="extension-icon-container">
                    <img src="data:${mimeType};base64,${filter.iconData}" 
                         alt="${filter.displayName}" class="extension-icon-img"
                         onerror="this.style.display='none'; this.nextElementSibling.style.display='block';" />
                    <div class="extension-icon" style="display:none;">${ICONS.FILTER}</div>
                </div>`;
      }
      else {
        iconHtml = `<div class="extension-icon">${ICONS.FILTER}</div>`;
      }

      filterHtml += `<div class="extension-card filter-card" data-filter="${filter.name}">
                ${iconHtml}
                <div class="extension-name">${filter.displayName}</div>
            </div>`;
    });

    filterHtml += `</div></div>`;
  }

  container.innerHTML = filterHtml;
  
  container.querySelectorAll('.filter-card').forEach(card => {
    card.addEventListener('click', () => {
      const filterName = card.getAttribute('data-filter');
      closeFilterPicker();
      showFilterForm(filterName);
    });
  });
}

/**
 * Close filter picker modal
 */
function closeFilterPicker() {
  const modal = document.getElementById('filter-picker-modal');
  if (modal) modal.remove();
}

/**
 * Show filter form for adding a new filter
 */
function showFilterForm(typeName) {
  const filter = availableFilters.find(f => f.name === typeName);
  if (!filter) {
    showMessage('Filter not found', true);
    return;
  }

  const formContainer = document.getElementById('filter-form');
  const formTitle = document.getElementById('filter-form-title');
  const filterFormContainer = document.getElementById('filter-form-container');

  if (!formContainer || !formTitle || !filterFormContainer) {
    showMessage('Filter form not available', true);
    return;
  }

  formTitle.textContent = `Add ${filter.displayName || filter.name}`;

  let html = '';
  const params = filter.parameters || [];

  params.forEach(param => {
    const paramName = param.name;
    let defaultVal = param.defaultValue ?? 0;
    const paramType = param.parameterType || param.type || '';

    const isBooleanType = paramType.includes('Boolean') || paramType.includes('Bool');
    const hasColorInName = paramName.toLowerCase().endsWith('color') ||
      paramName.toLowerCase().includes('color') && !paramName.toLowerCase().includes('random');
    const hasColorType = paramType.includes('SKColor');
    const isColorParam = !isBooleanType && (hasColorInName || hasColorType);

    let displayValue = defaultVal;
    if (isColorParam) {
      if (typeof defaultVal === 'number') {
        displayValue = '#' + (defaultVal >>> 0).toString(16).padStart(8, '0').toUpperCase();
      } else if (typeof defaultVal === 'string' && !defaultVal.startsWith('#')) {
        displayValue = '#' + defaultVal;
      }
    }

    html += `<div class="slider-container">
            <div class="slider-label">
                <span>${paramName}</span>
                <span id="${paramName}-value">${isColorParam ? displayValue : defaultVal}</span>
            </div>`;

    if (paramType.includes('Single' || 'Double' || 'Float')) {
      const min = param.minValue ?? 0;
      const max = param.maxValue ?? 1;
      const step = (max - min) / 100 || 0.01;
      html += `<input type="range" id="${paramName}" min="${min}" max="${max}" 
                step="${step}" value="${defaultVal}" oninput="updateSlider('${paramName}')">`;
    } else if (isBooleanType) {
      html += `<input type="checkbox" id="${paramName}" ${defaultVal ? 'checked' : ''}>`;
    } else if (paramType.includes('Int32' || 'Int' || 'Byte')) {
      const min = param.minValue ?? 0;
      const max = param.maxValue ?? (paramType.includes('Byte') ? 255 : 100);
      html += `<input type="range" id="${paramName}" min="${min}" max="${max}" 
                step="1" value="${defaultVal}" oninput="updateSlider('${paramName}')">`;
    } else if (isColorParam) {
      html += createColorInputWithAlpha(paramName, defaultVal,
        `document.getElementById('${paramName}-value').textContent = getColorWithAlpha('${paramName}')`);
    } else {
      html += `<input type="text" id="${paramName}" value="${defaultVal}">`;
    }

    if (param.description) {
      html += `<p class="param-description">${param.description}</p>`;
    }

    html += `</div>`;
  });

  formContainer.innerHTML = html;

  const footerContainer = document.getElementById('filter-form-footer');
  if (footerContainer) {
    footerContainer.innerHTML = `
      <button class="btn btn-secondary" onclick="hideFilterForm()">Cancel</button>
      <button class="btn btn-primary" onclick="addFilter('${typeName}')">Add Filter</button>
    `;
  }

  filterFormContainer.style.display = 'flex';
}

/**
 * Hide filter form
 */
function hideFilterForm() {
  const filterFormContainer = document.getElementById('filter-form-container');
  if (filterFormContainer) {
    filterFormContainer.style.display = 'none';
  }
}

/**
 * Add a new filter
 */
async function addFilter(typeName) {
  const filter = availableFilters.find(f => f.name === typeName);
  if (!filter) {
    showMessage('Filter not found', true);
    return;
  }

  const parameters = {};
  const params = filter.parameters || [];

  params.forEach(param => {
    const element = document.getElementById(param.name);
    if (element) {
      const paramType = param.parameterType || param.type || '';
      let value;

      if (paramType.includes('Boolean') || paramType.includes('Bool')) {
        value = element.checked;
      } else if (paramType.includes('Int32') || paramType.includes('Int') || paramType.includes('Byte')) {
        value = parseInt(element.value);
      } else if (paramType.includes('Single') || paramType.includes('Double') || paramType.includes('Float')) {
        value = parseFloat(element.value);
      } else {
        value = element.value;
      }

      parameters[param.name] = value;
    }
  });

  try {
    const result = await api.post('/api/filters/add', { filterType: filter.name, parameters });
    showMessage(result.data || 'Filter added', false);
    hideFilterForm();
    fetchFilters();
    fetchStatus();
  } catch (error) {
    showMessage('Failed to add filter: ' + (error.message || 'Unknown error'), true);
  }
}

/**
 * Edit an existing filter
 */
async function editFilter(index, filterType, currentParams) {
  const filter = availableFilters.find(f => f.name === filterType);
  if (!filter) {
    showMessage('Filter not found', true);
    return;
  }

  const formContainer = document.getElementById('filter-form');
  const formTitle = document.getElementById('filter-form-title');
  const filterFormContainer = document.getElementById('filter-form-container');

  if (!formContainer || !formTitle || !filterFormContainer) {
    showMessage('Filter form not available', true);
    return;
  }

  formTitle.textContent = `Edit ${filter.displayName || filter.name}`;

  let html = '';
  const params = filter.parameters || [];

  params.forEach(param => {
    const paramName = param.name;
    let currentValue = currentParams.hasOwnProperty(paramName) ? currentParams[paramName] : (param.defaultValue ?? 0);
    const paramType = param.parameterType || param.type || '';

    const isBooleanType = paramType.includes('Boolean') || paramType.includes('Bool');
    const hasColorInName = paramName.toLowerCase().endsWith('color') ||
      paramName.toLowerCase().includes('color') && !paramName.toLowerCase().includes('random');
    const hasColorType = paramType.includes('SKColor');
    const isColorParam = !isBooleanType && (hasColorInName || hasColorType);

    let displayValue = currentValue;
    if (isColorParam) {
      if (typeof currentValue === 'number') {
        displayValue = '#' + (currentValue >>> 0).toString(16).padStart(8, '0').toUpperCase();
      } else if (typeof currentValue === 'string' && !currentValue.startsWith('#')) {
        displayValue = '#' + currentValue;
      }
    }

    html += `<div class="slider-container">
            <div class="slider-label">
                <span>${paramName}</span>
                <span id="${paramName}-value">${isColorParam ? displayValue : currentValue}</span>
            </div>`;

    if (paramType.includes('Single') || paramType.includes('Double') || paramType.includes('Float')) {
      const min = param.minValue ?? 0;
      const max = param.maxValue ?? 1;
      const step = (max - min) / 100 || 0.01;
      html += `<input type="range" id="${paramName}" min="${min}" max="${max}" 
                step="${step}" value="${currentValue}" oninput="updateSliderAndFilter(${index}, '${paramName}')">`;
    } else if (paramType.includes('Int32') || paramType.includes('Int' || 'Byte')) {
      const min = param.minValue ?? 0;
      const max = param.maxValue ?? (paramType.includes('Byte') ? 255 : 100);
      html += `<input type="range" id="${paramName}" min="${min}" max="${max}" 
                step="1" value="${currentValue}" oninput="updateSliderAndFilter(${index}, '${paramName}')">`;
    } else if (paramType.includes('Boolean') || paramType.includes('Bool')) {
      html += `<input type="checkbox" id="${paramName}" ${currentValue ? 'checked' : ''} 
                onchange="updateSliderAndFilter(${index}, '${paramName}')">`;
    } else if (paramType.includes('Enum')) {
      html += `<select id="${paramName}" onchange="updateSliderAndFilter(${index}, '${paramName}')">`;
      (param.enumValues || []).forEach(enumValue => {
        const selected = enumValue === currentValue ? 'selected' : '';
        html += `<option value="${enumValue}" ${selected}>${enumValue}</option>`;
      });
      html += `</select>`;
    } else if (isColorParam) {
      html += createColorInputWithAlpha(paramName, currentValue,
        `updateSliderAndFilter(${index}, '${paramName}')`);
    } else {
      html += `<input type="text" id="${paramName}" value="${currentValue}" 
                onchange="updateSliderAndFilter(${index}, '${paramName}')">`;
    }

    if (param.description) {
      html += `<p class="param-description">${param.description}</p>`;
    }

    html += `</div>`;
  });

  window.currentEditingFilter = { index, filter };

  formContainer.innerHTML = html;

  const footerContainer = document.getElementById('filter-form-footer');
  if (footerContainer) {
    footerContainer.innerHTML = `
      <button class="btn btn-secondary" onclick="hideFilterForm()">Close</button>
    `;
  }

  filterFormContainer.style.display = 'flex';
}

/**
 * Update slider display and schedule filter update
 */
function updateSliderAndFilter(index, paramName) {
  const element = document.getElementById(paramName);
  const valueDisplay = document.getElementById(paramName + '-value');

  if (element && valueDisplay) {
    if (element.type === 'checkbox') {
      valueDisplay.textContent = element.checked ? 'true' : 'false';
    } else if (element.type === 'color') {
      valueDisplay.textContent = element.value;
    } else {
      valueDisplay.textContent = element.value;
    }
  }

  clearTimeout(liveUpdateTimer);
  liveUpdateTimer = setTimeout(() => applyLiveFilterUpdate(index), 150);
}

/**
 * Apply live filter update to server
 */
async function applyLiveFilterUpdate(index) {
  if (!window.currentEditingFilter) return;

  const { filter } = window.currentEditingFilter;
  const parameters = {};
  const params = filter.parameters || [];

  params.forEach(param => {
    const element = document.getElementById(param.name);
    if (element) {
      const paramType = param.parameterType || param.type || '';
      let value;

      if (paramType.includes('Boolean') || paramType.includes('Bool')) {
        value = element.checked;
      } else if (paramType.includes('Int32') || paramType.includes('Int') || paramType.includes('Byte')) {
        value = parseInt(element.value);
      } else if (paramType.includes('Single') || paramType.includes('Double') || paramType.includes('Float')) {
        value = parseFloat(element.value);
      } else {
        const colorInput = document.getElementById(`${param.name}-color`);
        if (colorInput) {
          value = getColorWithAlpha(param.name);
        } else {
          value = element.value;
        }
      }

      parameters[param.name] = value;
    }
  });

  try {
    await api.put(`/api/filters/${index}`, { parameters });
  } catch (error) {
    console.error('Failed to update filter:', error);
  }
}

/**
 * Remove a filter by index
 */
async function removeFilter(index) {
  try {
    const result = await api.del(`/api/filters/${index}`);
    showMessage(result.data || 'Filter removed', false);
    fetchFilters();
    fetchStatus();
  } catch (error) {
    showMessage('Failed to remove filter: ' + (error.message || 'Unknown error'), true);
  }
}

/**
 * Clear all filters
 */
async function clearAllFilters() {
  try {
    const result = await api.post('/api/filters/clear');
    showMessage(result.data || 'Filters cleared', false);
    fetchFilters();
    fetchStatus();
  } catch (error) {
    showMessage('Failed to clear filters: ' + (error.message || 'Unknown error'), true);
  }
}

// Expose globally
window.displayFilters = displayFilters;
window.showFilterPicker = showFilterPicker;
window.closeFilterPicker = closeFilterPicker;
window.showFilterForm = showFilterForm;
window.hideFilterForm = hideFilterForm;
window.addFilter = addFilter;
window.editFilter = editFilter;
window.updateSliderAndFilter = updateSliderAndFilter;
window.applyLiveFilterUpdate = applyLiveFilterUpdate;
window.removeFilter = removeFilter;
window.clearAllFilters = clearAllFilters;
