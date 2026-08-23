/* ============================================================================
   TAB NAVIGATION SYSTEM
   Handles tab switching, media player bar, and active extensions display
   ============================================================================ */

// Tab state
const tabState = {
  activeTab: 'studio',
  tabs: ['studio', 'schedule', 'canvas', 'ai-art', 'media', 'effects', 'console', 'settings']
};

// Media player bar state
let mediaPlayerUserClosed = false;

/**
 * Initialize tab navigation system
 */
function initTabs() {
  // Restore last active tab from localStorage
  const savedTab = localStorage.getItem('verpixeld-active-tab');
  if (savedTab && tabState.tabs.includes(savedTab)) {
    tabState.activeTab = savedTab;
  }
  
  // Set initial active tab
  switchTab(tabState.activeTab, false);
  
  // Initialize collapsible sections
  initCollapsibleSections();
  
  // Update UI periodically
  setInterval(updateStatusPlayer, 1000);
  updateStatusPlayer();
  
  console.log('[TABS] Tab navigation initialized');
}

/**
 * Switch to a different tab
 * @param {string} tabId - The tab identifier
 * @param {boolean} saveState - Whether to save state to localStorage (default: true)
 */
function switchTab(tabId, saveState = true) {
  if (!tabState.tabs.includes(tabId)) {
    console.warn(`[TABS] Unknown tab: ${tabId}`);
    return;
  }
  
  tabState.activeTab = tabId;
  
  // Update tab buttons
  document.querySelectorAll('.tab-btn').forEach(btn => {
    const isActive = btn.dataset.tab === tabId;
    btn.classList.toggle('active', isActive);
    btn.setAttribute('aria-selected', isActive);
  });
  
  // Update tab panels
  document.querySelectorAll('.tab-panel').forEach(panel => {
    const isActive = panel.id === `tab-${tabId}`;
    panel.classList.toggle('active', isActive);
    panel.setAttribute('aria-hidden', !isActive);
  });
  
  // Save state
  if (saveState) {
    localStorage.setItem('verpixeld-active-tab', tabId);
  }
  
  // Trigger resize for any canvas elements that might need redrawing
  window.dispatchEvent(new Event('resize'));
  
  // Fire custom event for other modules
  window.dispatchEvent(new CustomEvent('tabChanged', { detail: { tab: tabId } }));
}

/**
 * Update the media player bar and active extensions
 */
function updateStatusPlayer() {
  updateMediaPlayerBar();
  updateActiveExtensions();
}

/**
 * Hide media player bar - ONLY when user clicks X
 */
function hideMediaPlayerBar() {
  // Don't allow closing the bar while media is playing — there'd be no way to get controls back.
  const s = window.mediaState || {};
  if (s.isRunning) {
    if (window.toast) window.toast.info('Playback', 'Stop playback to hide the player bar');
    return;
  }
  const bar = document.getElementById('media-player-bar');
  if (bar) {
    bar.classList.add('hidden');
    mediaPlayerUserClosed = true;
  }
}

/**
 * Show media player bar - ALWAYS shows it and resets closed state
 */
function showMediaPlayerBar() {
  const bar = document.getElementById('media-player-bar');
  if (!bar) return;
  bar.classList.remove('hidden');
  mediaPlayerUserClosed = false;
}

/**
 * Update media player bar - both visibility and contents
 */
