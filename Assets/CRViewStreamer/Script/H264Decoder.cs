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

            [DllImport(Lib, EntryPoint = "GVST_GetError")]
            private static extern IntPtr GetErrorPtr(int id);
            public static string GetError(int id) => Marshal.PtrToStringAnsi(GetErrorPtr(id)) ?? "";
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
        private bool    _disposed    = false;

        // Pre-allocated and pinned staging buffer for GVSTDecoder.dll GVST_GetFrame output
        private byte[]   _stageBuf;
        private GCHandle _stagePin;
        private bool     _stagePinned = false;

        // == Properties ================================================================

        public int  Width   { get; private set; }
        public int  Height  { get; private set; }
        public bool IsReady => _id >= 0 && !_disposed;

        // == Initialize ================================================================

        public bool Initialize(int width, int height)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(H264Decoder));

            Width   = width;
            Height  = height;

            if (!ProbeBackend())
            {
                Debug.LogError("[H264Decoder] GVSTDecoder.dll not available.");
                return false;
            }

            _id = GvstApi.CreateDecoder();
            if (_id < 0) { Debug.LogError("[H264Decoder] GVST_CreateDecoder returned -1."); return false; }

            if (!GvstApi.Initialize(_id, width, height))
            {
                Debug.LogError($"[H264Decoder] GVST_Initialize failed: {GvstApi.GetError(_id)}");
                GvstApi.DeleteDecoder(_id); _id = -1;
                return false;
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
        public byte[] Decode(byte[] data, int offset, int length)
        {
            if (!IsReady || data == null || length <= 0) return null;

            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = new IntPtr(pin.AddrOfPinnedObject().ToInt64() + offset);

                if (!GvstApi.Feed(_id, ptr, length))
                {
                    Debug.LogWarning($"[H264Decoder] GVST_Feed failed: {GvstApi.GetError(_id)}");
                    return null;
                }

                int outW, outH;
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
                        return null; // MFT still buffering — normal for first few frames
                    }
                }

                // Actual resolution may differ from initialisation hint if SPS changed
                int validBytes = outW * outH * 4;
                byte[] buf = ArrayPool<byte>.Shared.Rent(validBytes);
                Buffer.BlockCopy(_stageBuf, 0, buf, 0, Math.Min(validBytes, _stageBuf.Length));
                Width  = outW;
                Height = outH;
                return buf;
            }
            finally
            {
                pin.Free();
            }
        }

        // == IDisposable ===============================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_stagePinned) { _stagePin.Free(); _stagePinned = false; }

            if (_id >= 0)
            {
                GvstApi.DeleteDecoder(_id);
                _id = -1;
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
        public bool Initialize(int w, int h)                           => false;
        public byte[] Decode(byte[] d, int o, int l)                   => null;
        public void Dispose() { }
    }
#endif

} // namespace GameViewStream
