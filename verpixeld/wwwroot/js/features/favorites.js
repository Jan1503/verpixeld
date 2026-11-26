/* ============================================================================
   FAVORITES & HISTORY - JavaScript for favorites and play history management
   ============================================================================ */

console.log('[FAVORITES] Module loading...');

// State
let favoritesState = {
  favorites: [],
  history: [],
  expanded: {
    favorites: true, // Start expanded
    history: true    // Start expanded
  },
  autoPlay: {
    enabled: false,
    shuffle: false,
    currentId: null,
    currentIndex: -1,
    total: 0
  }
};

/**
 * Initialize favorites module
 */
function initFavorites() {
  console.log('[FAVORITES] Initializing...');
  
  // Load data
  fetchFavorites();
  fetchHistory();
  
  // Set up expand/collapse handlers
  const favHeader = document.querySelector('.favorites-header');
  const histHeader = document.querySelector('.history-header');
  
  console.log('[FAVORITES] Found headers:', { favHeader: !!favHeader, histHeader: !!histHeader });
  
  if (favHeader) {
    favHeader.addEventListener('click', (e) => {
      // Buttons in the header (shuffle, skip, auto-play, clear) do their own thing, not toggle the section.
      if (e.target.closest('button')) return;
      toggleFavoritesSection('favorites');
    });
  }
  if (histHeader) {
    histHeader.addEventListener('click', (e) => {
      // Buttons in the header (shuffle, skip, auto-play, clear) do their own thing, not toggle the section.
      if (e.target.closest('button')) return;
      toggleFavoritesSection('history');
    });
  }
  
  // Apply initial state
  updateSectionStates();
  
  console.log('[FAVORITES] Initialized');
}

/**
 * Toggle section expand/collapse
 */
function toggleFavoritesSection(section) {
  favoritesState.expanded[section] = !favoritesState.expanded[section];
  updateSectionStates();
}

/**
 * Update section expand/collapse CSS classes
 */
function updateSectionStates() {
  const favSection = document.querySelector('.favorites-section');
  const histSection = document.querySelector('.history-section');
  
  if (favSection) {
    favSection.classList.toggle('expanded', favoritesState.expanded.favorites);
  }
  if (histSection) {
    histSection.classList.toggle('expanded', favoritesState.expanded.history);
  }
}

/**
 * Fetch favorites from API
 */
async function fetchFavorites() {
  try {
    const result = await api.get('/api/favorites');
    console.log('[FAVORITES] Fetched favorites:', result);
    favoritesState.favorites = result.favorites;
    renderFavorites();
  } catch (error) {
    console.error('[FAVORITES] Failed to fetch favorites:', error);
  }
}

/**
 * Fetch history from API
 */
async function fetchHistory() {
  console.log('[HISTORY] fetchHistory() called');
  try {
    const result = await api.get('/api/history');
    console.log('[HISTORY] Fetched history:', result);
    favoritesState.history = result.history;
    console.log('[HISTORY] Updated state with', result.history.length, 'items');
    renderHistory();
  } catch (error) {
    console.error('[HISTORY] Failed to fetch history:', error);
  }
}

/**
 * Render favorites list
 */