function updateMediaPlayerBar() {
  const bar = document.getElementById('media-player-bar');
  if (!bar) return;
  
  const s = window.mediaState || {};
  const playing = s.isRunning === true;
  
  // Show bar if playing (and user hasn't closed it)
  if (playing && !mediaPlayerUserClosed) {
    bar.classList.remove('hidden');
  }
  
  // If bar is hidden, stop here
  if (bar.classList.contains('hidden')) return;
  
  // === UPDATE CONTENTS ===
  const iconEl = document.getElementById('media-player-icon');
  const titleEl = document.getElementById('media-player-title');
  const timeEl = document.getElementById('media-player-time');
  const progressFill = document.getElementById('media-progress-fill');
  const pauseBtn = document.getElementById('media-pause-btn');
  const muteBtn = document.getElementById('media-mute-btn');
  const volumeSlider = document.getElementById('media-volume-slider');
  const progressBar = document.getElementById('media-progress-bar');
  
  // Icon
  if (iconEl) {
    iconEl.textContent = s.isAudioPlayback ? '🎵' : '🎬';
  }
  
  // Title with metadata support and scrolling
  if (titleEl) {
    let displayText = '';
    
    if (playing) {
      // Use metadata if available
      if (s.metadata?.hasMetadata) {
        // Build display string: "Artist - Title" or just "Title"
        if (s.metadata.artist && s.metadata.title) {
          displayText = `${s.metadata.artist} — ${s.metadata.title}`;
        } else if (s.metadata.title) {
          displayText = s.metadata.title;
        } else if (s.metadata.artist) {
          displayText = `${s.metadata.artist} — ${s.currentAudio || s.currentVideo || 'Unknown'}`;
        }
      }
      
      // Fallback to filename if no metadata
      if (!displayText) {
        if (s.isAudioPlayback && s.currentAudio) {
          displayText = s.currentAudio;
        } else if (s.lastPlayedYouTubeTitle && s.hasYouTubeReplay) {
          // YouTube stream - show actual title instead of "index.m3u8"
          displayText = s.lastPlayedYouTubeTitle;
        } else if (s.currentVideo) {
          displayText = s.currentVideo;
        } else {
          displayText = 'Playing...';
        }
      }
      
      // Add playlist track number if applicable
      if (s.isAudioPlayback && s.playlistCount > 1) {
        displayText += ` (${s.playlistIndex + 1}/${s.playlistCount})`;
      }
    } else {
      displayText = s.currentAudio || (s.lastPlayedYouTubeTitle && s.hasYouTubeReplay ? s.lastPlayedYouTubeTitle : null) || s.currentVideo || 'Stopped';
    }
    
    // Update title and handle scrolling
    titleEl.textContent = displayText;
    titleEl.setAttribute('data-text', displayText);
    
    // Enable scrolling for long titles (> 200px approx 25 chars)
    const needsScroll = displayText.length > 30;
    if (needsScroll) {
      titleEl.classList.add('scrolling');
    } else {
      titleEl.classList.remove('scrolling');
    }
  }
  
  // Progress
  const pos = s.videoPosition || 0;
  const dur = s.videoDuration || 0;
  const pct = dur > 0 ? (pos / dur) * 100 : 0;
  
  if (progressFill) progressFill.style.width = pct + '%';
  if (timeEl) timeEl.textContent = formatTime(pos) + ' / ' + formatTime(dur);
  
  // Progress bar cursor
  if (progressBar) {
    progressBar.style.cursor = playing ? 'pointer' : 'default';
    progressBar.title = playing ? 'Click to seek' : '';
  }
  
  // Play/Pause button - CRITICAL: must show correct state
  if (pauseBtn) {
    if (playing && !s.isPaused) {
      pauseBtn.textContent = '⏸️';
      pauseBtn.title = 'Pause';
    } else {
      pauseBtn.textContent = '▶️';
      pauseBtn.title = playing ? 'Resume' : 'Play';
    }
  }
  
  // Mute
  if (muteBtn) {
    muteBtn.textContent = s.isMuted ? '🔇' : '🔊';
    muteBtn.title = s.isMuted ? 'Unmute' : 'Mute';
  }
  
  // Volume
  if (volumeSlider && volumeSlider.value != s.volume) {
    volumeSlider.value = s.volume || 70;
  }
  
  // Playlist buttons - allow skip even when stopped if we have a playlist
  const prevBtn = document.getElementById('media-prev-btn');
  const nextBtn = document.getElementById('media-next-btn');
  const shuffleBtn = document.getElementById('media-shuffle-btn');
  const repeatBtn = document.getElementById('media-repeat-btn');
  
  // hasAudioPlaylist comes from API, playlistCount is fallback
  const hasPlaylist = s.hasAudioPlaylist || s.playlistCount > 0;
  
  if (prevBtn) {
    prevBtn.disabled = !hasPlaylist;
    prevBtn.classList.toggle('disabled', !hasPlaylist);
  }
  if (nextBtn) {
    nextBtn.disabled = !hasPlaylist;
    nextBtn.classList.toggle('disabled', !hasPlaylist);
  }
  if (shuffleBtn) shuffleBtn.classList.toggle('active', s.shuffleMode);
  if (repeatBtn) repeatBtn.classList.toggle('active', s.repeatMode);
}

/**
 * Update active extensions display
 */
