/* ============================================================================
   SETTINGS - Output, global, per-backend, Home Assistant
   ============================================================================ */

const OUTPUT_LABELS = {
  network: 'Network',
  hdmi: 'HDMI',
  spi: 'SPI',
  gpio: 'Hardware',
  simulation: 'Simulation'
};

const outputState = {
  active: '',
  saved: '',
  editing: '',
  canvas: null
};

async function initSettings() {
  await loadCurrentSettings();
  await loadImageCorrection();
  await loadNetworkConfig();
  await loadSeam();
  setupMatrixCalculation();
  await loadCertificateInfo();
}

async function loadCurrentSettings() {
  try {
    const result = await api.get('/api/settings');
    const d = result.data || result;
    if (!d) return;

    const app = d.app || {};
    const matrix = d.matrix || {};
    const hdmi = d.hdmi || {};
    const spi = d.spi || {};
    const network = d.network || {};
    const ha = d.homeAssistant || {};

    setValue('app-display-width', app.displayWidth);
    setValue('app-display-height', app.displayHeight);
    setValue('app-target-fps', app.targetFps);
    setChecked('app-verbose-logging', app.verboseLogging);

    setValue('matrix-rows', matrix.rows);
    setValue('matrix-cols', matrix.cols);
    setValue('matrix-chain', matrix.chainLength);
    setValue('matrix-parallel', matrix.parallel);
    setValue('matrix-gpio-slowdown', matrix.gpioSlowdown);
    setValue('matrix-pwm-bits', matrix.pwmBits);
    setValue('matrix-pwm-lsb', matrix.pwmLsbNanoseconds);
    setValue('matrix-pwm-dither', matrix.pwmDitherBits);
    setValue('matrix-brightness', matrix.brightness);
    setValue('matrix-limit-hz', matrix.limitRefreshRateHz);
    setValue('matrix-panel-type', matrix.panelType || '');
    setValue('matrix-hardware-mapping', matrix.hardwareMapping || 'regular');
    setValue('matrix-rgb-seq', matrix.ledRgbSequence || 'RGB');
    setValue('matrix-pixel-mapper', matrix.pixelMapperConfig || '');
    setValue('matrix-row-addr', matrix.rowAddressType);
    setValue('matrix-scan', String(matrix.scanMode ?? 0));
    setValue('matrix-mux', matrix.multiplexing);
    setChecked('matrix-no-pulse', matrix.disableHardwarePulsing);
    setChecked('matrix-no-busy', matrix.disableBusyWaiting);
    setChecked('matrix-show-refresh', matrix.showRefreshRate);
    setChecked('matrix-inverse', matrix.inverseColors);

    setValue('hdmi-device', hdmi.framebufferDevice);
    setValue('hdmi-offset-x', hdmi.offsetX);
    setValue('hdmi-offset-y', hdmi.offsetY);
    setValue('hdmi-scale', hdmi.scale);
    setValue('hdmi-wall-w', hdmi.wallWidth);
    setValue('hdmi-wall-h', hdmi.wallHeight);
    setChecked('hdmi-swap', hdmi.swapRedBlue);
    setChecked('hdmi-clear', hdmi.clearScreenOnStart !== false);

    setValue('spi-device', spi.device);
    setValue('spi-speed', spi.speedHz);
    setValue('spi-wall-w', spi.wallWidth);
    setValue('spi-wall-h', spi.wallHeight);
    setChecked('spi-swap', spi.swapRedBlue);

    setValue('net-host', network.host);
    setValue('net-port', network.port);
    setValue('net-mbps', network.targetMbps);
    if (network.colorBits != null) setValue('net-bits', String(network.colorBits));
    setChecked('net-swap', network.swapRedBlue);
    setValue('net-panel-id', network.panelId || '');
    updateBoundBadge();

    setChecked('ha-enabled', ha.enabled);
    setValue('ha-baseurl', ha.baseUrl);
    setValue('ha-token', ha.token);
    updateHaHint(ha);

    const active = d.activeMode || app.outputMode || 'simulation';
    const saved = d.savedMode || app.outputMode || active;
    setOutputModeUi(active, saved, d.canvas, outputState.editing || active);
    updateCalculatedResolution();
    updateSystemInfo(d.systemInfo);
  } catch (error) {
    console.error('[SETTINGS] Failed to load settings:', error);
    window.toast?.error('Settings', 'Failed to load settings');
  }
}

function setOutputModeUi(active, saved, canvas, editing) {
  outputState.active = active || outputState.active || 'simulation';
  outputState.saved = saved || outputState.saved || outputState.active;
  outputState.editing = editing || outputState.editing || outputState.active;
  if (canvas) outputState.canvas = canvas;

  document.querySelectorAll('input[name="output-mode"]').forEach(el => {
    el.checked = el.value === outputState.editing;
  });
  document.querySelectorAll('.output-mode-card').forEach(el => {
    const mode = el.dataset.mode;
    el.classList.toggle('is-live', mode === outputState.active);
    el.classList.toggle('is-saved', mode === outputState.saved);
  });
  document.querySelectorAll('.output-panel').forEach(el => {
    const id = el.id.replace('out-', '');
    el.hidden = id !== outputState.editing;
    if (!el.hidden) {
      el.classList.remove('collapsed');
      const body = el.querySelector('.tab-section-body');
      if (body) body.style.display = '';
    }
  });
  document.querySelectorAll('[data-switch-output]').forEach(btn => {
    btn.hidden = btn.getAttribute('data-switch-output') === outputState.active;
  });

  const status = document.getElementById('output-mode-status');
  if (status) {
    const size = outputState.canvas ? `${outputState.canvas.width}×${outputState.canvas.height}` : '';
    const liveName = OUTPUT_LABELS[outputState.active] || outputState.active;
    const editName = OUTPUT_LABELS[outputState.editing] || outputState.editing;
    let text = `Live: ${liveName}${size ? ' · canvas ' + size : ''}`;
    if (outputState.editing && outputState.editing !== outputState.active)
      text += ` · viewing ${editName} settings`;
    if (outputState.saved && outputState.saved !== outputState.active)
      text += ` · next start: ${OUTPUT_LABELS[outputState.saved] || outputState.saved}`;
    status.textContent = text;
  }
}

