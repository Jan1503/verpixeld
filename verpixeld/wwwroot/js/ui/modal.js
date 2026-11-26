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

    const overlay = showModal(`
      <div class="confirm-dialog">
        <div class="confirm-icon ${type}">${icon}</div>
        <div class="confirm-title">${escapeHtml(title)}</div>
        <div class="confirm-message">${escapeHtml(message)}</div>
        <div class="confirm-actions">
          <button class="btn btn-secondary" data-action="cancel">${escapeHtml(cancelText)}</button>
          <button class="btn ${type === 'danger' ? 'btn-danger' : 'btn-primary'}" id="confirm-ok" data-action="ok">${escapeHtml(confirmText)}</button>
        </div>
      </div>
    `, () => finish(false));

    // Closes only this dialog (a confirm often sits on top of another one) and answers exactly once:
    // OK, Cancel, Escape or a click on the backdrop.
    let done = false;
    function finish(result) {
      if (done) return;
      done = true;
      document.removeEventListener('keydown', onKey);
      overlay.remove();
      if (!document.querySelector('.modal-overlay')) document.body.style.overflow = '';
      resolve(result);
    }
    const onKey = e => { if (e.key === 'Escape') { e.stopPropagation(); finish(false); } };
    document.addEventListener('keydown', onKey);
    onAction(overlay, { ok: () => finish(true), cancel: () => finish(false) });
    setTimeout(() => overlay.querySelector('[data-action="ok"]')?.focus(), 100);
  });
}

/**
 * Ask for a line of text in the app's own dialog (instead of the browser's prompt()).
 * Resolves with the trimmed text, or null on Cancel, Escape, a click on the backdrop or empty input.
 */
function showPrompt(options) {
  return new Promise((resolve) => {
    const { title = 'Enter a value', message = '', value = '', placeholder = '', confirmText = 'OK', cancelText = 'Cancel' } = options;
    const overlay = showModal(`
      <form class="confirm-dialog" data-submit="ok" autocomplete="off">
        <div class="confirm-title">${escapeHtml(title)}</div>
        ${message ? `<div class="confirm-message">${escapeHtml(message)}</div>` : ''}
        <input type="text" class="settings-input prompt-input" value="${escapeHtml(value)}" placeholder="${escapeHtml(placeholder)}" maxlength="120"
               style="width:100%;margin:12px 0 4px;">
        <div class="confirm-actions">
          <button type="button" class="btn btn-secondary" data-action="cancel">${escapeHtml(cancelText)}</button>
          <button type="submit" class="btn btn-primary">${escapeHtml(confirmText)}</button>
        </div>
      </form>
    `, () => finish(null));
    const input = overlay.querySelector('.prompt-input');

    let done = false;
    function finish(result) {
      if (done) return;
      done = true;
      document.removeEventListener('keydown', onKey);
      overlay.remove();
      if (!document.querySelector('.modal-overlay')) document.body.style.overflow = '';
      resolve(result);
    }
    const onKey = e => { if (e.key === 'Escape') { e.stopPropagation(); finish(null); } };
    document.addEventListener('keydown', onKey);
    wire(overlay, {
      click: { cancel: () => finish(null) },
      submit: { ok: () => finish(input.value.trim() || null) }
    });
    setTimeout(() => { input.focus(); input.select(); }, 50);
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
function showModal(htmlContent, onBackdrop) {
  const modalOverlay = document.createElement('div');
  modalOverlay.className = 'modal-overlay';
  modalOverlay.innerHTML = htmlContent;
  modalOverlay.onclick = (e) => {
    if (e.target !== modalOverlay) return;
    if (onBackdrop) { onBackdrop(); return; }
    modalOverlay.remove();
    document.body.style.overflow = '';
  };
  document.body.appendChild(modalOverlay);
  document.body.style.overflow = 'hidden';
  return modalOverlay;
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
window.showPrompt = showPrompt;
window.showLoading = showLoading;
window.hideLoading = hideLoading;
window.showModal = showModal;
window.closeModal = closeModal;
window.showMessage = showMessage;
