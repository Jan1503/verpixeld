/* ============================================================================
   EXTENSION MANAGEMENT - Extension Parameter Editing & Method Invocation
   ============================================================================ */

/**
 * Update extension parameter and schedule server update
 */
function updateExtensionParameter(canvasName, paramName) {
  let element = document.getElementById(paramName);
  if (!element) {
    element = document.getElementById(paramName + '-color');
  }
  const valueDisplay = document.getElementById(paramName + '-value');

  if (element && valueDisplay) {
    let displayValue, actualValue;

    if (element.type === 'checkbox') {
      actualValue = element.checked;
      displayValue = actualValue ? 'true' : 'false';
    } else if (element.type === 'select-one') {
      actualValue = element.value;
      displayValue = actualValue;
    } else if (element.type === 'color') {
      const alphaInput = document.getElementById(`${paramName}-alpha`);
      if (alphaInput) {
        actualValue = getColorWithAlpha(paramName);
      } else {
        actualValue = element.value;
      }
      displayValue = actualValue;
    } else if (element.type === 'range') {
      actualValue = element.value;
      displayValue = actualValue;
    } else {
      actualValue = element.value;
      displayValue = actualValue;
    }

    valueDisplay.textContent = displayValue;
  }

  clearTimeout(extensionUpdateTimer);
  extensionUpdateTimer = setTimeout(() => applyExtensionParameterUpdate(canvasName), 150);
}

/**
 * Apply extension parameter update to server (only changed values)
 */
async function applyExtensionParameterUpdate(canvasName) {
  if (!window.currentEditingExtension) {
    return;
  }

  const { extension, originalConfig } = window.currentEditingExtension;
  const config = {};
  const params = extension.parameters || [];

  // Structured params (lists / objects) are tracked in the in-memory model, not the DOM.
  const struct = window.extStructured || {};
  Object.keys(struct).forEach((name) => {
    const st = struct[name];
    const val = st.kind === 'list' ? st.items : st.value;
    const orig = originalConfig ? originalConfig[name] : undefined;
    if (JSON.stringify(orig) !== JSON.stringify(val)) {
      config[name] = val;
    }
  });

  params.forEach((param) => {
    // Skip params handled by the structured model above.
    if (struct[param.name]) return;
    let element = document.getElementById(param.name);
    if (!element) {
      element = document.getElementById(param.name + '-color');
    }
    if (element) {
      const paramType = param.parameterType || param.type || '';

      if (paramType === 'String' && element.value.startsWith('{') && element.value.endsWith('}')) {
        try {
          const jsonValue = JSON.parse(element.value);
          config[param.name] = jsonValue;
        } catch (e) {
          config[param.name] = element.value;
        }
      } else {
        let value;

        if (paramType.includes('Boolean') || paramType.includes('Bool')) {
          value = element.checked;
        } else if (paramType.includes('Int32') || paramType.includes('Int') || paramType.includes('Byte')) {
          value = parseInt(element.value);
          if (isNaN(value)) {
            value = element.value;
          }
        } else if (paramType.includes('Single') || paramType.includes('Double') || paramType.includes('Float')) {
          value = parseFloat(element.value);
          if (isNaN(value)) {
            value = element.value;
          }
        } else {
          const colorInput = document.getElementById(`${param.name}-color`);
          if (colorInput) {
            value = getColorWithAlpha(param.name);
          } else {
            value = element.value;
          }
        }

        // Only include if value actually changed from original
        const originalValue = originalConfig ? originalConfig[param.name] : undefined;
        const hasChanged = !valuesEqual(originalValue, value);

        if (hasChanged) {
          config[param.name] = value;
        }
      }
    }
  });

  // If nothing changed, don't send anything
  if (Object.keys(config).length === 0) {
    return;
  }

  try {
    await api.post(`/api/layout/configure/${canvasName}`, config);
    // Update originalConfig with the new values
    if (window.currentEditingExtension && window.currentEditingExtension.originalConfig) {
      Object.keys(config).forEach(key => {
        window.currentEditingExtension.originalConfig[key] = config[key];
      });
    }
  } catch (error) {
    console.error('Failed to update extension:', error);
  }
}

/**
 * Edit extension parameters for a canvas
 */
async function editExtensionParameters(canvasName) {
  try {
    const contentResult = await api.get(`/api/layout/content/${canvasName}`);
    const content = contentResult.data;
    const extensionName = content.extensionName;

    const extResult = await api.get('/api/extensions/available');
    const extension = extResult.data.find(e => e.displayName === extensionName);

    if (!extension) {
      showMessage(`Extension '${extensionName}' not found`, true);
      return;
    }

    if (!extension.parameters || extension.parameters.length === 0) {
      showMessage('This extension has no configurable parameters', false);
      return;
    }

    const configResult = await api.get(`/api/layout/content/${canvasName}`);
    const currentConfig = configResult.data.currentParameters || configResult.data.configuration || {};

    showExtensionEditForm(canvasName, extension, currentConfig);

  } catch (error) {
    console.error('Failed to edit extension:', error);
    showMessage('Failed to edit extension: ' + (error.message || 'Unknown error'), true);
  }
}