function updateActiveExtensions() {
  const container = document.getElementById('active-extensions');
  if (!container) return;
  
  const chips = [];
  const mediaState = window.mediaState || {};
  
  // Check media player (video uses a canvas, audio doesn't)
  if (mediaState.isRunning && !mediaState.isAudioPlayback) {
    const canvas = (typeof mediaPlaybackCanvasName === 'function'
      ? mediaPlaybackCanvasName(mediaState)
      : mediaState.playbackCanvasName) || 'MediaPlayer';
    chips.push({ icon: '\u{1F3AC}', label: `Video (${canvas})`, type: 'media' });
  }
  
  // Check camera stream
  if (window.cameraStreamActive) {
    const canvas = document.getElementById('camera-target-canvas')?.value || 'Main';
    chips.push({ icon: '\u{1F4F7}', label: `Camera (${canvas})`, type: 'camera' });
  }
  
  // Check draw mode
  const drawLiveMode = document.getElementById('draw-live-mode')?.checked;
  if (drawLiveMode) {
    const canvas = document.getElementById('draw-target-canvas')?.value || 'Main';
    chips.push({ icon: '\u{1F3A8}', label: `Draw (${canvas})`, type: 'draw' });
  }
  
  // Check extensions from canvasContent
  if (window.canvasContent && window.canvasContent.length > 0) {
    window.canvasContent.forEach(content => {
      if (content.extensionName) {
        chips.push({ 
          icon: '\u{1F9E9}', 
          label: `${content.extensionName} (${content.canvasName})`, 
          type: 'extension' 
        });
      }
    });
  }
  
  // Render chips
  if (chips.length === 0) {
    container.innerHTML = '';
  } else {
    container.innerHTML = chips.map(chip => 
      `<span class="extension-chip ${chip.type}">${chip.icon} ${chip.label}</span>`
    ).join('');
  }
}

/**
 * Format seconds to mm:ss or hh:mm:ss
 */
function formatTime(seconds) {
  if (!seconds || isNaN(seconds)) return '0:00';
  
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  
  if (h > 0) {
    return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
  }
  return `${m}:${s.toString().padStart(2, '0')}`;
}

/**
 * Handle media player progress bar click for seeking
 */
async function statusPlayerSeek(event) {
  const mediaState = window.mediaState || {};
  
  if (!mediaState.isRunning || !mediaState.videoDuration) {
    return;
  }
  
  // Check if seeking is supported
  if (!mediaState.seekingSupported) {
    if (window.toast) {
      window.toast.warning('Playback', 'Seeking not supported for this stream');
    }
    return;
  }
  
  const progressBar = event.currentTarget;
  const rect = progressBar.getBoundingClientRect();
  const percent = Math.max(0, Math.min(100, ((event.clientX - rect.left) / rect.width) * 100));
  
  try {
    const result = await window.api.post(`/api/media/seek?percent=${percent}`);
    // Update position immediately for responsive feel
    if (window.mediaState) {
      window.mediaState.videoPosition = result.position || (mediaState.videoDuration * percent / 100);
    }
    updateMediaPlayerBar();
  } catch (error) {
    if (window.toast) {
      window.toast.error('Playback', error.message || 'Seek failed');
    }
    console.error('Failed to seek:', error);
  }
}

/**
 * Navigate to a specific tab and optionally scroll to a section
 */
