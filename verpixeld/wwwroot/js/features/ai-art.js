/**
 * AI Art Feature
 * Text-to-image, image-to-image, generation history, scheduled generation,
 * and provider configuration (Azure OpenAI / OpenAI)
 */
'use strict';

// Base URL for img src (gallery thumbs) - api client handles fetch paths
const AI_GALLERY_BASE = (window.API_BASE || '') + '/api/ai';

let aiState = {
  lastGeneratedImage: null,  // base64
  lastEditImage: null,       // base64
  editSourceImage: null,     // base64 of uploaded source
  history: [],
  gallery: [],               // { filename, createdAt, sizeKb }
  galleryPreviewFilename: null,
  galleryPreviewBase64: null,
  slideshowInterval: null,
  slideshowIndex: 0,
  displayWidth: 256,
  displayHeight: 128,
  statusPoll: null,
  overlayVisible: false,
};

const AI_PREVIEW_CANVAS_IDS = [
  'ai-preview-canvas',
  'gallery-preview-canvas',
  'ai-edit-before-canvas',
  'ai-edit-after-canvas',
];

// ═══════════════════════════════════════════════════════════════
// Initialization
// ═══════════════════════════════════════════════════════════════

function initAiArt() {
  loadAiConfig();
  loadAiHistory();
  initAiEditDropzone();

  const promptEl = document.getElementById('ai-prompt');
  if (promptEl) {
    promptEl.addEventListener('keydown', (e) => {
      if (e.ctrlKey && e.key === 'Enter') generateAiImage();
    });
  }
}

// ═══════════════════════════════════════════════════════════════
// Text-to-Image Generation
// ═══════════════════════════════════════════════════════════════

async function generateAiImage() {
  const prompt = document.getElementById('ai-prompt')?.value?.trim();
  if (!prompt) {
    window.toast?.error('AI Art', 'Please enter a prompt.');
    return;
  }

  const style = document.getElementById('ai-style')?.value || '';
  const quality = document.getElementById('ai-quality')?.value || 'medium';
  const canvasName = document.getElementById('ai-target-canvas')?.value || 'Main';

  const statusEl = document.getElementById('ai-status');
  const statusText = document.getElementById('ai-status-text');

  setAiBusy(true, 'Generating image... this may take 10-30 seconds');
  startAiStatusPoll();

  try {
    const result = await api.post('/api/ai/generate', {
      prompt,
      style,
      quality,
      canvasName,
      applyToDisplay: false
    });

    aiState.lastGeneratedImage = result.imageBase64;
    showAiPreview(result.imageBase64);
    loadAiHistory();
    window.toast?.success('AI Art', 'Image generated successfully!');
  } catch (err) {
    console.error('[AI] Generation error:', err);
    window.toast?.error('AI Art', formatAiError(err));
  } finally {
    stopAiStatusPoll();
    setAiBusy(false);
    if (statusEl) statusEl.style.display = 'none';
    if (statusText) statusText.textContent = 'Generating...';
  }
}

function drawPixelated(canvas, src) {
  if (!canvas) return;
  const ctx = canvas.getContext('2d');
  const img = new Image();
  img.onload = () => {
    ctx.imageSmoothingEnabled = false;
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
  };
  img.src = src;
}

function showAiPreview(base64Image) {
  const container = document.getElementById('ai-preview-container');
  const canvas = document.getElementById('ai-preview-canvas');
  if (!container || !canvas) return;
  drawPixelated(canvas, 'data:image/png;base64,' + base64Image);
  container.style.display = '';
}

async function applyAiImageToDisplay() {
  if (!aiState.lastGeneratedImage) {
    window.toast?.error('AI Art', 'No image to apply.');
    return;
  }
  const canvasName = document.getElementById('ai-target-canvas')?.value || 'Main';
  await applyBase64ToDisplay(aiState.lastGeneratedImage, canvasName);
}

async function saveAiImageToGallery() {
  if (!aiState.lastGeneratedImage) return;
  const prompt = document.getElementById('ai-prompt')?.value?.trim() || 'generated';
  const style = document.getElementById('ai-style')?.value || '';
  await saveBase64ToGallery(aiState.lastGeneratedImage, prompt, style);
}

// ═══════════════════════════════════════════════════════════════
// Image-to-Image (Stylize)
// ═══════════════════════════════════════════════════════════════