function selectOutputPanel(mode) {
  setOutputModeUi(outputState.active, outputState.saved, outputState.canvas, mode);
  const panel = document.getElementById('out-' + mode);
  if (panel) panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

function gpioGeometryLooksLikeCanvas() {
  const rows = num('matrix-rows', 64);
  const cols = num('matrix-cols', 64);
  const chain = num('matrix-chain', 1);
  const parallel = num('matrix-parallel', 1);
  return rows > 64 || (cols >= 128 && chain <= 1 && parallel <= 1);
}

async function maybeOfferSwitch(mode) {
  if (!mode || mode === outputState.active) return;
  const label = OUTPUT_LABELS[mode] || mode;
  const gpioRestart = mode === 'gpio' || outputState.active === 'gpio';
  const ok = await showConfirm({
    title: 'Switch output?',
    message: gpioRestart
      ? `${label} settings saved. Switch to ${label}? GPIO can only start at process launch — this is saved and applied after a restart.`
      : `${label} settings saved. Switch to ${label} now? Applied live if the canvas size matches, otherwise after a restart.`,
    confirmText: gpioRestart ? 'Use after restart' : 'Switch now',
    cancelText: 'Keep current output',
    type: 'warning',
    icon: '🔌'
  });
  if (ok) await switchOutput(mode);
}

async function requestOutputSwitch(mode) {
  if (mode === outputState.active) {
    window.toast?.info('Output', `Already using ${OUTPUT_LABELS[mode] || mode}.`);
    return;
  }
  if (mode === 'gpio' && gpioGeometryLooksLikeCanvas()) {
    window.toast?.error('Hardware',
      'Fix panel rows/cols first. GPIO wants HUB75 geometry (typically 64×64 × chains × parallel), not the 256×128 canvas size.');
    selectOutputPanel('gpio');
    return;
  }
  const label = OUTPUT_LABELS[mode] || mode;
  const gpioRestart = mode === 'gpio' || outputState.active === 'gpio';
  const ok = await showConfirm({
    title: `Use ${label}?`,
    message: gpioRestart
      ? `Switch to ${label}? Hardware GPIO can only be started at process launch. The choice is saved; restart verpixeld to apply.`
      : `Switch to ${label} now? Applied live if the canvas size matches, otherwise saved for the next start.`,
    confirmText: gpioRestart ? 'Use after restart' : 'Switch now',
    cancelText: 'Cancel',
    type: 'warning',
    icon: '🔌'
  });
  if (ok) await switchOutput(mode);
}

async function switchOutput(mode) {
  try {
    const result = await api.put('/api/settings/output', { mode });
    const d = result.data || result;
    const active = d.activeMode || outputState.active;
    const saved = d.savedMode || mode;
    setOutputModeUi(active, saved, outputState.canvas, outputState.editing || mode);
    if (d.requiresRestart) {
      window.toast?.warning('Output', d.message || 'Saved — restart required to switch output.');
    } else {
      window.toast?.success('Output', d.message || `Now using ${OUTPUT_LABELS[active] || active}`);
    }
    await loadNetworkConfig();
    await loadImageCorrection();
  } catch (error) {
    console.error('[SETTINGS] Output switch failed:', error);
    window.toast?.error('Output', error.message || 'Failed to switch output');
    await loadCurrentSettings();
  }
}

function setupMatrixCalculation() {
  ['matrix-rows', 'matrix-cols', 'matrix-chain', 'matrix-parallel'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('input', updateCalculatedResolution);
  });
}

function updateCalculatedResolution() {
  const rows = parseInt(document.getElementById('matrix-rows')?.value) || 64;
  const cols = parseInt(document.getElementById('matrix-cols')?.value) || 64;
  const chain = parseInt(document.getElementById('matrix-chain')?.value) || 1;
  const parallel = parseInt(document.getElementById('matrix-parallel')?.value) || 1;
  const display = document.getElementById('matrix-calculated-resolution');
  if (display) display.textContent = `${cols * chain} \u00D7 ${rows * parallel}`;
}

async function saveMatrixConfig() {
  const config = {
    rows: num('matrix-rows', 64),
    cols: num('matrix-cols', 64),
    chainLength: num('matrix-chain', 1),
    parallel: num('matrix-parallel', 1),
    gpioSlowdown: num('matrix-gpio-slowdown', 4),
    pwmBits: num('matrix-pwm-bits', 11),
    pwmLsbNanoseconds: num('matrix-pwm-lsb', 130),
    pwmDitherBits: num('matrix-pwm-dither', 0),
    brightness: num('matrix-brightness', 100),
    limitRefreshRateHz: num('matrix-limit-hz', 0),
    panelType: val('matrix-panel-type'),
    hardwareMapping: val('matrix-hardware-mapping') || 'regular',
    ledRgbSequence: val('matrix-rgb-seq'),
    pixelMapperConfig: val('matrix-pixel-mapper'),
    rowAddressType: num('matrix-row-addr', 0),
    scanMode: num('matrix-scan', 0),
    multiplexing: num('matrix-mux', 0),
    disableHardwarePulsing: checked('matrix-no-pulse'),
    disableBusyWaiting: checked('matrix-no-busy'),
    showRefreshRate: checked('matrix-show-refresh'),
    inverseColors: checked('matrix-inverse')
  };
  try {
    const result = await api.put('/api/settings/hardware', config);
    const d = result.data || result;
    window.toast?.[d.requiresRestart ? 'warning' : 'success'](
      'Hardware', d.message || result.message || 'Hardware settings saved');
    await maybeOfferSwitch('gpio');
  } catch (error) {
    console.error('[SETTINGS] Failed to save hardware config:', error);
    window.toast?.error('Hardware', error.message || 'Failed to save hardware settings');
  }
}

