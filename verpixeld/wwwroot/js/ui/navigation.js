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

// Initialize mobile nav when DOM is ready
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initMobileNav);
} else {
  initMobileNav();
}

// Expose globally
window.initMobileNav = initMobileNav;
window.setActiveNavItem = setActiveNavItem;
window.restoreCollapsedStates = restoreCollapsedStates;
window.toggleQuickSettingsExpand = toggleQuickSettingsExpand;

// Controls of #brightness-expanded in index.html: wired here instead of inline handlers.
onReady(() => wire(document.getElementById('brightness-expanded'), {
  click: {
    'toggle-quick-settings-expand': () => toggleQuickSettingsExpand()
  }
}));