function initAiEditDropzone() {
  const dropzone = document.getElementById('ai-edit-dropzone');
  if (!dropzone) return;

  dropzone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropzone.classList.add('dragover');
  });
  dropzone.addEventListener('dragleave', (e) => {
    e.preventDefault();
    dropzone.classList.remove('dragover');
  });
  dropzone.addEventListener('drop', (e) => {
    e.preventDefault();
    dropzone.classList.remove('dragover');
    if (e.dataTransfer.files.length > 0) loadAiEditImage(e.dataTransfer.files[0]);
  });
  dropzone.addEventListener('click', () => {
    document.getElementById('ai-edit-file-input')?.click();
  });
}

function handleAiEditFileUpload(input) {
  if (input.files && input.files[0]) loadAiEditImage(input.files[0]);
}

function loadAiEditImage(file) {
  if (!file.type.startsWith('image/')) {
    window.toast?.error('AI Art', 'Please select an image file.');
    return;
  }

  const reader = new FileReader();
  reader.onload = (e) => setAiEditSource(e.target.result, file.name);
  reader.readAsDataURL(file);
}

function setAiEditSource(dataUrl, label) {
  aiState.editSourceImage = dataUrl;
  const content = document.getElementById('ai-edit-dropzone-content');
  if (content) {
    content.innerHTML = `
      <img src="${dataUrl}" style="max-width:120px;max-height:60px;border-radius:4px;margin-bottom:4px;image-rendering:pixelated">
      <span class="upload-dropzone-text">${escapeHtml(label || 'Image')} — <a href="#" onclick="event.preventDefault();event.stopPropagation();document.getElementById('ai-edit-file-input').click()">change</a></span>
    `;
  }
}

async function generateAiEdit() {
  if (!aiState.editSourceImage) {
    window.toast?.error('AI Art', 'Please upload an image first.');
    return;
  }

  const prompt = document.getElementById('ai-edit-prompt')?.value?.trim() || '';
  const style = document.getElementById('ai-edit-style')?.value || 'pixel-art';

  const statusEl = document.getElementById('ai-edit-status');
  const statusText = document.getElementById('ai-edit-status-text');

  setAiBusy(true, 'Stylizing image... this may take 10-30 seconds');
  if (statusEl) statusEl.style.display = 'flex';
  if (statusText) statusText.textContent = 'Stylizing image... this may take 10-30 seconds';
  startAiStatusPoll();

  try {
    const result = await api.post('/api/ai/edit', {
      imageBase64: aiState.editSourceImage,
      prompt,
      style,
      applyToDisplay: false
    });

    aiState.lastEditImage = result.imageBase64;
    showAiEditComparison(aiState.editSourceImage, result.imageBase64);
    loadAiHistory();
    window.toast?.success('AI Art', 'Image stylized successfully!');
  } catch (err) {
    console.error('[AI] Edit error:', err);
    window.toast?.error('AI Art', formatAiError(err));
  } finally {
    stopAiStatusPoll();
    setAiBusy(false);
    if (statusEl) statusEl.style.display = 'none';
  }
}

function showAiEditComparison(originalDataUrl, resultBase64) {
  const container = document.getElementById('ai-edit-preview-container');
  if (!container) return;
  drawPixelated(document.getElementById('ai-edit-before-canvas'), originalDataUrl);
  drawPixelated(document.getElementById('ai-edit-after-canvas'), 'data:image/png;base64,' + resultBase64);
  container.style.display = '';
}

async function applyAiEditToDisplay() {
  if (!aiState.lastEditImage) {
    window.toast?.error('AI Art', 'No stylized image to apply.');
    return;
  }
  const canvasName = document.getElementById('ai-target-canvas')?.value || 'Main';
  await applyBase64ToDisplay(aiState.lastEditImage, canvasName);
}

async function saveAiEditToGallery() {
  if (!aiState.lastEditImage) return;
  const prompt = document.getElementById('ai-edit-prompt')?.value?.trim() || 'stylized';
  const style = document.getElementById('ai-edit-style')?.value || '';
  await saveBase64ToGallery(aiState.lastEditImage, prompt, style);
}

// ═══════════════════════════════════════════════════════════════
// History
// ═══════════════════════════════════════════════════════════════

