/**
 * Voice Commands Feature
 * Wake word detection + Speech-to-Text → AI image generation
 * Also handles Local USB Camera management.
 */
'use strict';

let voiceState = {
  pollInterval: null,
  triggerInProgress: false,
  lastKnownCommandCount: -1,  // Track voice command count to detect new generations
};

// ═══════════════════════════════════════════════════════════════
// Initialization
// ═══════════════════════════════════════════════════════════════

function initVoiceCommands() {
  loadVoiceConfig();
  loadLocalCamConfig();
  refreshLocalCamDevices();
  startVoiceStatusPolling();

  // Live-update effect while streaming (no need to restart)
  const effectSelect = document.getElementById('localcam-effect');
  if (effectSelect) {
    effectSelect.addEventListener('change', async () => {
      try {
        await api.post('/api/localcam/configure', { activeEffect: effectSelect.value });
      } catch { /* silent */ }
    });
  }
}

// ═══════════════════════════════════════════════════════════════
// Voice Status Polling
// ═══════════════════════════════════════════════════════════════

function startVoiceStatusPolling() {
  if (voiceState.pollInterval) return;
  voiceState.pollInterval = setInterval(pollVoiceStatus, 3000);
  pollVoiceStatus(); // Initial poll
}

async function pollVoiceStatus() {
  try {
    const data = await api.get('/api/voice/status');
    updateVoiceUI(data);

    // Detect new voice-triggered generations and auto-refresh AI history
    if (typeof data.commandCount === 'number') {
      if (voiceState.lastKnownCommandCount >= 0 && data.commandCount > voiceState.lastKnownCommandCount) {
        console.log('[VOICE] New command detected, refreshing AI history');
        if (typeof loadAiHistory === 'function') loadAiHistory();
      }
      voiceState.lastKnownCommandCount = data.commandCount;
    }
  } catch { /* silent */ }

  // Sync local camera button state (camera may be started/stopped via voice)
  try {
    const camData = await api.get('/api/localcam/status');
    updateLocalCamButtons(camData.streaming);
  } catch { return; }
}

function updateVoiceUI(data) {
  // State indicator
  const dot = document.querySelector('#voice-state-indicator .voice-state-dot');
  const stateText = document.getElementById('voice-state-text');
  if (dot && stateText) {
    dot.className = 'voice-state-dot ' + data.state;
    const stateLabels = {
      disabled: 'Disabled',
      idle: 'Listening for wake word…',
      waitingforkeyword: 'Waiting (no keyword model)',
      listening: '🎤 Listening…',
      processing: '🧠 Thinking…',
      generating: '🎨 Generating…',
      speaking: '🔊 Speaking…',
      displaying: '🖼️ Displaying',
      error: '❌ Error',
    };
    stateText.textContent = stateLabels[data.state] || data.state;
  }

  // Start/stop buttons
  const startBtn = document.getElementById('voice-start-btn');
  const stopBtn = document.getElementById('voice-stop-btn');
  if (startBtn && stopBtn) {
    if (data.enabled && data.state !== 'disabled') {
      startBtn.style.display = 'none';
      stopBtn.style.display = '';
    } else {
      startBtn.style.display = '';
      stopBtn.style.display = 'none';
    }
  }

  // Stats
  const countEl = document.getElementById('voice-command-count');
  if (countEl) countEl.textContent = `Commands: ${data.commandCount}`;

  const lastEl = document.getElementById('voice-last-command');
  if (lastEl) {
    lastEl.textContent = data.lastCommandTime
      ? `Last: ${new Date(data.lastCommandTime).toLocaleTimeString()}`
      : 'Last: --';
  }

  // Transcription
  const transEl = document.getElementById('voice-last-transcription');
  const transText = document.getElementById('voice-transcription-text');
  if (transEl && transText) {
    if (data.lastTranscription) {
      transEl.style.display = '';
      transText.textContent = `"${data.lastTranscription}"`;
    } else {
      transEl.style.display = 'none';
    }
  }

  // Intent + Response
  const intentEl = document.getElementById('voice-last-intent');
  if (intentEl) {
    if (data.lastIntent) {
      intentEl.style.display = '';
      intentEl.textContent = `Intent: ${data.lastIntent}`;
      if (data.lastResponse) intentEl.textContent += ` — "${data.lastResponse}"`;
    } else {
      intentEl.style.display = 'none';
    }
  }

  // Error
  const errorEl = document.getElementById('voice-error');
  if (errorEl) {
    if (data.lastError && data.state === 'error') {
      errorEl.style.display = '';
      errorEl.textContent = data.lastError;
    } else {
      errorEl.style.display = 'none';
    }
  }

  // SDK status
  if (!data.sdkAvailable) {
    const stateText2 = document.getElementById('voice-state-text');
    if (stateText2 && data.state === 'disabled') {
      stateText2.textContent = '⚠️ Speech SDK not available — publish with: dotnet publish -r linux-arm64';
    }
  }

  // Keyword status
  const kwStatus = document.getElementById('voice-keyword-status');
  if (kwStatus) {
    kwStatus.textContent = data.hasKeywordModel
      ? `✅ Model loaded (${data.keywordModelPath?.split('/').pop() || 'keyword_model.table'})`
      : 'No model loaded';
  }
}

