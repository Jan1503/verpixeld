/* ============================================================================
   MODAL & DIALOG SYSTEM
   ============================================================================ */

/**
 * Show a confirmation dialog
 */
function showConfirm(options) {
  return new Promise((resolve) => {
    const {
      title = 'Confirm Action',
      message = 'Are you sure?',
      confirmText = 'Confirm',
      cancelText = 'Cancel',
      type = 'warning', // 'warning' or 'danger'
      icon = type === 'danger' ? '⚠️' : '❓'
    } = options;

    const html = `
      <div class="confirm-dialog">
        <div class="confirm-icon ${type}">${icon}</div>
        <div class="confirm-title">${escapeHtml(title)}</div>
        <div class="confirm-message">${escapeHtml(message)}</div>
        <div class="confirm-actions">
          <button class="btn btn-secondary" id="confirm-cancel">${cancelText}</button>
          <button class="btn ${type === 'danger' ? 'btn-danger' : 'btn-primary'}" id="confirm-ok">${confirmText}</button>
        </div>
      </div>
    `;

    showModal(html);

    // Focus confirm button
    setTimeout(() => {
      document.getElementById('confirm-ok')?.focus();
    }, 100);

    // Handle confirm
    document.getElementById('confirm-ok').addEventListener('click', () => {
      closeModal();
      resolve(true);
    });

    // Handle cancel
    document.getElementById('confirm-cancel').addEventListener('click', () => {
      closeModal();
      resolve(false);
    });

    // ESC key cancels
    const escapeHandler = (e) => {
      if (e.key === 'Escape') {
        closeModal();
        resolve(false);
        document.removeEventListener('keydown', escapeHandler);
      }
    };
    document.addEventListener('keydown', escapeHandler);
  });
}

/**
 * Show loading overlay
 */
function showLoading(message = 'Loading...') {
  hideLoading();

  const overlay = document.createElement('div');
  overlay.id = 'loading-overlay';
  overlay.className = 'loading-overlay-fullscreen';

  overlay.innerHTML = `
    <div class="loading-spinner-container">
      <div class="loading-spinner"></div>
      <div class="loading-message">${escapeHtml(message)}</div>
    </div>
  `;

  document.body.appendChild(overlay);
  document.body.style.overflow = 'hidden';
}

/**
 * Hide loading overlay
 */
function hideLoading() {
  const overlay = document.getElementById('loading-overlay');
  if (overlay) {
    overlay.remove();
    document.body.style.overflow = '';
  }
}

/**
 * Show a generic modal (deprecated - use modal-overlay structure directly)
 */
function showModal(htmlContent) {
  const modalOverlay = document.createElement('div');
  modalOverlay.className = 'modal-overlay';
  modalOverlay.innerHTML = htmlContent;
  modalOverlay.onclick = (e) => {
    if (e.target === modalOverlay) {
      modalOverlay.remove();
      document.body.style.overflow = '';
    }
  };
  document.body.appendChild(modalOverlay);
  document.body.style.overflow = 'hidden';
}

/**
 * Close all modals
 */
function closeModal() {
  // Only remove dynamically created modal overlays, not static modals
  const dynamicModals = document.querySelectorAll('.modal-overlay:not(.static-modal)');
  dynamicModals.forEach(modal => modal.remove());
  
  // Hide (don't remove) static modals
  const visibleStaticModals = document.querySelectorAll('.static-modal[style*="display: flex"], .static-modal[style*="display:flex"]');
  visibleStaticModals.forEach(modal => {
    modal.style.display = 'none';
  });
  
  document.body.style.overflow = '';
}

/**
 * Show a message using toast (wrapper for backwards compatibility)
 */
function showMessage(text, typeOrIsError = 'success') {
  if (!window.toast) {
    console.warn('Toast not initialized, message:', text);
    return;
  }

  // Handle both old API (boolean) and new API (string type)
  let type;
  if (typeof typeOrIsError === 'boolean') {
    type = typeOrIsError ? 'error' : 'success';
  } else {
    type = typeOrIsError;
  }
  
  // Map 'info' to 'success' for consistency
  if (type === 'info') type = 'success';
  
  toast.show({
    type: type,
    message: text,
    duration: type === 'error' ? 6000 : 4000
  });
}

// Expose globally
window.showConfirm = showConfirm;
window.showLoading = showLoading;
window.hideLoading = hideLoading;
window.showModal = showModal;
window.closeModal = closeModal;
window.showMessage = showMessage;
