/* ============================================================================
   AUDIO OUTPUT & BLUETOOTH - Manage audio outputs and Bluetooth speakers
   ============================================================================ */

let audioSystemStatus = {
  pulseAudioAvailable: false,
  bluetoothAdapterPresent: false,
  bluetoothPoweredOn: false,
  bluetoothAvailable: false,
  defaultSink: '',
  availableSinks: [],
  pairedDevices: []
};

let isScanning = false;
let audioEventSource = null;

/**
 * Initialize audio output management
 */
async function initAudioOutput() {
  await refreshAudioStatus();
  
  // Connect to SSE for real-time audio updates
  connectAudioSSE();
}

/**
 * Connect to Server-Sent Events for real-time audio updates
 * This provides instant volume/sink change notifications without polling
 */
function connectAudioSSE() {
  if (audioEventSource) {
    audioEventSource.close();
  }
  
  try {
    audioEventSource = new EventSource(`${API_BASE}/api/audio/events`);
    
    audioEventSource.addEventListener('sink-change', async (event) => {
      // Sink changed (volume, mute, etc.) - refresh status silently
      await refreshAudioStatusSilent();
    });
    
    audioEventSource.onerror = () => {
      // Connection error - will auto-retry, or reconnect manually if closed
      if (audioEventSource.readyState === EventSource.CLOSED) {
        setTimeout(connectAudioSSE, 5000);
      }
    };
  } catch (error) {
    // SSE not supported or connection failed - fall back to manual refresh
  }
}

/**
 * Disconnect from audio SSE
 */
function disconnectAudioSSE() {
  if (audioEventSource) {
    audioEventSource.close();
    audioEventSource = null;
    console.log('[AUDIO-SSE] Disconnected');
  }
}

/**
 * Refresh audio status silently (no loading indicator, no console spam)
 * Used by SSE to update UI without disruption
 */
async function refreshAudioStatusSilent() {
  try {
    const result = await api.get('/api/audio/status');
    if (result.data) {
      audioSystemStatus = result.data;
      
      // Update UI without "loading" states
      updateAudioStatusDisplay();
      if (audioSystemStatus.pulseAudioAvailable) {
        updateAudioOutputList();
      }
      if (audioSystemStatus.bluetoothPoweredOn) {
        updateBluetoothDevicesList();
      }
      
      // Also update mini-player volume slider if present
      const volumeSlider = document.getElementById('media-volume-slider');
      const defaultSink = audioSystemStatus.availableSinks?.find(s => s.isDefault);
      if (volumeSlider && defaultSink && window.mediaState) {
        window.mediaState.volume = defaultSink.volume;
        if (volumeSlider.value != defaultSink.volume) {
          volumeSlider.value = defaultSink.volume;
        }
      }
    }
  } catch {
    return;
  }
}

/**
 * Refresh audio system status (full refresh with loading indicators)
 */
async function refreshAudioStatus() {
  const statusContainer = document.getElementById('audio-system-status');
  const outputSection = document.getElementById('audio-output-section');
  const bluetoothSection = document.getElementById('bluetooth-section');
  const notAvailable = document.getElementById('audio-not-available');
  
  if (statusContainer) {
    statusContainer.innerHTML = '<div class="audio-status-loading">Loading audio system status...</div>';
  }
  
  try {
    const result = await api.get('/api/audio/status');
    if (result.data) {
      audioSystemStatus = result.data;
      console.log('[AUDIO] Status refreshed, sinks:', audioSystemStatus.availableSinks?.map(s => `${s.name}:${s.volume}%`));
      
      // Show/hide sections based on availability
      if (outputSection) {
        outputSection.style.display = audioSystemStatus.pulseAudioAvailable ? 'block' : 'none';
      }
      if (bluetoothSection) {
        // Show Bluetooth section if adapter is present (even if off - we can show power button)
        bluetoothSection.style.display = audioSystemStatus.bluetoothAdapterPresent ? 'block' : 'none';
      }
      if (notAvailable) {
        notAvailable.style.display = audioSystemStatus.pulseAudioAvailable ? 'none' : 'block';
      }
      
      // Update status display
      updateAudioStatusDisplay();
      
      // Update output list
      if (audioSystemStatus.pulseAudioAvailable) {
        updateAudioOutputList();
      }
      
      // Update Bluetooth devices
      if (audioSystemStatus.bluetoothPoweredOn) {
        updateBluetoothDevicesList();
      } else {
        // Show message when Bluetooth is off
        const pairedContainer = document.getElementById('bluetooth-paired-devices');
        if (pairedContainer && audioSystemStatus.bluetoothAdapterPresent) {
          pairedContainer.innerHTML = '<p class="text-muted">Power on Bluetooth to see paired devices</p>';
        }
      }
    }
  } catch (error) {
    console.error('Failed to get audio status:', error);
    if (statusContainer) {
      statusContainer.innerHTML = '<div class="audio-status-error">Failed to connect to audio service</div>';
    }
  }
}