async function loadAiHistory() {
  try {
    const result = await api.get('/api/ai/history');
    aiState.history = result.history || [];
    renderAiHistory();
  } catch (err) {
    console.error('[AI] Failed to load history:', err);
  }
}

function renderAiHistory() {
  const container = document.getElementById('ai-history-list');
  if (!container) return;

  if (aiState.history.length === 0) {
    container.innerHTML = '<div class="ai-history-empty">No images generated yet.</div>';
    return;
  }

  container.innerHTML = aiState.history.map(item => {
    const time = new Date(item.createdAt).toLocaleString();
    const label = item.isEdit ? '✨ Edit' : item.style || 'Custom';
    const thumb = `${AI_GALLERY_BASE}/history/${encodeURIComponent(item.id)}/thumb`;

    return `
      <div class="ai-history-item" title="${escapeHtml(item.prompt)}">
        <img src="${thumb}" alt="Generated image" onerror="this.style.display='none'">
        <div class="ai-history-item-info">
          <div class="ai-history-item-prompt">${escapeHtml(item.prompt || '(no prompt)')}</div>
          <div class="ai-history-item-time">${label} &middot; ${time}</div>
        </div>
        <div class="ai-history-item-actions">
          <button class="btn btn-small btn-primary" onclick="applyHistoryItem('${item.id}')">Apply</button>
          <button class="btn btn-small btn-warning ai-dismiss-overlay-btn" style="display:${aiState.overlayVisible ? '' : 'none'}" onclick="dismissImageOverlay()">Dismiss</button>
          <button class="btn btn-small btn-secondary" onclick="stylizeHistoryItem('${item.id}')">Stylize</button>
        </div>
      </div>
    `;
  }).join('');
}

async function fetchHistoryImage(id) {
  const data = await api.get(`/api/ai/history/${encodeURIComponent(id)}`);
  return data.imageBase64;
}

async function applyHistoryItem(id) {
  try {
    const imageBase64 = await fetchHistoryImage(id);
    const canvasName = document.getElementById('ai-target-canvas')?.value || 'Main';
    await applyBase64ToDisplay(imageBase64, canvasName);
  } catch (err) {
    window.toast?.error('AI Art', formatAiError(err));
  }
}

async function stylizeHistoryItem(id) {
  try {
    const item = aiState.history.find(h => h.id === id);
    const imageBase64 = await fetchHistoryImage(id);
    setAiEditSource('data:image/png;base64,' + imageBase64, item?.prompt || 'History');
    switchAiSubtab('stylize');
  } catch (err) {
    window.toast?.error('AI Art', formatAiError(err));
  }
}

async function clearAiHistory() {
  try {
    await api.del('/api/ai/history');
    aiState.history = [];
    renderAiHistory();
    window.toast?.success('AI Art', 'History cleared');
  } catch (err) {
    window.toast?.error('AI Art', err.message);
  }
}

// ═══════════════════════════════════════════════════════════════
// Provider Settings
// ═══════════════════════════════════════════════════════════════

function toggleAiProviderFields() {
  const provider = document.getElementById('ai-provider')?.value || 'azure';
  const isAzure = provider === 'azure';

  // Connection fields
  const azureFields = document.getElementById('ai-azure-fields');
  const openaiFields = document.getElementById('ai-openai-fields');
  if (azureFields) azureFields.style.display = isAzure ? '' : 'none';
  if (openaiFields) openaiFields.style.display = isAzure ? 'none' : '';

  // Image model fields
  const azureImageFields = document.getElementById('ai-azure-image-fields');
  const openaiImageFields = document.getElementById('ai-openai-image-fields');
  if (azureImageFields) azureImageFields.style.display = isAzure ? '' : 'none';
  if (openaiImageFields) openaiImageFields.style.display = isAzure ? 'none' : '';
}

