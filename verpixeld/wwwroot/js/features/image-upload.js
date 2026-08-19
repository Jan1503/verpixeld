/**
 * Image & Video Upload Feature
 * Handles drag-drop, file selection, image preview, and video frame streaming
 */
'use strict';

let uploadState = {
  file: null,
  type: null,     // 'image' or 'video'
  imageData: null, // data URL of processed image
  videoStreamInterval: null,
};

/**
 * Initialize upload module
 */
function initImageUpload() {
  const dropzone = document.getElementById('upload-dropzone');
  if (!dropzone) return;

  // Drag events
  dropzone.addEventListener('dragover', (e) => {
    e.preventDefault();
    e.stopPropagation();
    dropzone.classList.add('dragover');
  });
  dropzone.addEventListener('dragleave', (e) => {
    e.preventDefault();
    e.stopPropagation();
    dropzone.classList.remove('dragover');
  });
  dropzone.addEventListener('drop', (e) => {
    e.preventDefault();
    e.stopPropagation();
    dropzone.classList.remove('dragover');
    const files = e.dataTransfer.files;
    if (files.length > 0) processFile(files[0]);
  });
  dropzone.addEventListener('click', () => {
    document.getElementById('upload-file-input')?.click();
  });
}

/**
 * Handle file input change
 */
function handleFileUpload(input) {
  if (input.files && input.files[0]) {
    processFile(input.files[0]);
  }
}

/**
 * Process uploaded file
 */
function processFile(file) {
  const isImage = file.type.startsWith('image/');
  const isVideo = file.type.startsWith('video/');

  if (!isImage && !isVideo) {
    window.toast?.error('Upload', 'Unsupported file type. Use images or videos.');
    return;
  }

  uploadState.file = file;
  uploadState.type = isImage ? 'image' : 'video';

  const fileInfo = document.getElementById('upload-file-info');
  if (fileInfo) {
    const sizeMB = (file.size / 1024 / 1024).toFixed(1);
    fileInfo.textContent = `${file.name} (${sizeMB} MB)`;
  }

  if (isImage) {
    loadImagePreview(file);
  } else {
    loadVideoPreview(file);
  }
}

/**
 * Load and preview an image file
 */
function loadImagePreview(file) {
  const reader = new FileReader();
  reader.onload = (e) => {
    const img = new Image();
    img.onload = () => {
      const canvas = document.getElementById('upload-preview-canvas');
      if (!canvas) return;
      const ctx = canvas.getContext('2d');
      
      // Scale to canvas size (384x192)
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
      
      uploadState.imageData = canvas.toDataURL('image/png');
      
      document.getElementById('upload-preview-container').style.display = '';
      document.getElementById('upload-video-controls').style.display = 'none';
    };
    img.src = e.target.result;
  };
  reader.readAsDataURL(file);
}

/**
 * Load and preview a video file
 */
function loadVideoPreview(file) {
  const video = document.getElementById('upload-video-element');
  if (!video) return;

  const url = URL.createObjectURL(file);
  video.src = url;
  
  video.onloadedmetadata = () => {
    const seekbar = document.getElementById('upload-video-seek');
    if (seekbar) {
      seekbar.max = video.duration;
      seekbar.value = 0;
    }
    updateVideoTime();
    
    // Show first frame
    video.currentTime = 0;
  };

  video.onseeked = () => {
    drawVideoFrame();
  };

  video.onloadeddata = () => {
    drawVideoFrame();
    document.getElementById('upload-preview-container').style.display = '';
    document.getElementById('upload-video-controls').style.display = '';
  };
}

/**
 * Draw current video frame to preview canvas
 */
function drawVideoFrame() {
  const video = document.getElementById('upload-video-element');
  const canvas = document.getElementById('upload-preview-canvas');
  if (!video || !canvas) return;

  const ctx = canvas.getContext('2d');
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
  uploadState.imageData = canvas.toDataURL('image/png');
}

/**
 * Seek video to position
 */
