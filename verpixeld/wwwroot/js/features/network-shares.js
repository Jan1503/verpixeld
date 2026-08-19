/* ============================================================================
   NETWORK SHARES - SMB/CIFS Configuration Management
   ============================================================================ */

let networkShares = [];
let editingShareId = null;
let networkStreamingSupported = true;
let smbSupportMessage = null;
let currentSearchTimeout = null;

/**
 * Fetch all network shares
 */
async function fetchNetworkShares() {
  try {
    const result = await api.get('/api/network/shares');
    networkShares = result.shares;
    networkStreamingSupported = result.networkStreamingSupported !== false;
    smbSupportMessage = result.smbSupportMessage;
    updateNetworkSharesUI();
  } catch (error) {
    console.error('Failed to fetch network shares:', error);
  }
}

/**
 * Add a new network share (SMB only)
 */
async function addNetworkShare() {
  const name = document.getElementById('share-name').value.trim();
  const server = document.getElementById('share-server').value.trim();
  const sharePath = document.getElementById('share-path').value.trim();
  const domain = document.getElementById('share-domain').value.trim();
  const username = document.getElementById('share-username').value.trim();
  const password = document.getElementById('share-password').value;
  
  if (!name || !server) {
    window.toast.error('Network', 'Name and server are required');
    return;
  }
  
  try {
    const result = await api.post('/api/network/shares', { name, server, sharePath, domain, username, password });
    window.toast.success('Network', `Added SMB share: ${result.share.name}`);
    hideShareForm();
    await fetchNetworkShares();
  } catch (error) {
    console.error('Failed to add share:', error);
    window.toast.error('Network', error.message || 'Failed to add share');
  }
}

/**
 * Update an existing network share (SMB only)
 */
async function updateNetworkShare() {
  if (!editingShareId) return;
  
  const name = document.getElementById('share-name').value.trim();
  const server = document.getElementById('share-server').value.trim();
  const sharePath = document.getElementById('share-path').value.trim();
  const domain = document.getElementById('share-domain').value.trim();
  const username = document.getElementById('share-username').value.trim();
  const password = document.getElementById('share-password').value;
  
  try {
    await api.put('/api/network/shares/' + editingShareId, { name, server, sharePath, domain, username, password: password || undefined });
    window.toast.success('Network', 'Share updated');
    hideShareForm();
    await fetchNetworkShares();
  } catch (error) {
    console.error('Failed to update share:', error);
    window.toast.error('Network', error.message || 'Failed to update share');
  }
}

/**
 * Delete a network share
 */
async function deleteNetworkShare(id) {
  if (!confirm('Delete this network share?')) return;
  
  try {
    await api.del('/api/network/shares/' + id);
    window.toast.success('Network', 'Share removed');
    await fetchNetworkShares();
  } catch (error) {
    console.error('Failed to delete share:', error);
  }
}

/**
 * Test connection to a share
 */
async function testNetworkShare(id) {
  const statusEl = document.getElementById(`share-status-${id}`);
  if (statusEl) {
    statusEl.className = 'connection-status testing';
    statusEl.textContent = '⏳ Testing...';
  }
  
  try {
    await api.post('/api/network/shares/' + id + '/test');
    if (statusEl) {
      statusEl.className = 'connection-status success';
      statusEl.textContent = '✓ Connected';
    }
    window.toast.success('Network', 'Connection successful');
  } catch (error) {
    if (statusEl) {
      statusEl.className = 'connection-status error';
      statusEl.textContent = '✗ Failed';
    }
    window.toast.error('Network', error.message || 'Connection failed');
    console.error('Failed to test connection:', error);
  }
}

/**
 * Browse a directory on a network share
 * @param {string} shareId - Share ID
 * @param {string} path - Directory path
 * @param {boolean} forceRefresh - Force refresh from network (bypass cache)
 */
