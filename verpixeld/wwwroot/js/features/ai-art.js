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
};

// ═══════════════════════════════════════════════════════════════
// Initialization
// ═══════════════════════════════════════════════════════════════

function initAiArt() {
  // Load saved config into form
  loadAiConfig();
  loadAiHistory();
  initAiEditDropzone();

  // Ctrl+Enter to generate
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
  const genBtn = document.getElementById('ai-generate-btn');

  // Show loading
  if (statusEl) statusEl.style.display = 'flex';
  if (statusText) statusText.textContent = 'Generating image... this may take 10-30 seconds';
  if (genBtn) genBtn.disabled = true;

  try {
    const result = await api.post('/api/ai/generate', {
      prompt,
      style,
      quality,
      canvasName,
      applyToDisplay: false
    });

    aiState.lastGeneratedImage = result.imageBase64;

    // Show preview
    showAiPreview(result.imageBase64);
    
    // Refresh history
    loadAiHistory();
    
    window.toast?.success('AI Art', 'Image generated successfully!');
  } catch (err) {
    console.error('[AI] Generation error:', err);
    window.toast?.error('AI Art', err.message || 'Generation failed');
  } finally {
    if (statusEl) statusEl.style.display = 'none';
    if (genBtn) genBtn.disabled = false;
  }
}

function showAiPreview(base64Image) {
  const container = document.getElementById('ai-preview-container');
  const canvas = document.getElementById('ai-preview-canvas');
  if (!container || !canvas) return;

  const ctx = canvas.getContext('2d');
  const img = new Image();
  img.onload = () => {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
    container.style.display = '';
  };
  img.src = 'data:image/png;base64,' + base64Image;
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
  window.toast?.info('AI Art', 'Image saved to history');
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
  reader.onload = (e) => {
    aiState.editSourceImage = e.target.result; // data URL

    // Update dropzone to show thumbnail
    const content = document.getElementById('ai-edit-dropzone-content');
    if (content) {
      content.innerHTML = `
        <img src="${e.target.result}" style="max-width:120px;max-height:60px;border-radius:4px;margin-bottom:4px">
        <span class="upload-dropzone-text">${file.name} — <a href="#" onclick="event.preventDefault();event.stopPropagation();document.getElementById('ai-edit-file-input').click()">change</a></span>
      `;
    }
  };
  reader.readAsDataURL(file);
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
  const editBtn = document.getElementById('ai-edit-btn');

  if (statusEl) statusEl.style.display = 'flex';
  if (statusText) statusText.textContent = 'Stylizing image... this may take 10-30 seconds';
  if (editBtn) editBtn.disabled = true;

  try {
    const result = await api.post('/api/ai/edit', {
      imageBase64: aiState.editSourceImage,
      prompt,
      style,
      applyToDisplay: false
    });

    aiState.lastEditImage = result.imageBase64;

    // Show comparison
    showAiEditComparison(aiState.editSourceImage, result.imageBase64);
    
    loadAiHistory();
    window.toast?.success('AI Art', 'Image stylized successfully!');
  } catch (err) {
    console.error('[AI] Edit error:', err);
    window.toast?.error('AI Art', err.message || 'Stylization failed');
  } finally {
    if (statusEl) statusEl.style.display = 'none';
    if (editBtn) editBtn.disabled = false;
  }
}

function showAiEditComparison(originalDataUrl, resultBase64) {
  const container = document.getElementById('ai-edit-preview-container');
  if (!container) return;

  // Before (original)
  const beforeCanvas = document.getElementById('ai-edit-before-canvas');
  if (beforeCanvas) {
    const ctx = beforeCanvas.getContext('2d');
    const img = new Image();
    img.onload = () => {
      ctx.clearRect(0, 0, beforeCanvas.width, beforeCanvas.height);
      ctx.drawImage(img, 0, 0, beforeCanvas.width, beforeCanvas.height);
    };
    img.src = originalDataUrl;
  }

  // After (stylized)
  const afterCanvas = document.getElementById('ai-edit-after-canvas');
  if (afterCanvas) {
    const ctx = afterCanvas.getContext('2d');
    const img = new Image();
    img.onload = () => {
      ctx.clearRect(0, 0, afterCanvas.width, afterCanvas.height);
      ctx.drawImage(img, 0, 0, afterCanvas.width, afterCanvas.height);
    };
    img.src = 'data:image/png;base64,' + resultBase64;
  }

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

function saveAiEditToGallery() {
  if (!aiState.lastEditImage) return;
  window.toast?.info('AI Art', 'Image saved to history');
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
    const imgSrc = item.thumbnailBase64
      ? `data:image/png;base64,${item.thumbnailBase64}`
      : '';
    const time = new Date(item.createdAt).toLocaleString();
    const label = item.isEdit ? '✨ Edit' : item.style || 'Custom';
    
    return `
      <div class="ai-history-item" title="${escapeHtml(item.prompt)}">
        ${imgSrc ? `<img src="${imgSrc}" alt="Generated image">` : '<div style="aspect-ratio:2/1;background:var(--color-bg-tertiary)"></div>'}
        <div class="ai-history-item-info">
          <div class="ai-history-item-prompt">${escapeHtml(item.prompt || '(no prompt)')}</div>
          <div class="ai-history-item-time">${label} &middot; ${time}</div>
        </div>
        <div class="ai-history-item-actions">
          <button class="btn btn-small btn-primary" onclick="applyHistoryItem('${item.id}')">Apply</button>
        </div>
      </div>
    `;
  }).join('');
}