function seekUploadVideo(value) {
  const video = document.getElementById('upload-video-element');
  if (!video) return;
  video.currentTime = parseFloat(value);
  updateVideoTime();
}

/**
 * Update video time display
 */
function updateVideoTime() {
  const video = document.getElementById('upload-video-element');
  const display = document.getElementById('upload-video-time');
  if (!video || !display) return;
  
  const t = video.currentTime || 0;
  const min = Math.floor(t / 60);
  const sec = Math.floor(t % 60).toString().padStart(2, '0');
  display.textContent = `${min}:${sec}`;
}

/**
 * Stream video frames to display at selected FPS
 */
function streamUploadVideo() {
  stopUploadVideoStream();
  
  const video = document.getElementById('upload-video-element');
  if (!video) return;

  const fps = parseInt(document.getElementById('upload-video-fps')?.value || '10', 10);
  const canvasName = document.getElementById('upload-target-canvas')?.value || 'Main';

  video.play();
  
  document.getElementById('upload-stream-btn').style.display = 'none';
  document.getElementById('upload-stream-stop-btn').style.display = '';

  uploadState.videoStreamInterval = setInterval(async () => {
    if (video.paused || video.ended) {
      stopUploadVideoStream();
      return;
    }

    drawVideoFrame();
    updateVideoTime();

    // Update seekbar
    const seekbar = document.getElementById('upload-video-seek');
    if (seekbar) seekbar.value = video.currentTime;

    // Send frame to backend
    if (uploadState.imageData) {
      try {
        await window.api.post(`/api/draw/apply/${canvasName}`, { imageData: uploadState.imageData });
      } catch {
        return;
      }
    }
  }, Math.floor(1000 / fps));
}

/**
 * Stop video streaming
 */
function stopUploadVideoStream() {
  if (uploadState.videoStreamInterval) {
    clearInterval(uploadState.videoStreamInterval);
    uploadState.videoStreamInterval = null;
  }
  
  const video = document.getElementById('upload-video-element');
  if (video) video.pause();
  
  document.getElementById('upload-stream-btn') && (document.getElementById('upload-stream-btn').style.display = '');
  document.getElementById('upload-stream-stop-btn') && (document.getElementById('upload-stream-stop-btn').style.display = 'none');
}

/**
 * Apply current image/frame to the display
 */
async function applyUploadedImage() {
  if (!uploadState.imageData) {
    window.toast?.error('Upload', 'No image loaded. Upload a file first.');
    return;
  }

  const canvasName = document.getElementById('upload-target-canvas')?.value || 'Main';
  
  try {
    await window.api.post(`/api/draw/apply/${canvasName}`, { imageData: uploadState.imageData });
    window.toast?.success('Upload', 'Image applied to display');
  } catch (err) {
    window.toast?.error('Upload', err.message);
  }
}

/**
 * Clear uploaded image
 */
function clearUploadedImage() {
  stopUploadVideoStream();
  uploadState.file = null;
  uploadState.type = null;
  uploadState.imageData = null;

  const canvas = document.getElementById('upload-preview-canvas');
  if (canvas) {
    const ctx = canvas.getContext('2d');
    ctx.clearRect(0, 0, canvas.width, canvas.height);
  }

  document.getElementById('upload-preview-container').style.display = 'none';
  document.getElementById('upload-video-controls').style.display = 'none';
  document.getElementById('upload-file-info') && (document.getElementById('upload-file-info').textContent = '--');

  // Reset file input
  const input = document.getElementById('upload-file-input');
  if (input) input.value = '';
}

// Expose globally
window.handleFileUpload = handleFileUpload;
window.applyUploadedImage = applyUploadedImage;
window.clearUploadedImage = clearUploadedImage;
window.seekUploadVideo = seekUploadVideo;
window.streamUploadVideo = streamUploadVideo;
window.stopUploadVideoStream = stopUploadVideoStream;

// Initialize
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initImageUpload);
} else {
  initImageUpload();
}