/* ----------------------------------------------------------------------------
   Structured parameter support (scalar / enum / colour leaves + object & list)
   ---------------------------------------------------------------------------- */

/** Normalises a field/param descriptor to a leaf widget kind. */
function leafKind(field) {
  const k = (field.kind || '').toLowerCase();
  if (k === 'enum' || (field.isEnum && field.enumValues && field.enumValues.length)) return 'enum';
  if (k === 'color') return 'color';
  const t = field.parameterType || field.type || '';
  if (t.includes('Boolean') || t.includes('Bool')) return 'bool';
  if (t.includes('Single') || t.includes('Double') || t.includes('Float') ||
      t.includes('Int') || t.includes('Byte')) return 'number';
  const n = (field.name || '').toLowerCase();
  if (t.includes('SKColor') || n.endsWith('color') || n.endsWith('colour')) return 'color';
  return 'text';
}

/** Renders only the input element for a leaf field. */
function renderLeafInput(field, value, fieldId, onChangeJs, isReadOnly) {
  const k = leafKind(field);
  const dis = isReadOnly ? 'disabled' : '';
  const evt = (k === 'number' || k === 'text') ? 'oninput' : 'onchange';
  const on = isReadOnly || !onChangeJs ? '' : `${evt}="${onChangeJs}"`;

  if (k === 'number') {
    const t = field.parameterType || '';
    const isFloat = t.includes('Single') || t.includes('Double') || t.includes('Float');
    const min = field.minValue ?? 0;
    const max = field.maxValue ?? (isFloat ? 1 : (t.includes('Byte') ? 255 : 100));
    const step = isFloat ? ((max - min) / 100 || 0.01) : 1;
    const v = (value !== undefined && value !== '') ? value : (field.defaultValue ?? min);
    return `<input type="range" id="${fieldId}" min="${min}" max="${max}" step="${step}" value="${v}" ${dis} ${on}>`;
  }
  if (k === 'bool') {
    return `<input type="checkbox" id="${fieldId}" ${value ? 'checked' : ''} ${dis} ${on}>`;
  }
  if (k === 'enum') {
    let h = `<select id="${fieldId}" ${dis} ${on}>`;
    (field.enumValues || []).forEach(ev => {
      h += `<option value="${ev}" ${ev === value ? 'selected' : ''}>${ev}</option>`;
    });
    return h + `</select>`;
  }
  if (k === 'color') {
    return createColorInputWithAlpha(fieldId, value, isReadOnly ? '' : onChangeJs);
  }
  const textInput = `<input type="text" id="${fieldId}" value="${value !== undefined ? escapeHtml(String(value)) : ''}" ${isReadOnly ? 'readonly' : ''} ${on}>`;
  if (!isReadOnly && isEntityField(field)) {
    const domain = haDomainFor(field);
    const multi = isMultiEntityField(field) ? 'true' : 'false';
    return `<span class="ha-entity-field">${textInput}<button type="button" class="btn btn-tiny" title="Pick entity" onclick="haPickEntity('${fieldId}', '${domain}', ${multi})">🔍</button></span>`;
  }
  if (!isReadOnly && isPlaceField(field)) {
    return `<span class="ha-entity-field">${textInput}<button type="button" class="btn btn-tiny" title="Search place" onclick="geoPickPlace('${fieldId}')">🔍</button></span>`;
  }
  return textInput;
}

function isEntityField(field) {
  const n = String(field.name || '').toLowerCase();
  return n === 'entityid' || n === 'entities' || n.endsWith('entity') || n.endsWith('entities') || n.includes('entity');
}

function isMultiEntityField(field) {
  const n = String(field.name || '').toLowerCase();
  return n === 'entities' || n.endsWith('entities');
}

function isPlaceField(field) {
  const n = String(field.name || '').toLowerCase();
  return n === 'location' || n === 'locationlabel' || n === 'latitude' || n === 'longitude' || n === 'city' || n === 'place';
}

function haDomainFor(field) {
  const n = String(field.name || '').toLowerCase();
  const blob = (n + ' ' + (field.displayName || '') + ' ' + (field.description || '')).toLowerCase();
  if (blob.includes('weather.' ) || blob.includes('weather *') || n.includes('weather')) return 'weather';
  if (blob.includes('climate.') || n.includes('climate')) return 'climate';
  if (blob.includes('media_player') || n.includes('media')) return 'media_player';
  if (blob.includes('binary_sensor')) return 'binary_sensor';
  if (blob.includes('depart') || blob.includes('hvv') || blob.includes('hafas')) return 'sensor';
  if (n.endsWith('entity') || n === 'entityid' || n === 'entities' || blob.includes('sensor') || blob.includes('power') || blob.includes('numeric') || blob.includes('pickup'))
    return 'sensor';
  return '';
}

