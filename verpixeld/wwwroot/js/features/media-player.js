/* ============================================================================
   MEDIA PLAYER - Video and audio playback functionality
   ============================================================================ */

// Global media state - exposed immediately for other scripts
window.mediaState = window.mediaState || {};

let mediaState = {
  isRunning: false,
  isPaused: false,
  currentVideo: null,
  currentAudio: null,
  isAudioPlayback: false,
  videoPosition: 0,
  videoDuration: 0,
  videoFps: 0,
  audioAvailable: false,
  isMuted: false,
  volume: 70,
  audioSyncOffsetMs: 0,
  modPlayerAvailable: false,
  isModPlaying: false,
  currentModFile: null,
  selectedModFile: null,
  ffmpegAvailable: false,
  availableVideos: [],
  availableAudioFiles: [],
  availableModFiles: [],
  isNetworkVideo: false,
  networkShareName: null,
  networkFilePath: null,
  networkProtocol: null,
  seekingSupported: true,
  targetCanvasName: 'Main',
  // Playlist state
  playlistIndex: -1,
  playlistCount: 0,
  autoAdvance: true,
  shuffleMode: false,
  repeatMode: false,
  hasNextTrack: false,
  hasPreviousTrack: false,
  // Media metadata (ID3 tags, container metadata)
  metadata: null,
  // Last played file tracking (for restart after stop)
  lastPlayedAudio: null,
  lastPlayedVideo: null,
  // YouTube replay tracking
  lastPlayedYouTubeUrl: null,
  lastPlayedYouTubeTitle: null,
  hasYouTubeReplay: false,
  // Remember if we were in audio mode (for UI after stop)
  wasAudioPlayback: false,
  // Computed properties for UI
  get progress() {
    return this.videoDuration > 0 ? (this.videoPosition / this.videoDuration) * 100 : 0;
  },
  get position() {
    return this.videoPosition;
  },
  get duration() {
    return this.videoDuration;
  },
  // Display title - uses metadata if available, then YouTube title, otherwise filename
  get displayTitle() {
    if (this.metadata?.title) return this.metadata.title;
    if (this.lastPlayedYouTubeTitle && this.hasYouTubeReplay) return this.lastPlayedYouTubeTitle;
    return this.currentAudio || this.currentVideo || 'Unknown';
  },
  // Display artist
  get displayArtist() {
    return this.metadata?.artist || '';
  },
  // Combined display: "Artist - Title" or just "Title"
  get displayString() {
    const title = this.displayTitle;
    return this.displayArtist ? `${this.displayArtist} - ${title}` : title;
  }
};

// Make mediaState globally accessible immediately
window.mediaState = mediaState;

let mediaStatusInterval = null;

/**
 * Play a video
 */
function mediaApiPath(filename) {
  return String(filename || '').split('/').map(encodeURIComponent).join('/');
}

async function playMediaVideo(filename, loop = true) {
  try {
    console.log('[MEDIA] playMediaVideo called:', filename);
    window.toast.info('Media', `Loading ${filename}...`);
    
    // IMMEDIATELY show mini-player bar - DIRECT DOM MANIPULATION
    const bar = document.getElementById('media-player-bar');
    console.log('[MEDIA] Found bar element:', bar);
    if (bar) {
      bar.classList.remove('hidden');
      console.log('[MEDIA] Removed hidden class from bar');
      const title = document.getElementById('media-player-title');
      if (title) title.textContent = `Loading ${filename}...`;
      const icon = document.getElementById('media-player-icon');
      if (icon) icon.textContent = '🎬';
    }
    // Also call the function if available
    if (window.showMediaPlayerBar) window.showMediaPlayerBar();
    
    const result = await api.post(`/api/media/play/${mediaApiPath(filename)}?loop=${loop}`);
    
    if (result.success) {
      window.toast.success('Media', `Playing: ${filename}`);
      startMediaStatusPolling();
      await fetchMediaStatus();
      if (typeof loadCanvasStack === 'function') loadCanvasStack();
      // Refresh favorites history
      if (typeof window.fetchHistory === 'function') window.fetchHistory();
    } else {
      window.toast.error('Media', result.message || 'Failed to play video');
      await fetchMediaStatus();
    }
  } catch (error) {
    console.error('[MEDIA] Failed to play video:', error);
    window.toast.error('Media', 'Failed to play video');
  }
}

/**
 * Stop playback
 * Note: Mini-player bar stays visible so user can restart or navigate playlist
 */
async function stopMediaPlayback() {
  try {
    const result = await api.post('/api/media/stop');
    
    if (result.success) {
      window.toast.info('Media', 'Playback stopped');
      // DON'T stop polling - we need it to detect media started from any source
      // Refresh canvas stack to remove media player indicator
      if (typeof loadCanvasStack === 'function') {
        loadCanvasStack();
      }
    }
    
    await fetchMediaStatus();
    // Update the mini-player bar (it will stay visible with updated state)
    if (typeof updateMediaPlayerBar === 'function') {
      updateMediaPlayerBar();
    }
  } catch (error) {
    console.error('Failed to stop playback:', error);
    window.toast.error('Media', 'Failed to stop');
  }
}

/**
 * Toggle pause/play
 * If stopped and we have a last played file, restart it
 */
async function toggleMediaPause() {
  try {
    // If not running but we have a last played file, restart it
    if (!mediaState.isRunning) {
      // Try YouTube replay first (if we have a last played YouTube URL)
      if (mediaState.hasYouTubeReplay || mediaState.lastPlayedYouTubeUrl) {
        console.log('[MEDIA] Replaying YouTube:', mediaState.lastPlayedYouTubeTitle);
        const result = await api.post('/api/media/youtube/replay');
        
        if (result.success) {
          window.toast?.success('YouTube', `Playing: ${result.title}`);
          startMediaStatusPolling();
          await fetchMediaStatus();
          if (typeof window.fetchHistory === 'function') window.fetchHistory();
        } else {
          window.toast?.error('YouTube', result.message || 'Failed to replay');
        }
        return;
      }
      
      // Try to restart last audio (use replay endpoint which handles local + network)
      if (mediaState.lastPlayedAudio) {
        console.log('[MEDIA] Replaying last audio:', mediaState.lastPlayedAudio);
        const result = await api.post('/api/media/audio/replay');
        
        if (result.success) {
          window.toast?.success('Audio', `Playing: ${result.currentAudio}`);
          startMediaStatusPolling();
          await fetchMediaStatus();
          if (typeof window.fetchHistory === 'function') window.fetchHistory();
        } else {
          window.toast?.error('Audio', result.message || 'Failed to replay');
        }
        return;
      } else if (mediaState.lastPlayedVideo) {
        // Replay video (always local for now)
        const videoFile = mediaState.lastPlayedVideo;
        await playMediaVideo(videoFile);
        return;
      }
      // Nothing to restart
      window.toast?.info('Media', 'No media to play. Select a file first.');
      return;
    }
    
    // If running, toggle pause
    const result = await api.post('/api/media/pause');
    
    if (result.success) {
      mediaState.isPaused = result.isPaused;
      updateMediaUI();
    }
  } catch (error) {
    console.error('Failed to toggle pause:', error);
  }
}