async function saveAppSettings() {
  const config = {
    targetFps: num('app-target-fps', 60),
    verboseLogging: checked('app-verbose-logging'),
    displayWidth: num('app-display-width', 256),
    displayHeight: num('app-display-height', 128)
  };
  try {
    await api.put('/api/settings/app', config);
    window.toast?.success('Settings', 'Global settings saved (FPS is live)');
  } catch (error) {
    console.error('[SETTINGS] Failed to save app settings:', error);
    window.toast?.error('Settings', error.message || 'Failed to save');
  }
}

async function saveHdmiSettings() {
  const body = {
    framebufferDevice: val('hdmi-device') || '/dev/fb0',
    offsetX: num('hdmi-offset-x', 0),
    offsetY: num('hdmi-offset-y', 0),
    scale: num('hdmi-scale', 1),
    wallWidth: num('hdmi-wall-w', 0),
    wallHeight: num('hdmi-wall-h', 0),
    swapRedBlue: checked('hdmi-swap'),
    clearScreenOnStart: checked('hdmi-clear')
  };
  try {
    const r = await api.put('/api/settings/hdmi', body);
    window.toast?.success('HDMI', r.message || 'HDMI settings saved');
    await maybeOfferSwitch('hdmi');
  } catch (error) {
    window.toast?.error('HDMI', error.message || 'Failed to save HDMI settings');
  }
}

async function saveSpiSettings() {
  const body = {
    device: val('spi-device') || '/dev/spidev0.0',
    speedHz: num('spi-speed', 40000000),
    wallWidth: num('spi-wall-w', 256),
    wallHeight: num('spi-wall-h', 128),
    swapRedBlue: checked('spi-swap')
  };
  try {
    const r = await api.put('/api/settings/spi', body);
    window.toast?.success('SPI', r.message || 'SPI settings saved');
    await maybeOfferSwitch('spi');
  } catch (error) {
    window.toast?.error('SPI', error.message || 'Failed to save SPI settings');
  }
}

function updateHaHint(ha) {
  const el = document.getElementById('ha-connected-hint');
  if (!el) return;
  if (!ha) { el.textContent = ''; return; }
  if (!ha.enabled) { el.textContent = 'disabled'; return; }
  el.textContent = ha.connected
    ? `connected · ${ha.entityCount || 0} entities`
    : 'connecting…';
}

async function saveHomeAssistant() {
  const body = {
    enabled: checked('ha-enabled'),
    baseUrl: val('ha-baseurl'),
    token: val('ha-token')
  };
  try {
    const r = await api.put('/api/settings/homeassistant', body);
    const d = r.data || r;
    updateHaHint(d);
    window.toast?.success('Home Assistant', r.message || 'Saved');
  } catch (error) {
    window.toast?.error('Home Assistant', error.message || 'Failed to save');
  }
}

function updateSystemInfo(info) {
  if (!info) return;
  const setStatus = (id, value, status) => {
    const el = document.getElementById(id);
    if (!el) return;
    el.textContent = value;
    el.className = 'system-info-value';
    if (status) el.classList.add(`status-${status}`);
  };
  if (info.ffmpegAvailable !== undefined)
    setStatus('sys-ffmpeg-status', info.ffmpegAvailable ? 'Available' : 'Not found',
      info.ffmpegAvailable ? 'ok' : 'error');
  if (info.pulseAudioAvailable !== undefined)
    setStatus('sys-pulseaudio-status', info.pulseAudioAvailable ? 'Running' : 'Not available',
      info.pulseAudioAvailable ? 'ok' : 'warning');
  if (info.bluetoothAvailable !== undefined)
    setStatus('sys-bluetooth-status', info.bluetoothAvailable ? 'Available' : 'Not available',
      info.bluetoothAvailable ? 'ok' : 'warning');
  if (info.configPath) setStatus('sys-config-path', info.configPath);
}

function setValue(id, value) {
  const el = document.getElementById(id);
  if (el && value !== undefined && value !== null) el.value = value;
}
function setChecked(id, value) {
  const el = document.getElementById(id);
  if (el) el.checked = !!value;
}
function val(id) { return document.getElementById(id)?.value?.trim() || ''; }
function num(id, fallback) {
  const n = parseFloat(document.getElementById(id)?.value);
  return Number.isFinite(n) ? n : fallback;
}
function checked(id) { return !!document.getElementById(id)?.checked; }

// ============================================================================
//   CERTIFICATE MANAGEMENT
// ============================================================================