/** Renders a labelled nested field row (used inside cards / object groups). */
function renderField(field, value, fieldId, onChangeJs) {
  const k = leafKind(field);
  const ro = field.isReadOnly === true;
  const label = escapeHtml(field.displayName || field.name);
  const valSpan = k === 'number' ? `<span id="${fieldId}-val" class="ext-field-val">${value ?? ''}</span>` : '';
  const desc = field.description ? `<p class="param-description">${escapeHtml(field.description)}</p>` : '';
  return `<div class="ext-field ext-field-${k}">
    <label for="${fieldId}">${label}${ro ? ' 🔒' : ''} ${valSpan}</label>
    ${renderLeafInput(field, value, fieldId, ro ? '' : onChangeJs, ro)}
    ${desc}
  </div>`;
}

/** Reads one leaf field's value from the DOM, coerced to the right JS type. */
function readFieldValue(field, fieldId) {
  const k = leafKind(field);
  if (k === 'color') return getColorWithAlpha(fieldId);
  const el = document.getElementById(fieldId);
  if (!el) return field.defaultValue;
  if (k === 'bool') return el.checked;
  if (k === 'number') {
    const t = field.parameterType || '';
    const isFloat = t.includes('Single') || t.includes('Double') || t.includes('Float');
    const v = isFloat ? parseFloat(el.value) : parseInt(el.value);
    return isNaN(v) ? el.value : v;
  }
  return el.value;
}

/** Builds a fresh item object from a list/object item schema using each field's default. */
function defaultItem(fields) {
  const o = {};
  (fields || []).forEach(f => {
    const k = leafKind(f);
    let d = f.defaultValue;
    if (d === undefined || d === null) {
      d = k === 'bool' ? false : k === 'number' ? (f.minValue ?? 0) :
        k === 'color' ? '#FFFFFFFF' : k === 'enum' ? (f.enumValues || [''])[0] : '';
    }
    o[f.name] = d;
  });
  return o;
}

/** Top-level dispatch: structured params get rich editors, scalars keep their slider row. */
function renderParam(canvasName, param, value) {
  const kind = (param.kind || '').toLowerCase();
  if (kind === 'list') return renderListParam(canvasName, param, value);
  if (kind === 'object') return renderObjectParam(canvasName, param, value);
  return renderScalarRow(canvasName, param, value);
}

function renderScalarRow(canvasName, param, value) {
  const ro = param.isReadOnly === true;
  const k = leafKind(param);
  const name = param.name;
  let display = value;
  if (k === 'color') {
    if (typeof value === 'number') display = '#' + (value >>> 0).toString(16).padStart(8, '0').toUpperCase();
    else if (typeof value === 'string' && !value.startsWith('#')) display = '#' + value;
  }
  const onChange = `updateExtensionParameter('${canvasName}', '${name}')`;
  const desc = param.description ? `<p class="param-description">${escapeHtml(param.description)}</p>` : '';
  return `<div class="slider-container${ro ? ' readonly' : ''}">
    <div class="slider-label">
      <span>${escapeHtml(param.displayName || name)}${ro ? ' 🔒' : ''}</span>
      <span id="${name}-value">${k === 'color' ? display : (value ?? '')}</span>
    </div>
    ${renderLeafInput(param, value, name, onChange, ro)}
    ${desc}
  </div>`;
}

function renderObjectParam(canvasName, param, value) {
  const v = (value && typeof value === 'object') ? { ...value } : {};
  window.extStructured[param.name] = { kind: 'object', fields: param.fields || [], value: v };
  const body = (param.fields || []).map(f => {
    const fid = `f_${param.name}_${f.name}`;
    const fv = v[f.name] !== undefined ? v[f.name] : f.defaultValue;
    return renderField(f, fv, fid, `extFieldChanged('${canvasName}', '${param.name}', '${fid}')`);
  }).join('');
  return `<div class="ext-group">
    <div class="ext-group-head">${escapeHtml(param.displayName || param.name)}</div>
    ${param.description ? `<p class="param-description">${escapeHtml(param.description)}</p>` : ''}
    <div class="ext-group-body">${body}</div>
  </div>`;
}

function renderListParam(canvasName, param, value) {
  const items = Array.isArray(value) ? value.map(v => ({ ...v })) : [];
  window.extStructured[param.name] = { kind: 'list', fields: param.fields || [], items };
  return `<div class="ext-list" data-param="${param.name}">
    <div class="ext-list-head">
      <span>${escapeHtml(param.displayName || param.name)}</span>
      <button type="button" class="btn btn-small btn-primary" onclick="extAddItem('${canvasName}', '${param.name}')">+ Add</button>
    </div>
    ${param.description ? `<p class="param-description">${escapeHtml(param.description)}</p>` : ''}
    <div class="ext-list-cards" id="extlist-${param.name}">${renderListCards(canvasName, param.name)}</div>
  </div>`;
}