function navigateToSection(tabId, sectionId) {
  switchTab(tabId);
  
  if (sectionId) {
    setTimeout(() => {
      const section = document.getElementById(sectionId);
      if (section) {
        section.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    }, 100);
  }
}

// Volume control debounce timer
let volumeUpdateTimer = null;

/**
 * Set media volume (0-100)
 * Called by media player volume slider
 */
async function setDemoVolume(value) {
  const volume = parseInt(value);
  console.log(`[AUDIO] setDemoVolume called with: ${volume}`);
  
  // Update local state immediately for responsive feel
  if (window.mediaState) {
    window.mediaState.volume = volume;
  }
  
  // Debounce API calls (don't spam server on slider drag)
  clearTimeout(volumeUpdateTimer);
  volumeUpdateTimer = setTimeout(async () => {
    try {
      console.log(`[AUDIO] Sending volume ${volume}% to APIs...`);
      
      // Update media service volume and PulseAudio volume (if available)
      await Promise.all([
        window.api.post(`/api/media/audio/volume?volume=${volume}`),
        window.api.post(`/api/audio/volume?volume=${volume}`)
      ]);
      
      console.log(`[AUDIO] Volume set to ${volume}%`);
      
      // Refresh audio output section after volume is applied (with small delay)
      setTimeout(async () => {
        console.log('[AUDIO] Calling refreshAudioStatus...');
        if (window.refreshAudioStatus) {
          await window.refreshAudioStatus();
        }
      }, 200);
    } catch (error) {
      console.error('Failed to set volume:', error);
    }
  }, 100);
}

/**
 * Toggle mute for media playback
 */
async function toggleDemoMute() {
  try {
    const result = await window.api.post('/api/media/audio/mute');
    if (window.mediaState) {
      window.mediaState.isMuted = result.isMuted;
      updateMediaPlayerBar();
      
      // Refresh audio output section
      if (window.refreshAudioStatus) {
        await window.refreshAudioStatus();
      }
    }
  } catch (error) {
    console.error('Failed to toggle mute:', error);
  }
}

/**
 * Toggle pause for media playback
 */
async function toggleDemoPause() {
  try {
    const result = await window.api.post('/api/media/pause');
    if (window.mediaState) {
      window.mediaState.isPaused = result.isPaused;
      updateMediaPlayerBar();
    }
  } catch (error) {
    console.error('Failed to toggle pause:', error);
  }
}

/**
 * Toggle a collapsible section
 * @param {HTMLElement} section - The .tab-section element or header element
 */
function toggleCollapsibleSection(section) {
  // If we received the header, get the parent section
  if (section.classList.contains('tab-section-header')) {
    section = section.parentElement;
  }
  if (!section || !section.classList.contains('tab-section')) {
    console.warn('[TABS] Invalid section for toggle');
    return;
  }
  section.classList.toggle('collapsed');
}

/**
 * Initialize collapsible sections with proper event handlers
 */
function initCollapsibleSections() {
  const sections = document.querySelectorAll('.tab-section.collapsible');
  console.log(`[TABS] Found ${sections.length} collapsible sections`);
  
  sections.forEach((section, idx) => {
    // Skip if already initialized
    if (section.dataset.collapsibleInit === 'true') {
      console.log(`[TABS] Section ${idx + 1} already initialized, skipping`);
      return;
    }
    section.dataset.collapsibleInit = 'true';
    
    const header = section.querySelector('.tab-section-header');
    const body = section.querySelector('.tab-section-body');
    
    if (!header || !body) {
      console.log(`[TABS] Section ${idx + 1} missing header or body, skipping`);
      return;
    }
    
    // Apply initial state - set body display based on collapsed class
    if (section.classList.contains('collapsed')) {
      body.style.display = 'none';
    }
    
    // Add click handler directly to header (no cloning needed since we track init state)
    header.addEventListener('click', (e) => {
      // Stop propagation to prevent any parent handlers
      e.stopPropagation();
      
      // Don't toggle if clicking on action buttons or their children
      if (e.target.closest('.tab-section-actions')) {
        return;
      }
      
      // Toggle collapsed state
      const isCurrentlyCollapsed = section.classList.contains('collapsed');
      
      if (isCurrentlyCollapsed) {
        // Expand
        section.classList.remove('collapsed');
        body.style.display = '';
      } else {
        // Collapse
        section.classList.add('collapsed');
        body.style.display = 'none';
      }
      
      console.log('[TABS] Section toggled:', header.querySelector('.tab-section-title')?.textContent?.trim(), '- collapsed:', !isCurrentlyCollapsed);
    });
    
    console.log(`[TABS] Initialized section ${idx + 1}:`, header.querySelector('.tab-section-title')?.textContent?.trim());
  });
  
  console.log('[TABS] Collapsible sections initialized');
}

// Expose globally
window.initTabs = initTabs;
window.switchTab = switchTab;
window.updateStatusPlayer = updateStatusPlayer;
window.updateMediaPlayerBar = updateMediaPlayerBar;
window.hideMediaPlayerBar = hideMediaPlayerBar;
window.showMediaPlayerBar = showMediaPlayerBar;
window.updateActiveExtensions = updateActiveExtensions;
window.statusPlayerSeek = statusPlayerSeek;
window.navigateToSection = navigateToSection;
window.tabState = tabState;
window.toggleCollapsibleSection = toggleCollapsibleSection;
window.initCollapsibleSections = initCollapsibleSections;