/* ============================================================================
   THEME MANAGEMENT
   ============================================================================ */

/**
 * Initialize theme based on saved preference
 */
function initTheme() {
  const themeToggle = document.getElementById('theme-toggle');
  const savedTheme = localStorage.getItem('theme') || 'light';

  document.documentElement.setAttribute('data-theme', savedTheme);
  document.documentElement.style.colorScheme = savedTheme;
  updateThemeIcon(savedTheme);

  if (themeToggle) {
    themeToggle.addEventListener('click', () => {
      const current = document.documentElement.getAttribute('data-theme');
      const next = current === 'dark' ? 'light' : 'dark';

      document.documentElement.setAttribute('data-theme', next);
      document.documentElement.style.colorScheme = next;
      localStorage.setItem('theme', next);
      updateThemeIcon(next);
    });
  }
}

/**
 * Update theme toggle icon
 */
function updateThemeIcon(theme) {
  const lightIcon = document.querySelector('.theme-icon-light');
  const darkIcon = document.querySelector('.theme-icon-dark');

  if (!lightIcon || !darkIcon) return;

  if (theme === 'dark') {
    lightIcon.style.display = 'none';
    darkIcon.style.display = 'inline';
  } else {
    lightIcon.style.display = 'inline';
    darkIcon.style.display = 'none';
  }
}

/**
 * Toggle theme
 */
function toggleTheme() {
  const current = document.documentElement.getAttribute('data-theme');
  const next = current === 'dark' ? 'light' : 'dark';
  
  document.documentElement.setAttribute('data-theme', next);
  document.documentElement.style.colorScheme = next;
  localStorage.setItem('theme', next);
  updateThemeIcon(next);
  
  return next;
}

/**
 * Set specific theme
 */
function setTheme(theme) {
  document.documentElement.setAttribute('data-theme', theme);
  document.documentElement.style.colorScheme = theme;
  localStorage.setItem('theme', theme);
  updateThemeIcon(theme);
}

/**
 * Get current theme
 */
function getTheme() {
  return document.documentElement.getAttribute('data-theme') || 'light';
}

// Expose globally
window.initTheme = initTheme;
window.updateThemeIcon = updateThemeIcon;
window.toggleTheme = toggleTheme;
window.setTheme = setTheme;
window.getTheme = getTheme;