async function applyHistoryItem(id) {
  const item = aiState.history.find(h => h.id === id);
  if (!item || !item.thumbnailBase64) {
    window.toast?.error('AI Art', 'Image data not available');
    return;
  }
  const canvasName = document.getElementById('ai-target-canvas')?.value || 'Main';
  await applyBase64ToDisplay(item.thumbnailBase64, canvasName);
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

    // Status indicator
    const statusEl = document.getElementById('ai-config-status');
    if (statusEl) {
      statusEl.textContent = data.configured ? 'Configured' : 'Not configured';
      statusEl.style.color = data.configured ? 'var(--color-success)' : 'var(--color-danger)';
    }
  } catch {
    return;
  }
}

async function saveAiConfig() {
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
    window.toast?.success('AI Art', 'Settings saved');
    const statusEl = document.getElementById('ai-config-status');
    if (statusEl) {
      statusEl.textContent = result.configured ? 'Configured' : 'Not configured';
      statusEl.style.color = result.configured ? 'var(--color-success)' : 'var(--color-danger)';
    }
  } catch (err) {
    window.toast?.error('AI Art', err.message);
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
    window.toast?.error('AI Art', 'Add at least one prompt for scheduled generation.');
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
    const statusEl = document.getElementById('ai-schedule-status');
    if (statusEl) statusEl.textContent = enabled ? `Active — every ${intervalMinutes}min` : 'Disabled';
  } catch (err) {
    window.toast?.error('AI Art', err.message);
  }
}

// ═══════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════

async function applyBase64ToDisplay(base64Image, canvasName) {
  try {
    await api.post('/api/ai/apply', { imageBase64: base64Image, canvasName });
    window.toast?.success('AI Art', 'Image applied to display');
    const dismissBtn = document.getElementById('gallery-dismiss-overlay-btn');
    if (dismissBtn) dismissBtn.style.display = '';
  } catch (err) {
    window.toast?.error('AI Art', err.message);
  }
}

async function dismissImageOverlay() {
  try {
    await api.post('/api/ai/dismiss');
    window.toast?.success('AI Art', 'Image removed from display');
    const dismissBtn = document.getElementById('gallery-dismiss-overlay-btn');
    if (dismissBtn) dismissBtn.style.display = 'none';
  } catch (err) {
    window.toast?.error('AI Art', err.message);
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
  // Refresh history when switching to generate tab
  if (subId === 'generate') loadAiHistory();
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
    grid.innerHTML = '<div class="ai-history-empty">No saved images. Enable "Save to disk" in the Schedule tab to build your gallery.</div>';
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

    // Draw to preview canvas
    const container = document.getElementById('gallery-preview-large');
    const canvas = document.getElementById('gallery-preview-canvas');
    const info = document.getElementById('gallery-preview-info');
    if (!container || !canvas) return;

    const ctx = canvas.getContext('2d');
    const img = new Image();
    img.onload = () => {
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
      container.style.display = '';
    };
    img.src = 'data:image/png;base64,' + data.imageBase64;

    if (info) info.textContent = filename;

    // Highlight in grid
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
  // Hide dismiss button when closing preview
  const dismissBtn = document.getElementById('gallery-dismiss-overlay-btn');
  if (dismissBtn) dismissBtn.style.display = 'none';
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
window.clearAiHistory = clearAiHistory;
window.toggleAiProviderFields = toggleAiProviderFields;
window.saveAiConfig = saveAiConfig;
window.toggleAiSchedule = toggleAiSchedule;
window.saveAiSchedule = saveAiSchedule;
window.loadAiConfig = loadAiConfig;
window.switchAiSubtab = switchAiSubtab;
window.loadGallery = loadGallery;
window.previewGalleryImage = previewGalleryImage;
window.applyGalleryPreviewToDisplay = applyGalleryPreviewToDisplay;
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