function renderListCards(canvasName, paramName) {
  const st = window.extStructured[paramName];
  if (!st) return '';
  if (st.items.length === 0) return `<p class="ext-empty">No entries yet — click “Add”.</p>`;
  return st.items.map((item, i) => renderCard(canvasName, paramName, st.fields, item, i, st.items.length)).join('');
}

function renderCard(canvasName, paramName, fields, item, index, count) {
  const titleField = (item.Text ?? item.text ?? '').toString();
  const title = titleField.trim() || `#${index + 1}`;
  const body = (fields || []).map(f => {
    const fid = `f_${paramName}_${index}_${f.name}`;
    const val = item[f.name] !== undefined ? item[f.name] : f.defaultValue;
    return renderField(f, val, fid, `extFieldChanged('${canvasName}', '${paramName}', '${fid}')`);
  }).join('');
  return `<div class="ext-card">
    <div class="ext-card-head">
      <span class="ext-card-title">${escapeHtml(title)}</span>
      <span class="ext-card-actions">
        <button type="button" class="btn btn-tiny" ${index === 0 ? 'disabled' : ''} title="Move up" onclick="extMoveItem('${canvasName}', '${paramName}', ${index}, -1)">▲</button>
        <button type="button" class="btn btn-tiny" ${index === count - 1 ? 'disabled' : ''} title="Move down" onclick="extMoveItem('${canvasName}', '${paramName}', ${index}, 1)">▼</button>
        <button type="button" class="btn btn-tiny btn-danger" title="Remove" onclick="extRemoveItem('${canvasName}', '${paramName}', ${index})">✕</button>
      </span>
    </div>
    <div class="ext-card-body">${body}</div>
  </div>`;
}

/** Re-reads all list items from the DOM into the model (preserves in-progress edits). */
function extReadList(paramName) {
  const st = window.extStructured[paramName];
  if (!st || st.kind !== 'list') return;
  st.items = st.items.map((item, i) => {
    const o = { ...item };
    (st.fields || []).forEach(f => { o[f.name] = readFieldValue(f, `f_${paramName}_${i}_${f.name}`); });
    return o;
  });
}

function extReadObject(paramName) {
  const st = window.extStructured[paramName];
  if (!st || st.kind !== 'object') return;
  const o = { ...st.value };
  (st.fields || []).forEach(f => { o[f.name] = readFieldValue(f, `f_${paramName}_${f.name}`); });
  st.value = o;
}

function extFieldChanged(canvasName, paramName, fieldId) {
  const el = document.getElementById(fieldId);
  const span = document.getElementById(fieldId + '-val');
  if (span && el && el.type === 'range') span.textContent = el.value;

  const st = window.extStructured[paramName];
  if (!st) return;
  if (st.kind === 'list') extReadList(paramName); else extReadObject(paramName);
  scheduleStructuredApply(canvasName);
}

function rerenderList(canvasName, paramName) {
  const c = document.getElementById('extlist-' + paramName);
  if (c) c.innerHTML = renderListCards(canvasName, paramName);
}

function extAddItem(canvasName, paramName) {
  const st = window.extStructured[paramName];
  extReadList(paramName);
  st.items.push(defaultItem(st.fields));
  rerenderList(canvasName, paramName);
  scheduleStructuredApply(canvasName);
}

function extRemoveItem(canvasName, paramName, index) {
  const st = window.extStructured[paramName];
  extReadList(paramName);
  st.items.splice(index, 1);
  rerenderList(canvasName, paramName);
  scheduleStructuredApply(canvasName);
}

function extMoveItem(canvasName, paramName, index, dir) {
  const st = window.extStructured[paramName];
  extReadList(paramName);
  const j = index + dir;
  if (j < 0 || j >= st.items.length) return;
  [st.items[index], st.items[j]] = [st.items[j], st.items[index]];
  rerenderList(canvasName, paramName);
  scheduleStructuredApply(canvasName);
}

function scheduleStructuredApply(canvasName) {
  clearTimeout(extensionUpdateTimer);
  extensionUpdateTimer = setTimeout(() => applyExtensionParameterUpdate(canvasName), 200);
}

