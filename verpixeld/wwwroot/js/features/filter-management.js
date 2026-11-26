/* ============================================================================
   FILTER MANAGEMENT - Active Filters & Filter Configuration
   ============================================================================ */

import { ICONS } from '../constants.js';
import { api } from '../core/api.js';
import { fetchFilters, fetchStatus, formatFilterName, formatParameters } from '../core/init.js';
import { showMessage } from '../ui/modal.js';
import { createColorInputWithAlpha, dataAttrs, escapeHtml, getColorWithAlpha, markerAttrs, notifyError, onAction, onReady, updateSlider, wire } from '../utils/helpers.js';

// Live update timer for filter editing
let liveUpdateTimer = null;

// Filter types the host offers (name, category, parameters), loaded once
let availableFilters = [];

/**
 * Fetch available filters from server
 */
export async function fetchAvailableFilters() {
  try {
    const result = await api.get('/api/filters/available');
    if (result.data) {
      availableFilters = result.data;
    }
  } catch (error) {
    console.error('Failed to fetch available filters:', error);
  }
}

/**
 * Display active filters in the UI
 */
export function displayFilters(filters) {
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
  <div class="card card-interactive filter-item" data-filter-index="${index}" data-filter-type="${escapeHtml(filter.type)}">
    <div class="card-header">
      <div class="card-title">${formatFilterName(filter.type)}</div>
    </div>
    
    <div class="card-body">
      <div style="font-size: 0.875rem; color: var(--color-text-secondary);">
        ${formatParameters(filter.parameters)}
      </div>
    </div>
    
    <div class="card-footer">
      <button class="btn btn-small btn-secondary" data-action="edit" data-index="${index}">Edit</button>
      <button class="btn btn-small btn-danger" data-action="remove" data-index="${index}">Remove</button>
    </div>
  </div>
`).join('');

  container.innerHTML = html;
  window.activeFilterParams = filters.map(f => f.parameters);

  onAction(container, {
    edit: (d, el) => editFilter(Number(d.index), el.closest('.filter-item').dataset.filterType, window.activeFilterParams[Number(d.index)]),
    remove: d => removeFilter(Number(d.index))
  });
}

/**
 * Show filter picker modal
 */
export async function showFilterPicker() {
  const html = `
    <div class="modal-overlay" id="filter-picker-modal">
      <div class="modal-content" style="max-width: 900px;">
        
        <div class="modal-header">
          <h2>${ICONS.FILTER} Add Filter</h2>
          <button class="modal-close" data-action="close">${ICONS.CLOSE}</button>
        </div>
        
        <div class="modal-body">
          <div class="filter-selector" id="filter-selector"></div>
        </div>
        
        <div class="modal-footer">
          <button class="btn btn-secondary" data-action="close">Cancel</button>
        </div>
        
      </div>
    </div>
  `;
  
  document.body.insertAdjacentHTML('beforeend', html);
  onAction(document.getElementById('filter-picker-modal'), { close: () => closeFilterPicker() });

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
            <h3 class="category-title">${escapeHtml(category)}</h3>
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
                         alt="${escapeHtml(filter.displayName)}" class="extension-icon-img"
                         data-fallback="next" />
                    <div class="extension-icon" style="display:none;">${ICONS.FILTER}</div>
                </div>`;
      }
      else {
        iconHtml = `<div class="extension-icon">${ICONS.FILTER}</div>`;
      }

      filterHtml += `<div class="extension-card filter-card" data-action="pick" data-filter="${escapeHtml(filter.name)}">
                ${iconHtml}
                <div class="extension-name">${escapeHtml(filter.displayName)}</div>
            </div>`;
    });

    filterHtml += `</div></div>`;
  }

  container.innerHTML = filterHtml;
  
  onAction(container, {
    pick: d => { closeFilterPicker(); showFilterForm(d.filter); }
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
                <span>${escapeHtml(param.displayName || paramName)}</span>
                <span id="${paramName}-value">${isColorParam ? displayValue : defaultVal}</span>
            </div>`;

    if (paramType.includes('Single') || paramType.includes('Double') || paramType.includes('Float')) {
      const min = param.minValue ?? 0;
      const max = param.maxValue ?? 1;
      const step = (max - min) / 100 || 0.01;
      html += `<input type="range" id="${paramName}" min="${min}" max="${max}"
                step="${step}" value="${defaultVal}" data-input="slider">`;
    } else if (isBooleanType) {
      html += `<input type="checkbox" id="${paramName}" ${defaultVal ? 'checked' : ''}>`;
    } else if (paramType.includes('Int') || paramType.includes('Byte')) {
      const min = param.minValue ?? 0;
      const max = param.maxValue ?? (paramType.includes('Byte') ? 255 : 100);
      html += `<input type="range" id="${paramName}" min="${min}" max="${max}"
                step="1" value="${defaultVal}" data-input="slider">`;
    } else if (isColorParam) {
      html += createColorInputWithAlpha(paramName, defaultVal, { action: 'color-preview', param: paramName });
    } else {
      html += `<input type="text" id="${paramName}" value="${defaultVal}">`;
    }

    if (param.description) {
      html += `<p class="param-description">${escapeHtml(param.description)}</p>`;
    }

    html += `</div>`;
  });

  formContainer.innerHTML = html;

  const footerContainer = document.getElementById('filter-form-footer');
  if (footerContainer) {
    footerContainer.innerHTML = `
      <button class="btn btn-secondary" data-action="hide-filter-form">Cancel</button>
      <button class="btn btn-primary" data-action="add-filter"${dataAttrs({ type: typeName })}>Add Filter</button>
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
    const live = { action: 'filter-param', index, param: paramName };  // applied to the running filter as it changes
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
                <span>${escapeHtml(param.displayName || paramName)}</span>
                <span id="${paramName}-value">${isColorParam ? displayValue : currentValue}</span>
            </div>`;

    if (paramType.includes('Single') || paramType.includes('Double') || paramType.includes('Float')) {
      const min = param.minValue ?? 0;
      const max = param.maxValue ?? 1;
      const step = (max - min) / 100 || 0.01;
      html += `<input type="range" id="${paramName}" min="${min}" max="${max}" 
                step="${step}" value="${currentValue}"${markerAttrs('input', live)}>`;
    } else if (paramType.includes('Int') || paramType.includes('Byte')) {
      const min = param.minValue ?? 0;
      const max = param.maxValue ?? (paramType.includes('Byte') ? 255 : 100);
      html += `<input type="range" id="${paramName}" min="${min}" max="${max}" 
                step="1" value="${currentValue}"${markerAttrs('input', live)}>`;
    } else if (paramType.includes('Boolean') || paramType.includes('Bool')) {
      html += `<input type="checkbox" id="${paramName}" ${currentValue ? 'checked' : ''}${markerAttrs('change', live)}>`;
    } else if (paramType.includes('Enum')) {
      html += `<select id="${paramName}"${markerAttrs('change', live)}>`;
      (param.enumValues || []).forEach(enumValue => {
        const selected = enumValue === currentValue ? 'selected' : '';
        html += `<option value="${escapeHtml(enumValue)}" ${selected}>${escapeHtml(enumValue)}</option>`;
      });
      html += `</select>`;
    } else if (isColorParam) {
      html += createColorInputWithAlpha(paramName, currentValue, live);
    } else {
      html += `<input type="text" id="${paramName}" value="${escapeHtml(currentValue)}"${markerAttrs('change', live)}>`;
    }

    if (param.description) {
      html += `<p class="param-description">${escapeHtml(param.description)}</p>`;
    }

    html += `</div>`;
  });

  window.currentEditingFilter = { index, filter };

  formContainer.innerHTML = html;

  const footerContainer = document.getElementById('filter-form-footer');
  if (footerContainer) {
    footerContainer.innerHTML = `
      <button class="btn btn-secondary" data-action="hide-filter-form">Close</button>
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
    notifyError('Filter', error);
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
export async function clearAllFilters() {
  try {
    const result = await api.post('/api/filters/clear');
    showMessage(result.data || 'Filters cleared', false);
    fetchFilters();
    fetchStatus();
  } catch (error) {
    showMessage('Failed to clear filters: ' + (error.message || 'Unknown error'), true);
  }
}

// Controls of #filter-form-container in index.html: wired here instead of inline handlers.
onReady(() => wire(document.getElementById('filter-form-container'), {
  click: {
    'hide-filter-form': () => hideFilterForm(),
    'add-filter': el => addFilter(el.dataset.type)
  },
  input: {
    slider: el => updateSlider(el.id),
    'color-preview': el => { document.getElementById(el.dataset.param + '-value').textContent = getColorWithAlpha(el.dataset.param); },
    'filter-param': el => updateSliderAndFilter(Number(el.dataset.index), el.dataset.param)
  },
  change: {
    'color-preview': el => { document.getElementById(el.dataset.param + '-value').textContent = getColorWithAlpha(el.dataset.param); },
    'filter-param': el => updateSliderAndFilter(Number(el.dataset.index), el.dataset.param)
  }
}));