// ═══════════════════════════════════════════════════════════════
// Voice Control
// ═══════════════════════════════════════════════════════════════

async function startVoiceListening() {
  try {
    await api.post('/api/voice/start');
    window.toast?.success('Voice', 'Started listening');
    pollVoiceStatus();
  } catch (err) {
    window.toast?.error('Voice', err.message);
  }
}

async function stopVoiceListening() {
  try {
    await api.post('/api/voice/stop');
    window.toast?.success('Voice', 'Stopped');
    pollVoiceStatus();
  } catch (err) {
    window.toast?.error('Voice', err.message);
  }
}

async function manualVoiceTrigger() {
  if (voiceState.triggerInProgress) return;
  voiceState.triggerInProgress = true;

  const btn = document.getElementById('voice-trigger-btn');
  if (btn) {
    btn.disabled = true;
    btn.textContent = '🎤 Listening...';
  }

  try {
    const data = await api.post('/api/voice/trigger');
    if (data.transcription) {
      window.toast?.success('Voice', `Heard: "${data.transcription}"`);
    } else {
      window.toast?.error('Voice', data.error || 'No speech detected');
    }
  } catch (err) {
    window.toast?.error('Voice', err.message);
  } finally {
    voiceState.triggerInProgress = false;
    if (btn) {
      btn.disabled = false;
      btn.textContent = '🎤 Push to Talk';
    }
    pollVoiceStatus();
  }
}

// ═══════════════════════════════════════════════════════════════
// Voice Configuration
// ═══════════════════════════════════════════════════════════════

async function loadVoiceConfig() {
  try {
    const data = await api.get('/api/voice/status');
    const setVal = (id, val) => {
      const el = document.getElementById(id);
      if (el && val != null) el.value = val;
    };
    const setChecked = (id, val) => {
      const el = document.getElementById(id);
      if (el) el.checked = !!val;
    };

    const speechKeyEl = document.getElementById('voice-speech-key');
    if (speechKeyEl) {
      if (data.speechKeyShared)
        speechKeyEl.placeholder = 'Same as Azure OpenAI key (Foundry)';
      else if (data.speechKeySet)
        speechKeyEl.placeholder = '••••••• (saved)';
    }

    setVal('voice-speech-region', data.speechRegion);
    setVal('voice-speech-language', data.speechLanguage || 'de-DE');
    setVal('voice-default-style', data.defaultStyle);
    setVal('voice-display-duration', data.displayDurationSeconds);
    setVal('voice-silence-timeout', data.silenceTimeoutMs);
    setVal('voice-segmentation', data.segmentationStrategy);
    setVal('voice-profanity', data.profanityFilter);
    setVal('voice-tts-enabled', data.ttsEnabled != null ? String(data.ttsEnabled) : 'true');
    setVal('voice-tts-voice', data.ttsVoiceName);
    setVal('voice-tts-ducking', data.ttsDuckingEnabled != null ? String(data.ttsDuckingEnabled) : 'true');
    setVal('voice-duck-volume', data.ttsDuckVolumePercent != null ? String(data.ttsDuckVolumePercent) : '15');
    setVal('voice-music-mode', data.musicAudioOnly === false ? 'video' : 'audio');
    setVal('voice-save-images', data.saveGeneratedImages != null ? String(data.saveGeneratedImages) : 'true');

    // Populate audio device dropdown if we have the value
    if (data.audioDevice) {
      const audioSelect = document.getElementById('voice-audio-device');
      if (audioSelect) {
        // Add as option if not exists
        let found = false;
        for (const opt of audioSelect.options) {
          if (opt.value === data.audioDevice) { found = true; break; }
        }
        if (!found) {
          const opt = document.createElement('option');
          opt.value = data.audioDevice;
          opt.textContent = data.audioDevice;
          audioSelect.appendChild(opt);
        }
        audioSelect.value = data.audioDevice;
      }
    }
  } catch { /* silent */ }
}

