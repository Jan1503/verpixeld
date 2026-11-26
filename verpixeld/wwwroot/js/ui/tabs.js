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
  setInterval(() => { if (!document.hidden) updateStatusPlayer(); }, 1000);
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

// Volume: the label and the fill follow the slider at once; the host gets one request per
// pause in the movement, and the status poll leaves the slider alone for a moment after a change.
const mpbVol = { timer: null, until: 0 };

function mpbVolumeInput(el) {
  el.style.setProperty('--vol', el.value + '%');
  const vv = document.getElementById('mpb-volume-value');
  if (vv) vv.textContent = el.value;
  mpbVol.until = Date.now() + 2000;
  if (window.mediaState) window.mediaState.volume = +el.value;
  clearTimeout(mpbVol.timer);
  mpbVol.timer = setTimeout(() => {
    if (typeof setMediaVolume === 'function') setMediaVolume(el.value);
  }, 120);
}
window.mpbVolumeInput = mpbVolumeInput;

// Scrubbing on the progress bar and the seek that follows it.
const mpbScrub = { active: false, pos: 0 };
const mpbSeek = { target: 0, until: 0 };

function mpbPosFromEvent(bar, ev) {
  const s = window.mediaState || {};
  const rect = bar.getBoundingClientRect();
  const share = Math.max(0, Math.min(1, (ev.clientX - rect.left) / rect.width));
  return { share, pos: share * (s.videoDuration || 0) };
}

function mpbInitScrub() {
  const bar = document.getElementById('media-progress-bar');
  if (!bar || bar.dataset.scrub) return;
  bar.dataset.scrub = '1';
  bar.addEventListener('pointerdown', ev => {
    const s = window.mediaState || {};
    if (!s.isRunning || !(s.videoDuration > 0) || s.seekingSupported === false) return;
    ev.preventDefault();
    bar.setPointerCapture(ev.pointerId);
    mpbScrub.active = true;
    mpbScrub.pos = mpbPosFromEvent(bar, ev).pos;
    bar.classList.add('scrubbing');
    updateMediaPlayerBar();
  });
  bar.addEventListener('pointermove', ev => {
    if (!mpbScrub.active) return;
    mpbScrub.pos = mpbPosFromEvent(bar, ev).pos;
    updateMediaPlayerBar();
  });
  const finish = async ev => {
    if (!mpbScrub.active) return;
    const { share, pos } = mpbPosFromEvent(bar, ev);
    mpbScrub.active = false;
    bar.classList.remove('scrubbing');
    mpbSeek.target = pos;
    mpbSeek.until = Date.now() + 10000;
    if (window.mediaState) window.mediaState.videoPosition = pos;
    updateMediaPlayerBar();
    try {
      await window.api.post(`/api/media/seek?percent=${(share * 100).toFixed(3)}`);
    } catch (error) {
      mpbSeek.until = 0;
      if (window.toast) window.toast.error('Playback', error.message || 'Seek failed');
    }
  };
  bar.addEventListener('pointerup', finish);
  bar.addEventListener('pointercancel', () => {
    mpbScrub.active = false;
    bar.classList.remove('scrubbing');
    updateMediaPlayerBar();
  });
}

document.addEventListener('DOMContentLoaded', mpbInitScrub);

// Picture of what plays (video frame, embedded cover, station logo, YouTube thumbnail).
let mpbArtKey = null;
async function mpbLoadArt(key) {
  const art = document.querySelector('#media-player-bar .mpb-art');
  if (!art) return;
  mpbArtKey = key;
  if (!key) {
    art.classList.remove('has-image');
    return;
  }
  try {
    const r = await window.api.get('/api/media/current-thumbnail');
    if (mpbArtKey !== key) return;
    let img = art.querySelector('img');
    if (r.thumbnail) {
      if (!img) {
        img = document.createElement('img');
        img.alt = '';
        img.referrerPolicy = 'no-referrer';
        img.onerror = () => art.classList.remove('has-image');
        art.appendChild(img);
      }
      img.src = r.thumbnail;
      art.classList.add('has-image');
    } else {
      art.classList.remove('has-image');
    }
  } catch {
    art.classList.remove('has-image');
  }
}