/**
 * Skip to next track
 */
async function playNextTrack() {
  try {
    const result = await api.post('/api/media/next');
    
    if (result.success) {
      window.toast.info('Media', `Playing: ${result.currentAudio}`);
      await fetchMediaStatus();
    } else {
      window.toast.info('Media', result.message || 'End of playlist');
    }
  } catch (error) {
    console.error('Failed to play next:', error);
  }
}

/**
 * Skip to previous track
 */
async function playPreviousTrack() {
  try {
    const result = await api.post('/api/media/previous');
    
    if (result.success) {
      window.toast.info('Media', `Playing: ${result.currentAudio}`);
      await fetchMediaStatus();
    } else {
      window.toast.info('Media', result.message || 'Start of playlist');
    }
  } catch (error) {
    console.error('Failed to play previous:', error);
  }
}

/**
 * Toggle shuffle mode
 */
async function toggleShuffleMode() {
  try {
    const newState = !mediaState.shuffleMode;
    const result = await api.post(`/api/media/playlist/shuffle?enabled=${newState}`);
    
    if (result.success) {
      mediaState.shuffleMode = result.shuffleMode;
      window.toast.info('Media', result.message);
      updateMediaUI();
    }
  } catch (error) {
    console.error('Failed to toggle shuffle:', error);
  }
}

/**
 * Toggle repeat mode
 */
async function toggleRepeatMode() {
  try {
    const newState = !mediaState.repeatMode;
    const result = await api.post(`/api/media/playlist/repeat?enabled=${newState}`);
    
    if (result.success) {
      mediaState.repeatMode = result.repeatMode;
      window.toast.info('Media', result.message);
      updateMediaUI();
    }
  } catch (error) {
    console.error('Failed to toggle repeat:', error);
  }
}

/**
 * Seek to a position in the video (triggered by clicking progress bar)
 */
async function seekMediaVideo(event) {
  if (!mediaState.isRunning || mediaState.videoDuration <= 0) return;
  
  // Check if seeking is supported
  if (!mediaState.seekingSupported) {
    const protocol = mediaState.networkProtocol?.toUpperCase() || 'network';
    window.toast.warning('Media', `Seeking not supported for ${protocol} streams. Use FTP or mount the share locally.`);
    return;
  }
  
  const progressBar = document.getElementById('media-progress-bar');
  if (!progressBar) return;
  
  // Calculate click position as percentage
  const rect = progressBar.getBoundingClientRect();
  const clickX = event.clientX - rect.left;
  const percent = (clickX / rect.width) * 100;
  
  try {
    window.toast.info('Media', `Seeking to ${Math.round(percent)}%...`);
    
    const result = await api.post(`/api/media/seek?percent=${percent}`);
    
    if (result.success) {
      // Update position immediately for responsive feel
      mediaState.videoPosition = result.position || (mediaState.videoDuration * percent / 100);
      updateMediaUI();
    } else {
      // Show appropriate message
      if (result.isNetworkVideo) {
        window.toast.warning('Media', result.message);
      } else {
        window.toast.error('Media', result.message || 'Seek failed');
      }
    }
  } catch (error) {
    console.error('Failed to seek:', error);
  }
}

/**
 * Toggle mute (actually mutes system audio)
 */
async function toggleMediaMute() {
  try {
    const result = await api.post('/api/media/audio/mute');
    
    if (result.success) {
      mediaState.isMuted = result.isMuted;
      updateMediaUI();
      window.toast.info('Audio', result.message);
    }
  } catch (error) {
    console.error('Failed to toggle mute:', error);
  }
}

/**
 * Set volume (0-100)
 * Note: The main implementation is in tabs.js which handles debouncing
 * and refreshing the audio output section. This is kept for compatibility
 * but delegates to the tabs.js version if available.
 */
async function setMediaVolume(volume) {
  try {
    // Update local state immediately
    mediaState.volume = volume;
    
    // Update volume display
    const volumeValue = document.getElementById('media-volume-value');
    if (volumeValue) volumeValue.textContent = `${volume}%`;
    
    // Call both APIs
    const [result] = await Promise.all([
      api.post(`/api/media/audio/volume?volume=${volume}`),
      api.post(`/api/audio/volume?volume=${volume}`)
    ]);
    if (result.success) {
      mediaState.volume = result.volume;
    }
    
    // Refresh audio output section after a short delay
    setTimeout(() => {
      if (window.refreshAudioStatus) {
        window.refreshAudioStatus();
      }
    }, 300);
  } catch (error) {
    console.error('Failed to set volume:', error);
  }
}

/**
 * Set audio sync offset (in milliseconds)
 * Positive = delay audio (when audio is ahead)
 * Negative = delay video (when video is ahead)
 */
async function setAudioSync(offsetMs) {
  try {
    const result = await api.post(`/api/media/audio/sync?offsetMs=${offsetMs}`);
    
    if (result.success) {
      mediaState.audioSyncOffsetMs = result.audioSyncOffsetMs;
      // Update sync display
      const syncValue = document.getElementById('media-sync-value');
      if (syncValue) {
        const ms = result.audioSyncOffsetMs;
        syncValue.textContent = ms === 0 ? '0ms' : (ms > 0 ? `+${ms}ms` : `${ms}ms`);
      }
      // Show note if restart needed
      if (result.note) {
        window.toast.info('Audio Sync', result.note);
      }
    }
  } catch (error) {
    console.error('Failed to set audio sync:', error);
  }
}

/**
 * Set video scale filter
 */
async function setScaleFilter(filter) {
  try {
    const result = await api.post(`/api/media/scale-filter?filter=${encodeURIComponent(filter)}`);
    
    if (result.success) {
      mediaState.scaleFilter = result.scaleFilter;
      window.toast?.success('Scale', result.description);
      if (result.note) {
        window.toast?.info('Scale', result.note);
      }
    } else {
      window.toast?.error('Scale', result.message);
    }
  } catch (error) {
    console.error('Failed to set scale filter:', error);
  }
}

/**
 * Select and play a MOD file
 */
async function selectModFile(filename) {
  if (!filename) {
    await stopModMusic();
    return;
  }
  
  try {
    const result = await api.post(`/api/media/music/${encodeURIComponent(filename)}`);
    
    if (result.success) {
      mediaState.selectedModFile = result.selectedModFile || filename;
      window.toast.success('Music', result.message);
      await fetchMediaStatus();
    } else {
      window.toast.error('Music', result.message || 'Failed to set music');
    }
  } catch (error) {
    console.error('Failed to set MOD:', error);
  }
}

/**
 * Stop MOD music
 */
async function stopModMusic() {
  try {
    const result = await api.del('/api/media/music');
    
    if (result.success) {
      mediaState.selectedModFile = null;
      mediaState.currentModFile = null;
      window.toast.info('Music', 'Music stopped');
      await fetchMediaStatus();
    }
  } catch (error) {
    console.error('Failed to stop music:', error);
  }
}

/**
 * Upload a video file
 */