function renderFavorites() {
  const container = document.querySelector('.favorites-list');
  const countEl = document.querySelector('.favorites-count');
  
  if (!container) {
    console.warn('[FAVORITES] Favorites container not found!');
    return;
  }
  
  if (countEl) {
    countEl.textContent = `(${favoritesState.favorites.length})`;
  }
  
  if (favoritesState.favorites.length === 0) {
    container.innerHTML = `
      <div class="favorites-empty">
        <div class="favorites-empty-icon">⭐</div>
        <p>No favorites yet</p>
        <p class="text-muted">Click the star in the mini-player to add</p>
      </div>
    `;
    return;
  }
  
  const isAutoPlaying = favoritesState.autoPlay.enabled;
  const currentAutoPlayId = favoritesState.autoPlay.currentId;
  
  container.innerHTML = favoritesState.favorites.map(fav => {
    const isNowPlaying = isAutoPlaying && fav.id === currentAutoPlayId;
    return `
    <div class="favorite-item${isNowPlaying ? ' now-playing' : ''}" data-id="${escapeHtml(fav.id)}" data-action="play">
      ${isNowPlaying ? '<div class="now-playing-indicator"><span></span><span></span><span></span></div>' : ''}
      <div class="item-thumbnail">
        ${fav.thumbnail 
          ? `<img src="${escapeHtml(fav.thumbnail)}" alt="${escapeHtml(fav.name)}">`
          : `<span class="item-icon">${escapeHtml(fav.icon)}</span>`
        }
      </div>
      <div class="item-info">
        <div class="item-name" title="${escapeHtml(fav.name)}">${escapeHtml(fav.name)}</div>
        <div class="item-meta">
          <span class="item-type-badge ${getTypeClass(fav.type)}">${formatType(fav.type)}</span>
          ${fav.avSyncOffset !== 0 
            ? `<span class="av-sync-badge adjusted">AV: ${fav.avSyncOffset > 0 ? '+' : ''}${fav.avSyncOffset}ms</span>`
            : ''
          }
        </div>
      </div>
      <div class="item-actions">
        <button class="item-action-btn" data-action="edit" data-id="${escapeHtml(fav.id)}" title="Edit">✏️</button>
        <button class="item-action-btn danger" data-action="remove" data-id="${escapeHtml(fav.id)}" title="Remove">🗑️</button>
      </div>
    </div>`;
  }).join('');
  // one delegated listener: the buttons carry their own action, so a button click never also plays the item
  onAction(container, {
    play: d => playFavorite(d.id),
    edit: d => editFavorite(d.id),
    remove: d => removeFavorite(d.id)
  });
}

/**
 * Render history list
 */
function renderHistory() {
  const container = document.querySelector('.history-list');
  const countEl = document.querySelector('.history-count');
  
  if (!container) {
    console.warn('[FAVORITES] History container not found!');
    return;
  }
  
  if (countEl) {
    countEl.textContent = `(${favoritesState.history.length})`;
  }
  
  if (favoritesState.history.length === 0) {
    container.innerHTML = `
      <div class="history-empty">
        <div class="history-empty-icon">🕐</div>
        <p>No play history</p>
        <p class="text-muted">Recently played media will appear here</p>
      </div>
    `;
    return;
  }
  
  container.innerHTML = favoritesState.history.map((item, index) => `
    <div class="history-item" data-index="${index}" data-action="play">
      <div class="item-thumbnail">
        ${item.thumbnail 
          ? `<img src="${escapeHtml(item.thumbnail)}" alt="${escapeHtml(item.name)}">`
          : `<span class="item-icon">${escapeHtml(item.icon)}</span>`
        }
      </div>
      <div class="item-info">
        <div class="item-name" title="${escapeHtml(item.name)}">${escapeHtml(item.name)}</div>
        <div class="item-meta">
          <span class="item-type-badge ${getTypeClass(item.type)}">${formatType(item.type)}</span>
          <span>${formatTimeAgo(item.playedAt)}</span>
        </div>
      </div>
      <div class="item-actions">
        <button class="item-action-btn" data-action="favorite" data-index="${index}" title="Add to favorites">⭐</button>
        <button class="item-action-btn danger" data-action="remove" data-index="${index}" title="Remove">✕</button>
      </div>
    </div>
  `).join('');
  onAction(container, {
    play: d => playHistoryItem(Number(d.index)),
    favorite: d => addHistoryToFavorites(Number(d.index)),
    remove: d => removeHistoryItem(Number(d.index))
  });
}

/**
 * Get CSS class for type badge
 */
function getTypeClass(type) {
  if (type.toLowerCase().includes('youtube')) return 'youtube';
  if (type.toLowerCase().includes('network')) return 'network';
  return 'local';
}

/**
 * Format type for display
 */
function formatType(type) {
  return type.replace(/([A-Z])/g, ' $1').trim();
}

/**
 * Format time ago
 */
