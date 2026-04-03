#pragma once
// GVSTDecoder.h  –  public C API for the GVSTDecoder Windows MFT H.264 decode plugin.
//
// Usage from C# via P/Invoke:
//   1. GVST_CreateDecoder()       → get an int id
//   2. GVST_Initialize(id, w, h)  → configure MFT pipeline
//   3. GVST_Feed(id, data, len)   → push one Annex-B NAL sequence
//   4. GVST_GetFrame(id, buf, &w, &h) → pull RGBA32 output (if ready)
//   5. GVST_DeleteDecoder(id)     → free resources
//
// Thread safety: one call at a time per decoder id is required.

#ifdef GVSTDECODER_EXPORTS
#  define GVST_API __declspec(dllexport)
#else
#  define GVST_API __declspec(dllimport)
#endif

extern "C"
{
    // Returns a new decoder id (>= 0), or -1 on failure.
    GVST_API int         GVST_CreateDecoder();

    // Must be called once before Feed/GetFrame.
    // width/height are hints only — the MFT will adapt when SPS arrives.
    GVST_API bool        GVST_Initialize(int id, int width, int height);

    // Feed one complete Annex-B H.264 NAL byte sequence (SPS+PPS+IDR or P-frame).
    // Returns true if accepted.
    GVST_API bool        GVST_Feed(int id, const unsigned char* data, int length);

    // Pull a decoded RGBA32 frame if one is ready.
    // outRgba must point to at least bufferSize bytes.
    // *outWidth and *outHeight are set to the actual decoded dimensions.
    // Returns true when a frame was written, false when nothing ready yet.
    // If the decoded frame is larger than bufferSize, the frame is NOT copied,
    // but outWidth/outHeight are still set so the caller can reallocate.
    GVST_API bool        GVST_GetFrame(int id, unsigned char* outRgba,
                                       int bufferSize,
                                       int* outWidth, int* outHeight);

    // Release all resources for this decoder.
    GVST_API void        GVST_DeleteDecoder(int id);

    // Signal the decoder to abort its current DrainOutputs loop.
    // Does NOT acquire decoderLock — safe to call while another thread is in Feed.
    // The decoder remains usable; call DeleteDecoder later for full cleanup.
    GVST_API void        GVST_AbortDecoder(int id);

    // Returns the last error string for the given decoder (or global if id == -1).
    GVST_API const char* GVST_GetError(int id);

    // Returns diagnostic counters as a human-readable string. Thread-safe.
    GVST_API const char* GVST_GetStats(int id);

    // ── Zero-copy GPU texture path ──────────────────────────────────────────────

    // Returns a function pointer (as intptr_t) to the Unity render-event callback.
    // Pass to GL.IssuePluginEvent(ptr, 0) every frame from C# Update().
    // The callback runs on Unity's render thread and uploads any pending decoded
    // frames to per-decoder D3D11 textures via UpdateSubresource.
    GVST_API long long    GVST_GetRenderCallback();

    // Returns the native ID3D11Texture2D* (as long long) for this decoder's
    // zero-copy output texture.  Returns 0 until the first frame has been
    // processed by the render callback.  When resolution changes, a new texture
    // is created and the pointer changes — C# must call Texture2D.UpdateExternalTexture
    // or recreate the Texture2D when the pointer changes.
    GVST_API long long    GVST_GetTexturePtr(int id);
}