async function uploadMediaVideo(input) {
  const file = input.files[0];
  if (!file) return;
  
  const formData = new FormData();
  formData.append('file', file);
  
  try {
    window.toast.info('Upload', `Uploading ${file.name}...`);
    
    const result = await api.postForm('/api/media/videos/upload', formData);
    
    if (result.success) {
      window.toast.success('Upload', `Uploaded: ${result.filename}`);
      await fetchMediaStatus();
      browseLocalMedia(localMediaCurrentPath());
    } else {
      window.toast.error('Upload', result.message || 'Upload failed');
    }
  } catch (error) {
    console.error('Failed to upload video:', error);
    window.toast.error('Upload', 'Upload failed');
  }
  
  input.value = '';
}

/**
 * Upload a MOD file
 */
async function uploadModFile(input) {
  const file = input.files[0];
  if (!file) return;
  
  const formData = new FormData();
  formData.append('file', file);
  
  try {
    window.toast.info('Upload', `Uploading ${file.name}...`);
    
    const result = await api.postForm('/api/media/music/upload', formData);
    
    if (result.success) {
      window.toast.success('Upload', `Uploaded: ${result.filename}`);
      await fetchMediaStatus();
      browseLocalMedia(localMediaCurrentPath());
    } else {
      window.toast.error('Upload', result.message || 'Upload failed');
    }
  } catch (error) {
    console.error('Failed to upload MOD:', error);
    window.toast.error('Upload', 'Upload failed');
  }
  
  input.value = '';
}

/**
 * Upload an audio file
 */
async function uploadAudioFile(input) {
  const file = input.files[0];
  if (!file) return;
  
  const formData = new FormData();
  formData.append('file', file);
  
  try {
    window.toast.info('Upload', `Uploading ${file.name}...`);
    
    const result = await api.postForm('/api/media/audio/upload', formData);
    
    if (result.success) {
      window.toast.success('Upload', `Uploaded: ${result.filename}`);
      await fetchMediaStatus();
      browseLocalMedia(localMediaCurrentPath());
    } else {
      window.toast.error('Upload', result.message || 'Upload failed');
    }
  } catch (error) {
    console.error('Failed to upload audio:', error);
    window.toast.error('Upload', 'Upload failed');
  }
  
  input.value = '';
}

/**
 * Play an audio file (with optional visualization)
 */
async function playAudioFile(filename, loop = false) {
  try {
    console.log('[MEDIA] playAudioFile called:', filename);
    window.toast.info('Audio', `Loading ${filename}...`);
    
    // IMMEDIATELY show mini-player bar - DIRECT DOM MANIPULATION
    const bar = document.getElementById('media-player-bar');
    console.log('[MEDIA] Found bar element:', bar);
    if (bar) {
      bar.classList.remove('hidden');
      console.log('[MEDIA] Removed hidden class from bar');
      const title = document.getElementById('media-player-title');
      if (title) title.textContent = `Loading ${filename}...`;
      const icon = document.getElementById('media-player-icon');
      if (icon) icon.textContent = '🎵';
    }
    // Also call the function if available
    if (window.showMediaPlayerBar) window.showMediaPlayerBar();
    
    const result = await api.post(`/api/media/audio/play/${mediaApiPath(filename)}?loop=${loop}`);
    
    if (result.success) {
      window.toast.success('Audio', `Playing: ${filename}`);
      startMediaStatusPolling();
      await fetchMediaStatus();
      // Refresh favorites history
      if (typeof window.fetchHistory === 'function') window.fetchHistory();
    } else {
      window.toast.error('Audio', result.message || 'Failed to play audio');
      await fetchMediaStatus();
    }
  } catch (error) {
    console.error('[MEDIA] Failed to play audio:', error);
    window.toast.error('Audio', 'Failed to play audio');
  }
}

/**
 * Delete an audio file
 */
async function deleteAudioFile(filename) {
  if (!confirm(`Delete ${filename}?`)) return;
  
  try {
    const result = await api.del(`/api/media/audio/${mediaApiPath(filename)}`);
    
    if (result.success) {
      window.toast.success('Audio', `Deleted: ${filename}`);
      await fetchMediaStatus();
      browseLocalMedia(localMediaCurrentPath());
    } else {
      window.toast.error('Audio', result.message || 'Delete failed');
    }
  } catch (error) {
    console.error('Failed to delete audio:', error);
  }
}

/**
 * Delete a video
 */
async function deleteMediaVideo(filename) {
  if (!confirm(`Delete ${filename}?`)) return;
  
  try {
    const result = await api.del(`/api/media/videos/${mediaApiPath(filename)}`);
    
    if (result.success) {
      window.toast.success('Media', `Deleted: ${filename}`);
      await fetchMediaStatus();
      browseLocalMedia(localMediaCurrentPath());
    } else {
      window.toast.error('Media', result.message || 'Delete failed');
    }
  } catch (error) {
    console.error('Failed to delete video:', error);
  }
}

/**
 * Update UI based on state
 */
function updateMediaUI() {
  const statusText = document.getElementById('media-status-text');
  const mediaDot = document.getElementById('media-dot');
  const currentVideoEl = document.getElementById('media-current-video');
  const ffmpegWarning = document.getElementById('media-ffmpeg-warning');
  
  // FFmpeg warning
  if (ffmpegWarning) {
    ffmpegWarning.style.display = mediaState.ffmpegAvailable ? 'none' : 'block';
  }
  
  // Status indicator
  if (mediaState.isRunning) {
    if (mediaDot) mediaDot.classList.add('running');
    if (statusText) statusText.textContent = mediaState.isPaused ? 'Paused' : 'Playing';
  } else {
    if (mediaDot) mediaDot.classList.remove('running');
    if (statusText) statusText.textContent = 'Stopped';
  }
  
  // Current video/audio display
  if (currentVideoEl) {
    if (mediaState.isRunning) {
      if (mediaState.isAudioPlayback && mediaState.currentAudio) {
        const trackInfo = mediaState.playlistCount > 0 
          ? ` (${mediaState.playlistIndex + 1}/${mediaState.playlistCount})`
          : '';
        currentVideoEl.textContent = `🎵 ${mediaState.currentAudio}${trackInfo}`;
      } else if (mediaState.lastPlayedYouTubeTitle && mediaState.hasYouTubeReplay) {
        currentVideoEl.textContent = `📺 ${mediaState.lastPlayedYouTubeTitle}`;
      } else if (mediaState.currentVideo) {
        currentVideoEl.textContent = `🎬 ${mediaState.currentVideo}`;
      } else {
        currentVideoEl.textContent = 'Playing...';
      }
    } else {
      currentVideoEl.textContent = 'No media selected';
    }
  }
  
  // Update playlist control buttons
  // Allow skip buttons if: currently playing audio OR we have a playlist (even if stopped)
  const prevBtn = document.getElementById('media-prev-btn');
  const nextBtn = document.getElementById('media-next-btn');
  const shuffleBtn = document.getElementById('media-shuffle-btn');
  const repeatBtn = document.getElementById('media-repeat-btn');
  
  // hasAudioPlaylist comes from API, playlistCount is fallback
  const hasPlaylist = mediaState.hasAudioPlaylist || mediaState.playlistCount > 0;
  const canSkip = hasPlaylist;
  
  if (prevBtn) {
    prevBtn.disabled = !canSkip;
    prevBtn.classList.toggle('disabled', !canSkip);
  }
  
  if (nextBtn) {
    // Can always skip if we have a playlist (will wrap in repeat mode)
    nextBtn.disabled = !canSkip;
    nextBtn.classList.toggle('disabled', !canSkip);
  }
  
  if (shuffleBtn) {
    shuffleBtn.classList.toggle('active', mediaState.shuffleMode);
  }
  
  if (repeatBtn) {
    repeatBtn.classList.toggle('active', mediaState.repeatMode);
  }
  
  // Audio sync controls (keep this for fine-tuning)
  const syncSlider = document.getElementById('media-sync-slider');
  const syncValue = document.getElementById('media-sync-value');
  
  if (syncSlider && parseInt(syncSlider.value) !== mediaState.audioSyncOffsetMs) {
    syncSlider.value = mediaState.audioSyncOffsetMs;
  }
  
  if (syncValue) {
    const ms = mediaState.audioSyncOffsetMs;
    syncValue.textContent = ms === 0 ? '0ms' : (ms > 0 ? `+${ms}ms` : `${ms}ms`);
  }

  // Scale filter dropdown sync
  const scaleSelect = document.getElementById('media-scale-filter');
  if (scaleSelect && mediaState.scaleFilter && scaleSelect.value !== mediaState.scaleFilter) {
    scaleSelect.value = mediaState.scaleFilter;
  }
  
  // Update lists
  updateVideoList();
  updateModList();
  updateAudioList();
}