function formatTimeAgo(dateStr) {
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now - date;
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMins / 60);
  const diffDays = Math.floor(diffHours / 24);
  
  if (diffMins < 1) return 'just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  if (diffHours < 24) return `${diffHours}h ago`;
  if (diffDays < 7) return `${diffDays}d ago`;
  return date.toLocaleDateString();
}

/**
 * Show loading state on an item
 */
function showItemLoading(el, message) {
  if (!el) return;
  el.classList.add('loading');
  // Add loading overlay if not already present
  if (!el.querySelector('.item-loading-overlay')) {
    const overlay = document.createElement('div');
    overlay.className = 'item-loading-overlay';
    overlay.innerHTML = `<div class="item-spinner"></div><span>${escapeHtml(message || 'Loading...')}</span>`;
    el.appendChild(overlay);
  }
}

/**
 * Remove loading state from an item
 */
function removeItemLoading(el) {
  if (!el) return;
  el.classList.remove('loading');
  const overlay = el.querySelector('.item-loading-overlay');
  if (overlay) overlay.remove();
}

/**
 * Remove all loading states
 */
function removeAllLoadingStates() {
  document.querySelectorAll('.favorite-item.loading, .history-item.loading').forEach(el => {
    removeItemLoading(el);
  });
}

/**
 * Play a favorite - YouTube items use smart loading (current playback continues)
 */
async function playFavorite(id) {
  const fav = favoritesState.favorites.find(f => f.id === id);
  const el = document.querySelector(`.favorite-item[data-id="${id}"]`);
  
  // YouTube items: use /api/youtube/play directly so current playback continues
  if (fav && fav.type === 'YouTube' && fav.source) {
    showItemLoading(el, 'Loading YouTube...');
    
    // Apply saved settings from favorite first
    if (fav.avSyncOffset) {
      try {
        await api.post(`/api/media/audio/sync?offsetMs=${fav.avSyncOffset}`);
        // Update slider UI
        const slider = document.getElementById('media-sync-slider');
        const valueEl = document.getElementById('media-sync-value');
        if (slider) slider.value = fav.avSyncOffset;
        if (valueEl) valueEl.textContent = `${fav.avSyncOffset}ms`;
      } catch { /* ignore */ }
    }
    if (fav.scaleFilter) {
      try {
        await api.post(`/api/media/scale-filter?filter=${encodeURIComponent(fav.scaleFilter)}`);
        const scaleSelect = document.getElementById('media-scale-filter');
        if (scaleSelect) scaleSelect.value = fav.scaleFilter;
      } catch { /* ignore */ }
    }
    
    try {
      const result = await api.post('/api/youtube/play', { url: fav.source, loop: false });
      removeItemLoading(el);
      window.toast?.success('Favorites', `Playing: ${escapeHtml(fav.name)}`);
      if (window.showMediaPlayerBar) window.showMediaPlayerBar();
      // Mark favorite as played in the background
      api.post(`/api/favorites/${id}/mark-played`).catch(() => {});
      fetchFavorites();
      setTimeout(() => { if (typeof window.fetchHistory === 'function') window.fetchHistory(); }, 1000);
    } catch (error) {
      removeItemLoading(el);
      console.error('Failed to play YouTube favorite:', error);
      window.toast?.error('YouTube', error.message || 'Failed to play');
    }
    return;
  }
  
  // Non-YouTube items: use the standard favorite play endpoint
  showItemLoading(el);
  
  try {
    const result = await api.post(`/api/favorites/${id}/play`);
    removeItemLoading(el);
    window.toast?.success('Favorites', result.message);
    if (window.showMediaPlayerBar) window.showMediaPlayerBar();
    fetchFavorites();
    setTimeout(() => { if (typeof window.fetchHistory === 'function') window.fetchHistory(); }, 1000);
  } catch (error) {
    removeItemLoading(el);
    console.error('Failed to play favorite:', error);
    window.toast?.error('Favorites', error.message || 'Failed to play');
  }
}

/**
 * Remove a favorite
 */