async function loadCertificateInfo() {
  try {
    const result = await api.get('/api/settings/certificate');
    if (!result.data) return;
    const d = result.data;
    if (d.available === false) {
      setCertField('cert-subject', 'No certificate loaded', 'warning');
      setCertField('cert-issuer', '-');
      setCertField('cert-type', '-');
      setCertField('cert-expiry', '-');
      setCertField('cert-thumbprint', '-');
      return;
    }
    setCertField('cert-subject', d.subject || '-');
    setCertField('cert-issuer', d.issuer || '-');
    setCertField('cert-type', d.isSelfSigned ? 'Self-Signed' : 'CA-Signed', d.isSelfSigned ? 'warning' : 'ok');
    if (d.daysUntilExpiry !== undefined) {
      const status = d.daysUntilExpiry < 0 ? 'error' : d.daysUntilExpiry < 30 ? 'warning' : 'ok';
      const expiryText = d.daysUntilExpiry < 0
        ? `Expired (${d.notAfter})` : `${d.notAfter} (${d.daysUntilExpiry} days)`;
      setCertField('cert-expiry', expiryText, status);
    } else {
      setCertField('cert-expiry', d.notAfter || '-');
    }
    setCertField('cert-thumbprint', d.thumbprint || '-');
  } catch (error) {
    setCertField('cert-subject', error.message || 'Failed to load', 'error');
  }
}

function setCertField(id, value, status) {
  const el = document.getElementById(id);
  if (!el) return;
  el.textContent = value;
  el.className = 'system-info-value' + (id === 'cert-thumbprint' ? ' mono' : '');
  if (status) el.classList.add(`status-${status}`);
}

async function uploadCertificate() {
  const fileInput = document.getElementById('cert-file');
  const passwordInput = document.getElementById('cert-password');
  const file = fileInput?.files?.[0];
  const password = passwordInput?.value;
  if (!file) { window.toast?.error('Certificate', 'Please select a .pfx certificate file.'); return; }
  if (!password) { window.toast?.error('Certificate', 'Please enter the certificate password.'); return; }
  const confirmed = await window.showConfirm({
    title: 'Upload Certificate',
    message: 'This will replace the current HTTPS certificate. The server must be restarted for changes to take effect. Continue?',
    confirmText: 'Upload', cancelText: 'Cancel', type: 'warning', icon: '🔒'
  });
  if (!confirmed) return;
  try {
    const formData = new FormData();
    formData.append('certificate', file);
    formData.append('password', password);
    const result = await api.postForm('/api/settings/certificate/upload', formData);
    window.toast?.success('Certificate', result.message);
    fileInput.value = '';
    passwordInput.value = '';
    await loadCertificateInfo();
  } catch (error) {
    window.toast?.error('Certificate', error.message || 'Failed to upload certificate');
  }
}

async function regenerateCertificate() {
  const confirmed = await window.showConfirm({
    title: 'Regenerate Certificate',
    message: 'This will replace the current certificate with a new self-signed one. The server must be restarted for changes to take effect. Continue?',
    confirmText: 'Regenerate', cancelText: 'Cancel', type: 'warning', icon: '🔄'
  });
  if (!confirmed) return;
  try {
    const result = await api.post('/api/settings/certificate/regenerate');
    window.toast?.success('Certificate', result.message);
    await loadCertificateInfo();
  } catch (error) {
    window.toast?.error('Certificate', error.message || 'Failed to regenerate certificate');
  }
}

/* ----------------------------------------------------------------------------
   Global image correction
   ---------------------------------------------------------------------------- */

let _imgCorrTimer = null;

function readImageCorrectionInputs() {
  return {
    curve: document.getElementById('ic-curve')?.value || 'none',
    gamma: parseFloat(document.getElementById('ic-gamma')?.value) || 2.2,
    contrast: parseFloat(document.getElementById('ic-contrast')?.value) || 1.0,
    brightness: parseFloat(document.getElementById('ic-brightness')?.value) || 1.0,
    gainR: parseFloat(document.getElementById('ic-gainr')?.value) || 1.0,
    gainG: parseFloat(document.getElementById('ic-gaing')?.value) || 1.0,
    gainB: parseFloat(document.getElementById('ic-gainb')?.value) || 1.0
  };
}

function updateImageCorrectionLabels(v) {
  const set = (id, x) => { const el = document.getElementById(id); if (el) el.textContent = x; };
  set('ic-gamma-val', v.gamma.toFixed(2));
  set('ic-contrast-val', v.contrast.toFixed(2));
  set('ic-brightness-val', v.brightness.toFixed(2));
  set('ic-gainr-val', v.gainR.toFixed(2));
  set('ic-gaing-val', v.gainG.toFixed(2));
  set('ic-gainb-val', v.gainB.toFixed(2));
}

async function loadImageCorrection() {
  try {
    const result = await api.get('/api/settings/image-correction');
    const v = result.data || result;
    if (v && typeof v.gamma === 'number') {
      const curveEl = document.getElementById('ic-curve');
      if (curveEl && v.curve) curveEl.value = v.curve;
      setValue('ic-gamma', v.gamma);
      setValue('ic-contrast', v.contrast);
      setValue('ic-brightness', v.brightness);
      setValue('ic-gainr', v.gainR);
      setValue('ic-gaing', v.gainG);
      setValue('ic-gainb', v.gainB);
      updateImageCorrectionLabels(v);
    }
  } catch (error) {
    console.error('[SETTINGS] Failed to load image correction:', error);
  }
}

function onImageCorrectionInput() {
  const v = readImageCorrectionInputs();
  updateImageCorrectionLabels(v);
  if (_imgCorrTimer) clearTimeout(_imgCorrTimer);
  _imgCorrTimer = setTimeout(async () => {
    try { await api.post('/api/settings/image-correction', { ...v, save: false }); }
    catch (error) { console.error('[SETTINGS] Live image correction apply failed:', error); }
  }, 120);
}

