// H264Decoder.cs  -  per-client stateful H.264 decoder for Windows PC server.
//
// Backend: GVSTDecoder.dll  - Windows MFT H264 decoder (hardware via D3D11VA on
//          NVIDIA, AMD, or Intel GPU; or Microsoft software decoder as fallback).
//          No GPU vendor requirement.  Outputs RGBA32 directly.
//
// Design:
//   * One H264Decoder instance per connected client (managed by ViewDecoder).
//   * NOT thread-safe for concurrent calls - callers must lock externally.
//   * IDisposable: releases native decoder.

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace GameViewStream
{

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN

    public sealed class H264Decoder : IDisposable
    {
        // == GVSTDecoder.dll P/Invoke (MFT backend) ===================================

        private static class GvstApi
        {
            private const string Lib = "GVSTDecoder";

            [DllImport(Lib, EntryPoint = "GVST_CreateDecoder")]
            public static extern int  CreateDecoder();

            [DllImport(Lib, EntryPoint = "GVST_Initialize")]
            public static extern bool Initialize(int id, int width, int height);

            [DllImport(Lib, EntryPoint = "GVST_Feed")]
            public static extern bool Feed(int id, IntPtr data, int length);

            [DllImport(Lib, EntryPoint = "GVST_GetFrame")]
            public static extern bool GetFrame(int id, IntPtr outRgba,
                                               int bufferSize,
                                               out int outWidth, out int outHeight);

            [DllImport(Lib, EntryPoint = "GVST_DeleteDecoder")]
            public static extern void DeleteDecoder(int id);

            [DllImport(Lib, EntryPoint = "GVST_AbortDecoder")]
            public static extern void AbortDecoder(int id);

            [DllImport(Lib, EntryPoint = "GVST_GetError")]
            private static extern IntPtr GetErrorPtr(int id);
            public static string GetError(int id) => Marshal.PtrToStringAnsi(GetErrorPtr(id)) ?? "";

            [DllImport(Lib, EntryPoint = "GVST_GetStats")]
            private static extern IntPtr GetStatsPtr(int id);
            public static string GetStats(int id) => Marshal.PtrToStringAnsi(GetStatsPtr(id)) ?? "";

            // ── Zero-copy GPU texture path ──────────────────────────────────────────────
            [DllImport(Lib, EntryPoint = "GVST_GetRenderCallback")]
            public static extern IntPtr GetRenderCallback();

            [DllImport(Lib, EntryPoint = "GVST_GetTexturePtr")]
            public static extern IntPtr GetTexturePtr(int id);
        }

        // == Backend availability ======================================================

        private static bool? s_available;

        private static bool ProbeBackend()
        {
            if (s_available.HasValue) return s_available.Value;

            try
            {
                int testId = GvstApi.CreateDecoder();
                if (testId >= 0) { GvstApi.DeleteDecoder(testId); }
                Debug.Log("[H264Decoder] Backend: GVSTDecoder.dll (Windows MFT, any GPU)");
                return (s_available = true).Value;
            }
            catch (DllNotFoundException)
            {
                Debug.LogWarning("[H264Decoder] GVSTDecoder.dll not found.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[H264Decoder] GVSTDecoder probe error: {e.Message}");
            }

            return (s_available = false).Value;
        }

        /// <summary>True when GVSTDecoder.dll is available on this machine.</summary>
        public static bool IsAvailable => ProbeBackend();

        // == Instance state ============================================================

        private int     _id          = -1;
        private volatile bool _disposed = false;

        // Global lock: GVSTDecoder.dll uses a shared D3D11 device internally.
        // The D3D11 immediate context is NOT thread-safe, so concurrent GVST_Feed /
        // GVST_GetFrame calls from different worker threads (decoding different clients)
        // corrupt GPU state and produce visual artifacts.  Serialising all native calls
        // through this lock costs nothing at typical frame rates (<1 ms per decode at
        // 320×240) and fixes the multi-client artifact bug.
        private static readonly object s_nativeLock = new object();

        // Pre-allocated and pinned staging buffer for GVSTDecoder.dll GVST_GetFrame output
        private byte[]   _stageBuf;
        private GCHandle _stagePin;
        private bool     _stagePinned = false;

        // == Properties ================================================================

        public int  Width   { get; private set; }
        public int  Height  { get; private set; }
        public int  DecoderId => _id;

        /// <summary>Returns native diagnostic counters as a string (thread-safe, no lock).</summary>
        public string GetStats()
        {
            if (_id < 0 || _disposed) return "disposed";
            try { return GvstApi.GetStats(_id); } catch { return "error"; }
        }

        public bool IsReady => _id >= 0 && !_disposed;

        // ── Zero-copy GPU texture path ────────────────────────────────────────────
        /// <summary>
        /// Returns the render-event callback pointer to pass to GL.IssuePluginEvent.
        /// 0 when GVSTDecoder.dll is not loaded.  Safe to call from any thread.
        /// </summary>
        public static IntPtr GetRenderCallback()
        {
            if (!ProbeBackend()) return IntPtr.Zero;
            try { return GvstApi.GetRenderCallback(); } catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// Returns the native ID3D11Texture2D* for zero-copy rendering.
        /// Returns IntPtr.Zero until the first frame has been uploaded by the render callback.
        /// The pointer changes when the decoded resolution changes.
        /// </summary>
        public IntPtr GetTexturePtr()
        {
            if (_id < 0 || _disposed) return IntPtr.Zero;
            try { return GvstApi.GetTexturePtr(_id); } catch { return IntPtr.Zero; }
        }

        // == Initialize ================================================================

        public bool Initialize(int width, int height)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(H264Decoder));
            if (_id >= 0) return true; // Already initialized — prevent double-init leak

            Width   = width;
            Height  = height;

            if (!ProbeBackend())
            {
                Debug.LogError("[H264Decoder] GVSTDecoder.dll not available.");
                return false;
            }

            lock (s_nativeLock)
            {
                _id = GvstApi.CreateDecoder();
                if (_id < 0) { Debug.LogError("[H264Decoder] GVST_CreateDecoder returned -1."); return false; }

                if (!GvstApi.Initialize(_id, width, height))
                {
                    Debug.LogError($"[H264Decoder] GVST_Initialize failed: {GvstApi.GetError(_id)}");
                    GvstApi.DeleteDecoder(_id); _id = -1;
                    return false;
                }
            }

            // Pin the staging buffer once for the lifetime of this decoder;
            // avoids GCHandle alloc on every GetFrame call.
            _stageBuf    = new byte[width * height * 4];
            _stagePin    = GCHandle.Alloc(_stageBuf, GCHandleType.Pinned);
            _stagePinned = true;
            return true;
        }

        // == Decode ====================================================================

        /// <summary>
        /// Decode one complete Annex-B H.264 NAL sequence.
        /// Returns a rented ArrayPool RGBA32 buffer on success; null when MFT needs more input.
        /// Caller MUST return the buffer to ArrayPool(byte).Shared when done.
        /// </summary>
        public bool Feed(byte[] data, int offset, int length)
        {
            if (!IsReady || data == null || length <= 0) return false;

            lock (s_nativeLock)
            {
                GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    IntPtr ptr = new IntPtr(pin.AddrOfPinnedObject().ToInt64() + offset);
                    if (!GvstApi.Feed(_id, ptr, length))
                    {
                        Debug.LogWarning($"[H264Decoder] GVST_Feed failed: {GvstApi.GetError(_id)}");
                        return false;
                    }
                }
                finally
                {
                    pin.Free();
                }
            }
            return true;
        }

        public byte[] GetNextReadyFrame()
        {
            if (!IsReady) return null;

            byte[] stageBuf;
            int outW, outH;

            lock (s_nativeLock)
            {
                if (!GvstApi.GetFrame(_id, _stagePin.AddrOfPinnedObject(),
                                     _stageBuf.Length, out outW, out outH))
                {
                    // If dimensions changed and buffer was too small, reallocate and retry.
                    // GetFrame returns false but still sets outW/outH.
                    int needed = outW * outH * 4;
                    if (outW > 0 && outH > 0 && needed > _stageBuf.Length)
                    {
                        _stagePin.Free();
                        _stageBuf    = new byte[needed];
                        _stagePin    = GCHandle.Alloc(_stageBuf, GCHandleType.Pinned);
                        // Retry with the bigger buffer
                        if (!GvstApi.GetFrame(_id, _stagePin.AddrOfPinnedObject(),
                                              _stageBuf.Length, out outW, out outH))
                            return null;
                    }
                    else
                    {
                        return null; // No frame ready
                    }
                }
                stageBuf = _stageBuf;
            }

            // Copy out of lock to improve multi-client concurrency
            int validBytes = outW * outH * 4;
            byte[] buf = ArrayPool<byte>.Shared.Rent(validBytes);
            Buffer.BlockCopy(stageBuf, 0, buf, 0, Math.Min(validBytes, stageBuf.Length));
            Width  = outW;
            Height = outH;
            return buf;
        }

        public byte[] Decode(byte[] data, int offset, int length)
        {
            if (Feed(data, offset, length))
                return GetNextReadyFrame();
            return null;
        }


        // == IDisposable ===============================================================

        /// <summary>
        /// Signal the native decoder to abort its current output-drain loop.
        /// Does NOT acquire s_nativeLock — safe to call while a worker thread
        /// is inside Feed/GetNextReadyFrame.  Call before joining the worker.
        /// </summary>
        public void Abort()
        {
            if (_id >= 0 && !_disposed)
            {
                try { GvstApi.AbortDecoder(_id); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            // Use Monitor.TryEnter with a timeout instead of lock(s_nativeLock).
            // During domain reload, the H264 worker thread may still be alive inside
            // Feed() holding s_nativeLock (stuck in a native GPU wait).  A plain lock()
            // would deadlock the main thread → Unity hangs on "Reloading Domain".
            // With TryEnter, if we can't acquire within 3 s we abandon the decoder
            // (small native leak) rather than hanging the editor forever.
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(s_nativeLock, 3000, ref lockTaken);
                if (!lockTaken)
                {
                    Debug.LogWarning("[H264Decoder] Dispose: couldn't acquire lock in 3 s — abandoning decoder to avoid hang.");
                    _disposed = true;
                    return;
                }

                if (_disposed) return; // double-check inside lock
                _disposed = true;

                if (_stagePinned)
                {
                    _stagePin.Free();
                    _stagePinned = false;
                }

                if (_id >= 0)
                {
                    GvstApi.DeleteDecoder(_id);
                    _id = -1;
                }
            }
            finally
            {
                if (lockTaken) Monitor.Exit(s_nativeLock);
            }
        }
    }

#else
    public sealed class H264Decoder : IDisposable
    {
        public static bool IsAvailable => false;
        public bool IsReady  => false;
        public int  Width    { get; private set; }
        public int  Height   { get; private set; }
        public int  DecoderId => -1;
        public bool Initialize(int w, int h)                           => false;
        public bool Feed(byte[] d, int o, int l)                       => false;
        public byte[] GetNextReadyFrame()                              => null;
        public byte[] Decode(byte[] d, int o, int l)                   => null;
        public string GetStats()                                       => "stub";
        public static IntPtr GetRenderCallback()                       => IntPtr.Zero;
        public IntPtr GetTexturePtr()                                  => IntPtr.Zero;
        public void Abort() { }
        public void Dispose() { }
    }
#endif

} // namespace GameViewStream
