/* ============================================================================
   SYSTEM CONTROL - Connection Status & System Operations
   ============================================================================ */

/**
 * Update connection status indicator
 */
function updateConnectionStatus(status) {
  const indicator = document.getElementById('connection-status');
  if (!indicator) return;
  
  indicator.className = 'connection-indicator ' + status;
  const textEl = indicator.querySelector('.connection-text');
  
  if (textEl) {
    switch (status) {
      case 'connected':
        textEl.textContent = 'Connected';
        break;
      case 'disconnected':
        textEl.textContent = 'Disconnected';
        break;
      case 'connecting':
        textEl.textContent = 'Connecting...';
        break;
    }
  }
}

/**
 * Restart render loop with confirmation
 */
async function confirmRestartRender() {
  const confirmed = await showConfirm({
    title: '↻ Restart Render Loop',
    message: 'This will stop and restart the display render loop. Use this if the display appears frozen or unresponsive. Extensions will continue running.',
    confirmText: 'Restart Render',
    cancelText: 'Cancel',
    type: 'warning',
    icon: '↻'
  });

  if (confirmed) {
    const restartBtn = document.getElementById('restart-render-btn');
    setButtonLoading(restartBtn, true);
    
    try {
      await window.api.post('/api/system/restart-render');
      toast.success('Render Restarted', 'The render loop has been successfully restarted.');
    } catch (error) {
      toast.error('Error', 'Failed to restart render loop: ' + error.message);
    } finally {
      setButtonLoading(restartBtn, false);
    }
  }
}

/**
 * System reboot with confirmation
 */
async function confirmSystemReboot() {
  const confirmed = await showConfirm({
    title: '⏻ Reboot System',
    message: 'Are you sure you want to reboot the system? All active displays will be interrupted and the device will restart.',
    confirmText: 'Reboot Now',
    cancelText: 'Cancel',
    type: 'danger',
    icon: '⚠️'
  });

  if (confirmed) {
    const rebootBtn = document.getElementById('reboot-btn');
    setButtonLoading(rebootBtn, true);
    
    try {
      await window.api.post('/api/system/reboot');
      toast.show({
        type: 'warning',
        title: 'Rebooting...',
        message: 'System is restarting. Please wait...',
        duration: 30000
      });
      
      updateConnectionStatus('disconnected');
      startReconnectionCheck();
    } catch (error) {
      toast.error('Error', 'Failed to send reboot command: ' + error.message);
      setButtonLoading(rebootBtn, false);
    }
  }
}

/**
 * Check for system reconnection after reboot
 */
function startReconnectionCheck() {
  let attempts = 0;
  const maxAttempts = 60; // Check for 2 minutes (every 2 seconds)
  
  const checkInterval = setInterval(async () => {
    attempts++;
    
    try {
      await window.api.get('/api/status');
      clearInterval(checkInterval);
      toast.success('System Online', 'The system has restarted successfully!');
      updateConnectionStatus('connected');
      
      const rebootBtn = document.getElementById('reboot-btn');
      setButtonLoading(rebootBtn, false);
      
      // Refresh all data
      fetchStatus();
      refreshLayoutInfo();
      fetchSavedLayouts();
      fetchFilters();
    } catch (e) {
      if (attempts >= maxAttempts) {
        clearInterval(checkInterval);
        toast.error('Connection Lost', 'Could not reconnect to the system. Please check manually.');
        
        const rebootBtn = document.getElementById('reboot-btn');
        setButtonLoading(rebootBtn, false);
      }
    }
  }, 2000);
}

// Expose globally
window.updateConnectionStatus = updateConnectionStatus;
window.confirmRestartRender = confirmRestartRender;
window.confirmSystemReboot = confirmSystemReboot;
window.startReconnectionCheck = startReconnectionCheck;