/**
 * Update video list UI
 */
function updateVideoList() {
  const container = document.getElementById('media-video-list');
  if (!container) return;
  
  if (mediaState.availableVideos.length === 0) {
    container.innerHTML = `
      <div class="media-empty-state">
        <div class="media-empty-icon">🎬</div>
        <p>No videos yet</p>
        <p class="text-muted">Local Media lists files under Media (Docker: the /app/Media mount). Subfolders are included.</p>
      </div>
    `;
    return;
  }

  container.innerHTML = mediaState.availableVideos.map(video => `
    <div class="media-video-card ${mediaState.currentVideo === video ? 'active' : ''}" data-video="${encodeURIComponent(video)}">
      <div class="media-video-icon">🎬</div>
      <div class="media-video-info">
        <div class="media-video-name">${video.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</div>
      </div>
      <div class="media-video-actions">
        <button class="btn btn-small btn-primary" onclick="playMediaVideo(decodeURIComponent(this.closest('.media-video-card').dataset.video))" title="Play">▶️</button>
        <button class="btn btn-small btn-danger" onclick="deleteMediaVideo(decodeURIComponent(this.closest('.media-video-card').dataset.video))" title="Delete">🗑️</button>
      </div>
    </div>
  `).join('');
}

/**
 * Update MOD list UI
 */
function updateModList() {
  const select = document.getElementById('media-mod-select');
  const musicSection = document.getElementById('media-music-section');
  const musicStatus = document.getElementById('media-music-status');
  
  if (!musicSection) return;
  
  // Get the parent collapsible section (the whole MOD Music section)
  const parentSection = musicSection.closest('.tab-section.collapsible');
  
  // Show/hide entire MOD section based on MOD player availability
  if (mediaState.modPlayerAvailable) {
    // Show the parent section if hidden (but don't override collapse state)
    if (parentSection) {
      parentSection.style.display = '';
    }
    // DON'T set musicSection.style.display here - let collapse logic handle it
    
    // Use selectedModFile for dropdown (what's selected), currentModFile for status (what's playing)
    const selectedFile = mediaState.selectedModFile || mediaState.currentModFile;
    
    if (select) {
      select.innerHTML = '<option value="">-- No music --</option>';
      mediaState.availableModFiles.forEach(file => {
        const option = document.createElement('option');
        option.value = file;
        option.textContent = file;
        if (file === selectedFile) {
          option.selected = true;
        }
        select.appendChild(option);
      });
    }
    
    if (musicStatus) {
      if (mediaState.isModPlaying && mediaState.currentModFile) {
        musicStatus.textContent = `🎵 Playing: ${mediaState.currentModFile}`;
        musicStatus.classList.add('playing');
      } else if (selectedFile) {
        musicStatus.textContent = `Selected: ${selectedFile} (plays with video)`;
        musicStatus.classList.remove('playing');
      } else {
        musicStatus.textContent = 'No music selected';
        musicStatus.classList.remove('playing');
      }
    }
  } else {
    // Hide entire MOD section when mod player not available
    if (parentSection) {
      parentSection.style.display = 'none';
    }
  }
}

/**
 * Update audio file list UI
 */
function updateAudioList() {
  const container = document.getElementById('media-audio-list');
  if (!container) return;
  
  if (!mediaState.availableAudioFiles || mediaState.availableAudioFiles.length === 0) {
    container.innerHTML = `
      <div class="media-empty-state">
        <div class="media-empty-icon">🎵</div>
        <p>No audio files yet</p>
        <p class="text-muted">Upload audio files to play with visualizations</p>
      </div>
    `;
    return;
  }
  
  const isPlayingAudio = mediaState.isAudioPlayback && mediaState.currentAudio;
  
  container.innerHTML = mediaState.availableAudioFiles.map(audio => `
    <div class="media-video-card ${mediaState.currentAudio === audio ? 'active' : ''}" data-audio="${audio}">
      <div class="media-video-icon">🎵</div>
      <div class="media-video-info">
        <div class="media-video-name">${audio}</div>
      </div>
      <div class="media-video-actions">
        <button class="btn btn-small btn-primary" onclick="playAudioFile('${audio}')" title="Play">▶️</button>
        <button class="btn btn-small btn-danger" onclick="deleteAudioFile('${audio}')" title="Delete">🗑️</button>
      </div>
    </div>
  `).join('');
}

/**
 * Fetch media status
 */
async function fetchMediaStatus() {
  try {
    const result = await api.get('/api/media/status');
    const wasRunning = mediaState.isRunning;
    const wasAudio = mediaState.isAudioPlayback;
    
    // Track last played files before updating state
    if (mediaState.currentAudio) {
      mediaState.lastPlayedAudio = mediaState.currentAudio;
    }
    if (mediaState.currentVideo) {
      mediaState.lastPlayedVideo = mediaState.currentVideo;
    }
    if (mediaState.isAudioPlayback) {
      mediaState.wasAudioPlayback = true;
    }
    
    // Update state from API response
    Object.assign(mediaState, result.data);
    
    // Preserve last played info even when stopped
    if (!mediaState.isRunning) {
      // Keep the "was audio" flag for UI purposes
      if (wasAudio || mediaState.wasAudioPlayback) {
        mediaState.wasAudioPlayback = true;
      }
    } else {
      // Reset when something new starts playing
      mediaState.wasAudioPlayback = mediaState.isAudioPlayback;
    }
    
    window.mediaState = mediaState;
    
    // If media just started, show mini-player
    if (mediaState.isRunning && !wasRunning) {
      if (window.showMediaPlayerBar) window.showMediaPlayerBar();
    }
    
    updateMediaUI();
    if (window.updateMediaPlayerBar) window.updateMediaPlayerBar();
    if (window.updateAutoPlayFromStatus) window.updateAutoPlayFromStatus();
    updateAlertUI();
  } catch {
    return;
  }
}

