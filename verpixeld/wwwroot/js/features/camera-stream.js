// ==========================================================================
// CAMERA STREAM MODULE
// Live camera streaming to LED matrix canvas
// ==========================================================================

const CameraStream = (function() {
  // State
  let videoStream = null;
  let streamInterval = null;
  let isStreaming = false;
  let streamTarget = null; // canvas the active stream is pinned to
  let actualFps = 0;
  let frameCount = 0;
  let lastFpsUpdate = 0;

  // DOM Elements (cached on init)
  let video = null;
  let processCanvas = null;
  let processCtx = null;
  let previewCanvas = null;
  let previewCtx = null;
  let statusEl = null;
  let statusText = null;
  let previewWrapper = null;
  let streamIndicator = null;
  let fpsDisplay = null;
  let resolutionDisplay = null;

  // Canvas dimensions (will be fetched from selected canvas)
  let targetWidth = 64;
  let targetHeight = 32;

  /**
   * Initialize the camera stream module
   */
  function init() {
    // Cache DOM elements
    video = document.getElementById('camera-video');
    processCanvas = document.getElementById('camera-process-canvas');
    previewCanvas = document.getElementById('camera-preview-canvas');
    statusEl = document.getElementById('camera-status');
    statusText = statusEl?.querySelector('.camera-status-text');
    previewWrapper = document.getElementById('camera-preview-wrapper');
    streamIndicator = document.getElementById('camera-stream-indicator');
    fpsDisplay = document.getElementById('camera-stream-fps');
    resolutionDisplay = document.getElementById('camera-resolution');

    if (processCanvas) processCtx = processCanvas.getContext('2d', { willReadFrequently: true });
    if (previewCanvas) previewCtx = previewCanvas.getContext('2d');

    // Check if camera is available
    if (!window.isSecureContext) {
      setStatus('Camera requires HTTPS connection', 'error');
      const startBtn = document.getElementById('camera-start-btn');
      if (startBtn) {
        startBtn.disabled = true;
        startBtn.title = 'Camera requires HTTPS';
      }
      console.warn('CameraStream: HTTPS required for camera access');
    } else if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      setStatus('Camera not supported in this browser', 'error');
      const startBtn = document.getElementById('camera-start-btn');
      if (startBtn) {
        startBtn.disabled = true;
        startBtn.title = 'Camera not supported';
      }
      console.warn('CameraStream: MediaDevices API not supported');
    }

    // Attach slider event listeners
    attachSliderListeners();

    // Update target canvas dropdown when layout changes
    updateCameraTargetCanvases();

    // Stop streaming if the pinned target canvas is removed (e.g. deleted in the Studio) so the
    // stream doesn't drift onto Main and fight with its content.
    window.addEventListener('layoutChanged', onLayoutChangedCheckTarget);

    console.log('CameraStream module initialized');
  }

  async function onLayoutChangedCheckTarget() {
    if (!isStreaming || !streamTarget) return;
    try {
      const r = await window.api.get('/api/layout/current');
      const names = (r.data.canvases || []).map(c => c.name);
      if (!names.includes(streamTarget)) {
        stop();
        if (window.toast) toast.warning('Camera', `Target canvas "${streamTarget}" was removed — streaming stopped`);
      }
    } catch (e) { /* ignore */ }
  }

  /**
   * Attach event listeners for adjustment sliders
   */
  function attachSliderListeners() {
    const sliders = [
      { id: 'camera-brightness', valueId: 'camera-brightness-value' },
      { id: 'camera-contrast', valueId: 'camera-contrast-value' },
      { id: 'camera-saturation', valueId: 'camera-saturation-value' }
    ];

    sliders.forEach(({ id, valueId }) => {
      const slider = document.getElementById(id);
      const valueDisplay = document.getElementById(valueId);
      if (slider && valueDisplay) {
        slider.addEventListener('input', () => {
          valueDisplay.textContent = slider.value;
        });
      }
    });
  }

  /**
   * Update the target canvas dropdown with available canvases
   */
  async function updateCameraTargetCanvases() {
    const select = document.getElementById('camera-target-canvas');
    if (!select) return;

    try {
      const result = await window.api.get('/api/layout/current');
      if (result.data.canvases) {
        const currentValue = select.value;
        select.innerHTML = result.data.canvases.map(canvas => 
          `<option value="${canvas.name}">${canvas.name} (${canvas.width}×${canvas.height})</option>`
        ).join('');
        
        // Restore previous selection if still available
        if ([...select.options].some(opt => opt.value === currentValue)) {
          select.value = currentValue;
        }
        
        // Update target dimensions
        updateTargetDimensions();
      }
    } catch {
      return;
    }

    // Update dimensions when selection changes
    select.addEventListener('change', updateTargetDimensions);
  }

  /**
   * Update target dimensions based on selected canvas
   */
  async function updateTargetDimensions() {
    const select = document.getElementById('camera-target-canvas');
    if (!select) return;

    const canvasName = select.value;
    
    try {
      const result = await window.api.get('/api/layout/current');
      if (result.data.canvases) {
        const canvas = result.data.canvases.find(c => c.name === canvasName);
        if (canvas) {
          targetWidth = canvas.width;
          targetHeight = canvas.height;
          
          if (resolutionDisplay) {
            resolutionDisplay.textContent = `${targetWidth} × ${targetHeight}`;
          }

          // Update preview canvas dimensions
          if (previewCanvas) {
            previewCanvas.width = targetWidth;
            previewCanvas.height = targetHeight;
          }

          console.log(`Camera target dimensions: ${targetWidth}×${targetHeight}`);
        }
      }
    } catch {
      return;
    }
  }

  /**
   * Start camera stream
   */
  async function start() {
    if (isStreaming) {
      console.log('Camera already streaming');
      return;
    }

    // Check for secure context (HTTPS required for camera access)
    if (!window.isSecureContext) {
      const errorMsg = 'Camera access requires HTTPS. Please access this page via HTTPS or localhost.';
      console.error(errorMsg);
      setStatus(errorMsg, 'error');
      toast.error('HTTPS Required', 'Camera access requires a secure connection (HTTPS)');
      return;
    }

    // Check for MediaDevices API support
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      const errorMsg = 'Camera API not supported in this browser';
      console.error(errorMsg);
      setStatus(errorMsg, 'error');
      toast.error('Not Supported', 'Your browser does not support camera access');
      return;
    }

    const cameraSource = document.getElementById('camera-source')?.value || 'user';
    const fps = parseInt(document.getElementById('camera-fps')?.value || '10', 10);

    try {
      setStatus('Accessing camera...', 'loading');

      // Request camera access
      const constraints = {
        video: {
          facingMode: cameraSource,
          width: { ideal: 640 },
          height: { ideal: 480 }
        },
        audio: false
      };

      videoStream = await navigator.mediaDevices.getUserMedia(constraints);
      video.srcObject = videoStream;
      
      await new Promise((resolve) => {
        video.onloadedmetadata = () => {
          video.play();
          resolve();
        };
      });

      // Update target dimensions
      await updateTargetDimensions();

      // Setup processing canvas
      if (processCanvas) {
        processCanvas.width = targetWidth;
        processCanvas.height = targetHeight;
      }

      // Start streaming — pin to the chosen target so it can't silently drift to Main if the
      // dropdown re-defaults (e.g. when the target canvas is removed in the Studio).
      streamTarget = document.getElementById('camera-target-canvas')?.value || 'Main';
      isStreaming = true;
      frameCount = 0;
      lastFpsUpdate = performance.now();

      // Calculate interval from FPS
      const interval = Math.floor(1000 / fps);
      streamInterval = setInterval(captureAndStreamFrame, interval);

      // Update UI
      setStatus('Streaming', 'streaming');
      previewWrapper?.classList.add('streaming');
      streamIndicator?.classList.add('active');
      
      document.getElementById('camera-start-btn').disabled = true;
      document.getElementById('camera-stop-btn').disabled = false;

      toast.success('Camera Started', 'Streaming to display');
      console.log(`Camera streaming started at ${fps} FPS`);

    } catch (error) {
      console.error('Failed to start camera:', error);
      
      let errorMessage = 'Could not access camera';
      let statusMessage = 'Camera access denied or unavailable';
      
      if (error.name === 'NotAllowedError' || error.name === 'PermissionDeniedError') {
        errorMessage = 'Camera permission denied. Please allow camera access and try again.';
        statusMessage = 'Camera permission denied';
      } else if (error.name === 'NotFoundError' || error.name === 'DevicesNotFoundError') {
        errorMessage = 'No camera found on this device';
        statusMessage = 'No camera found';
      } else if (error.name === 'NotReadableError' || error.name === 'TrackStartError') {
        errorMessage = 'Camera is already in use by another application';
        statusMessage = 'Camera in use';
      } else if (error.name === 'OverconstrainedError') {
        errorMessage = 'Camera does not support requested settings';
        statusMessage = 'Camera settings not supported';
      } else if (error.name === 'TypeError') {
        errorMessage = 'Camera API not available. Try using HTTPS.';
        statusMessage = 'HTTPS required for camera access';
      }
      
      setStatus(statusMessage, 'error');
      toast.error('Camera Error', errorMessage);
    }
  }

  /**
   * Stop camera stream
   */
  function stop() {
    if (streamInterval) {
      clearInterval(streamInterval);
      streamInterval = null;
    }

    if (videoStream) {
      videoStream.getTracks().forEach(track => track.stop());
      videoStream = null;
    }

    if (video) {
      video.srcObject = null;
    }

    isStreaming = false;
    streamTarget = null;

    // Update UI
    setStatus('Click "Start" to begin streaming', 'idle');
    previewWrapper?.classList.remove('streaming');
    streamIndicator?.classList.remove('active');
    
    if (fpsDisplay) fpsDisplay.textContent = '--';

    document.getElementById('camera-start-btn').disabled = false;
    document.getElementById('camera-stop-btn').disabled = true;

    // Clear preview
    if (previewCtx && previewCanvas) {
      previewCtx.fillStyle = '#000';
      previewCtx.fillRect(0, 0, previewCanvas.width, previewCanvas.height);
    }

    toast.info('Camera Stopped', 'Stream ended');
    console.log('Camera streaming stopped');
  }

  /**
   * Capture frame and stream to canvas
   */
  async function captureAndStreamFrame() {
    if (!isStreaming || !video || !processCtx || !previewCtx) return;

    try {
      // Get settings
      const mirror = document.getElementById('camera-mirror')?.checked ?? true;
      const dither = document.getElementById('camera-dither')?.checked ?? false;
      const grayscale = document.getElementById('camera-grayscale')?.checked ?? false;
      const brightness = parseInt(document.getElementById('camera-brightness')?.value || '0', 10);
      const contrast = parseInt(document.getElementById('camera-contrast')?.value || '0', 10);
      const saturation = parseInt(document.getElementById('camera-saturation')?.value || '0', 10);
      const effect = document.getElementById('camera-effect')?.value || 'none';

      // Draw video frame to processing canvas (scaled down)
      processCtx.save();
      
      if (mirror) {
        processCtx.translate(targetWidth, 0);
        processCtx.scale(-1, 1);
      }
      
      processCtx.drawImage(video, 0, 0, targetWidth, targetHeight);
      processCtx.restore();

      // Get image data for processing
      let imageData = processCtx.getImageData(0, 0, targetWidth, targetHeight);

      // Apply adjustments
      if (brightness !== 0 || contrast !== 0 || saturation !== 0 || grayscale) {
        imageData = applyAdjustments(imageData, { brightness, contrast, saturation, grayscale });
      }

      // Apply visual effect
      if (effect !== 'none') {
        imageData = applyEffect(imageData, effect);
      }

      // Apply dithering if enabled
      if (dither) {
        imageData = applyDithering(imageData);
      }

      // Put processed image back
      processCtx.putImageData(imageData, 0, 0);

      // Update preview canvas
      previewCtx.drawImage(processCanvas, 0, 0);

      // Get image as data URL
      const imageDataUrl = processCanvas.toDataURL('image/png');

      // Send to backend — use the pinned target (set at start, updated only on explicit dropdown change).
      const canvasName = streamTarget || document.getElementById('camera-target-canvas')?.value || 'Main';

      await window.api.post(`/api/draw/apply/${canvasName}`, { imageData: imageDataUrl });

      // Update FPS counter
      frameCount++;
      const now = performance.now();
      if (now - lastFpsUpdate >= 1000) {
        actualFps = frameCount;
        frameCount = 0;
        lastFpsUpdate = now;
        if (fpsDisplay) fpsDisplay.textContent = actualFps;
      }

    } catch (error) {
      console.error('Frame capture error:', error);
    }
  }

  /**
   * Apply brightness, contrast, saturation adjustments
   */
  function applyAdjustments(imageData, { brightness, contrast, saturation, grayscale }) {
    const data = imageData.data;
    const brightnessF = brightness / 100;
    const contrastF = (contrast + 100) / 100;
    const saturationF = (saturation + 100) / 100;

    for (let i = 0; i < data.length; i += 4) {
      let r = data[i];
      let g = data[i + 1];
      let b = data[i + 2];

      // Apply brightness
      r += 255 * brightnessF;
      g += 255 * brightnessF;
      b += 255 * brightnessF;

      // Apply contrast
      r = ((r / 255 - 0.5) * contrastF + 0.5) * 255;
      g = ((g / 255 - 0.5) * contrastF + 0.5) * 255;
      b = ((b / 255 - 0.5) * contrastF + 0.5) * 255;

      // Apply saturation
      const gray = 0.299 * r + 0.587 * g + 0.114 * b;
      r = gray + saturationF * (r - gray);
      g = gray + saturationF * (g - gray);
      b = gray + saturationF * (b - gray);

      // Apply grayscale
      if (grayscale) {
        const grayVal = 0.299 * r + 0.587 * g + 0.114 * b;
        r = g = b = grayVal;
      }

      // Clamp values
      data[i] = Math.max(0, Math.min(255, r));
      data[i + 1] = Math.max(0, Math.min(255, g));
      data[i + 2] = Math.max(0, Math.min(255, b));
    }

    return imageData;
  }

  /**
   * Apply a visual effect to the image data
   */
  function applyEffect(imageData, effect) {
    switch (effect) {
      case 'edge':      return applyEdgeDetection(imageData);
      case 'invert':    return applyInvert(imageData);
      case 'sepia':     return applySepia(imageData);
      case 'nightvision': return applyNightVision(imageData);
      case 'thermal':   return applyThermal(imageData);
      case 'posterize': return applyPosterize(imageData);
      case 'pixelate':  return applyPixelate(imageData);
      case 'rgbshift':  return applyRgbShift(imageData);
      case 'emboss':    return applyEmboss(imageData);
      case 'blur':      return applyBlur(imageData);
      default:          return imageData;
    }
  }

  function applyEdgeDetection(imageData) {
    const src = new Uint8ClampedArray(imageData.data);
    const dst = imageData.data;
    const w = imageData.width, h = imageData.height;
    // Sobel operator
    for (let y = 1; y < h - 1; y++) {
      for (let x = 1; x < w - 1; x++) {
        const idx = (y * w + x) * 4;
        let gx = 0, gy = 0;
        for (let c = 0; c < 3; c++) {
          const tl = src[((y-1)*w+(x-1))*4+c], t = src[((y-1)*w+x)*4+c], tr = src[((y-1)*w+(x+1))*4+c];
          const l  = src[(y*w+(x-1))*4+c],                                 r  = src[(y*w+(x+1))*4+c];
          const bl = src[((y+1)*w+(x-1))*4+c], b = src[((y+1)*w+x)*4+c], br = src[((y+1)*w+(x+1))*4+c];
          gx += Math.abs(-tl + tr - 2*l + 2*r - bl + br);
          gy += Math.abs(-tl - 2*t - tr + bl + 2*b + br);
        }
        const mag = Math.min(255, (gx + gy) / 3);
        dst[idx] = dst[idx+1] = dst[idx+2] = mag;
      }
    }
    return imageData;
  }

  function applyInvert(imageData) {
    const d = imageData.data;
    for (let i = 0; i < d.length; i += 4) {
      d[i] = 255 - d[i];
      d[i+1] = 255 - d[i+1];
      d[i+2] = 255 - d[i+2];
    }
    return imageData;
  }

  function applySepia(imageData) {
    const d = imageData.data;
    for (let i = 0; i < d.length; i += 4) {
      const r = d[i], g = d[i+1], b = d[i+2];
      d[i]   = Math.min(255, r * 0.393 + g * 0.769 + b * 0.189);
      d[i+1] = Math.min(255, r * 0.349 + g * 0.686 + b * 0.168);
      d[i+2] = Math.min(255, r * 0.272 + g * 0.534 + b * 0.131);
    }
    return imageData;
  }

  function applyNightVision(imageData) {
    const d = imageData.data;
    for (let i = 0; i < d.length; i += 4) {
      const lum = d[i] * 0.299 + d[i+1] * 0.587 + d[i+2] * 0.114;
      // Boost green channel, add slight noise
      const noise = (Math.random() - 0.5) * 20;
      d[i]   = Math.max(0, Math.min(255, lum * 0.2 + noise));
      d[i+1] = Math.max(0, Math.min(255, lum * 1.4 + noise));
      d[i+2] = Math.max(0, Math.min(255, lum * 0.2 + noise));
    }
    return imageData;
  }

  function applyThermal(imageData) {
    const d = imageData.data;
    // Thermal color palette: black → blue → purple → red → orange → yellow → white
    for (let i = 0; i < d.length; i += 4) {
      const temp = (d[i] * 0.299 + d[i+1] * 0.587 + d[i+2] * 0.114) / 255;
      if (temp < 0.2) {
        d[i] = 0; d[i+1] = 0; d[i+2] = temp * 5 * 200;
      } else if (temp < 0.4) {
        const t = (temp - 0.2) * 5;
        d[i] = t * 180; d[i+1] = 0; d[i+2] = 200 - t * 100;
      } else if (temp < 0.6) {
        const t = (temp - 0.4) * 5;
        d[i] = 180 + t * 75; d[i+1] = t * 100; d[i+2] = 100 - t * 100;
      } else if (temp < 0.8) {
        const t = (temp - 0.6) * 5;
        d[i] = 255; d[i+1] = 100 + t * 155; d[i+2] = 0;
      } else {
        const t = (temp - 0.8) * 5;
        d[i] = 255; d[i+1] = 255; d[i+2] = t * 255;
      }
    }
    return imageData;
  }

  function applyPosterize(imageData) {
    const d = imageData.data;
    const levels = 4; // Number of color levels
    const step = 255 / (levels - 1);
    for (let i = 0; i < d.length; i += 4) {
      d[i]   = Math.round(d[i] / step) * step;
      d[i+1] = Math.round(d[i+1] / step) * step;
      d[i+2] = Math.round(d[i+2] / step) * step;
    }
    return imageData;
  }

  function applyPixelate(imageData) {
    const d = imageData.data;
    const w = imageData.width, h = imageData.height;
    const blockSize = Math.max(2, Math.floor(Math.min(w, h) / 16));
    for (let by = 0; by < h; by += blockSize) {
      for (let bx = 0; bx < w; bx += blockSize) {
        let r = 0, g = 0, b = 0, count = 0;
        for (let y = by; y < Math.min(by + blockSize, h); y++) {
          for (let x = bx; x < Math.min(bx + blockSize, w); x++) {
            const idx = (y * w + x) * 4;
            r += d[idx]; g += d[idx+1]; b += d[idx+2]; count++;
          }
        }
        r = Math.round(r / count); g = Math.round(g / count); b = Math.round(b / count);
        for (let y = by; y < Math.min(by + blockSize, h); y++) {
          for (let x = bx; x < Math.min(bx + blockSize, w); x++) {
            const idx = (y * w + x) * 4;
            d[idx] = r; d[idx+1] = g; d[idx+2] = b;
          }
        }
      }
    }
    return imageData;
  }

  function applyRgbShift(imageData) {
    const src = new Uint8ClampedArray(imageData.data);
    const dst = imageData.data;
    const w = imageData.width, h = imageData.height;
    const shift = Math.max(1, Math.floor(w / 40)); // Shift amount based on resolution
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const idx = (y * w + x) * 4;
        // Red channel shifted left
        const rxIdx = (y * w + Math.max(0, x - shift)) * 4;
        dst[idx] = src[rxIdx];
        // Green stays in place
        dst[idx+1] = src[idx+1];
        // Blue channel shifted right
        const bxIdx = (y * w + Math.min(w - 1, x + shift)) * 4;
        dst[idx+2] = src[bxIdx + 2];
      }
    }
    return imageData;
  }

  function applyEmboss(imageData) {
    const src = new Uint8ClampedArray(imageData.data);
    const dst = imageData.data;
    const w = imageData.width, h = imageData.height;
    // Emboss kernel: [-2,-1,0],[-1,1,1],[0,1,2]
    for (let y = 1; y < h - 1; y++) {
      for (let x = 1; x < w - 1; x++) {
        const idx = (y * w + x) * 4;
        for (let c = 0; c < 3; c++) {
          const val = 128 +
            -2 * src[((y-1)*w+(x-1))*4+c] + -1 * src[((y-1)*w+x)*4+c] +
            -1 * src[(y*w+(x-1))*4+c] + 1 * src[(y*w+x)*4+c] + 1 * src[(y*w+(x+1))*4+c] +
            1 * src[((y+1)*w+x)*4+c] + 2 * src[((y+1)*w+(x+1))*4+c];
          dst[idx + c] = Math.max(0, Math.min(255, val));
        }
      }
    }
    return imageData;
  }

  function applyBlur(imageData) {
    const src = new Uint8ClampedArray(imageData.data);
    const dst = imageData.data;
    const w = imageData.width, h = imageData.height;
    // 3x3 box blur
    for (let y = 1; y < h - 1; y++) {
      for (let x = 1; x < w - 1; x++) {
        const idx = (y * w + x) * 4;
        for (let c = 0; c < 3; c++) {
          let sum = 0;
          for (let dy = -1; dy <= 1; dy++) {
            for (let dx = -1; dx <= 1; dx++) {
              sum += src[((y+dy)*w+(x+dx))*4+c];
            }
          }
          dst[idx + c] = Math.round(sum / 9);
        }
      }
    }
    return imageData;
  }

  /**
   * Apply Floyd-Steinberg dithering for better color representation
   */
  function applyDithering(imageData) {
    const data = imageData.data;
    const width = imageData.width;
    const height = imageData.height;

    // Simple error diffusion dithering
    for (let y = 0; y < height; y++) {
      for (let x = 0; x < width; x++) {
        const idx = (y * width + x) * 4;

        // Quantize to fewer colors (for LED matrix)
        const oldR = data[idx];
        const oldG = data[idx + 1];
        const oldB = data[idx + 2];

        // Quantize to 8 levels per channel
        const newR = Math.round(oldR / 32) * 32;
        const newG = Math.round(oldG / 32) * 32;
        const newB = Math.round(oldB / 32) * 32;

        data[idx] = newR;
        data[idx + 1] = newG;
        data[idx + 2] = newB;

        // Calculate error
        const errR = oldR - newR;
        const errG = oldG - newG;
        const errB = oldB - newB;

        // Distribute error to neighboring pixels
        const distribute = (dx, dy, factor) => {
          const nx = x + dx;
          const ny = y + dy;
          if (nx >= 0 && nx < width && ny >= 0 && ny < height) {
            const nidx = (ny * width + nx) * 4;
            data[nidx] = Math.max(0, Math.min(255, data[nidx] + errR * factor));
            data[nidx + 1] = Math.max(0, Math.min(255, data[nidx + 1] + errG * factor));
            data[nidx + 2] = Math.max(0, Math.min(255, data[nidx + 2] + errB * factor));
          }
        };

        // Floyd-Steinberg distribution
        distribute(1, 0, 7 / 16);
        distribute(-1, 1, 3 / 16);
        distribute(0, 1, 5 / 16);
        distribute(1, 1, 1 / 16);
      }
    }

    return imageData;
  }

  /**
   * Set status display
   */
  function setStatus(message, state) {
    if (!statusEl || !statusText) return;

    statusText.textContent = message;
    
    statusEl.classList.remove('streaming', 'error', 'loading');
    if (state !== 'idle') {
      statusEl.classList.add(state);
    }

    // Update icon
    const icon = statusEl.querySelector('.camera-status-icon');
    if (icon) {
      switch (state) {
        case 'loading':
          icon.textContent = '⏳';
          break;
        case 'streaming':
          icon.textContent = '🎥';
          break;
        case 'error':
          icon.textContent = '❌';
          break;
        default:
          icon.textContent = '📷';
      }
    }
  }

  /**
   * Check if camera API is supported
   */
  function isSupported() {
    return window.isSecureContext && !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
  }

  // Public API
  return {
    init,
    start,
    stop,
    isSupported,
    updateTargetCanvases: updateCameraTargetCanvases
  };
})();

// Global functions for onclick handlers
function startCameraStream() {
  CameraStream.start();
}

function stopCameraStream() {
  CameraStream.stop();
}

function updateCameraTargetCanvases() {
  CameraStream.updateTargetCanvases();
}

// Initialize when DOM is ready
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => {
    CameraStream.init();
  });
} else {
  CameraStream.init();
}