/**
 * Update the audio system status display
 */
function updateAudioStatusDisplay() {
  const container = document.getElementById('audio-system-status');
  if (!container) return;
  
  const pulseStatus = audioSystemStatus.pulseAudioAvailable 
    ? '<span class="status-ok">\u2705 PulseAudio</span>'
    : '<span class="status-warn">\u274C PulseAudio</span>';
  
  let btStatus;
  if (audioSystemStatus.bluetoothPoweredOn) {
    btStatus = '<span class="status-ok">\u2705 Bluetooth</span>';
  } else if (audioSystemStatus.bluetoothAdapterPresent) {
    btStatus = `
      <span class="status-warn">\u26A0\uFE0F Bluetooth off</span>
      <button class="btn btn-small btn-primary" onclick="powerOnBluetooth()" style="margin-left: 8px;">
        \u26A1 Power On
      </button>
    `;
  } else {
    btStatus = '<span class="status-warn">\u274C No Bluetooth</span>';
  }
  
  container.innerHTML = `
    <div class="audio-status-badges">
      ${pulseStatus}
      ${btStatus}
    </div>
  `;
}

/**
 * Update the audio output list
 */
function updateAudioOutputList() {
  const container = document.getElementById('audio-output-list');
  if (!container) return;
  
  if (audioSystemStatus.availableSinks.length === 0) {
    container.innerHTML = '<p class="text-muted">No audio outputs detected</p>';
    return;
  }
  
  const html = audioSystemStatus.availableSinks.map(sink => {
    const isActive = sink.isDefault;
    const typeIcon = getAudioTypeIcon(sink.type);
    const muteIcon = sink.isMuted ? '\u{1F507}' : '\u{1F50A}';
    
    return `
      <div class="audio-output-item ${isActive ? 'active' : ''}" data-sink="${sink.name}">
        <div class="audio-output-info">
          <span class="audio-output-icon">${typeIcon}</span>
          <div class="audio-output-details">
            <span class="audio-output-name">${sink.description || sink.name}</span>
            <span class="audio-output-type">${sink.type}</span>
          </div>
        </div>
        <div class="audio-output-controls">
          <span class="audio-output-volume">${muteIcon} ${sink.volume}%</span>
          ${!isActive ? `<button class="btn btn-small btn-primary" onclick="setAudioOutput('${sink.name}')">Use</button>` : '<span class="audio-output-active-badge">Active</span>'}
        </div>
      </div>
    `;
  }).join('');
  
  container.innerHTML = html;
}

/**
 * Get icon for audio output type
 */
function getAudioTypeIcon(type) {
  switch (type) {
    case 'Bluetooth': return '\u{1F4F6}';
    case 'HDMI': return '\u{1F5A5}\uFE0F';
    case 'USB': return '\u{1F50C}';
    case 'Analog': return '\u{1F3A7}';
    default: return '\u{1F50A}';
  }
}

/**
 * Set the default audio output
 */