/**
 * Start polling for status
 */
function startMediaStatusPolling() {
  stopMediaStatusPolling();
  // Fetch immediately, then start adaptive polling
  fetchMediaStatus();
  scheduleNextPoll();
}

/**
 * Adaptive polling: 1s when active (playing/alert), 3s when idle
 */
function scheduleNextPoll() {
  if (mediaStatusInterval) clearTimeout(mediaStatusInterval);
  const isActive = mediaState.isRunning || mediaState.alertActive || mediaState.autoPlayFavorites;
  const interval = isActive ? 1000 : 3000;
  mediaStatusInterval = setTimeout(async () => {
    await fetchMediaStatus();
    scheduleNextPoll();
  }, interval);
}

/**
 * Stop polling
 */
function stopMediaStatusPolling() {
  if (mediaStatusInterval) {
    clearTimeout(mediaStatusInterval);
    mediaStatusInterval = null;
  }
}

/**
 * Format duration
 */
function formatDuration(seconds) {
  const mins = Math.floor(seconds / 60);
  const secs = Math.floor(seconds % 60);
  return `${mins}:${secs.toString().padStart(2, '0')}`;
}

// Expose globally
window.mediaState = mediaState;
window.playMediaVideo = playMediaVideo;
window.stopMediaPlayback = stopMediaPlayback;
window.toggleMediaPause = toggleMediaPause;
window.seekMediaVideo = seekMediaVideo;
window.toggleMediaMute = toggleMediaMute;
window.setMediaVolume = setMediaVolume;
window.setAudioSync = setAudioSync;
window.setScaleFilter = setScaleFilter;
window.selectModFile = selectModFile;
window.stopModMusic = stopModMusic;
window.uploadMediaVideo = uploadMediaVideo;
window.uploadModFile = uploadModFile;
window.uploadAudioFile = uploadAudioFile;
window.playAudioFile = playAudioFile;
window.deleteAudioFile = deleteAudioFile;
window.deleteMediaVideo = deleteMediaVideo;
window.fetchMediaStatus = fetchMediaStatus;
window.startMediaStatusPolling = startMediaStatusPolling;
window.stopMediaStatusPolling = stopMediaStatusPolling;
window.updateMediaUI = updateMediaUI;
window.updateAudioList = updateAudioList;
window.playNextTrack = playNextTrack;
window.playPreviousTrack = playPreviousTrack;
window.toggleShuffleMode = toggleShuffleMode;
window.toggleRepeatMode = toggleRepeatMode;

/**
 * Populate the target canvas selector with available canvases
 */
async function populateMediaCanvasSelector() {
  const select = document.getElementById('media-target-canvas');
  if (!select) return;
  
  try {
    const result = await api.get('/api/canvas/stack');
    
    if (result.success && result.data) {
      // Keep current selection or use backend value
      const currentValue = mediaState.targetCanvasName || select.value || 'Main';
      
      // Clear and repopulate
      select.innerHTML = '';
      
      // Sort by z-order (lowest first = bottom to top in layer stack)
      const canvases = (typeof contentTargetCanvases === 'function'
        ? contentTargetCanvases(result.data) : result.data).sort((a, b) => a.zOrder - b.zOrder);
      
      canvases.forEach(canvas => {
        const option = document.createElement('option');
        option.value = canvas.name;
        option.textContent = `${canvas.name} (${canvas.width}×${canvas.height})`;
        if (canvas.name === currentValue) {
          option.selected = true;
        }
        select.appendChild(option);
      });
      
      // If Main was not in the list, add it as default
      if (!canvases.find(c => c.name === 'Main')) {
        const option = document.createElement('option');
        option.value = 'Main';
        option.textContent = 'Main (Default)';
        select.insertBefore(option, select.firstChild);
      }
      
      // Add change listener if not already added
      if (!select.dataset.listenerAdded) {
        select.addEventListener('change', (e) => setMediaTargetCanvas(e.target.value));
        select.dataset.listenerAdded = 'true';
      }
    }
  } catch (error) {
    console.error('Failed to populate canvas selector:', error);
  }
}

/**
 * Set the target canvas for video/audio playback
 */
async function setMediaTargetCanvas(canvasName) {
  try {
    const result = await api.post(`/api/media/target-canvas?canvasName=${encodeURIComponent(canvasName)}`);
    
    if (result.success) {
      mediaState.targetCanvasName = result.targetCanvasName;
      console.log(`[MEDIA] Target canvas set to: ${result.targetCanvasName}`);
    }
  } catch (error) {
    console.error('Failed to set target canvas:', error);
  }
}

/**
 * Switch between Videos and Audio sub-tabs in Local Media section
 */
function switchMediaSubTab(tab) {
  // Update tab buttons
  document.querySelectorAll('.media-sub-tab').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.mediaTab === tab);
  });
  
  // Update panels
  document.querySelectorAll('.media-sub-panel').forEach(panel => {
    panel.style.display = 'none';
    panel.classList.remove('active');
  });
  
  const targetPanel = document.getElementById(`media-sub-${tab}`);
  if (targetPanel) {
    targetPanel.style.display = 'block';
    targetPanel.classList.add('active');
  }
}

function localMediaCurrentPath() {
  const browserEl = document.getElementById('local-media-browser');
  return browserEl?.dataset.currentPath || '';
}

