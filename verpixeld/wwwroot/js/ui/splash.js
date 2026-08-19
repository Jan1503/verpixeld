/* ============================================================================
   PIXELD - SPLASH SCREEN CONTROLLER
   Handles animated splash screen with particles and auto-hide
   ============================================================================ */

/**
 * Initialize splash screen with particle effects
 */
function initSplashScreen() {
  const splashScreen = document.getElementById('splash-screen');
  const particleContainer = document.getElementById('splash-particles');
  
  if (!splashScreen || !particleContainer) {
    // No splash screen, show content immediately
    document.body.classList.add('splash-complete');
    return;
  }
  
  // Create particles
  createParticles(particleContainer, 40);
  
  // Set minimum display time (matches loader animation)
  const minDisplayTime = 3400; // 3.4 seconds
  const startTime = Date.now();
  
  // Hide splash when page is fully loaded
  const hideSplash = () => {
    const elapsed = Date.now() - startTime;
    const remainingTime = Math.max(0, minDisplayTime - elapsed);
    
    setTimeout(() => {
      splashScreen.classList.add('fade-out');
      
      // Remove from DOM after animation
      setTimeout(() => {
        splashScreen.classList.add('hidden');
        // Trigger any post-splash animations
        document.body.classList.add('splash-complete');
      }, 600);
    }, remainingTime);
  };
  
  // Wait for window load
  if (document.readyState === 'complete') {
    hideSplash();
  } else {
    window.addEventListener('load', hideSplash);
  }
}

/**
 * Create animated particles in container
 */
function createParticles(container, count) {
  const colors = [
    '#ff007f', // Pink
    '#ff7f00', // Orange
    '#00ffc8', // Cyan
    '#00bfff', // Blue
    '#9400d3', // Violet
    '#ff1493', // Deep pink
    '#ffffff'  // White
  ];
  
  for (let i = 0; i < count; i++) {
    const particle = document.createElement('div');
    particle.className = 'splash-particle';
    
    // Random starting position near center
    const startX = 50 + (Math.random() - 0.5) * 20;
    const startY = 50 + (Math.random() - 0.5) * 20;
    
    // Random end position (flying outward)
    const angle = Math.random() * Math.PI * 2;
    const distance = 100 + Math.random() * 200;
    const endX = Math.cos(angle) * distance;
    const endY = Math.sin(angle) * distance;
    
    // Random properties
    const size = 2 + Math.random() * 3;
    const duration = 2 + Math.random() * 2;
    const delay = Math.random() * 2;
    const color = colors[Math.floor(Math.random() * colors.length)];
    
    particle.style.cssText = `
      left: ${startX}%;
      top: ${startY}%;
      width: ${size}px;
      height: ${size}px;
      background: ${color};
      box-shadow: 0 0 ${size * 2}px ${color};
      --tx: ${endX}px;
      --ty: ${endY}px;
      animation-duration: ${duration}s;
      animation-delay: ${delay}s;
    `;
    
    container.appendChild(particle);
  }
}

/**
 * Skip splash screen (for development)
 */
function skipSplash() {
  const splashScreen = document.getElementById('splash-screen');
  if (splashScreen) {
    splashScreen.classList.add('fade-out');
    setTimeout(() => {
      splashScreen.classList.add('hidden');
      document.body.classList.add('splash-complete');
    }, 100);
  }
}

// Expose globally
window.initSplashScreen = initSplashScreen;
window.skipSplash = skipSplash;

// Initialize splash screen immediately
initSplashScreen();