async function loadAiConfig() {
  try {
    const data = await api.get('/api/ai/status');

    // Provider
    const providerEl = document.getElementById('ai-provider');
    if (providerEl) providerEl.value = data.provider || 'azure';
    toggleAiProviderFields();

    // Azure fields
    if (data.azureEndpoint) document.getElementById('ai-azure-endpoint').value = data.azureEndpoint;
    if (data.azureDeployment) document.getElementById('ai-azure-deployment').value = data.azureDeployment;
    if (data.azureChatDeployment) document.getElementById('ai-azure-chat-deployment').value = data.azureChatDeployment;
    if (data.azureApiVersion) document.getElementById('ai-azure-api-version').value = data.azureApiVersion;
    if (data.azureKeySet) document.getElementById('ai-azure-key').placeholder = '••••••• (saved)';

    // OpenAI fields
    if (data.openAiKeySet) document.getElementById('ai-openai-key').placeholder = '••••••• (saved)';
    if (data.openAiModel) document.getElementById('ai-openai-model').value = data.openAiModel;

    // Schedule
    const schedEnabledEl = document.getElementById('ai-schedule-enabled');
    if (schedEnabledEl) schedEnabledEl.checked = data.scheduleEnabled;
    
    const schedIntervalEl = document.getElementById('ai-schedule-interval');
    if (schedIntervalEl) schedIntervalEl.value = data.scheduleIntervalMinutes || 60;
    
    const schedStyleEl = document.getElementById('ai-schedule-style');
    if (schedStyleEl) schedStyleEl.value = data.scheduleStyle || 'pixel-art';
    
    const schedCanvasEl = document.getElementById('ai-schedule-canvas');
    if (schedCanvasEl) schedCanvasEl.value = data.scheduleCanvasName || 'Main';

    const schedSaveEl = document.getElementById('ai-schedule-save');
    if (schedSaveEl) schedSaveEl.checked = data.scheduleSaveToDisk ?? true;
    
    const schedPromptsEl = document.getElementById('ai-schedule-prompts');
    if (schedPromptsEl && data.schedulePrompts) {
      schedPromptsEl.value = data.schedulePrompts.join('\n');
    }

    const statusEl = document.getElementById('ai-config-status');
    if (statusEl) {
      statusEl.textContent = data.configured ? 'Configured' : 'Not configured';
      statusEl.style.color = data.configured ? 'var(--color-success)' : 'var(--color-danger)';
    }

    applyAiPreviewSize(data.displayWidth, data.displayHeight);
    applyAiScheduleMeta(data);
    setAiDismissButtonsVisible(!!data.overlayVisible);
    if (data.generating) {
      setAiBusy(true, 'A generation is already in progress...');
      startAiStatusPoll();
    }
  } catch {
    return;
  }
}

async function saveAiConfig(opts = {}) {
  const provider = document.getElementById('ai-provider')?.value || 'azure';

  const payload = { provider };

  if (provider === 'azure') {
    payload.azureEndpoint = document.getElementById('ai-azure-endpoint')?.value || '';
    const azureKey = document.getElementById('ai-azure-key')?.value;
    if (azureKey) payload.azureApiKey = azureKey; // Only send if changed
    payload.azureDeployment = document.getElementById('ai-azure-deployment')?.value || '';
    payload.azureApiVersion = document.getElementById('ai-azure-api-version')?.value || '2025-04-01-preview';
  } else {
    const openaiKey = document.getElementById('ai-openai-key')?.value;
    if (openaiKey) payload.openAiApiKey = openaiKey;
    payload.openAiModel = document.getElementById('ai-openai-model')?.value || 'dall-e-3';
  }

  // Chat deployment is always Azure (even if image provider is OpenAI)
  payload.azureChatDeployment = document.getElementById('ai-azure-chat-deployment')?.value || '';

  try {
    const result = await api.post('/api/ai/configure', payload);
    const statusEl = document.getElementById('ai-config-status');
    if (statusEl) {
      statusEl.textContent = result.configured ? 'Configured' : 'Not configured';
      statusEl.style.color = result.configured ? 'var(--color-success)' : 'var(--color-danger)';
    }
    if (!opts.silent) window.toast?.success('AI Art', 'Settings saved');
    return result;
  } catch (err) {
    if (!opts.silent) window.toast?.error('AI Art', err.message);
    throw err;
  }
}

async function saveAllAiSettings() {
  try {
    await saveAiConfig({ silent: true });
    if (typeof saveVoiceConfig === 'function')
      await saveVoiceConfig({ silent: true });
    window.toast?.success('AI Art', 'All settings saved');
  } catch (err) {
    window.toast?.error('AI Art', err.message || 'Save failed');
  }
}

// ═══════════════════════════════════════════════════════════════
// Schedule
// ═══════════════════════════════════════════════════════════════

