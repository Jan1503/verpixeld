/* ============================================================================
   TOAST NOTIFICATION SYSTEM
   ============================================================================ */

class ToastManager {
  constructor() {
    this.container = null;
    this.toasts = new Map();
    this.init();
  }

  init() {
    if (!document.getElementById('toast-container')) {
      this.container = document.createElement('div');
      this.container.id = 'toast-container';
      this.container.className = 'toast-container';
      document.body.appendChild(this.container);
    } else {
      this.container = document.getElementById('toast-container');
    }
  }

  show(options) {
    const {
      type = 'info',
      title = '',
      message = '',
      duration = 4000,
      icon = this.getDefaultIcon(type)
    } = options;

    const id = `toast-${Date.now()}-${Math.random()}`;

    const toast = document.createElement('div');
    toast.id = id;
    toast.className = `toast toast-${type}`;

    toast.innerHTML = `
      ${icon ? `<div class="toast-icon">${icon}</div>` : ''}
      <div class="toast-content">
        ${title ? `<div class="toast-title">${escapeHtml(title)}</div>` : ''}
        ${message ? `<div class="toast-message">${escapeHtml(message)}</div>` : ''}
      </div>
      <button class="toast-close" aria-label="Close">×</button>
    `;

    const closeBtn = toast.querySelector('.toast-close');
    closeBtn.addEventListener('click', () => this.dismiss(id));

    this.container.appendChild(toast);
    this.toasts.set(id, toast);

    if (duration > 0) {
      setTimeout(() => this.dismiss(id), duration);
    }

    return id;
  }

  dismiss(id) {
    const toast = this.toasts.get(id);
    if (!toast) return;

    toast.style.animation = 'toastSlideOut 0.3s ease-out forwards';

    setTimeout(() => {
      toast.remove();
      this.toasts.delete(id);
    }, 300);
  }

  success(title, message, duration) {
    return this.show({ type: 'success', title, message, duration });
  }

  error(title, message, duration) {
    return this.show({ type: 'error', title, message, duration });
  }

  warning(title, message, duration) {
    return this.show({ type: 'warning', title, message, duration });
  }

  info(title, message, duration) {
    return this.show({ type: 'info', title, message, duration });
  }

  getDefaultIcon(type) {
    const icons = {
      success: ICONS.SUCCESS,
      error: ICONS.ERROR,
      warning: ICONS.WARNING,
      info: ICONS.INFO
    };
    return icons[type] || '';
  }

  clearAll() {
    this.toasts.forEach((toast, id) => this.dismiss(id));
  }
}

// Initialize global toast instance
window.toast = new ToastManager();