function onAudioDeviceChange() {
  const select = document.getElementById('voice-audio-device');
  const customGroup = document.getElementById('voice-audio-custom-group');
  if (select && customGroup) {
    customGroup.style.display = select.value === '__custom__' ? '' : 'none';
  }
}

function getSelectedAudioDevice() {
  const select = document.getElementById('voice-audio-device');
  if (!select) return '';
  if (select.value === '__custom__') {
    return document.getElementById('voice-audio-custom')?.value || '';
  }
  return select.value;
}

async function saveVoiceConfig(opts = {}) {
  const payload = {
    speechKey: document.getElementById('voice-speech-key')?.value || undefined,
    speechRegion: document.getElementById('voice-speech-region')?.value || undefined,
    audioDevice: getSelectedAudioDevice() || undefined,
    defaultStyle: document.getElementById('voice-default-style')?.value ?? '',
    speechLanguage: document.getElementById('voice-speech-language')?.value || 'de-DE',
    displayDurationSeconds: parseInt(document.getElementById('voice-display-duration')?.value || '60', 10),
    silenceTimeoutMs: parseInt(document.getElementById('voice-silence-timeout')?.value || '3500', 10),
    segmentationStrategy: document.getElementById('voice-segmentation')?.value || 'Semantic',
    profanityFilter: document.getElementById('voice-profanity')?.value || 'raw',
    ttsEnabled: document.getElementById('voice-tts-enabled')?.value === 'true',
    ttsVoiceName: document.getElementById('voice-tts-voice')?.value || 'de-DE-ConradNeural',
    ttsDuckingEnabled: document.getElementById('voice-tts-ducking')?.value === 'true',
    ttsDuckVolumePercent: parseInt(document.getElementById('voice-duck-volume')?.value || '15', 10),
    musicAudioOnly: document.getElementById('voice-music-mode')?.value !== 'video',
    saveGeneratedImages: document.getElementById('voice-save-images')?.value === 'true',
  };

  // Don't send empty speechKey to avoid overwriting
  if (!payload.speechKey) delete payload.speechKey;

  try {
    await api.post('/api/voice/configure', payload);
    const statusEl = document.getElementById('voice-config-status');
    if (statusEl) statusEl.textContent = 'Saved';
    setTimeout(() => { if (statusEl) statusEl.textContent = ''; }, 3000);
    pollVoiceStatus();
    if (!opts.silent) window.toast?.success('Voice', 'Settings saved');
  } catch (err) {
    if (!opts.silent) window.toast?.error('Voice', err.message);
    throw err;
  }
}

async function uploadKeywordModel(input) {
  if (!input.files || !input.files[0]) return;

  const file = input.files[0];
  const formData = new FormData();
  formData.append('keywordFile', file);

  try {
    const data = await api.postForm('/api/voice/keyword-upload', formData);
    window.toast?.success('Voice', `Keyword model uploaded (${(data.sizeBytes / 1024).toFixed(0)}KB)`);
    pollVoiceStatus();
  } catch (err) {
    window.toast?.error('Voice', err.message);
  }

  input.value = ''; // Reset file input
}

// ═══════════════════════════════════════════════════════════════
// Local Camera
// ═══════════════════════════════════════════════════════════════

async function loadLocalCamConfig() {
  try {
    const data = await api.get('/api/localcam/status');
    const setVal = (id, val) => {
      const el = document.getElementById(id);
      if (el && val) el.value = val;
    };

    setVal('localcam-fps', data.fps);
    setVal('localcam-format', data.inputFormat);
    setVal('localcam-resolution', data.inputResolution);
    setVal('localcam-scale', data.scaleFilter);
    setVal('localcam-effect', data.activeEffect);

    if (data.videoDevice) {
      const devSelect = document.getElementById('localcam-device');
      if (devSelect) {
        let found = false;
        for (const opt of devSelect.options) {
          if (opt.value === data.videoDevice) { found = true; break; }
        }
        if (!found) {
          const opt = document.createElement('option');
          opt.value = data.videoDevice;
          opt.textContent = data.videoDevice;
          devSelect.appendChild(opt);
        }
        devSelect.value = data.videoDevice;
      }
    }

    // Update button state
    updateLocalCamButtons(data.streaming);
  } catch { /* silent */ }
}