async function saveImageCorrection() {
  const v = readImageCorrectionInputs();
  try {
    await api.post('/api/settings/image-correction', { ...v, save: true });
    window.toast?.success('Settings', 'Image correction saved');
  } catch (error) {
    window.toast?.error('Settings', error.message || 'Failed to save image correction');
  }
}

async function resetImageCorrection() {
  const d = { curve: 'none', gamma: 2.2, contrast: 1, brightness: 1, gainR: 1, gainG: 1, gainB: 1 };
  const curveEl = document.getElementById('ic-curve');
  if (curveEl) curveEl.value = d.curve;
  setValue('ic-gamma', d.gamma);
  setValue('ic-contrast', d.contrast);
  setValue('ic-brightness', d.brightness);
  setValue('ic-gainr', d.gainR);
  setValue('ic-gaing', d.gainG);
  setValue('ic-gainb', d.gainB);
  updateImageCorrectionLabels(d);
  try {
    await api.post('/api/settings/image-correction', { ...d, save: true });
    window.toast?.info('Settings', 'Image correction reset');
  } catch (error) {
    console.error('[SETTINGS] Failed to reset image correction:', error);
  }
}

async function loadNetworkConfig() {
  try {
    const result = await api.get('/api/settings/network');
    const v = result.data || result;
    if (!v) return;
    setValue('net-host', v.host);
    setValue('net-port', v.port);
    setValue('net-mbps', v.targetMbps);
    if (v.colorBits != null) setValue('net-bits', String(v.colorBits));
    setChecked('net-swap', v.swapRedBlue);
    setValue('net-panel-id', v.panelId || '');
    updateBoundBadge();
  } catch (error) {
    console.error('[SETTINGS] Failed to load network config:', error);
  }
}

async function applyNetworkConfig(save, opts) {
  const bindOnly = !!(opts && opts.bindOnly);
  const body = {
    host: val('net-host'),
    port: num('net-port', 7777),
    targetMbps: num('net-mbps', 19),
    colorBits: num('net-bits', 14),
    swapRedBlue: checked('net-swap'),
    panelId: val('net-panel-id'),
    save: !!save
  };
  if (!body.host) { window.toast?.error('Network', 'Enter the panel IP address.'); return false; }
  try {
    await api.post('/api/settings/network', body);
    updateBoundBadge();
    if (bindOnly) return true;
    window.toast?.success('Network',
      (save ? 'Saved & applied \u2192 ' : 'Applied \u2192 ') + body.host + ':' + body.port);
    if (save) await maybeOfferSwitch('network');
    return true;
  } catch (error) {
    window.toast?.error('Network', error.message || 'Failed to apply network config');
    return false;
  }
}

let lastPanelScan = [];

function updateBoundBadge() {
  const wrap = document.getElementById('net-bound');
  const label = document.getElementById('net-bound-label');
  if (!wrap || !label) return;
  const id = (val('net-panel-id') || '').trim();
  if (!id) {
    wrap.hidden = true;
    label.textContent = '';
    return;
  }
  const hex = id.replace(/[^0-9a-f]/gi, '');
  const mdns = hex.length >= 12 ? 'panel-' + hex.slice(6, 12) : '';
  const host = val('net-host') || '';
  label.textContent = 'Bound to ' + (mdns || id) + (host ? ' @ ' + host : '');
  wrap.hidden = false;
}

async function scanPanels() {
  const btn = document.getElementById('net-scan-btn');
  const status = document.getElementById('net-scan-status');
  const list = document.getElementById('net-panel-list');
  if (btn) btn.disabled = true;
  if (status) status.textContent = 'Scanning UDP 7778\u2026';
  if (list) list.innerHTML = '';
  try {
    const result = await api.get('/api/settings/network/discover?timeout=2500');
    const panels = result.data;
    lastPanelScan = Array.isArray(panels) ? panels : [];
    renderPanelList(lastPanelScan);
    if (status) {
      status.textContent = lastPanelScan.length
        ? lastPanelScan.length + ' panel(s) found'
        : (result.message || 'No panels answered. Flash firmware 1.1+ and check the LAN.');
    }
  } catch (error) {
    if (status) status.textContent = error.message || 'Scan failed';
    window.toast?.error('Network', error.message || 'Scan failed');
  } finally {
    if (btn) btn.disabled = false;
  }
}

function renderPanelList(panels) {
  const hostEl = document.getElementById('net-panel-list');
  if (!hostEl) return;
  const bound = (val('net-panel-id') || '').toLowerCase();
  hostEl.innerHTML = panels.map((p, i) => {
    const id = (p.id || '').toLowerCase();
    const name = escapeHtml(p.displayName || p.name || p.mdnsHost || p.host || 'panel');
    const via = (p.via || '').toLowerCase();
    const oldFw = !p.version || p.version === '1.0' || !id;
    const meta = [
      p.host,
      p.width && p.height ? p.width + '\u00d7' + p.height : '',
      p.colorBits ? p.colorBits + '-bit' : '',
      p.version ? 'fw ' + p.version : '',
      via === 'udp' ? 'UDP 7778' : (via === 'http' ? 'HTTP /status (no 7778)' : via),
      p.mdnsHost ? p.mdnsHost + '.local' : '',
      oldFw ? 'OTA 1.1 not applied' : ''
    ].filter(Boolean).map(escapeHtml).join(' \u00b7 ');
    const boundCls = id && id === bound ? ' is-bound' : '';
    return `<div class="net-panel-card${boundCls}">
      <div class="net-panel-info">
        <div class="net-panel-name">${name}</div>
        <div class="net-panel-meta">${meta}</div>
      </div>
      <div class="net-panel-actions">
        <button type="button" class="btn btn-small" onclick="identifyScannedPanel(${i})">Identify</button>
        <button type="button" class="btn btn-small btn-primary" onclick="useScannedPanel(${i})">Use this panel</button>
      </div>
    </div>`;
  }).join('');
}