function browseLocalMedia(path = '', forceRefresh = false) {
  const browserEl = document.getElementById('local-media-browser');
  const listEl = document.getElementById('local-media-list');
  const titleEl = document.getElementById('local-media-browser-title');
  const statusEl = document.getElementById('local-media-status');
  if (!browserEl || !listEl) return;

  const rel = path || '';
  browserEl.dataset.currentPath = rel;
  if (titleEl) titleEl.textContent = rel ? `📂 ${rel}` : '📂 Media';
  if (statusEl) statusEl.textContent = '⏳ Loading...';
  const searchInput = document.getElementById('local-media-search-input');
  if (searchInput) searchInput.value = '';
  listEl.innerHTML = '<div class="network-empty-state"><span class="loading">⏳ Loading...</span></div>';

  const params = new URLSearchParams();
  if (rel) params.set('path', rel);
  if (forceRefresh) params.set('refresh', 'true');
  const query = params.toString();

  api.get('/api/media/browse' + (query ? '?' + query : ''))
    .then(result => {
      if (!result.success) {
        if (statusEl) statusEl.textContent = '❌';
        listEl.innerHTML = `
          <div class="network-empty-state">
            <div class="empty-icon">📂</div>
            <p>${escapeHtml(result.message || 'Failed to list folder')}</p>
            <p class="text-muted">${escapeHtml(result.root || '')}</p>
          </div>`;
        return;
      }

      if (statusEl) {
        const n = (result.directories?.length || 0) + (result.videos?.length || 0) + (result.audioFiles?.length || 0);
        statusEl.textContent = `${n} items`;
      }

      let html = '';
      if (result.parentPath !== null && result.parentPath !== undefined) {
        html += `
          <div class="network-video-item network-dir-item" data-path="${encodeURIComponent(result.parentPath)}" onclick="browseLocalMedia(decodeURIComponent(this.dataset.path))">
            <span class="video-icon">⬆️</span>
            <span class="video-name">..</span>
          </div>`;
      }

      (result.directories || []).forEach(dir => {
        html += `
          <div class="network-video-item network-dir-item" data-path="${encodeURIComponent(dir.path)}" onclick="browseLocalMedia(decodeURIComponent(this.dataset.path))">
            <span class="video-icon">📁</span>
            <span class="video-name">${escapeHtml(dir.name)}</span>
          </div>`;
      });

      (result.videos || []).forEach(video => {
        html += `
          <div class="network-video-item" data-path="${encodeURIComponent(video.path)}" onclick="playLocalMediaItem(this.dataset.path, 'video')">
            <span class="video-icon">🎬</span>
            <span class="video-name">${escapeHtml(video.name)}</span>
            <button class="btn btn-small btn-primary" onclick="event.stopPropagation(); playLocalMediaItem(this.closest('.network-video-item').dataset.path, 'video')" title="Play">▶️</button>
          </div>`;
      });

      (result.audioFiles || []).forEach(audio => {
        html += `
          <div class="network-video-item network-audio-item" data-path="${encodeURIComponent(audio.path)}" onclick="playLocalMediaItem(this.dataset.path, 'audio')">
            <span class="video-icon">🎵</span>
            <span class="video-name">${escapeHtml(audio.name)}</span>
            <button class="btn btn-small btn-primary" onclick="event.stopPropagation(); playLocalMediaItem(this.closest('.network-video-item').dataset.path, 'audio')" title="Play">▶️</button>
          </div>`;
      });

      if (!html) {
        html = `
          <div class="network-empty-state">
            <div class="empty-icon">📂</div>
            <p>Empty folder</p>
            <p class="text-muted">Open a subdirectory or check that /app/Media is mounted</p>
          </div>`;
      }

      listEl.innerHTML = html;
    })
    .catch(error => {
      console.error('Failed to browse local media:', error);
      if (statusEl) statusEl.textContent = '❌ Error';
      listEl.innerHTML = `
        <div class="network-empty-state">
          <p>Failed to load folder</p>
          <p class="text-muted">${escapeHtml(error.message || 'Check the Media mount')}</p>
        </div>`;
    });
}

function playLocalMediaItem(encodedPath, kind) {
  const path = decodeURIComponent(encodedPath || '');
  if (kind === 'audio') playAudioFile(path);
  else playMediaVideo(path);
}

function filterLocalMediaBrowser(query) {
  const listEl = document.getElementById('local-media-list');
  if (!listEl) return;
  const items = listEl.querySelectorAll('.network-video-item');
  const lowerQuery = (query || '').toLowerCase().trim();
  let visibleCount = 0;
  items.forEach(item => {
    const nameEl = item.querySelector('.video-name');
    if (!nameEl) return;
    const name = nameEl.textContent;
    if (name === '..') {
      item.style.display = '';
      return;
    }
    const matches = !lowerQuery || name.toLowerCase().includes(lowerQuery);
    item.style.display = matches ? '' : 'none';
    if (matches && lowerQuery) {
      const regex = new RegExp(`(${lowerQuery.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')})`, 'gi');
      nameEl.innerHTML = name.replace(regex, '<mark>$1</mark>');
      visibleCount++;
    } else {
      nameEl.textContent = name;
      if (matches) visibleCount++;
    }
  });
  const statusEl = document.getElementById('local-media-status');
  if (statusEl && lowerQuery) statusEl.textContent = `🔍 ${visibleCount} matches`;
}

/**
 * Switch between media sub-pages (Favorites, Local, Network, YouTube, Tools)
 */
function switchMediaPage(page) {
  // Update nav buttons
  document.querySelectorAll('.media-nav-btn').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.mediaPage === page);
  });
  
  // Update pages
  document.querySelectorAll('.media-page').forEach(p => {
    p.classList.remove('active');
  });
  
  const targetPage = document.getElementById(`media-page-${page}`);
  if (targetPage) {
    targetPage.classList.add('active');
  }
  
  // Persist selection
  try {
    localStorage.setItem('verpixeld-media-page', page);
  } catch (e) { /* ignore */ }

  if (page === 'local') browseLocalMedia(localMediaCurrentPath());
}

// Restore last selected media page on load
(function restoreMediaPage() {
  try {
    const saved = localStorage.getItem('verpixeld-media-page');
    if (saved) {
      // Defer to ensure DOM is ready
      const apply = () => {
        const btn = document.querySelector(`.media-nav-btn[data-media-page="${saved}"]`);
        if (btn) switchMediaPage(saved);
      };
      if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', apply);
      } else {
        setTimeout(apply, 0);
      }
    }
  } catch (e) { /* ignore */ }
})();

// ============================================================================
// YOUTUBE PLAYBACK
// ============================================================================

/**
 * Check YouTube/stream availability and update UI
 */
async function checkYouTubeAvailability() {
  try {
    const result = await api.get('/api/youtube/status');
    
    const unavailableEl = document.getElementById('youtube-unavailable');
    
    if (!result.ytDlpAvailable) {
      // Show warning but don't disable controls - generic streams still work without yt-dlp
      if (unavailableEl) unavailableEl.style.display = 'flex';
    } else {
      if (unavailableEl) unavailableEl.style.display = 'none';
    }
  } catch (error) {
    console.error('Failed to check stream status:', error);
  }
}

/**
 * Play a YouTube video or generic stream URL
 */