function toggleAiSchedule() {
  const enabled = document.getElementById('ai-schedule-enabled')?.checked;
  const statusEl = document.getElementById('ai-schedule-status');
  if (statusEl) {
    statusEl.textContent = enabled ? 'Will save on next Save' : '';
  }
}

async function saveAiSchedule() {
  const enabled = document.getElementById('ai-schedule-enabled')?.checked || false;
  const intervalMinutes = parseInt(document.getElementById('ai-schedule-interval')?.value || '60', 10);
  const style = document.getElementById('ai-schedule-style')?.value || 'pixel-art';
  const canvasName = document.getElementById('ai-schedule-canvas')?.value || 'Main';
  const saveToDisk = document.getElementById('ai-schedule-save')?.checked ?? true;
  const promptsRaw = document.getElementById('ai-schedule-prompts')?.value || '';
  const prompts = promptsRaw.split('\n').map(l => l.trim()).filter(l => l.length > 0);

  if (enabled && prompts.length === 0) {
    window.toast?.error('AI Art', 'Add at least one prompt for auto-generate.');
    return;
  }

  try {
    const result = await api.post('/api/ai/schedule', {
      enabled,
      intervalMinutes,
      style,
      canvasName,
      saveToDisk,
      prompts
    });
    window.toast?.success('AI Art', result.message);
    try {
      const status = await api.get('/api/ai/status');
      applyAiScheduleMeta(status);
    } catch { /* keep local label */ }
    const statusEl = document.getElementById('ai-schedule-status');
    if (statusEl) statusEl.textContent = enabled ? `Active — every ${intervalMinutes}min` : 'Disabled';
  } catch (err) {
    window.toast?.error('AI Art', formatAiError(err));
  }
}

async function runAiScheduleNow() {
  const runBtn = document.getElementById('ai-schedule-run-btn');
  if (runBtn) runBtn.disabled = true;
  setAiBusy(true, 'Auto-generating...');
  startAiStatusPoll();
  try {
    const result = await api.post('/api/ai/schedule/run');
    applyAiScheduleMeta(result);
    if (result.imageBase64) {
      aiState.lastGeneratedImage = result.imageBase64;
      showAiPreview(result.imageBase64);
    }
    loadAiHistory();
    loadGallery();
    window.toast?.success('AI Art', result.record?.prompt
      ? `Generated: ${result.record.prompt}`
      : 'Auto-generate finished');
  } catch (err) {
    window.toast?.error('AI Art', formatAiError(err));
    try { applyAiScheduleMeta(await api.get('/api/ai/status')); } catch { /* ignore */ }
  } finally {
    stopAiStatusPoll();
    setAiBusy(false);
    if (runBtn) runBtn.disabled = false;
  }
}

// ═══════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════

async function applyBase64ToDisplay(base64Image, canvasName) {
  try {
    await api.post('/api/ai/apply', { imageBase64: base64Image, canvasName });
    window.toast?.success('AI Art', `Image applied to ${canvasName}`);
    setAiDismissButtonsVisible(true);
  } catch (err) {
    window.toast?.error('AI Art', formatAiError(err));
  }
}

async function saveBase64ToGallery(imageBase64, prompt, style, force = false) {
  try {
    const result = await api.post('/api/ai/gallery', { imageBase64, prompt, style, force });
    if (result.alreadyExists && !force) {
      const again = window.confirm(
        `This image is already in the gallery as ${result.filename}.\nSave another copy?`
      );
      if (again) return saveBase64ToGallery(imageBase64, prompt, style, true);
      return;
    }
    window.toast?.success('Gallery', result.filename ? `Saved ${result.filename}` : 'Saved to gallery');
    loadGallery();
  } catch (err) {
    window.toast?.error('Gallery', formatAiError(err));
  }
}

async function dismissImageOverlay() {
  try {
    await api.post('/api/ai/dismiss');
    window.toast?.success('AI Art', 'Image removed from display');
    setAiDismissButtonsVisible(false);
  } catch (err) {
    window.toast?.error('AI Art', formatAiError(err));
  }
}

function setAiDismissButtonsVisible(visible) {
  aiState.overlayVisible = !!visible;
  document.querySelectorAll('.ai-dismiss-overlay-btn').forEach(btn => {
    btn.style.display = visible ? '' : 'none';
  });
}