async function identifyScannedPanel(i) {
  const p = lastPanelScan[i];
  if (!p?.host) return;
  try {
    await api.post('/api/settings/network/identify', { host: p.host, webPort: p.webPort || 5000 });
    window.toast?.info('Network', 'Identify flash sent to ' + p.host);
  } catch (error) {
    window.toast?.error('Network', error.message || 'Identify failed');
  }
}

async function useScannedPanel(i) {
  const p = lastPanelScan[i];
  if (!p?.host) return;
  setValue('net-host', p.host);
  if (p.udpPort) setValue('net-port', p.udpPort);
  if (p.colorBits) setValue('net-bits', String(p.colorBits));
  setValue('net-panel-id', p.id || '');
  updateBoundBadge();
  const ok = await applyNetworkConfig(true, { bindOnly: true });
  if (!ok) return;
  window.toast?.success('Network',
    'Bound to ' + (p.displayName || p.host) + ' — output not switched');
  renderPanelList(lastPanelScan);
}

async function unbindPanel() {
  setValue('net-panel-id', '');
  updateBoundBadge();
  await applyNetworkConfig(true, { bindOnly: true });
  window.toast?.info('Network', 'Panel unbound. Host IP is kept.');
  renderPanelList(lastPanelScan);
}

const SEAM_KNOT_INS = [0, 32, 64, 96, 128, 160, 192, 224, 255];
let seamPreviewOn = false;
let seamGreyBound = false;
let seamCurveTimer = 0;
let seamActiveBits = 14;
let seamSwitching = false;

function profileColumns(v, bits) {
  const key = String(bits === 8 ? 8 : 14);
  const fromProfile = v?.profiles?.[key]?.columns;
  if (Array.isArray(fromProfile) && fromProfile.length) return fromProfile;
  return v?.columns || [];
}

function updateSeamTabUi() {
  document.querySelectorAll('.seam-bit-tab').forEach(btn => {
    const b = parseInt(btn.dataset.seamBits, 10);
    btn.classList.toggle('active', b === seamActiveBits);
    btn.disabled = seamSwitching;
  });
  const hint = document.getElementById('seam-mode-hint');
  if (!hint) return;
  hint.textContent = seamSwitching
    ? `Switching panel to ${seamActiveBits}-bit…`
    : `Panel locked to ${seamActiveBits}-bit while this tab is open. Leaving Settings returns to canvas depth.`;
}

function applySeamPayload(v) {
  const cols = profileColumns(v, seamActiveBits);
  renderSeamRows(cols);
  renderSeamKnots((cols[0] && cols[0].knots) || []);
  setSeamGreyLabel(v.previewLevel);
  const grey = document.getElementById('seam-grey');
  if (grey && v.previewLevel >= 0) grey.value = String(v.previewLevel);
  bindSeamGrey();
  updateSeamTabUi();
}

async function loadSeam(opts) {
  try {
    const r = await api.get('/api/settings/seam');
    const v = r.data || r;
    if (v.calibrateBits === 8 || v.calibrateBits === 14) seamActiveBits = v.calibrateBits;
    else if (v.bits === 8 || v.bits === 14) seamActiveBits = v.bits;
    applySeamPayload(v);
    if (opts?.lock) await lockSeamMode(seamActiveBits);
  } catch (error) {
    console.error('[SETTINGS] Failed to load seam correction:', error);
  }
}

async function lockSeamMode(bits) {
  const r = await api.post('/api/settings/seam/mode', { bits });
  const v = r.data || r;
  if (v.bits === 8 || v.bits === 14) seamActiveBits = bits;
  if (v.columns) applySeamPayload({ ...v, profiles: { [String(bits)]: { columns: v.columns } } });
  else updateSeamTabUi();
}

async function releaseSeamMode() {
  try { await api.post('/api/settings/seam/mode', { bits: 0 }); }
  catch (error) { console.warn('[SETTINGS] Seam mode release failed:', error); }
}

async function selectSeamBits(bits) {
  bits = bits === 8 ? 8 : 14;
  if (seamSwitching) return;
  seamSwitching = true;
  updateSeamTabUi();
  try {
    if (document.getElementById('seam-rows')?.dataset.count)
      await applySeam(false);
    seamActiveBits = bits;
    await lockSeamMode(bits);
  } catch (error) {
    window.toast?.error('Seam', error.message || 'Failed to switch panel depth');
  } finally {
    seamSwitching = false;
    updateSeamTabUi();
  }
}

function bindSeamGrey() {
  const grey = document.getElementById('seam-grey');
  if (!grey || seamGreyBound) return;
  seamGreyBound = true;
  grey.addEventListener('input', () => {
    const n = parseInt(grey.value);
    setSeamGreyLabel(n);
    setSeamPreview(n);
  });
}

function setSeamGreyLabel(level) {
  const el = document.getElementById('seam-grey-val');
  if (!el) return;
  if (level == null || level < 0) {
    el.textContent = 'off';
    seamPreviewOn = false;
  } else {
    el.textContent = String(level);
    seamPreviewOn = true;
  }
}

async function setSeamPreview(level) {
  try {
    await api.post('/api/settings/seam/preview', { level: level < 0 ? -1 : level });
    setSeamGreyLabel(level);
  } catch (error) {
    window.toast?.error('Seam', error.message || 'Preview failed');
  }
}

function identityKnots() {
  return SEAM_KNOT_INS.map(i => ({ in: i, out: i }));
}