async function browseNetworkVideos(shareId, path = '', forceRefresh = false) {
  const browserEl = document.getElementById('network-video-browser');
  const listEl = document.getElementById('network-video-list');
  const share = networkShares.find(s => s.id === shareId);
  
  if (!browserEl || !listEl || !share) return;
  
  // Show browser
  browserEl.style.display = 'block';
  browserEl.dataset.shareId = shareId;
  browserEl.dataset.currentPath = path;
  
  const pathDisplay = path ? `/${path}` : '';
  const titleEl = document.getElementById('network-browser-title');
  titleEl.innerHTML = `📂 ${share.name}${pathDisplay}`;
  
  // Clear search input when browsing
  const searchInput = document.getElementById('network-search-input');
  if (searchInput) searchInput.value = '';
  
  // Show search/filter box for all protocols (client-side filtering)
  const searchBox = document.getElementById('network-search-box');
  if (searchBox) {
    searchBox.style.display = 'flex';
    if (searchInput) searchInput.dataset.shareId = shareId;
  }
  
  // Update cache status indicator
  const cacheStatusEl = document.getElementById('network-cache-status');
  if (cacheStatusEl) cacheStatusEl.textContent = forceRefresh ? '🔄 Refreshing...' : '⏳ Loading...';
  
  listEl.innerHTML = '<div class="network-empty-state"><span class="loading">⏳ Loading...</span></div>';
  
  try {
    const params = new URLSearchParams();
    if (path) params.append('path', path);
    if (forceRefresh) params.append('refresh', 'true');
    const query = params.toString();
    const url = '/api/network/shares/' + shareId + '/browse' + (query ? '?' + query : '');
    const result = await api.get(url);
    
    // Update cache status
    if (cacheStatusEl) {
      cacheStatusEl.textContent = result.fromCache ? '📋 Cached' : '🌐 Fresh';
      cacheStatusEl.title = result.fromCache ? 'Loaded from cache (click 🔄 to refresh)' : 'Freshly loaded from network';
    }
    
    let html = '';
    
    // Add "Go up" button if not at root
    if (result.parentPath !== null && result.parentPath !== undefined) {
      html += `
        <div class="network-video-item network-dir-item" onclick="browseNetworkVideos('${shareId}', '${result.parentPath}')">
          <span class="video-icon">⬆️</span>
          <span class="video-name">..</span>
        </div>
      `;
    }
    
    // Add directories
    if (result.directories && result.directories.length > 0) {
      html += result.directories.map(dir => `
        <div class="network-video-item network-dir-item" onclick="browseNetworkVideos('${shareId}', '${dir.path}')">
          <span class="video-icon">📁</span>
          <span class="video-name">${dir.name}</span>
        </div>
      `).join('');
    }
    
    // Add videos
    if (result.videos && result.videos.length > 0) {
      html += result.videos.map(video => `
        <div class="network-video-item" onclick="playNetworkVideo('${shareId}', '${video.path}')">
          <span class="video-icon">🎬</span>
          <span class="video-name">${video.name}</span>
          <button class="btn btn-small btn-primary" onclick="event.stopPropagation(); playNetworkVideo('${shareId}', '${video.path}')" title="Play">▶️</button>
        </div>
      `).join('');
    }
    
    // Add audio files
    if (result.audioFiles && result.audioFiles.length > 0) {
      html += result.audioFiles.map(audio => `
        <div class="network-video-item network-audio-item" onclick="playNetworkAudio('${shareId}', '${audio.path}')">
          <span class="video-icon">🎵</span>
          <span class="video-name">${audio.name}</span>
          <button class="btn btn-small btn-primary" onclick="event.stopPropagation(); playNetworkAudio('${shareId}', '${audio.path}')" title="Play">▶️</button>
        </div>
      `).join('');
    }
    
    if (!html) {
      html = `
        <div class="network-empty-state">
          <div class="empty-icon">📂</div>
          <p>Empty directory</p>
          <p class="text-muted">No media files or subdirectories found</p>
        </div>
      `;
    }
    
    listEl.innerHTML = html;
    
  } catch (error) {
    console.error('Failed to browse directory:', error);
    if (cacheStatusEl) cacheStatusEl.textContent = '❌ Error';
    listEl.innerHTML = `
      <div class="network-empty-state">
        <p>Failed to load directory</p>
        <p class="text-muted">Check network connection and server availability</p>
      </div>
    `;
  }
}

/**
 * Filter current directory listing (client-side, instant)
 */