const MPB_ICONS = {
  play: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 4.5v15l12-7.5z"/></svg>',
  pause: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M6.5 4.5h4v15h-4zM13.5 4.5h4v15h-4z"/></svg>',
  vol: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5 6 9H2v6h4l5 4z" fill="currentColor"/><path d="M15.5 8.5a5 5 0 0 1 0 7M19 5a10 10 0 0 1 0 14"/></svg>',
  mute: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5 6 9H2v6h4l5 4z" fill="currentColor"/><path d="M23 9l-6 6M17 9l6 6"/></svg>'
};

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
  
  mpbInitScrub();
  const artKey = playing
    ? [s.currentVideo, s.currentAudio, s.networkFilePath, s.radioStationName, s.lastPlayedYouTubeTitle].join('|')
    : null;
  if (artKey !== mpbArtKey) {
    mpbLoadArt(artKey);
    window.refreshFavoriteStar?.();
  }

  // Icon
  if (iconEl) {
    iconEl.textContent = s.isRadioPlaying ? '📻' : s.isAudioPlayback ? '🎵' : '🎬';
  }
  bar.classList.toggle('playing', playing && !s.isPaused);
  bar.classList.toggle('paused', playing && !!s.isPaused);
  const stateText = document.getElementById('media-player-state-text');
  if (stateText) {
    const kind = s.isRadioPlaying ? 'RADIO' : s.isAudioPlayback ? 'MUSIC' : 'VIDEO';
    stateText.textContent = !playing ? 'STOPPED' : s.isPaused ? `PAUSED · ${kind}` : `PLAYING · ${kind}`;
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
    
    // Only touch the DOM when the title changed. A running marquee animation otherwise kept
    // showing the old text until the mouse paused it; restarting the animation repaints it.
    if (titleEl.getAttribute('data-text') !== displayText) {
      titleEl.classList.remove('scrolling');
      titleEl.textContent = displayText;
      titleEl.setAttribute('data-text', displayText);
      void titleEl.offsetWidth;
      const box = titleEl.parentElement;
      if (box && titleEl.scrollWidth > box.clientWidth + 4) titleEl.classList.add('scrolling');
    }
  }
  
  // Progress: while dragging the bar shows the finger; after a seek it holds the target
  // until the host reports a position near it (FFmpeg needs a moment to restart).
  let pos = s.videoPosition || 0;
  const dur = s.videoDuration || 0;
  if (mpbScrub.active) {
    pos = mpbScrub.pos;
  } else if (mpbSeek.until > Date.now()) {
    if (Math.abs(pos - mpbSeek.target) < 2.5) mpbSeek.until = 0;
    else pos = mpbSeek.target;
  }
  const pct = dur > 0 ? Math.min(100, (pos / dur) * 100) : 0;
  
  if (progressFill) progressFill.style.width = pct + '%';
  const knob = document.getElementById('media-progress-knob');
  if (knob) knob.style.left = pct + '%';
  const posEl = document.getElementById('media-player-time-pos');
  const live = playing && dur <= 0;
  if (posEl) {
    posEl.textContent = formatTime(pos);
    if (timeEl) timeEl.textContent = live ? 'LIVE' : formatTime(dur);
  } else if (timeEl) {
    timeEl.textContent = formatTime(pos) + ' / ' + formatTime(dur);
  }
  if (progressBar) progressBar.classList.toggle('live', live);
  
  // Progress bar cursor
  if (progressBar) {
    progressBar.style.cursor = playing ? 'pointer' : 'default';
    progressBar.title = playing ? 'Click to seek' : '';
  }
  
  // Play/Pause button - CRITICAL: must show correct state
  if (pauseBtn) {
    const showPause = playing && !s.isPaused;
    if (pauseBtn.dataset.icon !== (showPause ? 'pause' : 'play')) {
      pauseBtn.innerHTML = showPause ? MPB_ICONS.pause : MPB_ICONS.play;
      pauseBtn.dataset.icon = showPause ? 'pause' : 'play';
    }
    pauseBtn.title = showPause ? 'Pause' : (playing ? 'Resume' : 'Play');
  }
  
  // Mute
  if (muteBtn) {
    if (muteBtn.dataset.icon !== (s.isMuted ? 'mute' : 'vol')) {
      muteBtn.innerHTML = s.isMuted ? MPB_ICONS.mute : MPB_ICONS.vol;
      muteBtn.dataset.icon = s.isMuted ? 'mute' : 'vol';
    }
    muteBtn.title = s.isMuted ? 'Unmute' : 'Mute';
    muteBtn.classList.toggle('active', !!s.isMuted);
  }
  
  // Volume
  if (volumeSlider && Date.now() > mpbVol.until && s.volume != null && volumeSlider.value != s.volume) {
    volumeSlider.value = s.volume;
  }
  if (volumeSlider) {
    volumeSlider.style.setProperty('--vol', volumeSlider.value + '%');
    const vv = document.getElementById('mpb-volume-value');
    if (vv) vv.textContent = volumeSlider.value;
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
      // No stopPropagation: header buttons (data-action) are handled by delegation further up.
      // Don't toggle if clicking on action buttons or their children
      if (e.target.closest('.tab-section-actions, button, input, select, a')) {
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

// Controls of #media-player-bar in index.html: wired here instead of inline handlers.
onReady(() => wire(document.getElementById('media-player-bar'), {
  input: {
    'mpb-volume-input': (el, event) => mpbVolumeInput(el)
  },
  click: {
    'hide-media-player-bar': () => hideMediaPlayerBar()
  }
}));

// Tab bar in index.html: each button carries its tab id in data-tab.
onReady(() => wire(document.getElementById('tab-navigation'), {
  click: { 'switch-tab': el => switchTab(el.dataset.tab) }
}));