/** Injects the (theme-aware) styles for the structured list/object editors once. */
function ensureExtStyles() {
  if (document.getElementById('ext-struct-styles')) return;
  const style = document.createElement('style');
  style.id = 'ext-struct-styles';
  style.textContent = `
    .ext-list, .ext-group { margin: var(--spacing-md, 12px) 0; }
    .ext-list-head { display:flex; align-items:center; justify-content:space-between; gap:8px;
      font-weight:600; margin-bottom:6px; }
    .ext-group-head { font-weight:600; margin-bottom:6px; }
    .ext-list-cards { display:flex; flex-direction:column; gap:10px; }
    .ext-empty { opacity:.6; font-style:italic; padding:8px 0; }
    .ext-card { border:1px solid var(--border-color, rgba(255,255,255,.15));
      border-radius:10px; background:var(--surface-2, rgba(255,255,255,.04)); overflow:hidden; }
    .ext-card-head { display:flex; align-items:center; justify-content:space-between;
      padding:6px 10px; background:var(--surface-3, rgba(255,255,255,.06)); }
    .ext-card-title { font-weight:600; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; max-width:70%; }
    .ext-card-actions { display:flex; gap:4px; }
    .ext-card-body { padding:8px 10px; display:grid; grid-template-columns:repeat(auto-fit, minmax(150px, 1fr));
      gap:8px 14px; }
    .ext-field { display:flex; flex-direction:column; gap:2px; min-width:0; }
    .ext-field label { font-size:.8rem; opacity:.85; display:flex; justify-content:space-between; gap:6px; }
    .ext-field-val { opacity:.7; font-variant-numeric:tabular-nums; }
    .ext-field input[type=range], .ext-field select, .ext-field input[type=text] { width:100%; }
    .ext-field-bool { flex-direction:row; align-items:center; }
    .ext-group-body { display:grid; grid-template-columns:repeat(auto-fit, minmax(150px, 1fr)); gap:8px 14px; }
    .btn-tiny { padding:2px 7px; font-size:.75rem; line-height:1.2; min-width:0; }
  `;
  document.head.appendChild(style);
}

/**
 * Show extension edit form with parameters and methods
 */
function showExtensionEditForm(canvasName, extension, currentConfig) {
  ensureExtStyles();

  // Build form HTML. Structured (list/object) params are tracked in an in-memory model so the
  // user edits real cards instead of JSON; scalars keep their existing DOM-driven flow.
  window.extStructured = {};
  let formFieldsHtml = '';
  const params = extension.parameters || [];

  params.forEach((param) => {
    const paramName = param.name;
    const currentValue = currentConfig[paramName] !== undefined ? currentConfig[paramName] : param.defaultValue;
    formFieldsHtml += renderParam(canvasName, param, currentValue);
  });

  // Build methods HTML
  let methodsHtml = '';
  const methods = extension.methods || [];
  
  if (methods.length > 0) {
    const methodsByCategory = {};
    methods.forEach(method => {
      const category = method.category || 'General';
      if (!methodsByCategory[category]) {
        methodsByCategory[category] = [];
      }
      methodsByCategory[category].push(method);
    });

    Object.entries(methodsByCategory).forEach(([category, categoryMethods]) => {
      methodsHtml += `<div class="method-category">
        <h4 class="method-category-title">${category}</h4>`;
      
      categoryMethods.forEach(method => {
        const hasParams = method.parameters && method.parameters.length > 0;
        
        methodsHtml += `<div class="method-item" data-method="${method.name}">
          <div class="method-header">
            <span class="method-name">${method.displayName || method.name}</span>
            ${method.description ? `<span class="method-description">${method.description}</span>` : ''}
          </div>`;
        
        if (hasParams) {
          methodsHtml += `<div class="method-params">`;
          method.parameters.forEach(param => {
            const paramId = `method-${method.name}-${param.name}`;
            const defaultVal = param.defaultValue ?? '';
            const paramType = param.parameterType || 'String';
            const optionalLabel = param.isOptional ? ' (optional)' : '';
            
            methodsHtml += `<div class="method-param">
              <label for="${paramId}">${param.name}${optionalLabel}</label>`;
            
            if (paramType.includes('Int') || paramType.includes('Byte') || paramType.includes('Single') || paramType.includes('Double') || paramType.includes('Float')) {
              methodsHtml += `<input type="number" id="${paramId}" value="${defaultVal}" 
                placeholder="${param.name}">`;
            } else if (paramType.includes('Boolean') || paramType.includes('Bool')) {
              methodsHtml += `<input type="checkbox" id="${paramId}" ${defaultVal ? 'checked' : ''}>`;
            } else {
              methodsHtml += `<input type="text" id="${paramId}" value="${defaultVal}" 
                placeholder="${param.name}">`;
            }
            
            methodsHtml += `</div>`;
          });
          methodsHtml += `</div>`;
        }
        
        methodsHtml += `<button class="btn btn-small btn-primary method-invoke-btn" 
          onclick="invokeExtensionMethod('${canvasName}', '${method.name}', ${JSON.stringify(method.parameters || []).replace(/"/g, '&quot;')})">
          ${ICONS.PLAY || '▶'} ${method.displayName || method.name}
        </button>
        </div>`;
      });
      
      methodsHtml += `</div>`;
    });
  }

  const hasParams = params.length > 0;
  const hasMethods = methods.length > 0;
  const showTabs = hasParams && hasMethods;

  const modalHtml = `
    <div class="modal-overlay" id="extension-edit-modal">
      <div class="modal-content extension-edit-modal">
        
        <div class="modal-header">
          <h2>${ICONS.EDIT} ${extension.displayName}</h2>
          <button class="modal-close" onclick="closeExtensionEditModal()">${ICONS.CLOSE}</button>
        </div>
        
        <div class="modal-body">
          <p class="text-muted" style="margin-bottom: var(--spacing-md);">
            Canvas: <strong>${canvasName}</strong>
          </p>
          
          ${showTabs ? `
          <div class="extension-tabs">
            <button class="tab-btn active" data-tab="params" onclick="switchExtensionTab('params')">
              ${ICONS.SETTINGS || '⚙'} Parameters
            </button>
            <button class="tab-btn" data-tab="methods" onclick="switchExtensionTab('methods')">
              ${ICONS.PLAY || '▶'} Actions
            </button>
          </div>
          ` : ''}
          
          <div class="tab-content ${showTabs ? '' : 'no-tabs'}">
            ${hasParams ? `
            <div class="tab-pane active" id="tab-params">
              <div class="params-section">
                ${formFieldsHtml}
              </div>
            </div>
            ` : ''}
            
            ${hasMethods ? `
            <div class="tab-pane ${showTabs ? '' : 'active'}" id="tab-methods">
              <div class="methods-section">
                ${methodsHtml}
              </div>
            </div>
            ` : ''}
          </div>
        </div>
        
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="closeExtensionEditModal()">Close</button>
        </div>
        
      </div>
    </div>
  `;

  document.body.insertAdjacentHTML('beforeend', modalHtml);

  window.currentEditingExtension = { 
    canvasName, 
    extension,
    originalConfig: JSON.parse(JSON.stringify(currentConfig))
  };
}

