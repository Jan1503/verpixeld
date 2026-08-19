/* ============================================================================
   MOBILE NAVIGATION & COLLAPSIBLE SECTIONS
   ============================================================================ */

/**
 * Initialize mobile navigation
 */
function initMobileNav() {
  const mobileNav = document.getElementById('mobile-nav');
  if (!mobileNav) return;
  
  const navItems = mobileNav.querySelectorAll('.mobile-nav-item');
  const sections = [];
  
  // Collect section elements
  navItems.forEach(item => {
    const sectionId = item.getAttribute('data-section');
    const section = document.getElementById(sectionId);
    if (section) {
      sections.push({ id: sectionId, element: section, navItem: item });
    }
  });
  
  // Handle click navigation
  navItems.forEach(item => {
    item.addEventListener('click', (e) => {
      e.preventDefault();
      const sectionId = item.getAttribute('data-section');
      const section = document.getElementById(sectionId);
      
      if (section) {
        // Scroll to section with offset for mobile nav
        const offset = 20;
        const elementPosition = section.getBoundingClientRect().top;
        const offsetPosition = elementPosition + window.pageYOffset - offset;
        
        window.scrollTo({
          top: offsetPosition,
          behavior: 'smooth'
        });
        
        // Update active state
        setActiveNavItem(item);
      }
    });
  });
  
  // Track scroll to update active nav item
  let scrollTimeout;
  window.addEventListener('scroll', () => {
    if (scrollTimeout) {
      window.cancelAnimationFrame(scrollTimeout);
    }
    
    scrollTimeout = window.requestAnimationFrame(() => {
      updateActiveNavOnScroll(sections);
    });
  }, { passive: true });
}

/**
 * Set active navigation item
 */
function setActiveNavItem(activeItem) {
  const mobileNav = document.getElementById('mobile-nav');
  if (!mobileNav) return;
  
  mobileNav.querySelectorAll('.mobile-nav-item').forEach(item => {
    item.classList.remove('active');
  });
  activeItem.classList.add('active');
}

/**
 * Update active navigation item based on scroll position
 */
function updateActiveNavOnScroll(sections) {
  const scrollPosition = window.scrollY + 100; // Offset for better UX
  
  let activeSection = sections[0];
  
  for (const section of sections) {
    if (section.element.offsetTop <= scrollPosition) {
      activeSection = section;
    }
  }
  
  if (activeSection) {
    setActiveNavItem(activeSection.navItem);
  }
}

// ============================================================================
// COLLAPSIBLE SECTIONS
// ============================================================================

/**
 * Toggle section collapse state
 */
function toggleSection(sectionId) {
  const section = document.getElementById(sectionId);
  if (!section) return;
  
  const isCollapsed = section.classList.contains('collapsed');
  
  if (isCollapsed) {
    section.classList.remove('collapsed');
    saveCollapsedState(sectionId, false);
  } else {
    section.classList.add('collapsed');
    saveCollapsedState(sectionId, true);
  }
}

/**
 * Save collapsed state to localStorage
 */
function saveCollapsedState(sectionId, isCollapsed) {
  try {
    const collapsedSections = JSON.parse(localStorage.getItem('collapsedSections') || '{}');
    collapsedSections[sectionId] = isCollapsed;
    localStorage.setItem('collapsedSections', JSON.stringify(collapsedSections));
  } catch (e) {
    console.warn('Could not save collapsed state:', e);
  }
}

/**
 * Restore collapsed states from localStorage
 */
function restoreCollapsedStates() {
  try {
    const collapsedSections = JSON.parse(localStorage.getItem('collapsedSections') || '{}');
    
    for (const [sectionId, isCollapsed] of Object.entries(collapsedSections)) {
      if (isCollapsed) {
        const section = document.getElementById(sectionId);
        if (section) {
          section.classList.add('collapsed');
        }
      }
    }
  } catch (e) {
    console.warn('Could not restore collapsed states:', e);
  }
}