async function removeFavorite(id) {
  const fav = favoritesState.favorites.find(f => f.id === id);
  const ok = await showConfirm({ title: 'Remove favorite', message: `Remove "${fav?.name || 'this item'}" from your favorites?`, confirmText: 'Remove', type: 'danger' });
  if (!ok) return;
  
  try {
    await api.del(`/api/favorites/${id}`);
    window.toast?.success('Favorites', 'Removed from favorites');
    await fetchFavorites();
    refreshFavoriteStar();
  } catch (error) {
    console.error('Failed to remove favorite:', error);
    window.toast?.error('Favorites', error.message);
  }
}

/**
 * Edit a favorite (rename)
 */
async function editFavorite(id) {
  const fav = favoritesState.favorites.find(f => f.id === id);
  if (!fav) return;

  const newName = await showPrompt({ title: 'Rename favorite', value: fav.name, confirmText: 'Rename' });
  if (!newName || newName === fav.name) return;
  
  updateFavorite(id, newName);
}

/**
 * Update favorite name
 */
async function updateFavorite(id, name) {
  try {
    await api.put(`/api/favorites/${id}?name=${encodeURIComponent(name)}`);
    window.toast?.success('Favorites', 'Updated');
    fetchFavorites();
  } catch (error) {
    console.error('Failed to update favorite:', error);
    window.toast?.error('Favorites', error.message);
  }
}

/**
 * Play item from history - YouTube items use smart loading (current playback continues)
 */
async function playHistoryItem(index) {
  const item = favoritesState.history[index];
  const el = document.querySelector(`.history-item[data-index="${index}"]`);
  
  // YouTube items: use /api/youtube/play directly so current playback continues
  if (item && item.type === 'YouTube' && item.source) {
    showItemLoading(el, 'Loading YouTube...');
    
    try {
      await api.post('/api/youtube/play', { url: item.source, loop: false });
      removeItemLoading(el);
      setTimeout(fetchHistory, 1000);   // the entry moved to the top: reload so the next click hits the right one
      window.toast?.success('History', `Playing: ${escapeHtml(item.name)}`);
      if (window.showMediaPlayerBar) window.showMediaPlayerBar();
    } catch (error) {
      removeItemLoading(el);
      console.error('Failed to play YouTube history item:', error);
      window.toast?.error('YouTube', error.message || 'Failed to play');
    }
    return;
  }
  
  // Non-YouTube items: use the standard history play endpoint  
  showItemLoading(el);
  
  try {
    const result = await api.post(`/api/history/${index}/play?key=${encodeURIComponent(item?.key || '')}`);
    removeItemLoading(el);
    fetchHistory();   // the entry moved to the top: reload so the next click hits the right one
    window.toast?.success('History', result.message);
    if (window.showMediaPlayerBar) window.showMediaPlayerBar();
  } catch (error) {
    removeItemLoading(el);
    console.error('Failed to play history item:', error);
    window.toast?.error('History', error.message || 'Failed to play');
    fetchHistory();
  }
}

/**
 * Add history item to favorites
 */
async function addHistoryToFavorites(index) {
  const item = favoritesState.history[index];
  if (!item) return;
  
  const name = await showPrompt({ title: 'Add to favorites', message: 'Name for the favorite', value: item.name, confirmText: 'Add' });
  if (!name) return;
  
  try {
    const result = await api.post(`/api/history/${index}/favorite?name=${encodeURIComponent(name)}&key=${encodeURIComponent(item.key || '')}`);
    window.toast?.success('Favorites', `Added: ${result.favorite.name}`);
    fetchFavorites();
  } catch (error) {
    console.error('Failed to add to favorites:', error);
    window.toast?.error('Favorites', error.message);
  }
}

/**
 * Remove history item
 */
async function removeHistoryItem(index) {
  try {
    const item = favoritesState.history[index];
    await api.del(`/api/history/${index}?key=${encodeURIComponent(item?.key || '')}`);
    fetchHistory();
  } catch (error) {
    console.error('Failed to remove history item:', error);
  }
}

/**
 * Clear all history
 */