/**
 * Switch between parameter and method tabs
 */
function switchExtensionTab(tabName) {
  document.querySelectorAll('.extension-tabs .tab-btn').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.tab === tabName);
  });
  
  document.querySelectorAll('.tab-pane').forEach(pane => {
    pane.classList.toggle('active', pane.id === `tab-${tabName}`);
  });
}

/**
 * Invoke a method on the extension
 */
async function invokeExtensionMethod(canvasName, methodName, methodParams) {
  const args = [];
  if (methodParams && methodParams.length > 0) {
    methodParams.forEach(param => {
      const inputId = `method-${methodName}-${param.name}`;
      const input = document.getElementById(inputId);
      if (input) {
        let value;
        const paramType = param.parameterType || 'String';
        
        if (input.type === 'checkbox') {
          value = input.checked;
        } else if (paramType.includes('Int') || paramType.includes('Byte')) {
          value = parseInt(input.value) || 0;
        } else if (paramType.includes('Single') || paramType.includes('Double') || paramType.includes('Float')) {
          value = parseFloat(input.value) || 0;
        } else {
          value = input.value;
        }
        args.push(value);
      }
    });
  }
  
  try {
    await api.post(`/api/layout/invoke/${canvasName}`, { methodName, args });
    toast.success('Action Executed', `${methodName} completed successfully`);
  } catch (error) {
    console.error('Method invocation error:', error);
    toast.error('Error', 'Failed to invoke method: ' + (error.message || 'Unknown error'));
  }
}

/**
 * Close extension edit modal
 */
function closeExtensionEditModal() {
  const modal = document.getElementById('extension-edit-modal');
  if (modal) modal.remove();
  window.currentEditingExtension = null;
  window.extStructured = {};
  // If the full editor was opened from the Studio for a rotation step, persist the live canvas
  // parameters back into that step (the editor writes to the live canvas, not the step config).
  const step = window.leFullParamStep;
  window.leFullParamStep = null;
  if (step && typeof captureStepFromLiveCanvas === 'function') {
    captureStepFromLiveCanvas(step.name, step.index);
  }
}

// Expose globally
window.updateExtensionParameter = updateExtensionParameter;
window.applyExtensionParameterUpdate = applyExtensionParameterUpdate;
window.editExtensionParameters = editExtensionParameters;
window.showExtensionEditForm = showExtensionEditForm;
window.switchExtensionTab = switchExtensionTab;
window.invokeExtensionMethod = invokeExtensionMethod;
window.closeExtensionEditModal = closeExtensionEditModal;
window.extAddItem = extAddItem;
window.extRemoveItem = extRemoveItem;
window.extMoveItem = extMoveItem;
window.extFieldChanged = extFieldChanged;

// ── Home Assistant entity picker ───────────────────────────────────────────
const HA_DOMAINS = ['', 'sensor', 'weather', 'climate', 'media_player', 'binary_sensor', 'light', 'switch'];