function formatAiError(err) {
  const msg = err?.message || 'Request failed';
  if (err?.status === 429) return 'Rate limited. Wait a minute and try again.';
  return msg;
}

function applyAiPreviewSize(width, height) {
  const w = Math.max(1, width || aiState.displayWidth || 256);
  const h = Math.max(1, height || aiState.displayHeight || 128);
  aiState.displayWidth = w;
  aiState.displayHeight = h;
  for (const id of AI_PREVIEW_CANVAS_IDS) {
    const canvas = document.getElementById(id);
    if (!canvas) continue;
    if (canvas.width !== w) canvas.width = w;
    if (canvas.height !== h) canvas.height = h;
  }
  const sizeEl = document.getElementById('ai-preview-size');
  if (sizeEl) sizeEl.textContent = `${w}\u00D7${h}`;
}

function setAiBusy(busy, text) {
  const genBtn = document.getElementById('ai-generate-btn');
  const editBtn = document.getElementById('ai-edit-btn');
  const runBtn = document.getElementById('ai-schedule-run-btn');
  if (genBtn) genBtn.disabled = !!busy;
  if (editBtn) editBtn.disabled = !!busy;
  if (runBtn) runBtn.disabled = !!busy;

  const statusEl = document.getElementById('ai-status');
  const statusText = document.getElementById('ai-status-text');
  if (busy) {
    if (statusEl) statusEl.style.display = 'flex';
    if (statusText && text) statusText.textContent = text;
  } else if (statusEl) {
    statusEl.style.display = 'none';
  }
}

function startAiStatusPoll() {
  stopAiStatusPoll();
  aiState.statusPoll = setInterval(async () => {
    try {
      const data = await api.get('/api/ai/status');
      applyAiScheduleMeta(data);
      if (!data.generating) {
        stopAiStatusPoll();
      }
    } catch { /* ignore poll errors */ }
  }, 2000);
}

function stopAiStatusPoll() {
  if (aiState.statusPoll) {
    clearInterval(aiState.statusPoll);
    aiState.statusPoll = null;
  }
}

function applyAiScheduleMeta(data) {
  if (!data) return;
  const lastEl = document.getElementById('ai-schedule-last-run');
  const nextEl = document.getElementById('ai-schedule-next-run');
  const errEl = document.getElementById('ai-schedule-last-error');
  const statusEl = document.getElementById('ai-schedule-status');

  if (lastEl) {
    const when = data.scheduleLastRunUtc ? new Date(data.scheduleLastRunUtc).toLocaleString() : '—';
    const prompt = data.scheduleLastPrompt ? ` — ${data.scheduleLastPrompt}` : '';
    lastEl.textContent = `Last run: ${when}${prompt}`;
  }
  if (nextEl) {
    nextEl.textContent = data.scheduleEnabled && data.scheduleNextRunUtc
      ? `Next run: ${new Date(data.scheduleNextRunUtc).toLocaleString()}`
      : 'Next run: —';
  }
  if (errEl) {
    errEl.textContent = data.scheduleLastError
      ? data.scheduleLastError
      : (data.scheduleLastSkip || '');
    errEl.style.color = data.scheduleLastError
      ? 'var(--color-danger)'
      : 'var(--color-text-muted)';
  }
  if (statusEl && data.scheduleEnabled != null) {
    statusEl.textContent = data.scheduleEnabled
      ? `Active — every ${data.scheduleIntervalMinutes || 60}min`
      : 'Disabled';
  }
}

function escapeHtml(str) {
  const div = document.createElement('div');
  div.textContent = str;
  return div.innerHTML;
}

// ═══════════════════════════════════════════════════════════════
// Subtab Navigation
// ═══════════════════════════════════════════════════════════════

function switchAiSubtab(subId) {
  // Update buttons
  document.querySelectorAll('.ai-subtab-btn').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.aisub === subId);
  });
  // Update panels
  document.querySelectorAll('.ai-subtab-panel').forEach(panel => {
    panel.classList.toggle('active', panel.id === `ai-sub-${subId}`);
  });
  // Load gallery when switching to it
  if (subId === 'gallery') loadGallery();
  if (subId === 'generate') loadAiHistory();
  if (subId === 'schedule') {
    api.get('/api/ai/status').then(applyAiScheduleMeta).catch(() => {});
  }
  if (subId === 'settings') {
    loadAiConfig();
    if (typeof loadVoiceConfig === 'function') loadVoiceConfig();
  }
}