async function clearHistory() {
  const ok = await showConfirm({ title: 'Clear history', message: 'Clear the whole play history?', confirmText: 'Clear', type: 'danger' });
  if (!ok) return;
  
  try {
    await api.del('/api/history');
    window.toast?.success('History', 'History cleared');
    fetchHistory();
  } catch (error) {
    console.error('Failed to clear history:', error);
    window.toast?.error('History', error.message);
  }
}

/**
 * Add current playing to favorites (called from mini-player)
 */
// ── Star in the player bar ──────────────────────────────────────────────────
// Filled (★) when what plays now is already a favorite; a click then offers to remove it.
let currentFavoriteId = null;

async function refreshFavoriteStar() {
  const btn = document.getElementById('media-favorite-btn');
  if (!btn) return;
  try {
    const r = await api.get('/api/favorites/current');
    currentFavoriteId = r.favoriteId || null;
  } catch {
    currentFavoriteId = null;
  }
  btn.textContent = currentFavoriteId ? '★' : '☆';
  btn.classList.toggle('active', !!currentFavoriteId);
  btn.title = currentFavoriteId ? 'In favorites: click to remove' : 'Add to favorites';
  btn.setAttribute('aria-pressed', currentFavoriteId ? 'true' : 'false');
}

async function addCurrentToFavorites() {
  await refreshFavoriteStar();
  if (currentFavoriteId) {
    await removeFavorite(currentFavoriteId);
    return;
  }

  // Get suggested name from current playing media
  const s = window.mediaState || {};
  let suggestedName = '';
  
  if (s.metadata?.title) {
    // Use metadata title, optionally with artist
    suggestedName = s.metadata.artist 
      ? `${s.metadata.artist} - ${s.metadata.title}` 
      : s.metadata.title;
  } else if (s.lastPlayedYouTubeTitle && s.hasYouTubeReplay) {
    // YouTube title takes priority over currentVideo which is just "index.m3u8"
    suggestedName = s.lastPlayedYouTubeTitle;
  } else if (s.currentAudio) {
    suggestedName = s.currentAudio;
  } else if (s.currentVideo) {
    suggestedName = s.currentVideo;
  }
  
  const name = await showPrompt({ title: 'Add to favorites', message: 'Name for the favorite', value: suggestedName, confirmText: 'Add' });
  if (!name) return;
  
  try {
    const result = await api.post(`/api/favorites/add-current?name=${encodeURIComponent(name)}`);
    window.toast?.success('Favorites', result.message);
    await fetchFavorites();
    refreshFavoriteStar();
  } catch (error) {
    console.error('[FAVORITES] Failed to add to favorites:', error);
    window.toast?.error('Favorites', error.message || 'Failed to add');
  }
}

// ============================================================
// AUTO-PLAY CONTROLS
// ============================================================

/**
 * Toggle auto-play on/off
 */
async function toggleAutoPlay() {
  if (favoritesState.autoPlay.enabled) {
    // Stop auto-play
    try {
      await api.post('/api/favorites/auto-play/stop');
      favoritesState.autoPlay.enabled = false;
      favoritesState.autoPlay.currentId = null;
      favoritesState.autoPlay.currentIndex = -1;
      favoritesState.autoPlay.total = 0;
      updateAutoPlayUI();
      renderFavorites();
      window.toast?.success('Auto-Play', 'Stopped');
    } catch (error) {
      console.error('[AUTOPLAY] Failed to stop:', error);
    }
  } else {
    // Start auto-play
    if (favoritesState.favorites.length === 0) {
      window.toast?.error('Auto-Play', 'No favorites to play');
      return;
    }
    
    try {
      const result = await api.post(`/api/favorites/auto-play/start?shuffle=${favoritesState.autoPlay.shuffle}`);
      favoritesState.autoPlay.enabled = true;
      favoritesState.autoPlay.total = result.total;
      updateAutoPlayUI();
      window.toast?.success('Auto-Play', `Playing ${result.total} favorites`);
    } catch (error) {
      console.error('[AUTOPLAY] Failed to start:', error);
      window.toast?.error('Auto-Play', error.message || 'Failed to start');
    }
  }
}

/**
 * Toggle shuffle mode for auto-play
 */