async function setAudioOutput(sinkName) {
  try {
    window.toast.info('Audio', 'Switching audio output...');
    
    const result = await api.post(`/api/audio/output?sinkName=${encodeURIComponent(sinkName)}`);
    
    window.toast.success('Audio', result.message);
    await refreshAudioStatus();
  } catch (error) {
    console.error('Failed to set audio output:', error);
    window.toast.error('Audio', 'Failed to set audio output');
  }
}

/**
 * Update the Bluetooth devices list (paired/known devices)
 */
function updateBluetoothDevicesList() {
  const container = document.getElementById('bluetooth-paired-devices');
  const countBadge = document.getElementById('bluetooth-paired-count');
  if (!container) return;
  
  const devices = audioSystemStatus.pairedDevices || [];
  
  // Update count badge
  if (countBadge) {
    countBadge.textContent = devices.length > 0 ? `(${devices.length})` : '';
  }
  
  // Show all paired devices
  if (devices.length === 0) {
    container.innerHTML = `
      <div class="bluetooth-empty">
        <p class="text-muted">No paired Bluetooth devices</p>
        <p class="text-muted" style="font-size: 0.8rem;">Scan for devices and pair with a speaker to see it here.</p>
      </div>
    `;
    return;
  }
  
  const html = devices.map(device => {
    const statusIcon = device.isConnected ? '\u{1F7E2}' : '\u26AA';
    const statusText = device.isConnected ? 'Connected' : 'Not connected';
    
    // Determine device icon based on type/name
    let deviceIcon = '\u{1F4F1}';
    const nameLower = (device.name || '').toLowerCase();
    const iconLower = (device.icon || '').toLowerCase();
    if (iconLower.includes('audio') || nameLower.includes('speaker') || 
        nameLower.includes('headphone') || nameLower.includes('soundbar') ||
        nameLower.includes('jbl') || nameLower.includes('bose') || 
        nameLower.includes('sony') || nameLower.includes('beats')) {
      deviceIcon = '\u{1F50A}';
    }
    
    return `
      <div class="bluetooth-device ${device.isConnected ? 'connected' : ''}" data-address="${device.address}">
        <div class="bluetooth-device-info">
          <span class="bluetooth-device-icon">${deviceIcon}</span>
          <div class="bluetooth-device-details">
            <span class="bluetooth-device-name">${device.name || device.address}</span>
            <span class="bluetooth-device-address">${device.address}</span>
            <span class="bluetooth-device-status">${statusIcon} ${statusText}</span>
          </div>
        </div>
        <div class="bluetooth-device-actions">
          ${device.isConnected 
            ? `<button class="btn btn-small btn-secondary" onclick="disconnectBluetoothDevice('${device.address}')">Disconnect</button>`
            : `<button class="btn btn-small btn-primary" onclick="connectBluetoothDevice('${device.address}')">Connect</button>`
          }
          <button class="btn btn-small btn-danger" onclick="removeBluetoothDevice('${device.address}')" title="Forget/Remove device">\u{1F5D1}\uFE0F</button>
        </div>
      </div>
    `;
  }).join('');
  
  container.innerHTML = html;
}

/**
 * Scan for Bluetooth devices
 */