// ═══════════════════════════════════════════════════════════════
// Gallery
// ═══════════════════════════════════════════════════════════════

async function loadGallery() {
  try {
    const data = await api.get('/api/ai/gallery');
    aiState.gallery = data.images || [];
    const countEl = document.getElementById('gallery-count');
    if (countEl) countEl.textContent = `(${data.count || 0})`;
    renderGallery();
  } catch (err) {
    console.error('[AI] Failed to load gallery:', err);
  }
}

function renderGallery() {
  const grid = document.getElementById('gallery-grid');
  if (!grid) return;

  if (aiState.gallery.length === 0) {
    grid.innerHTML = '<div class="ai-history-empty">No saved images. Use Save to Gallery after generating, or enable Save to disk in Auto-generate.</div>';
    return;
  }

  grid.innerHTML = aiState.gallery.map(item => {
    const date = new Date(item.createdAt).toLocaleString();
    // Parse filename for display: timestamp_style_prompt.png
    const parts = item.filename.replace('.png', '').split('_');
    const displayName = parts.length > 2 ? parts.slice(2).join(' ') : item.filename;
    
    return `
      <div class="gallery-item" onclick="previewGalleryImage('${escapeHtml(item.filename)}')" title="${escapeHtml(item.filename)}">
        <img src="${AI_GALLERY_BASE}/gallery/${encodeURIComponent(item.filename)}/thumb" 
             onerror="this.style.display='none'"
             loading="lazy">
        <div class="gallery-item-info">
          <div class="gallery-item-name">${escapeHtml(displayName)}</div>
          <div class="gallery-item-meta">${date} &middot; ${item.sizeKb}KB</div>
        </div>
      </div>
    `;
  }).join('');
}

async function previewGalleryImage(filename) {
  try {
    const data = await api.get(`/api/ai/gallery/${encodeURIComponent(filename)}`);

    aiState.galleryPreviewFilename = filename;
    aiState.galleryPreviewBase64 = data.imageBase64;

    const container = document.getElementById('gallery-preview-large');
    const canvas = document.getElementById('gallery-preview-canvas');
    const info = document.getElementById('gallery-preview-info');
    if (!container || !canvas) return;

    drawPixelated(canvas, 'data:image/png;base64,' + data.imageBase64);
    container.style.display = '';

    if (info) info.textContent = filename;

    document.querySelectorAll('.gallery-item').forEach(el => el.classList.remove('selected'));
  } catch {
    return;
  }
}

async function applyGalleryPreviewToDisplay() {
  if (!aiState.galleryPreviewBase64) return;
  const canvasName = document.getElementById('gallery-slideshow-canvas')?.value || 'Main';
  await applyBase64ToDisplay(aiState.galleryPreviewBase64, canvasName);
}

function stylizeGalleryPreview() {
  if (!aiState.galleryPreviewBase64) return;
  setAiEditSource(
    'data:image/png;base64,' + aiState.galleryPreviewBase64,
    aiState.galleryPreviewFilename || 'Gallery'
  );
  switchAiSubtab('stylize');
}

async function deleteGalleryPreviewImage() {
  if (!aiState.galleryPreviewFilename) return;
  try {
    await api.del(`/api/ai/gallery/${encodeURIComponent(aiState.galleryPreviewFilename)}`);
    closeGalleryPreview();
    loadGallery();
    window.toast?.success('Gallery', 'Image deleted');
  } catch (err) {
    window.toast?.error('Gallery', err.message);
  }
}

function closeGalleryPreview() {
  const container = document.getElementById('gallery-preview-large');
  if (container) container.style.display = 'none';
  aiState.galleryPreviewFilename = null;
  aiState.galleryPreviewBase64 = null;
}

// ═══════════════════════════════════════════════════════════════
// Slideshow
// ═══════════════════════════════════════════════════════════════