function renderSeamKnots(knots) {
  const host = document.getElementById('seam-knots');
  if (!host) return;
  const byIn = new Map();
  (knots || []).forEach(k => byIn.set(Number(k.in), Number(k.out)));
  const pts = SEAM_KNOT_INS.map(i => ({
    in: i,
    out: Number.isFinite(byIn.get(i)) ? Math.max(0, Math.min(255, byIn.get(i))) : i
  }));
  host.innerHTML = pts.map((p, i) => `
    <div class="seam-knot">
      <label>in ${p.in}</label>
      <input type="range" id="seam-knot-r-${i}" min="0" max="255" value="${p.out}">
      <input type="number" id="seam-knot-n-${i}" min="0" max="255" value="${p.out}">
    </div>`).join('');
  pts.forEach((_, i) => {
    const r = document.getElementById('seam-knot-r-' + i);
    const n = document.getElementById('seam-knot-n-' + i);
    const sync = (fromRange) => {
      const v = Math.max(0, Math.min(255, parseInt((fromRange ? r : n).value) || 0));
      r.value = String(v);
      n.value = String(v);
      drawSeamCurve();
      scheduleSeamCurveApply();
    };
    r.addEventListener('input', () => sync(true));
    n.addEventListener('input', () => sync(false));
  });
  drawSeamCurve();
}

function readSeamKnots() {
  return SEAM_KNOT_INS.map((inp, i) => {
    const n = parseInt(document.getElementById('seam-knot-n-' + i)?.value);
    return { in: inp, out: Number.isFinite(n) ? Math.max(0, Math.min(255, n)) : inp };
  });
}

function expandSeamKnots(knots) {
  const map = new Uint8Array(256);
  for (let i = 0; i < 256; i++) map[i] = i;
  const pts = (knots || []).map(k => ({
    in: Math.max(0, Math.min(255, Number(k.in) || 0)),
    out: Math.max(0, Math.min(255, Number(k.out) || 0))
  })).sort((a, b) => a.in - b.in);
  if (!pts.length) return map;
  if (pts[0].in !== 0) pts.unshift({ in: 0, out: pts[0].out });
  if (pts[pts.length - 1].in !== 255) pts.push({ in: 255, out: pts[pts.length - 1].out });
  for (let s = 0; s < pts.length - 1; s++) {
    const a = pts[s], b = pts[s + 1];
    const span = Math.max(1, b.in - a.in);
    for (let x = a.in; x <= b.in; x++) {
      map[x] = Math.round(a.out + (x - a.in) / span * (b.out - a.out));
    }
  }
  return map;
}

function drawSeamCurve() {
  const c = document.getElementById('seam-curve-plot');
  if (!c || !c.getContext) return;
  const ctx = c.getContext('2d');
  const w = c.width, h = c.height;
  ctx.clearRect(0, 0, w, h);
  ctx.strokeStyle = 'rgba(148,163,184,0.25)';
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(0, h - 1);
  ctx.lineTo(w, 0);
  ctx.stroke();
  const map = expandSeamKnots(readSeamKnots());
  ctx.strokeStyle = '#38bdf8';
  ctx.lineWidth = 2;
  ctx.beginPath();
  for (let i = 0; i < 256; i++) {
    const x = i / 255 * (w - 1);
    const y = h - 1 - map[i] / 255 * (h - 1);
    if (i === 0) ctx.moveTo(x, y);
    else ctx.lineTo(x, y);
  }
  ctx.stroke();
  ctx.fillStyle = '#f8fafc';
  SEAM_KNOT_INS.forEach((inp, idx) => {
    const out = parseInt(document.getElementById('seam-knot-n-' + idx)?.value) || inp;
    const x = inp / 255 * (w - 1);
    const y = h - 1 - out / 255 * (h - 1);
    ctx.beginPath();
    ctx.arc(x, y, 3.5, 0, Math.PI * 2);
    ctx.fill();
  });
}

function scheduleSeamCurveApply() {
  clearTimeout(seamCurveTimer);
  seamCurveTimer = setTimeout(() => applySeam(false), 80);
}

function resetSeamCurve() {
  renderSeamKnots(identityKnots());
  applySeam(false);
}

function resetSeamGainLift() {
  const host = document.getElementById('seam-rows');
  const n = parseInt(host?.dataset.count) || 0;
  for (let i = 0; i < n; i++) {
    const set = (id, v) => { const el = document.getElementById(id); if (el) el.value = v; };
    set('seam-gr-' + i, '1.000');
    set('seam-gg-' + i, '1.000');
    set('seam-gb-' + i, '1.000');
    set('seam-lr-' + i, '0.000');
    set('seam-lg-' + i, '0.000');
    set('seam-lb-' + i, '0.000');
  }
  applySeam(false);
}