async function scanBluetoothDevices() {
  if (isScanning) return;
  
  const scanBtn = document.getElementById('bluetooth-scan-btn');
  const discoveredSection = document.getElementById('bluetooth-discovered');
  const discoveredList = document.getElementById('bluetooth-discovered-list');
  const scanHint = document.getElementById('bluetooth-scan-hint');
  
  isScanning = true;
  if (scanBtn) {
    scanBtn.disabled = true;
    scanBtn.innerHTML = '\u23F3 Scanning...';
  }
  if (discoveredSection) {
    discoveredSection.style.display = 'block';
  }
  if (scanHint) {
    scanHint.style.display = 'none';
  }
  if (discoveredList) {
    discoveredList.innerHTML = '<p class="text-muted">Scanning for nearby devices...</p>';
  }
  
  try {
    window.toast.info('Bluetooth', 'Scanning for devices (10 seconds)...');
    
    const result = await api.post('/api/audio/bluetooth/scan?duration=10');
    
    if (result.data !== undefined) {
      const deviceCount = result.data?.length || 0;
      window.toast.success('Bluetooth', `Scan complete - found ${deviceCount} device(s)`);
      
      // Update discovered devices list
      if (discoveredList) {
        if (result.data && result.data.length > 0) {
          const html = result.data.map(device => {
            // Determine icon based on device name/type
            const isAudio = device.name?.toLowerCase().includes('speaker') || 
                           device.name?.toLowerCase().includes('headphone') ||
                           device.name?.toLowerCase().includes('audio') ||
                           device.name?.toLowerCase().includes('soundbar') ||
                           device.name?.toLowerCase().includes('jbl') ||
                           device.name?.toLowerCase().includes('sony') ||
                           device.name?.toLowerCase().includes('bose');
            const icon = isAudio ? '\u{1F50A}' : '\u{1F4F1}';
            
            return `
              <div class="bluetooth-device discovered" data-address="${device.address}">
                <div class="bluetooth-device-info">
                  <span class="bluetooth-device-icon">${icon}</span>
                  <div class="bluetooth-device-details">
                    <span class="bluetooth-device-name">${device.name || 'Unknown Device'}</span>
                    <span class="bluetooth-device-address">${device.address}</span>
                  </div>
                </div>
                <div class="bluetooth-device-actions">
                  <button class="btn btn-small btn-primary" onclick="pairBluetoothDevice('${device.address}')">Pair</button>
                </div>
              </div>
            `;
          }).join('');
          discoveredList.innerHTML = html;
        } else {
          discoveredList.innerHTML = '<p class="text-muted">No new devices found. Make sure your Bluetooth device is in pairing mode.</p>';
        }
      }
      
      // Also refresh the main status to update paired devices
      await refreshAudioStatus();
    }
  } catch (error) {
    console.error('Failed to scan:', error);
    window.toast.error('Bluetooth', 'Failed to scan for devices');
  } finally {
    isScanning = false;
    if (scanBtn) {
      scanBtn.disabled = false;
      scanBtn.innerHTML = 'Scan';
    }
  }
}

/**
 * Pair with a Bluetooth device
 */
async function pairBluetoothDevice(address) {
  try {
    window.toast.info('Bluetooth', 'Pairing and connecting... This may take up to 10 seconds.');
    
    const result = await api.post(`/api/audio/bluetooth/pair/${encodeURIComponent(address)}`);
    
    window.toast.success('Bluetooth', result.message || 'Connected successfully!');
    
    // Wait a moment then refresh to show updated audio outputs
    await new Promise(resolve => setTimeout(resolve, 1000));
    await refreshAudioStatus();
  } catch (error) {
    console.error('Failed to pair:', error);
    window.toast.error('Bluetooth', 'Failed to pair. Make sure the device is in pairing mode.');
  }
}

/**
 * Connect to a paired Bluetooth device
 */
async function connectBluetoothDevice(address) {
  try {
    window.toast.info('Bluetooth', 'Connecting (this may take up to 20 seconds)...');
    
    const result = await api.post(`/api/audio/bluetooth/connect/${encodeURIComponent(address)}`);
    
    console.log('[BLUETOOTH] Connect response:', result);
    
    // Check if audio sink was detected
    if (result.audioSinkDetected) {
      window.toast.success('Bluetooth', result.message);
    } else {
      console.log('[BLUETOOTH] No audio sink - showing manual dialog');
      // Connection succeeded but no audio - show manual connect dialog
      showManualConnectDialog(address, result.deviceName || address);
    }
    
    // Refresh status either way
    await new Promise(resolve => setTimeout(resolve, 1000));
    await refreshAudioStatus();
  } catch (error) {
    console.log('[BLUETOOTH] Connection failed, showManualConnect:', error.data?.showManualConnect);
    // Check if we should show manual connect option
    if (error.data?.showManualConnect) {
      showManualConnectDialog(address, error.data.deviceName || address);
    } else {
      window.toast.error('Bluetooth', error.message || 'Connection failed');
    }
  }
}