async function playYouTubeVideo() {
  const urlInput = document.getElementById('youtube-url');
  const url = urlInput?.value?.trim();
  
  if (!url) {
    window.toast?.error('Stream', 'Please enter a URL');
    return;
  }
  
  // Detect if this is a YouTube URL or a generic stream
  const isYouTube = /youtube\.com|youtu\.be|youtube-nocookie\.com/i.test(url);
  const toastCategory = isYouTube ? 'YouTube' : 'Stream';
  
  // Show loading state with animation
  const statusEl = document.getElementById('youtube-status');
  const infoEl = document.getElementById('youtube-info');
  const controlsEl = document.getElementById('youtube-controls');
  
  // Add loading overlay
  if (controlsEl) {
    controlsEl.classList.add('youtube-loading');
    let loadingOverlay = controlsEl.querySelector('.youtube-loading-overlay');
    if (!loadingOverlay) {
      loadingOverlay = document.createElement('div');
      loadingOverlay.className = 'youtube-loading-overlay';
      loadingOverlay.innerHTML = `<div class="youtube-spinner"></div><div class="youtube-loading-text">${isYouTube ? 'Loading video info...' : 'Connecting to stream...'}</div>`;
      controlsEl.appendChild(loadingOverlay);
    }
  }
  
  if (statusEl) statusEl.textContent = 'Loading...';
  
  try {
    // For YouTube URLs, get video info preview first
    if (isYouTube) {
      const infoResult = await api.post('/api/youtube/info', { url });
      
      if (infoResult.success) {
        showYouTubeInfo(infoResult.video);
        const loadingText = controlsEl?.querySelector('.youtube-loading-text');
        if (loadingText) loadingText.textContent = 'Starting playback...';
      }
    }
    
    // Play it (backend routes YouTube vs generic automatically)
    const playResult = await api.post('/api/youtube/play', { url, loop: !isYouTube }); // loop generic streams by default
    
    // Remove loading overlay
    if (controlsEl) {
      controlsEl.classList.remove('youtube-loading');
      const overlay = controlsEl.querySelector('.youtube-loading-overlay');
      if (overlay) overlay.remove();
    }
    
    if (playResult.success) {
      if (statusEl) statusEl.textContent = 'Playing';
      window.toast?.success(toastCategory, playResult.message);
      
      // Hide YouTube info panel for generic streams
      if (!isYouTube && infoEl) infoEl.style.display = 'none';
      
      // Show mini-player
      if (window.showMediaPlayerBar) window.showMediaPlayerBar();
      
      // Update status
      await fetchMediaStatus();
      // Refresh favorites history
      if (typeof window.fetchHistory === 'function') window.fetchHistory();
    } else {
      if (statusEl) statusEl.textContent = 'Error';
      window.toast?.error(toastCategory, playResult.message || 'Failed to play');
    }
  } catch (error) {
    console.error('Stream play error:', error);
    // Remove loading overlay on error
    if (controlsEl) {
      controlsEl.classList.remove('youtube-loading');
      const overlay = controlsEl.querySelector('.youtube-loading-overlay');
      if (overlay) overlay.remove();
    }
    if (statusEl) statusEl.textContent = 'Error';
    window.toast?.error('Stream', 'Failed to play');
  }
}

/**
 * Show YouTube video info in UI
 */
function showYouTubeInfo(video) {
  const infoEl = document.getElementById('youtube-info');
  const titleEl = document.getElementById('youtube-title');
  const channelEl = document.getElementById('youtube-channel');
  const durationEl = document.getElementById('youtube-duration');
  const formatEl = document.getElementById('youtube-format');
  const thumbnailEl = document.getElementById('youtube-thumbnail');
  
  if (!infoEl) return;
  
  infoEl.style.display = 'flex';
  
  if (titleEl) titleEl.textContent = video.title || 'Unknown';
  if (channelEl) channelEl.textContent = video.channel || '';
  if (durationEl) durationEl.textContent = video.durationFormatted || '';
  
  if (formatEl && video.selectedFormat) {
    const sf = video.selectedFormat;
    formatEl.textContent = `${sf.width}x${sf.height} ${sf.isCombined ? '(combined)' : '(adaptive)'} @ ${sf.bitrate || '?'}kbps`;
  }
  
  if (thumbnailEl && video.thumbnail) {
    thumbnailEl.innerHTML = `<img src="${video.thumbnail}" alt="Thumbnail">`;
  }
}

/**
 * Get YouTube video info without playing
 */
async function getYouTubeInfo(url) {
  try {
    return await api.post('/api/youtube/info', { url });
  } catch (error) {
    console.error('Failed to get YouTube info:', error);
    return { success: false, message: error.message };
  }
}

// Expose YouTube functions globally
window.playYouTubeVideo = playYouTubeVideo;
window.showYouTubeInfo = showYouTubeInfo;
window.getYouTubeInfo = getYouTubeInfo;
window.checkYouTubeAvailability = checkYouTubeAvailability;

// Expose canvas selector functions globally
window.populateMediaCanvasSelector = populateMediaCanvasSelector;
window.setMediaTargetCanvas = setMediaTargetCanvas;
window.switchMediaSubTab = switchMediaSubTab;
window.switchMediaPage = switchMediaPage;
window.browseLocalMedia = browseLocalMedia;
window.filterLocalMediaBrowser = filterLocalMediaBrowser;
window.playLocalMediaItem = playLocalMediaItem;

// ═══════════════════════════════════════════
// CAMERA ALERT
// ═══════════════════════════════════════════

/**
 * Update alert UI based on media status polling data
 */
function updateAlertUI() {
  const banner = document.getElementById('alert-banner');
  const bannerRemaining = document.getElementById('alert-banner-remaining');
  const statusBadge = document.getElementById('alert-status-badge');
  const dismissBtn = document.getElementById('alert-dismiss-btn');
  
  const isActive = mediaState.alertActive;
  const remaining = mediaState.alertRemainingSeconds || 0;
  
  if (banner) {
    banner.style.display = isActive ? 'block' : 'none';
  }
  if (bannerRemaining) {
    bannerRemaining.textContent = remaining;
  }
  if (statusBadge) {
    statusBadge.style.display = isActive ? 'inline-flex' : 'none';
  }
  if (dismissBtn) {
    dismissBtn.style.display = isActive ? 'inline-flex' : 'none';
  }
}

/**
 * Dismiss the camera alert
 */
async function dismissAlert() {
  try {
    const result = await api.post('/api/alert/dismiss');
    if (result.success) {
      showToast('Alert dismissed', 'success');
    }
  } catch (error) {
    console.error('Failed to dismiss alert:', error);
    showToast('Failed to dismiss alert', 'error');
  }
}

/**
 * Save alert configuration
 */
async function saveAlertConfig() {
  const streamUrl = document.getElementById('alert-stream-url')?.value?.trim();
  const timeout = document.getElementById('alert-timeout')?.value;
  const scaleFilter = document.getElementById('alert-scale-filter')?.value;
  
  if (!streamUrl) {
    showToast('Please enter a stream URL', 'warning');
    return;
  }
  
  try {
    const params = new URLSearchParams();
    params.append('streamUrl', streamUrl);
    params.append('timeoutSeconds', timeout);
    params.append('scaleFilter', scaleFilter);
    
    const result = await api.post(`/api/alert/configure?${params}`);
    if (result.success) {
      showToast('Alert configuration saved', 'success');
    } else {
      showToast('Failed to save configuration', 'error');
    }
  } catch (error) {
    console.error('Failed to save alert config:', error);
    showToast('Failed to save alert configuration', 'error');
  }
}

/**
 * Test alert trigger (same as camera webhook would do)
 */
async function testAlertTrigger() {
  try {
    const result = await api.post('/api/alert/trigger');
    if (result.active) {
      showToast('Alert triggered! Camera stream starting...', 'success');
    } else {
      showToast(result.message || 'Alert trigger failed', 'warning');
    }
  } catch (error) {
    console.error('Failed to trigger alert:', error);
    showToast('Failed to trigger alert', 'error');
  }
}

/**
 * Load alert config from server and populate the form
 */