function filterNetworkBrowser(query) {
  const listEl = document.getElementById('network-video-list');
  if (!listEl) return;
  
  const items = listEl.querySelectorAll('.network-video-item');
  const lowerQuery = query.toLowerCase().trim();
  let visibleCount = 0;
  
  items.forEach(item => {
    // Always show "go up" item
    const nameEl = item.querySelector('.video-name');
    if (!nameEl) return;
    
    const name = nameEl.textContent;
    if (name === '..') {
      item.style.display = '';
      return;
    }
    
    // Check if name matches filter
    const matches = !lowerQuery || name.toLowerCase().includes(lowerQuery);
    item.style.display = matches ? '' : 'none';
    
    // Highlight matching text
    if (matches && lowerQuery) {
      const regex = new RegExp(`(${lowerQuery.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')})`, 'gi');
      nameEl.innerHTML = name.replace(regex, '<mark>$1</mark>');
      visibleCount++;
    } else {
      nameEl.textContent = name; // Remove highlighting
      if (matches) visibleCount++;
    }
  });
  
  // Update status
  const cacheStatusEl = document.getElementById('network-cache-status');
  if (cacheStatusEl) {
    if (lowerQuery) {
      cacheStatusEl.textContent = `🔍 ${visibleCount} matches`;
    } else {
      cacheStatusEl.textContent = ''; // Clear when no filter
    }
  }
}

/**
 * Handle search input (instant client-side filtering)
 */
function handleNetworkSearchInput(shareId, inputEl) {
  filterNetworkBrowser(inputEl.value);
}

/**
 * Clear search filter
 */
function clearNetworkSearch(shareId) {
  const searchInput = document.getElementById('network-search-input');
  if (searchInput) searchInput.value = '';
  filterNetworkBrowser('');
}

/**
 * Refresh the current directory (force refresh from network)
 */
function refreshNetworkDirectory() {
  const browserEl = document.getElementById('network-video-browser');
  if (!browserEl) return;
  
  const shareId = browserEl.dataset.shareId;
  const currentPath = browserEl.dataset.currentPath || '';
  
  if (shareId) {
    browseNetworkVideos(shareId, currentPath, true);
  }
}

/**
 * Clear all directory cache
 */
async function clearNetworkCache() {
  try {
    await api.post('/api/network/cache/clear');
    window.toast.success('Network', 'Directory cache cleared');
    refreshNetworkDirectory();
  } catch (error) {
    console.error('Failed to clear cache:', error);
    window.toast.error('Network', 'Failed to clear cache');
  }
}

/**
 * Play a video from a network share
 */
async function playNetworkVideo(shareId, filePath) {
  try {
    window.toast.info('Network', `Loading video: ${filePath}...`);
    
    await api.post('/api/network/shares/' + shareId + '/play/' + encodeURIComponent(filePath));
    window.toast.success('Network', `Playing: ${filePath}`);
    
    if (window.showMediaPlayerBar) window.showMediaPlayerBar();
    if (typeof fetchMediaStatus === 'function') await fetchMediaStatus();
    if (typeof loadCanvasStack === 'function') loadCanvasStack();
    if (typeof window.fetchHistory === 'function') {
      window.fetchHistory();
      setTimeout(() => {
        if (typeof window.fetchHistory === 'function') window.fetchHistory();
      }, 5000);
    }
  } catch (error) {
    console.error('Failed to play network video:', error);
    window.toast.error('Network', error.message || 'Failed to play video');
  }
}

/**
 * Play an audio file from a network share
 */
async function playNetworkAudio(shareId, filePath) {
  try {
    window.toast.info('Network', `Loading audio: ${filePath}...`);
    
    await api.post('/api/network/shares/' + shareId + '/play-audio/' + encodeURIComponent(filePath));
    window.toast.success('Network', `Playing: ${filePath}`);
    
    if (window.showMediaPlayerBar) window.showMediaPlayerBar();
    if (typeof fetchMediaStatus === 'function') await fetchMediaStatus();
    if (typeof window.fetchHistory === 'function') window.fetchHistory();
  } catch (error) {
    console.error('Failed to play network audio:', error);
    window.toast.error('Network', error.message || 'Failed to play audio');
  }
}

/**
 * Show the add/edit share form (SMB only)
 */
function showShareForm(shareId = null) {
  const form = document.getElementById('network-share-form');
  const title = document.getElementById('share-form-title');
  const submitBtn = document.getElementById('share-form-submit');
  
  if (!form) return;
  
  // Reset form
  document.getElementById('share-name').value = '';
  document.getElementById('share-server').value = '';
  document.getElementById('share-path').value = '';
  document.getElementById('share-domain').value = '';
  document.getElementById('share-username').value = '';
  document.getElementById('share-password').value = '';
  
  if (shareId) {
    // Edit mode
    editingShareId = shareId;
    const share = networkShares.find(s => s.id === shareId);
    if (share) {
      document.getElementById('share-name').value = share.name;
      document.getElementById('share-server').value = share.server;
      document.getElementById('share-path').value = share.sharePath || '';
      document.getElementById('share-domain').value = share.domain || '';
      document.getElementById('share-username').value = share.username || '';
    }
    title.textContent = '✏️ Edit Network Share';
    submitBtn.textContent = 'Update Share';
    submitBtn.onclick = updateNetworkShare;
  } else {
    // Add mode
    editingShareId = null;
    title.textContent = '➕ Add SMB Share';
    submitBtn.textContent = 'Add Share';
    submitBtn.onclick = addNetworkShare;
  }
  
  form.classList.add('visible');
}

