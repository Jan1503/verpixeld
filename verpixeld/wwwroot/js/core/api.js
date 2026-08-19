/* ============================================================================
   API CLIENT - Centralized fetch wrapper for all backend communication
   ============================================================================

   Usage:
     const result = await api.get('/api/media/status');
     const result = await api.post('/api/media/seek', { percent: 50 });
     const result = await api.put('/api/favorites/123', { name: 'New' });
     const result = await api.del('/api/media/music');
     const result = await api.postForm('/api/media/videos/upload', formData);

   All methods return the parsed JSON response on success.
   On failure (HTTP error or { success: false }), an ApiError is thrown
   with .message, .status, and .data properties.

   For raw responses (SSE streams, binary data), pass { raw: true }:
     const response = await api.get('/api/preview/stream', { raw: true });
   ============================================================================ */

const api = (() => {
    const BASE = window.API_BASE || '';

    /**
     * Custom error class for API failures.
     * Carries the HTTP status and the full response body.
     */
    class ApiError extends Error {
        constructor(message, status, data) {
            super(message);
            this.name = 'ApiError';
            this.status = status;
            this.data = data;
        }
    }

    /**
     * Core request method.
     *
     * @param {string} method - HTTP method (GET, POST, PUT, DELETE)
     * @param {string} url - API path (e.g. '/api/media/status')
     * @param {Object} [options]
     * @param {*} [options.body] - Request body (object for JSON, FormData for uploads, or undefined)
     * @param {boolean} [options.raw=false] - If true, return the raw Response object
     * @returns {Promise<Object|Response>} Parsed JSON result or raw Response
     * @throws {ApiError} On HTTP error or { success: false } response
     */
    async function request(method, url, options = {}) {
        const { body, raw = false } = options;

        const fetchOptions = { method };

        if (body instanceof FormData) {
            // Let the browser set Content-Type with boundary
            fetchOptions.body = body;
        } else if (body !== undefined && body !== null) {
            fetchOptions.headers = { 'Content-Type': 'application/json' };
            fetchOptions.body = JSON.stringify(body);
        }

        const response = await fetch(BASE + url, fetchOptions);

        // Raw mode: return the Response directly (for SSE, streams, binary)
        if (raw) return response;

        // Parse JSON
        if (!response.ok) {
            const errData = await response.json().catch(() => ({}));
            const message = errData.error || errData.message || `HTTP ${response.status}`;
            throw new ApiError(message, response.status, errData);
        }

        const result = await response.json();

        // Check backend success envelope
        if (result.success === false) {
            const message = result.error || result.message || 'Request failed';
            throw new ApiError(message, response.status, result);
        }

        return result;
    }

    return {
        /** GET request. Returns parsed JSON. */
        get: (url, opts) => request('GET', url, opts),

        /** POST request with optional JSON body. */
        post: (url, body, opts) => request('POST', url, { body, ...opts }),

        /** PUT request with optional JSON body. */
        put: (url, body, opts) => request('PUT', url, { body, ...opts }),

        /** DELETE request. */
        del: (url, opts) => request('DELETE', url, opts),

        /** POST with FormData (file uploads). */
        postForm: (url, formData, opts) => request('POST', url, { body: formData, ...opts }),

        /** PUT with FormData. */
        putForm: (url, formData, opts) => request('PUT', url, { body: formData, ...opts }),

        /** The ApiError class, for instanceof checks. */
        ApiError
    };
})();

// Expose globally
window.api = api;