function startGallerySlideshow() {
  stopGallerySlideshow();

  if (aiState.gallery.length === 0) {
    window.toast?.error('Slideshow', 'No images in gallery.');
    return;
  }

  const intervalSec = parseInt(document.getElementById('gallery-slideshow-interval')?.value || '10', 10);
  const order = document.getElementById('gallery-slideshow-order')?.value || 'shuffle';

  // Build playlist
  let playlist = [...aiState.gallery];
  if (order === 'shuffle') {
    for (let i = playlist.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [playlist[i], playlist[j]] = [playlist[j], playlist[i]];
    }
  }

  aiState.slideshowIndex = 0;

  const statusEl = document.getElementById('gallery-slideshow-status');
  document.getElementById('gallery-slideshow-start').style.display = 'none';
  document.getElementById('gallery-slideshow-stop').style.display = '';

  async function showNext() {
    if (aiState.slideshowIndex >= playlist.length) {
      // Loop: reshuffle if needed
      if (order === 'shuffle') {
        for (let i = playlist.length - 1; i > 0; i--) {
          const j = Math.floor(Math.random() * (i + 1));
          [playlist[i], playlist[j]] = [playlist[j], playlist[i]];
        }
      }
      aiState.slideshowIndex = 0;
    }

    const item = playlist[aiState.slideshowIndex];
    aiState.slideshowIndex++;

    if (statusEl) {
      statusEl.textContent = `Playing ${aiState.slideshowIndex}/${playlist.length} — ${item.filename}`;
      statusEl.classList.add('active');
    }

    try {
      const data = await api.get(`/api/ai/gallery/${encodeURIComponent(item.filename)}`);
      if (data.imageBase64) {
        const canvasName = document.getElementById('gallery-slideshow-canvas')?.value || 'Main';
        await applyBase64ToDisplay(data.imageBase64, canvasName);
      }
    } catch {
      // Silent failure - continue to next image
    }
  }

  // Show first image immediately
  showNext();

  // Then cycle
  aiState.slideshowInterval = setInterval(showNext, intervalSec * 1000);
  window.toast?.success('Slideshow', `Started — ${playlist.length} images, every ${intervalSec}s`);
}

function stopGallerySlideshow() {
  if (aiState.slideshowInterval) {
    clearInterval(aiState.slideshowInterval);
    aiState.slideshowInterval = null;
  }

  document.getElementById('gallery-slideshow-start') && (document.getElementById('gallery-slideshow-start').style.display = '');
  document.getElementById('gallery-slideshow-stop') && (document.getElementById('gallery-slideshow-stop').style.display = 'none');

  const statusEl = document.getElementById('gallery-slideshow-status');
  if (statusEl) {
    statusEl.textContent = 'Stopped';
    statusEl.classList.remove('active');
  }

  // Dismiss the image overlay when stopping the slideshow
  dismissImageOverlay();
}

// ═══════════════════════════════════════════════════════════════
// Expose globally
// ═══════════════════════════════════════════════════════════════

window.generateAiImage = generateAiImage;
window.applyAiImageToDisplay = applyAiImageToDisplay;
window.saveAiImageToGallery = saveAiImageToGallery;
window.generateAiEdit = generateAiEdit;
window.handleAiEditFileUpload = handleAiEditFileUpload;
window.applyAiEditToDisplay = applyAiEditToDisplay;
window.saveAiEditToGallery = saveAiEditToGallery;
window.applyHistoryItem = applyHistoryItem;
window.stylizeHistoryItem = stylizeHistoryItem;
window.clearAiHistory = clearAiHistory;
window.toggleAiProviderFields = toggleAiProviderFields;
window.saveAiConfig = saveAiConfig;
window.saveAllAiSettings = saveAllAiSettings;
window.toggleAiSchedule = toggleAiSchedule;
window.saveAiSchedule = saveAiSchedule;
window.runAiScheduleNow = runAiScheduleNow;
window.loadAiConfig = loadAiConfig;
window.switchAiSubtab = switchAiSubtab;
window.loadGallery = loadGallery;
window.previewGalleryImage = previewGalleryImage;
window.applyGalleryPreviewToDisplay = applyGalleryPreviewToDisplay;
window.stylizeGalleryPreview = stylizeGalleryPreview;
window.deleteGalleryPreviewImage = deleteGalleryPreviewImage;
window.closeGalleryPreview = closeGalleryPreview;
window.startGallerySlideshow = startGallerySlideshow;
window.stopGallerySlideshow = stopGallerySlideshow;
window.dismissImageOverlay = dismissImageOverlay;

// Initialize
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initAiArt);
} else {
  initAiArt();
}