/**
 * Show dialog with manual connect instructions
 */
function showManualConnectDialog(address, deviceName) {
  const command = `bluetoothctl connect ${address}`;
  
  console.log('[BLUETOOTH] Showing manual connect dialog for', address);
  
  // Create modal
  const modal = document.createElement('div');
  modal.className = 'modal-overlay';
  modal.innerHTML = `
    <div class="modal-content" style="max-width: 500px;">
      <div class="modal-header">
        <h2>Manual Connection Required</h2>
      </div>
      <div class="modal-body">
        <p>The automatic Bluetooth connection succeeded, but the audio profile didn't activate properly.</p>
        <p>This is a known system limitation. Please connect manually by running this command in a terminal:</p>
        
        <div style="background: var(--color-bg-tertiary, #1a1a2e); padding: 12px; border-radius: 6px; margin: 16px 0; font-family: monospace; display: flex; align-items: center; gap: 8px;">
          <code id="bt-manual-cmd" style="flex: 1; color: var(--color-accent, #4ecdc4); font-size: 14px; word-break: break-all;">${command}</code>
          <button class="btn btn-small btn-primary" id="bt-copy-btn" title="Copy to clipboard">Copy</button>
        </div>
        
        <p style="font-size: 13px; color: var(--color-text-secondary, #888); margin-top: 12px;">
          <strong>Tip:</strong> SSH into your Raspberry Pi and run the command above. 
          Once connected, click "Check Connection" below.
        </p>
      </div>
      <div class="modal-footer">
        <button class="btn btn-secondary" id="bt-close-btn">Close</button>
        <button class="btn btn-primary" id="bt-check-btn">Check Connection</button>
      </div>
    </div>
  `;
  
  document.body.appendChild(modal);
  
  // Add event listeners (safer than inline onclick)
  modal.querySelector('#bt-copy-btn').addEventListener('click', function() {
    copyToClipboard(command, this);
  });
  
  modal.querySelector('#bt-close-btn').addEventListener('click', function() {
    modal.remove();
  });
  
  modal.querySelector('#bt-check-btn').addEventListener('click', function() {
    checkManualConnection(address, this);
  });
  
  // Close on overlay click
  modal.addEventListener('click', (e) => {
    if (e.target === modal) modal.remove();
  });
  
  // Also show a toast to draw attention
  window.toast.warning('Bluetooth', 'Manual connection required - see dialog');
}

/**
 * Copy text to clipboard
 */
function copyToClipboard(text, button) {
  navigator.clipboard.writeText(text).then(() => {
    const originalText = button.innerHTML;
    button.innerHTML = '\u2705 Copied!';
    setTimeout(() => { button.innerHTML = originalText; }, 2000);
  }).catch(() => {
    window.toast.error('Clipboard', 'Failed to copy - please select and copy manually');
  });
}

/**
 * Check if manual Bluetooth connection succeeded
 */
async function checkManualConnection(address, button) {
  const originalText = button.innerHTML;
  button.innerHTML = '\u23F3 Checking...';
  button.disabled = true;
  
  try {
    await refreshAudioStatus();
    
    // Check if there's now a Bluetooth sink
    const btSink = audioSystemStatus.availableSinks?.find(s => 
      s.type === 'Bluetooth' || s.name?.includes('bluez')
    );
    
    if (btSink) {
      window.toast.success('Bluetooth', `Connected! Audio output: ${btSink.description || btSink.name}`);
      button.closest('.modal-overlay').remove();
    } else {
      window.toast.warning('Bluetooth', 'No audio sink detected yet. Make sure the device is connected and try again.');
      button.innerHTML = originalText;
      button.disabled = false;
    }
  } catch (error) {
    window.toast.error('Bluetooth', 'Failed to check connection');
    button.innerHTML = originalText;
    button.disabled = false;
  }
}