/**
 * Hide the share form
 */
function hideShareForm() {
  const form = document.getElementById('network-share-form');
  if (form) {
    form.classList.remove('visible');
    editingShareId = null;
  }
}

/**
 * Hide the video browser
 */
function hideVideoBrowser() {
  const browser = document.getElementById('network-video-browser');
  if (browser) {
    browser.style.display = 'none';
  }
}

/**
 * Update the network shares UI (SMB only)
 */
function updateNetworkSharesUI() {
  const container = document.getElementById('network-share-list');
  const warningEl = document.getElementById('network-smb-warning');
  if (!container) return;
  
  // Show streaming status/warning
  if (warningEl) {
    if (!networkStreamingSupported) {
      // No streaming available at all
      warningEl.style.display = 'flex';
      warningEl.className = 'media-warning';
      warningEl.innerHTML = `
        <span class="media-warning-icon">⚠️</span>
        <div>
          <strong>SMB streaming not available</strong><br>
          <span class="text-muted">Install smbclient for SMB browsing:</span><br>
          <code>sudo apt install smbclient</code><br>
          <span class="text-muted">For best performance, compile FFmpeg with libsmbclient support.</span>
        </div>
      `;
    } else {
      // FFmpeg has native SMB support
      warningEl.style.display = 'flex';
      warningEl.className = 'media-info';
      warningEl.innerHTML = `
        <span class="media-warning-icon">✅</span>
        <span>FFmpeg has native SMB support - full functionality available</span>
      `;
    }
  }
  
  if (networkShares.length === 0) {
    container.innerHTML = `
      <div class="network-empty-state">
        <div class="empty-icon">📁</div>
        <p>No SMB shares configured</p>
        <p class="text-muted">Add an SMB share to stream videos from your network</p>
      </div>
    `;
    return;
  }
  
  container.innerHTML = networkShares.map(share => `
    <div class="network-share-card ${share.isDefault ? 'default' : ''}" data-id="${share.id}">
      <div class="network-share-icon">📁</div>
      <div class="network-share-info">
        <div class="network-share-name">
          ${share.name}
          <span class="badge badge-protocol">SMB</span>
          ${share.isDefault ? '<span class="badge">Default</span>' : ''}
          <span id="share-status-${share.id}" class="connection-status"></span>
        </div>
        <div class="network-share-url">${share.displayUrl}</div>
      </div>
      <div class="network-share-actions">
        <button class="btn btn-small" onclick="browseNetworkVideos('${share.id}')" title="Browse videos">📂</button>
        <button class="btn btn-small" onclick="testNetworkShare('${share.id}')" title="Test connection">🔌</button>
        <button class="btn btn-small" onclick="showShareForm('${share.id}')" title="Edit">✏️</button>
        <button class="btn btn-small btn-danger" onclick="deleteNetworkShare('${share.id}')" title="Delete">🗑️</button>
      </div>
    </div>
  `).join('');
}

// Expose functions globally
window.fetchNetworkShares = fetchNetworkShares;
window.addNetworkShare = addNetworkShare;
window.updateNetworkShare = updateNetworkShare;
window.deleteNetworkShare = deleteNetworkShare;
window.testNetworkShare = testNetworkShare;
window.browseNetworkVideos = browseNetworkVideos;
window.playNetworkVideo = playNetworkVideo;
window.playNetworkAudio = playNetworkAudio;
window.showShareForm = showShareForm;
window.hideShareForm = hideShareForm;
window.hideVideoBrowser = hideVideoBrowser;
window.refreshNetworkDirectory = refreshNetworkDirectory;
window.clearNetworkCache = clearNetworkCache;
window.handleNetworkSearchInput = handleNetworkSearchInput;
window.filterNetworkBrowser = filterNetworkBrowser;
window.clearNetworkSearch = clearNetworkSearch;

// Initialize on load
document.addEventListener('DOMContentLoaded', () => {
  fetchNetworkShares();
});