async function refreshLocalCamDevices() {
  try {
    const data = await api.get('/api/localcam/devices');
    console.log('[VOICE] Devices response:', JSON.stringify(data));

    // Populate video device dropdown
    const videoSelect = document.getElementById('localcam-device');
    if (videoSelect) {
      const currentVal = videoSelect.value;
      videoSelect.innerHTML = '<option value="">-- Select Device --</option>';
      (data.videoDevices || []).forEach(d => {
        const opt = document.createElement('option');
        opt.value = d.path;
        opt.textContent = `${d.name} (${d.path})`;
        videoSelect.appendChild(opt);
      });
      if (currentVal) videoSelect.value = currentVal;
    }

    // Populate audio device dropdown
    const audioSelect = document.getElementById('voice-audio-device');
    if (audioSelect) {
      const currentVal = audioSelect.value;
      audioSelect.innerHTML = '<option value="">System Default</option>';
      (data.audioDevices || []).forEach(d => {
        console.log('[VOICE] Adding audio device:', d.name, d.path);
        const opt = document.createElement('option');
        opt.value = d.path;
        opt.textContent = `${d.name} (${d.path})`;
        audioSelect.appendChild(opt);
      });
      // Add custom option at end
      const customOpt = document.createElement('option');
      customOpt.value = '__custom__';
      customOpt.textContent = 'Custom device path...';
      audioSelect.appendChild(customOpt);
      if (currentVal) audioSelect.value = currentVal;
    }
  } catch (err) {
    console.error('[VOICE] Device refresh error:', err);
  }
}

async function startLocalCamera() {
  try {
    await api.post('/api/localcam/start');
    window.toast?.success('Camera', 'Stream started');
    updateLocalCamButtons(true);
  } catch (err) {
    window.toast?.error('Camera', err.message);
  }
}

async function stopLocalCamera() {
  try {
    await api.post('/api/localcam/stop');
    window.toast?.success('Camera', 'Stream stopped');
    updateLocalCamButtons(false);
  } catch (err) {
    window.toast?.error('Camera', err.message);
  }
}

async function captureLocalCameraFrame() {
  try {
    window.toast?.info('Camera', 'Capturing frame...');
    const data = await api.get('/api/localcam/capture');
    if (data.imageBase64) {
      window.toast?.success('Camera', 'Frame captured and applied');
    } else {
      window.toast?.error('Camera', data.error || 'Capture failed');
    }
  } catch (err) {
    window.toast?.error('Camera', err.message);
  }
}

async function saveLocalCamConfig() {
  const payload = {
    videoDevice: document.getElementById('localcam-device')?.value || undefined,
    fps: parseInt(document.getElementById('localcam-fps')?.value || '15', 10),
    inputFormat: document.getElementById('localcam-format')?.value || 'mjpeg',
    inputResolution: document.getElementById('localcam-resolution')?.value || '640x480',
    scaleFilter: document.getElementById('localcam-scale')?.value || 'area',
    activeEffect: document.getElementById('localcam-effect')?.value || 'none',
  };

  try {
    const data = await api.post('/api/localcam/configure', payload);
    window.toast?.success('Camera', 'Settings saved');
    const statusEl = document.getElementById('localcam-status');
    if (statusEl) statusEl.textContent = 'Saved';
    setTimeout(() => { if (statusEl) statusEl.textContent = ''; }, 3000);
  } catch (err) {
    window.toast?.error('Camera', err.message);
  }
}

function updateLocalCamButtons(streaming) {
  const startBtn = document.getElementById('localcam-start-btn');
  const stopBtn = document.getElementById('localcam-stop-btn');
  if (startBtn) startBtn.style.display = streaming ? 'none' : '';
  if (stopBtn) stopBtn.style.display = streaming ? '' : 'none';
}

// ═══════════════════════════════════════════════════════════════
// Voice Image Settings (shown in AI Art tab)
// ═══════════════════════════════════════════════════════════════

async function saveVoiceImageSettings() {
  return saveVoiceConfig();
}

// ═══════════════════════════════════════════════════════════════
// Expose globally
// ═══════════════════════════════════════════════════════════════

window.onAudioDeviceChange = onAudioDeviceChange;
window.startVoiceListening = startVoiceListening;
window.stopVoiceListening = stopVoiceListening;
window.manualVoiceTrigger = manualVoiceTrigger;
window.saveVoiceConfig = saveVoiceConfig;
window.saveVoiceImageSettings = saveVoiceImageSettings;
window.uploadKeywordModel = uploadKeywordModel;
window.startLocalCamera = startLocalCamera;
window.stopLocalCamera = stopLocalCamera;
window.captureLocalCameraFrame = captureLocalCameraFrame;
window.saveLocalCamConfig = saveLocalCamConfig;
window.refreshLocalCamDevices = refreshLocalCamDevices;

// Initialize
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initVoiceCommands);
} else {
  initVoiceCommands();
}