async function loadAlertConfig() {
  try {
    const result = await api.get('/api/alert/status');
    if (result.success) {
      const urlInput = document.getElementById('alert-stream-url');
      const timeoutSlider = document.getElementById('alert-timeout');
      const timeoutValue = document.getElementById('alert-timeout-value');
      const scaleSelect = document.getElementById('alert-scale-filter');
      
      if (urlInput && result.streamUrl) urlInput.value = result.streamUrl;
      if (timeoutSlider && result.timeoutSeconds) {
        timeoutSlider.value = result.timeoutSeconds;
        if (timeoutValue) timeoutValue.textContent = result.timeoutSeconds + 's';
      }
      if (scaleSelect && result.scaleFilter) scaleSelect.value = result.scaleFilter;
    }
  } catch (error) {
    console.error('Failed to load alert config:', error);
  }
}

// Expose alert functions globally
window.dismissAlert = dismissAlert;
window.saveAlertConfig = saveAlertConfig;
window.testAlertTrigger = testAlertTrigger;
window.loadAlertConfig = loadAlertConfig;

// ════════════════════════════════════════════════════════════
// MUSIC SEARCH (YouTube Music)
// ════════════════════════════════════════════════════════════

let musicSearchInProgress = false;
let musicPreferVideo = false; // false = Songs, true = Music Videos

/**
 * Toggle between Songs and Music Videos mode
 */
function setMusicMode(mode) {
  musicPreferVideo = (mode === 'videos');
  document.getElementById('music-mode-songs')?.classList.toggle('active', !musicPreferVideo);
  document.getElementById('music-mode-videos')?.classList.toggle('active', musicPreferVideo);

  // Audio-only option is only relevant for songs (videos always show video)
  const audioOnlyWrapper = document.getElementById('music-audio-only-wrapper');
  if (audioOnlyWrapper) {
    audioOnlyWrapper.style.display = musicPreferVideo ? 'none' : 'flex';
  }

  // If user already has results, re-search with new mode
  const input = document.getElementById('music-search-input');
  if (input?.value?.trim()) {
    searchMusic();
  }
}

/**
 * Search YouTube Music and display results
 */
async function searchMusic() {
  const input = document.getElementById('music-search-input');
  const query = input?.value?.trim();
  if (!query) {
    window.toast?.error('Music', 'Please enter a search term');
    return;
  }

  if (musicSearchInProgress) return;
  musicSearchInProgress = true;

  const statusEl = document.getElementById('music-search-status');
  const resultsEl = document.getElementById('music-search-results');
  const modeLabel = musicPreferVideo ? 'Music Videos' : 'Songs';
  if (statusEl) statusEl.textContent = `Searching ${modeLabel}...`;
  if (resultsEl) resultsEl.innerHTML = `<div class="text-muted" style="padding: 1rem; text-align: center;">Searching ${modeLabel}...</div>`;

  try {
    const data = await api.post('/api/music/search', { query, maxResults: 10, preferVideo: musicPreferVideo });

    if (!data.results || data.results.length === 0) {
      if (resultsEl) resultsEl.innerHTML = '<div class="text-muted" style="padding: 1rem; text-align: center;">No results found</div>';
      if (statusEl) statusEl.textContent = 'No results';
      return;
    }

    renderMusicResults(data.results);
    if (statusEl) statusEl.textContent = `${data.results.length} ${modeLabel.toLowerCase()}`;
  } catch (error) {
    console.error('[MUSIC] Search error:', error);
    if (resultsEl) resultsEl.innerHTML = '<div class="text-muted" style="padding: 1rem; text-align: center; color: var(--danger);">Search failed</div>';
    if (statusEl) statusEl.textContent = 'Error';
    window.toast?.error('Music', 'Search failed');
  } finally {
    musicSearchInProgress = false;
  }
}

/**
 * Render music search results as a list
 */
function renderMusicResults(results) {
  const container = document.getElementById('music-search-results');
  if (!container) return;

  container.innerHTML = results.map((r, i) => {
    const typeIcon = r.type === 'video' ? '🎬' : '🎵';
    const albumInfo = r.album ? ' · ' + escapeHtml(r.album) : '';
    return `
    <div class="music-result" onclick="playMusicResult(${i})" data-url="${encodeURIComponent(r.url)}" data-title="${encodeURIComponent(r.title)}" data-artist="${encodeURIComponent(r.artist)}" data-type="${r.type || 'song'}">
      <div class="music-result-index">${typeIcon}</div>
      <div class="music-result-info">
        <div class="music-result-title">${escapeHtml(r.title)}</div>
        <div class="music-result-artist">${escapeHtml(r.artist)}${albumInfo}</div>
      </div>
      <div class="music-result-duration">${r.duration || ''}</div>
      <div class="music-result-play">▶</div>
    </div>`;
  }).join('');
}

/**
 * Play a specific music search result
 */
async function playMusicResult(index) {
  const container = document.getElementById('music-search-results');
  const resultEl = container?.querySelectorAll('.music-result')[index];
  if (!resultEl) return;

  const url = decodeURIComponent(resultEl.dataset.url);
  const title = decodeURIComponent(resultEl.dataset.title);
  const artist = decodeURIComponent(resultEl.dataset.artist);
  const type = resultEl.dataset.type || 'song';

  // Audio-only: only applies to songs, not videos
  const audioOnlyCheckbox = document.getElementById('music-audio-only');
  const audioOnly = (type === 'song' && audioOnlyCheckbox?.checked) || false;

  // Visual feedback
  resultEl.classList.add('music-result-loading');
  const statusEl = document.getElementById('music-search-status');
  if (statusEl) statusEl.textContent = `Loading: ${title}...`;

  try {
    const data = await api.post('/api/music/play', { url, title, artist, audioOnly });

    if (data.success) {
      const modeNote = audioOnly ? ' (audio only)' : '';
      window.toast?.success('Music', (data.message || `Playing: ${title}`) + modeNote);
      if (statusEl) statusEl.textContent = `Playing: ${title}${modeNote}`;
      startMediaStatusPolling();
      await fetchMediaStatus();
      if (typeof window.fetchHistory === 'function') window.fetchHistory();
    } else {
      window.toast?.error('Music', data.message || 'Failed to play');
      if (statusEl) statusEl.textContent = 'Playback failed';
    }
  } catch (error) {
    console.error('[MUSIC] Play error:', error);
    window.toast?.error('Music', 'Playback failed');
    if (statusEl) statusEl.textContent = 'Error';
  } finally {
    resultEl.classList.remove('music-result-loading');
  }
}

/**
 * Escape HTML entities for safe rendering
 */
function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text || '';
  return div.innerHTML;
}

// Expose music functions globally
window.searchMusic = searchMusic;
window.playMusicResult = playMusicResult;
window.setMusicMode = setMusicMode;

// Initialize on load
document.addEventListener('DOMContentLoaded', () => {
  // Start continuous status polling - essential to detect media from any source
  startMediaStatusPolling();
  populateMediaCanvasSelector();
  
  // Check YouTube availability
  checkYouTubeAvailability();
  
  // Load alert configuration
  loadAlertConfig();
  
  // Re-populate canvas selector when canvases change
  window.addEventListener('layoutChanged', populateMediaCanvasSelector);
});