function toggleAutoPlayShuffle() {
  favoritesState.autoPlay.shuffle = !favoritesState.autoPlay.shuffle;
  updateAutoPlayUI();
  window.toast?.success('Auto-Play', `Shuffle: ${favoritesState.autoPlay.shuffle ? 'On' : 'Off'}`);
}

/**
 * Skip to next track in auto-play
 */
async function skipAutoPlay() {
  if (!favoritesState.autoPlay.enabled) return;
  
  try {
    await api.post('/api/favorites/auto-play/skip');
  } catch (error) {
    console.error('[AUTOPLAY] Failed to skip:', error);
    window.toast?.error('Auto-Play', error.message);
  }
}

/**
 * Update auto-play UI state from mediaState (called by status polling)
 */
function updateAutoPlayFromStatus() {
  const s = window.mediaState || {};
  const wasEnabled = favoritesState.autoPlay.enabled;
  const prevId = favoritesState.autoPlay.currentId;
  
  favoritesState.autoPlay.enabled = !!s.autoPlayFavorites;
  favoritesState.autoPlay.currentId = s.autoPlayCurrentId || null;
  favoritesState.autoPlay.currentIndex = s.autoPlayCurrentIndex ?? -1;
  favoritesState.autoPlay.total = s.autoPlayTotal ?? 0;
  
  // Update UI if state changed
  if (wasEnabled !== favoritesState.autoPlay.enabled || prevId !== favoritesState.autoPlay.currentId) {
    updateAutoPlayUI();
    renderFavorites();
  }
}

/**
 * Update auto-play button states and status display
 */
function updateAutoPlayUI() {
  const toggleBtn = document.getElementById('autoplay-toggle-btn');
  const shuffleBtn = document.getElementById('autoplay-shuffle-btn');
  const skipBtn = document.getElementById('autoplay-skip-btn');
  const statusEl = document.getElementById('autoplay-status');
  
  const isActive = favoritesState.autoPlay.enabled;
  
  if (toggleBtn) {
    toggleBtn.classList.toggle('active', isActive);
    toggleBtn.innerHTML = isActive ? '⏹ Stop' : '▶ Auto';
    toggleBtn.title = isActive ? 'Stop Auto-Play' : 'Auto-Play All';
  }
  
  if (shuffleBtn) {
    shuffleBtn.classList.toggle('active', favoritesState.autoPlay.shuffle);
  }
  
  if (skipBtn) {
    skipBtn.style.display = isActive ? '' : 'none';
  }
  
  if (statusEl) {
    if (isActive && favoritesState.autoPlay.total > 0) {
      const idx = favoritesState.autoPlay.currentIndex + 1;
      statusEl.textContent = `${idx}/${favoritesState.autoPlay.total}`;
      statusEl.style.display = '';
    } else {
      statusEl.style.display = 'none';
    }
  }
}

// Expose functions globally BEFORE the module loaded check
window.fetchHistory = fetchHistory;
window.updateAutoPlayFromStatus = updateAutoPlayFromStatus;

console.log('[FAVORITES] Functions exposed globally');

// Initialize - handle case where DOMContentLoaded already fired
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => {
    setTimeout(initFavorites, 100);
  });
} else {
  // DOM already loaded
  setTimeout(initFavorites, 100);
}

console.log('[FAVORITES] Module fully loaded');

// Controls of #tab-media in index.html: wired here instead of inline handlers.
onReady(() => wire(document.getElementById('tab-media'), {
  click: {
    'toggle-auto-play-shuffle': () => toggleAutoPlayShuffle(),
    'skip-auto-play': () => skipAutoPlay(),
    'toggle-auto-play': () => toggleAutoPlay(),
    'clear-history': () => clearHistory()
  }
}));

// Controls of #media-player-bar in index.html: wired here instead of inline handlers.
onReady(() => wire(document.getElementById('media-player-bar'), {
  click: {
    'add-current-to-favorites': () => addCurrentToFavorites()
  }
}));

// The player bar asks again whenever the playing media changes (tabs.js).
window.refreshFavoriteStar = refreshFavoriteStar;