// ============================================================================
// QUICK SETTINGS
// ============================================================================

/**
 * Toggle quick settings brightness panel
 */
function toggleQuickSettingsExpand() {
  const expandedPanel = document.getElementById('brightness-expanded');
  if (!expandedPanel) return;
  
  const isHidden = expandedPanel.style.display === 'none';
  
  if (isHidden) {
    expandedPanel.style.display = 'block';
  } else {
    expandedPanel.style.display = 'none';
  }
}

// ============================================================================
// LAYOUT VISUAL PREVIEW
// ============================================================================

const layoutConfigurations = {
  fullscreen: {
    canvases: [{ left: 0, top: 0, width: 100, height: 100 }]
  },
  splitview: {
    canvases: [
      { left: 0, top: 0, width: 50, height: 100 },
      { left: 50, top: 0, width: 50, height: 100 }
    ]
  },
  headercontent: {
    canvases: [
      { left: 0, top: 0, width: 100, height: 25 },
      { left: 0, top: 25, width: 100, height: 75 }
    ]
  },
  threepanel: {
    canvases: [
      { left: 0, top: 0, width: 100, height: 25 },
      { left: 0, top: 25, width: 50, height: 75 },
      { left: 50, top: 25, width: 50, height: 75 }
    ]
  },
  dashboard: {
    canvases: [
      { left: 0, top: 0, width: 50, height: 50 },
      { left: 50, top: 0, width: 50, height: 50 },
      { left: 0, top: 50, width: 50, height: 50 },
      { left: 50, top: 50, width: 50, height: 50 }
    ]
  }
};

/**
 * Update layout preview based on selected layout
 */
function updateLayoutPreview(layoutValue) {
  const previewContainer = document.getElementById('layout-preview');
  if (!previewContainer) return;
  
  const config = layoutConfigurations[layoutValue];
  if (!config) return;
  
  // Clear existing canvases
  previewContainer.innerHTML = '';
  previewContainer.setAttribute('data-layout', layoutValue);
  
  // Create canvas previews
  config.canvases.forEach((canvas, index) => {
    const canvasEl = document.createElement('div');
    canvasEl.className = 'layout-preview-canvas';
    canvasEl.style.left = `calc(${canvas.left}% + 2px)`;
    canvasEl.style.top = `calc(${canvas.top}% + 2px)`;
    canvasEl.style.width = `calc(${canvas.width}% - 4px)`;
    canvasEl.style.height = `calc(${canvas.height}% - 4px)`;
    
    // Add slight delay for animation effect
    canvasEl.style.opacity = '0';
    canvasEl.style.transform = 'scale(0.8)';
    setTimeout(() => {
      canvasEl.style.transition = 'all 0.2s ease-out';
      canvasEl.style.opacity = '0.7';
      canvasEl.style.transform = 'scale(1)';
    }, index * 50);
    
    previewContainer.appendChild(canvasEl);
  });
}

/**
 * Initialize enhanced UI features
 */
function initEnhancedUI() {
  // Restore collapsed section states
  restoreCollapsedStates();
  
  // Initialize layout preview with current selection
  const layoutSelect = document.getElementById('layout-profile');
  if (layoutSelect) {
    updateLayoutPreview(layoutSelect.value);
  }
}

// Initialize mobile nav when DOM is ready
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initMobileNav);
} else {
  initMobileNav();
}

// Expose globally
window.initMobileNav = initMobileNav;
window.setActiveNavItem = setActiveNavItem;
window.toggleSection = toggleSection;
window.saveCollapsedState = saveCollapsedState;
window.restoreCollapsedStates = restoreCollapsedStates;
window.toggleQuickSettingsExpand = toggleQuickSettingsExpand;
window.updateLayoutPreview = updateLayoutPreview;
window.initEnhancedUI = initEnhancedUI;