async function haPickEntity(fieldId, domain, multi) {
  const target = document.getElementById(fieldId);
  if (!target) return;
  ensureHaPickStyles();
  domain = domain || '';
  multi = !!multi;

  document.getElementById('ha-pick-modal')?.remove();
  const chips = HA_DOMAINS.map(d => {
    const label = d || 'All';
    const on = d === domain ? ' active' : '';
    return `<button type="button" class="ha-pick-chip${on}" data-domain="${d}">${label}</button>`;
  }).join('');
  const html = `
    <div class="modal-overlay" id="ha-pick-modal">
      <div class="modal-content" style="max-width:560px;">
        <div class="modal-header">
          <h2>${multi ? 'Add Home Assistant entity' : 'Pick Home Assistant entity'}</h2>
          <button class="modal-close" onclick="haPickClose()">${typeof ICONS !== 'undefined' ? ICONS.CLOSE : '✕'}</button>
        </div>
        <div class="modal-body">
          <input type="text" id="ha-pick-search" class="ha-pick-search" placeholder="Search by name…" autocomplete="off">
          <div class="ha-pick-chips">${chips}</div>
          <div class="ha-pick-list" id="ha-pick-list"><p class="text-muted">Loading…</p></div>
        </div>
      </div>
    </div>`;
  document.body.insertAdjacentHTML('beforeend', html);

  const search = document.getElementById('ha-pick-search');
  const list = document.getElementById('ha-pick-list');
  let currentDomain = domain;
  let timer = null;

  document.querySelectorAll('#ha-pick-modal .ha-pick-chip').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('#ha-pick-modal .ha-pick-chip').forEach(b => b.classList.toggle('active', b === btn));
      currentDomain = btn.dataset.domain || '';
      load(search.value.trim());
    });
  });

  async function load(q) {
    try {
      const qs = [];
      if (q) qs.push('q=' + encodeURIComponent(q));
      if (currentDomain) qs.push('domain=' + encodeURIComponent(currentDomain));
      const res = await window.api.get('/api/homeassistant/entities' + (qs.length ? '?' + qs.join('&') : ''));
      const items = (res && res.data) || [];
      if (!items.length) {
        let hint = 'No matching entities.';
        try {
          const st = await window.api.get('/api/homeassistant/status');
          if (st && st.data && st.data.connected === false)
            hint = 'Home Assistant is not connected. Enable it in Settings, then come back.';
          else if (st && st.data && !st.data.entityCount)
            hint = 'Connected, but no entities yet — wait a moment for the snapshot.';
        } catch (e) { /* ignore */ }
        list.innerHTML = `<p class="text-muted">${hint}</p>`;
        return;
      }
      list.innerHTML = items.slice(0, 250).map(e => {
        const id = String(e.entityId).replace(/\\/g, '\\\\').replace(/'/g, "\\'");
        const name = e.friendlyName || e.entityId;
        const meta = [(e.state ?? '') + (e.unit ? ' ' + e.unit : ''), e.entityId].filter(Boolean).join(' · ');
        return `<button type="button" class="ha-pick-item" onclick="haPickChoose('${fieldId}', '${id}', ${multi})">
          <span class="ha-pick-name">${escapeHtml(name)}</span>
          <span class="ha-pick-meta">${escapeHtml(meta)}</span>
        </button>`;
      }).join('');
    } catch (e) {
      list.innerHTML = '<p class="text-muted">Failed to load entities.</p>';
    }
  }

  search.addEventListener('input', () => {
    clearTimeout(timer);
    timer = setTimeout(() => load(search.value.trim()), 150);
  });
  await load('');
  search.focus();
}

function haPickChoose(fieldId, entityId, multi) {
  const target = document.getElementById(fieldId);
  if (target) {
    if (multi) {
      const cur = (target.value || '').split(',').map(s => s.trim()).filter(Boolean);
      if (!cur.includes(entityId)) cur.push(entityId);
      target.value = cur.join(', ');
    } else {
      target.value = entityId;
    }
    target.dispatchEvent(new Event('input', { bubbles: true }));
    target.dispatchEvent(new Event('change', { bubbles: true }));
  }
  haPickClose();
}

function haPickClose() {
  document.getElementById('ha-pick-modal')?.remove();
}