/**
 * Disconnect from a Bluetooth device
 */
async function disconnectBluetoothDevice(address) {
  try {
    window.toast.info('Bluetooth', 'Disconnecting...');
    
    const result = await api.post(`/api/audio/bluetooth/disconnect/${encodeURIComponent(address)}`);
    
    window.toast.success('Bluetooth', result.message);
    await refreshAudioStatus();
  } catch (error) {
    console.error('Failed to disconnect:', error);
    window.toast.error('Bluetooth', 'Failed to disconnect');
  }
}

/**
 * Remove (unpair) a Bluetooth device
 */
async function removeBluetoothDevice(address) {
  const confirmed = await showConfirm({
    title: 'Remove Device',
    message: 'Remove this Bluetooth device? You will need to pair again to use it.',
    confirmText: 'Remove',
    cancelText: 'Cancel',
    type: 'danger'
  });
  
  if (!confirmed) return;
  
  try {
    window.toast.info('Bluetooth', 'Removing device...');
    
    const result = await api.del(`/api/audio/bluetooth/device/${encodeURIComponent(address)}`);
    
    window.toast.success('Bluetooth', result.message);
    await refreshAudioStatus();
  } catch (error) {
    console.error('Failed to remove:', error);
    window.toast.error('Bluetooth', 'Failed to remove device');
  }
}

/**
 * Power on Bluetooth adapter
 */
async function powerOnBluetooth() {
  try {
    window.toast.info('Bluetooth', 'Powering on Bluetooth (this may take a moment)...');
    
    const result = await api.post('/api/audio/bluetooth/power?on=true');
    
    if (result.poweredOn) {
      window.toast.success('Bluetooth', 'Bluetooth powered on - loading paired devices...');
      
      // Wait for Bluetooth stack to fully initialize before refreshing
      await new Promise(resolve => setTimeout(resolve, 2000));
      await refreshAudioStatus();
      
      // Show count of paired devices found
      const pairedCount = audioSystemStatus.pairedDevices?.length || 0;
      if (pairedCount > 0) {
        window.toast.info('Bluetooth', `Found ${pairedCount} paired device(s)`);
      }
    } else {
      // Show error with hint (api succeeded but poweredOn was false)
      let errorMsg = result.error || 'Failed to power on Bluetooth';
      if (result.hint) {
        errorMsg += '\n\n\u{1F4A1} ' + result.hint;
      }
      window.toast.error('Bluetooth', errorMsg, 8000); // Show longer
      console.error('[BLUETOOTH] Power on failed:', result);
      
      // Still refresh to show current state
      await refreshAudioStatus();
    }
  } catch (error) {
    console.error('Failed to power on Bluetooth:', error);
    window.toast.error('Bluetooth', 'Failed to power on Bluetooth');
  }
}

/**
 * Power off Bluetooth adapter
 */
async function powerOffBluetooth() {
  try {
    window.toast.info('Bluetooth', 'Powering off Bluetooth...');
    
    const result = await api.post('/api/audio/bluetooth/power?on=false');
    
    window.toast.success('Bluetooth', 'Bluetooth powered off');
    await refreshAudioStatus();
  } catch (error) {
    console.error('Failed to power off Bluetooth:', error);
    window.toast.error('Bluetooth', 'Failed to power off Bluetooth');
  }
}

// Expose functions globally
window.initAudioOutput = initAudioOutput;
window.refreshAudioStatus = refreshAudioStatus;
window.setAudioOutput = setAudioOutput;
window.powerOnBluetooth = powerOnBluetooth;
window.powerOffBluetooth = powerOffBluetooth;
window.scanBluetoothDevices = scanBluetoothDevices;
window.pairBluetoothDevice = pairBluetoothDevice;
window.connectBluetoothDevice = connectBluetoothDevice;
window.disconnectBluetoothDevice = disconnectBluetoothDevice;
window.removeBluetoothDevice = removeBluetoothDevice;

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', () => {
  initAudioOutput();
});
