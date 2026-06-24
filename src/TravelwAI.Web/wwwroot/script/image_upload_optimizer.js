(function () {
  const MAX_SIDE = 1600;
  const WEBP_QUALITY = 0.82;

  let webpSupportPromise = null;

  function isImageFile(file) {
    return !!file && !!file.type && file.type.startsWith('image/');
  }

  function shouldSkip(file) {
    if (!isImageFile(file)) return true;

    const type = (file.type || '').toLowerCase();

    return type === 'image/gif' || type === 'image/webp' || type === 'image/svg+xml';
  }

  function changeExtension(fileName, extension) {
    const safeName = (fileName || 'image').replace(/\.[^/.]+$/, '');
    return `${safeName || 'image'}.${extension}`;
  }

  function canvasToBlob(canvas, mimeType, quality) {
    return new Promise((resolve) => canvas.toBlob(resolve, mimeType, quality));
  }

  async function canEncodeWebp() {
    if (webpSupportPromise) return webpSupportPromise;

    webpSupportPromise = new Promise((resolve) => {
      try {
        const canvas = document.createElement('canvas');
        canvas.width = 2;
        canvas.height = 2;
        canvas.toBlob((blob) => {
          resolve(!!blob && blob.type === 'image/webp' && blob.size > 0);
        }, 'image/webp', 0.8);
      } catch (_) {
        resolve(false);
      }
    });

    return webpSupportPromise;
  }

  async function loadBitmap(file) {
    if (typeof createImageBitmap === 'function') {
      return createImageBitmap(file);
    }

    return new Promise((resolve, reject) => {
      const image = new Image();
      image.onload = () => resolve(image);
      image.onerror = reject;
      image.src = URL.createObjectURL(file);
    });
  }

  function getBitmapSize(bitmap) {
    return {
      width: bitmap.width || bitmap.naturalWidth || 1,
      height: bitmap.height || bitmap.naturalHeight || 1
    };
  }

  async function optimizeImageFile(file) {
    if (shouldSkip(file)) return file;

    try {
      const bitmap = await loadBitmap(file);
      const size = getBitmapSize(bitmap);
      const scale = Math.min(1, MAX_SIDE / Math.max(size.width, size.height));
      const width = Math.max(1, Math.round(size.width * scale));
      const height = Math.max(1, Math.round(size.height * scale));

      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;
      const ctx = canvas.getContext('2d', { alpha: true });
      if (!ctx) return file;

      const supportsWebp = await canEncodeWebp();
      if (!supportsWebp) {
        throw new Error('Trình duyệt không hỗ trợ tạo ảnh WebP.');
      }

      ctx.drawImage(bitmap, 0, 0, width, height);
      if (typeof bitmap.close === 'function') bitmap.close();

      const blob = await canvasToBlob(canvas, 'image/webp', WEBP_QUALITY);

      if (!blob || blob.size <= 0) return file;

      return new File([blob], changeExtension(file.name, 'webp'), {
        type: 'image/webp',
        lastModified: Date.now()
      });
    } catch (error) {
      console.warn('Không thể tối ưu ảnh trước khi upload:', error);
      return file;
    }
  }

  async function optimizeImageFileVariants(file) {
    if (!isImageFile(file)) {
      return { primary: file, webp: null, jpg: null, files: [file] };
    }

    const type = (file.type || '').toLowerCase();
    if (type === 'image/gif' || type === 'image/svg+xml') {
      return { primary: file, webp: null, jpg: null, files: [file] };
    }

    if (type === 'image/webp') {
      return { primary: file, webp: file, jpg: null, files: [file] };
    }

    try {
      const webpFile = await optimizeImageFile(file);
      return { primary: webpFile, webp: webpFile, jpg: null, files: [webpFile] };
    } catch (error) {
      console.warn('Không thể tạo WebP trước khi upload:', error);
      return { primary: file, webp: null, jpg: null, files: [file] };
    }
  }

  async function optimizeImageFiles(files) {
    const list = Array.from(files || []);
    return Promise.all(list.map(optimizeImageFile));
  }

  window.TravelwAIImageOptimizer = {
    canEncodeWebp,
    optimizeImageFile,
    optimizeImageFileVariants,
    optimizeImageFiles
  };
})();