// ── Place search (Nominatim via the host) ──────────────────────────────────
async function geoPickPlace(fieldId) {
  const target = document.getElementById(fieldId);
  if (!target) return;
  ensureHaPickStyles();

  document.getElementById('ha-pick-modal')?.remove();
  const html = `
    <div class="modal-overlay" id="ha-pick-modal">
      <div class="modal-content" style="max-width:520px;">
        <div class="modal-header">
          <h2>Search place</h2>
          <button class="modal-close" onclick="haPickClose()">${typeof ICONS !== 'undefined' ? ICONS.CLOSE : '✕'}</button>
        </div>
        <div class="modal-body">
          <input type="text" id="ha-pick-search" class="ha-pick-search" placeholder="City, region, address…" autocomplete="off">
          <div class="ha-pick-list" id="ha-pick-list"><p class="text-muted">Type at least two letters.</p></div>
        </div>
      </div>
    </div>`;
  document.body.insertAdjacentHTML('beforeend', html);

  const search = document.getElementById('ha-pick-search');
  const list = document.getElementById('ha-pick-list');
  let timer = null;
  const seed = (target.value || '').trim();
  if (seed && !/^-?\d+(\.\d+)?$/.test(seed)) search.value = seed;

  async function load(q) {
    if (!q || q.length < 2) {
      list.innerHTML = '<p class="text-muted">Type a city or place name.</p>';
      return;
    }
    try {
      const res = await window.api.get('/api/geo/search?q=' + encodeURIComponent(q));
      const items = (res && res.data) || [];
      if (!items.length) {
        list.innerHTML = '<p class="text-muted">No places found.</p>';
        return;
      }
      list.innerHTML = items.map((p, i) => {
        return `<button type="button" class="ha-pick-item" data-idx="${i}">
          <span class="ha-pick-name">${escapeHtml(p.name || p.displayName)}</span>
          <span class="ha-pick-meta">${escapeHtml(p.displayName || '')}</span>
        </button>`;
      }).join('');
      list.querySelectorAll('.ha-pick-item').forEach((btn, i) => {
        btn.addEventListener('click', () => geoPickChoose(fieldId, items[i]));
      });
    } catch (e) {
      list.innerHTML = '<p class="text-muted">Place search failed.</p>';
    }
  }

  search.addEventListener('input', () => {
    clearTimeout(timer);
    timer = setTimeout(() => load(search.value.trim()), 200);
  });
  if (search.value.trim().length >= 2) await load(search.value.trim());
  search.focus();
}

function geoPickChoose(fieldId, place) {
  const lat = String(place.lat ?? '');
  const lon = String(place.lon ?? '');
  const name = place.name || place.displayName || '';
  const prefix = fieldId.replace(/(LocationLabel|Location|Latitude|Longitude|City|Place)$/i, '');
  const fill = (suffix, value) => {
    const el = document.getElementById(prefix + suffix) || document.getElementById(suffix);
    if (!el || value === '') return;
    el.value = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  };
  fill('Location', name);
  fill('LocationLabel', name);
  fill('City', name);
  fill('Place', name);
  fill('Latitude', lat);
  fill('Longitude', lon);
  // If the button was on lat/lon itself, still write the clicked field.
  const target = document.getElementById(fieldId);
  if (target && /latitude/i.test(fieldId)) target.value = lat;
  if (target && /longitude/i.test(fieldId)) target.value = lon;
  if (target && /location|city|place/i.test(fieldId) && !/latitude|longitude/i.test(fieldId)) target.value = name;
  if (target) {
    target.dispatchEvent(new Event('input', { bubbles: true }));
    target.dispatchEvent(new Event('change', { bubbles: true }));
  }
  haPickClose();
}

function ensureHaPickStyles() {
  if (document.getElementById('ha-pick-styles')) return;
  const el = document.createElement('style');
  el.id = 'ha-pick-styles';
  el.textContent = `
    .ha-entity-field{display:flex;gap:4px;align-items:center;}
    .ha-entity-field input{flex:1;}
    .ha-pick-search{width:100%;box-sizing:border-box;margin-bottom:8px;padding:6px 8px;}
    .ha-pick-chips{display:flex;flex-wrap:wrap;gap:4px;margin:0 0 8px;}
    .ha-pick-chip{padding:3px 8px;border:1px solid #3a3a3a;border-radius:999px;background:#1d1d1f;color:#ccc;cursor:pointer;font-size:.72rem;text-transform:lowercase;}
    .ha-pick-chip.active{border-color:#5ab4ff;background:rgba(90,180,255,.18);color:#fff;}
    .ha-pick-list{max-height:50vh;overflow:auto;display:flex;flex-direction:column;gap:4px;}
    .ha-pick-item{display:flex;flex-direction:column;align-items:flex-start;gap:1px;text-align:left;padding:6px 8px;border:1px solid #2e2e30;border-radius:6px;background:#1b1b1d;color:#ddd;cursor:pointer;}
    .ha-pick-item:hover{border-color:#5ab4ff;background:#222;}
    .ha-pick-name{font-size:.9rem;color:#fff;}
    .ha-pick-id{font-family:monospace;font-size:.8rem;color:#9fd;}
    .ha-pick-meta{font-size:.72rem;color:var(--text-muted,#999);font-family:ui-monospace,monospace;}`;
  document.head.appendChild(el);
}

window.haPickEntity = haPickEntity;
window.haPickChoose = haPickChoose;
window.haPickClose = haPickClose;
window.geoPickPlace = geoPickPlace;