function renderSeamRows(cols) {
  const host = document.getElementById('seam-rows');
  if (!host) return;
  if (!cols.length) {
    cols = [63, 127, 191, 255].map(x => ({
      x, gainR: 0.85, gainG: 0.85, gainB: 0.85, liftR: 0.004, liftG: 0.004, liftB: 0.004
    }));
  }
  const ch = (c, label) => `<th class="seam-ch-${c}">${label}</th>`;
  host.innerHTML = `<table class="seam-table">
    <thead><tr>
      <th>Column</th>
      ${ch('r','R gain')}${ch('g','G gain')}${ch('b','B gain')}
      ${ch('r','R lift')}${ch('g','G lift')}${ch('b','B lift')}
    </tr></thead>
    <tbody>${cols.map((c, i) => `
      <tr>
        <td><input type="number" id="seam-x-${i}" value="${c.x}" min="0" max="255"></td>
        <td><input type="number" id="seam-gr-${i}" value="${fmt3(c.gainR)}" min="0" max="4" step="0.01"></td>
        <td><input type="number" id="seam-gg-${i}" value="${fmt3(c.gainG ?? c.gainR)}" min="0" max="4" step="0.01"></td>
        <td><input type="number" id="seam-gb-${i}" value="${fmt3(c.gainB ?? c.gainR)}" min="0" max="4" step="0.01"></td>
        <td><input type="number" id="seam-lr-${i}" value="${fmt3(c.liftR)}" min="0" max="1" step="0.001"></td>
        <td><input type="number" id="seam-lg-${i}" value="${fmt3(c.liftG ?? c.liftR)}" min="0" max="1" step="0.001"></td>
        <td><input type="number" id="seam-lb-${i}" value="${fmt3(c.liftB ?? c.liftR)}" min="0" max="1" step="0.001"></td>
      </tr>`).join('')}
    </tbody></table>`;
  host.dataset.count = cols.length;
}

function fmt3(v) {
  const n = Number(v);
  return Number.isFinite(n) ? n.toFixed(3) : '0.000';
}

async function applySeam(save) {
  const host = document.getElementById('seam-rows');
  const n = parseInt(host?.dataset.count) || 0;
  const knots = readSeamKnots();
  const columns = [];
  for (let i = 0; i < n; i++) {
    const x = parseInt(document.getElementById('seam-x-' + i)?.value);
    if (isNaN(x)) continue;
    columns.push({
      x,
      gainR: parseFloat(document.getElementById('seam-gr-' + i)?.value) || 1,
      gainG: parseFloat(document.getElementById('seam-gg-' + i)?.value) || 1,
      gainB: parseFloat(document.getElementById('seam-gb-' + i)?.value) || 1,
      liftR: parseFloat(document.getElementById('seam-lr-' + i)?.value) || 0,
      liftG: parseFloat(document.getElementById('seam-lg-' + i)?.value) || 0,
      liftB: parseFloat(document.getElementById('seam-lb-' + i)?.value) || 0,
      knots
    });
  }
  try {
    await api.post('/api/settings/seam', { columns, save: !!save, bits: seamActiveBits });
    if (save) window.toast?.success('Seam', `Saved ${seamActiveBits}-bit curve`);
  } catch (error) {
    window.toast?.error('Seam', error.message || 'Failed to apply seam correction');
  }
}

window.selectOutputPanel = selectOutputPanel;
window.requestOutputSwitch = requestOutputSwitch;
window.saveHdmiSettings = saveHdmiSettings;
window.saveSpiSettings = saveSpiSettings;
window.saveHomeAssistant = saveHomeAssistant;
window.loadSeam = loadSeam;
window.applySeam = applySeam;
window.selectSeamBits = selectSeamBits;
window.setSeamPreview = setSeamPreview;
window.resetSeamCurve = resetSeamCurve;
window.resetSeamGainLift = resetSeamGainLift;
window.loadNetworkConfig = loadNetworkConfig;
window.applyNetworkConfig = applyNetworkConfig;
window.scanPanels = scanPanels;
window.identifyScannedPanel = identifyScannedPanel;
window.useScannedPanel = useScannedPanel;
window.unbindPanel = unbindPanel;
window.loadImageCorrection = loadImageCorrection;
window.onImageCorrectionInput = onImageCorrectionInput;
window.saveImageCorrection = saveImageCorrection;
window.resetImageCorrection = resetImageCorrection;
window.initSettings = initSettings;
window.loadCurrentSettings = loadCurrentSettings;
window.saveMatrixConfig = saveMatrixConfig;
window.saveAppSettings = saveAppSettings;
window.updateCalculatedResolution = updateCalculatedResolution;
window.uploadCertificate = uploadCertificate;
window.regenerateCertificate = regenerateCertificate;
window.loadCertificateInfo = loadCertificateInfo;

async function reloadPlugins() {
  const btn = document.getElementById('plugin-reload-btn');
  const status = document.getElementById('plugin-reload-status');
  if (btn) btn.disabled = true;
  if (status) status.textContent = 'Reloading…';
  try {
    const r = await api.post('/api/plugins/reload');
    const v = r.data || r;
    const ext = v.extensions || {};
    const filt = v.filters || {};
    const fail = [...(ext.failed || []), ...(filt.failed || [])];
    const msg = `${ext.available ?? 0} extensions, ${filt.available ?? 0} filters`
      + (fail.length ? ` — ${fail.length} restore failed` : '');
    if (status) status.textContent = msg;
    if (fail.length) window.toast?.error('Plugins', fail[0]);
    else window.toast?.success('Plugins', 'Reloaded — ' + msg);
    window.dispatchEvent(new CustomEvent('pluginsReloaded', { detail: v }));
  } catch (error) {
    if (status) status.textContent = error.message || 'Reload failed';
    window.toast?.error('Plugins', error.message || 'Reload failed');
  } finally {
    if (btn) btn.disabled = false;
  }
}

window.reloadPlugins = reloadPlugins;

window.addEventListener('tabChanged', (e) => {
  if (e.detail?.tab === 'settings') {
    loadCurrentSettings();
    loadImageCorrection();
    loadNetworkConfig();
    loadSeam({ lock: true });
    loadCertificateInfo();
  } else {
    if (seamPreviewOn) setSeamPreview(-1);
    releaseSeamMode();
  }
});

window.addEventListener('pagehide', () => {
  if (seamPreviewOn) setSeamPreview(-1);
  releaseSeamMode();
});
