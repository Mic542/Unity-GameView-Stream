// H264Encoder.cs  –  Pure-C# H.264 encoder using Android MediaCodec via Unity JNI.
//
// NO Java source file required.  Entirely self-contained — exports correctly in
// any Unity package without needing Assets/Plugins/Android/*.java.
//
// Key design: AndroidJavaObject is used for most MediaCodec calls, BUT
// MediaCodec$BufferInfo (inner class) is created and read via raw AndroidJNI
// because Unity's AndroidJavaObject constructor cannot reliably construct
// Java inner classes, causing the NullReferenceException seen in Encode().

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GameViewStream
{

#if UNITY_ANDROID && !UNITY_EDITOR

    public sealed class H264Encoder : IDisposable
    {
        // ── MediaCodec integer constants ──────────────────────────────────────
        private const int  CONFIGURE_FLAG_ENCODE    = 1;
        private const int  BUFFER_FLAG_CODEC_CONFIG = 2;
        private const int  BUFFER_FLAG_SYNC_FRAME   = 1;
        private const int  INFO_TRY_AGAIN_LATER     = -1;
        private const int  INFO_OUTPUT_FORMAT_CHANGED = -2;
        private const long DEQUEUE_INPUT_TIMEOUT_US = 10_000L;

        private const int COLOR_SemiPlanar = 21; // COLOR_FormatYUV420SemiPlanar (NV12)
        private const int COLOR_Planar     = 19; // COLOR_FormatYUV420Planar     (I420)

        // ── State ─────────────────────────────────────────────────────────────
        private AndroidJavaObject _codec;
        private int  _width, _height, _colorFormat;
        private byte[] _spsBuffer, _ppsBuffer;
        private bool _ready = false, _disposed = false;

        // Raw JNI handles for MediaCodec$BufferInfo — avoids inner-class JNI issues
        private IntPtr _biObj     = IntPtr.Zero; // global ref to BufferInfo instance
        private IntPtr _biClass   = IntPtr.Zero; // global ref to the class
        private IntPtr _fldFlags  = IntPtr.Zero;
        private IntPtr _fldOffset = IntPtr.Zero;
        private IntPtr _fldSize   = IntPtr.Zero;
        // Cached method ID for dequeueOutputBuffer(BufferInfo, long)
        private IntPtr _midDequeueOutput = IntPtr.Zero;

        // Stopwatch for PTS — avoids calling new AndroidJavaClass every frame
        private readonly System.Diagnostics.Stopwatch _pts = new System.Diagnostics.Stopwatch();

        // ── Availability ──────────────────────────────────────────────────────
        public static bool IsAvailable => true;

        // ── Initialize ────────────────────────────────────────────────────────
        public bool Initialize(int width, int height, int bitrateBps, int fps)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(H264Encoder));
            _width = width; _height = height;

            try
            {
                // 1. Create encoder
                using var mc = new AndroidJavaClass("android.media.MediaCodec");
                _codec = mc.CallStatic<AndroidJavaObject>("createEncoderByType", "video/avc");

                // 2. Pick color format — try SemiPlanar first (NV12, most common),
                //    fall back to Planar (I420). Avoids querying colorFormats via JNI.
                _colorFormat = TryConfigureAndStart(width, height, bitrateBps, fps, COLOR_SemiPlanar)
                             ? COLOR_SemiPlanar
                             : TryConfigureAndStart(width, height, bitrateBps, fps, COLOR_Planar)
                             ? COLOR_Planar
                             : -1;

                if (_colorFormat < 0)
                {
                    Debug.LogError("[H264Encoder] configure() failed for all color formats.");
                    return false;
                }

                // 3. Create MediaCodec$BufferInfo using raw JNI (inner class — AndroidJavaObject
                //    constructor cannot reliably instantiate inner classes on all devices).
                if (!InitBufferInfo())
                {
                    Debug.LogError("[H264Encoder] Failed to create MediaCodec.BufferInfo via JNI.");
                    _codec.Call("stop"); _codec.Call("release");
                    return false;
                }

                // 4. Cache dequeueOutputBuffer method ID
                IntPtr codecObj  = _codec.GetRawObject();
                IntPtr codecClass = AndroidJNI.GetObjectClass(codecObj);
                _midDequeueOutput = AndroidJNI.GetMethodID(
                    codecClass,
                    "dequeueOutputBuffer",
                    "(Landroid/media/MediaCodec$BufferInfo;J)I");
                AndroidJNI.DeleteLocalRef(codecClass);

                _pts.Restart();
                _ready = true;
                Debug.Log($"[H264Encoder] Initialised: {width}x{height} colorFmt={_colorFormat} @ {bitrateBps}bps {fps}fps");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[H264Encoder] Initialize failed: {e.GetType().Name}: {e.Message}");
                _codec?.Call("release"); _codec = null;
                FreeBufferInfo();
                _ready = false;
                return false;
            }
        }

        /// <summary>Try configuring the codec with a given color format. Returns true if it started.</summary>
        private bool TryConfigureAndStart(int w, int h, int bitrate, int fps, int colorFmt)
        {
            try
            {
                // Stop/reset if previously started
                try { _codec.Call("reset"); } catch { /* first call — codec not started yet */ }

                using var mfClass = new AndroidJavaClass("android.media.MediaFormat");
                using var fmt = mfClass.CallStatic<AndroidJavaObject>("createVideoFormat", "video/avc", w, h);
                fmt.Call("setInteger", "bitrate",          bitrate);
                fmt.Call("setInteger", "frame-rate",       fps);
                fmt.Call("setInteger", "color-format",     colorFmt);
                fmt.Call("setInteger", "i-frame-interval", 1);

                _codec.Call("configure", fmt, null, null, CONFIGURE_FLAG_ENCODE);
                _codec.Call("start");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[H264Encoder] colorFmt={colorFmt} failed: {e.Message}");
                return false;
            }
        }

        /// <summary>Create a MediaCodec$BufferInfo instance using raw JNI and cache field IDs.</summary>
        private bool InitBufferInfo()
        {
            // JNI class name uses slashes and $ for inner classes
            IntPtr localClass = AndroidJNI.FindClass("android/media/MediaCodec$BufferInfo");
            if (localClass == IntPtr.Zero) return false;

            _biClass = AndroidJNI.NewGlobalRef(localClass);
            AndroidJNI.DeleteLocalRef(localClass);

            IntPtr ctor    = AndroidJNI.GetMethodID(_biClass, "<init>", "()V");
            IntPtr localBi = AndroidJNI.NewObject(_biClass, ctor, new jvalue[0]);
            if (localBi == IntPtr.Zero) return false;

            _biObj = AndroidJNI.NewGlobalRef(localBi);
            AndroidJNI.DeleteLocalRef(localBi);

            _fldFlags  = AndroidJNI.GetFieldID(_biClass, "flags",  "I");
            _fldOffset = AndroidJNI.GetFieldID(_biClass, "offset", "I");
            _fldSize   = AndroidJNI.GetFieldID(_biClass, "size",   "I");

            return _fldFlags != IntPtr.Zero && _fldOffset != IntPtr.Zero && _fldSize != IntPtr.Zero;
        }

        private void FreeBufferInfo()
        {
            if (_biObj   != IntPtr.Zero) { AndroidJNI.DeleteGlobalRef(_biObj);   _biObj   = IntPtr.Zero; }
            if (_biClass != IntPtr.Zero) { AndroidJNI.DeleteGlobalRef(_biClass); _biClass = IntPtr.Zero; }
        }

        // ── Encode ────────────────────────────────────────────────────────────
        public byte[] Encode(byte[] rgbaData)
        {
            if (!_ready || rgbaData == null) return null;
            // Ensure this thread is attached to the JVM before any raw JNI calls.
            // Unity attaches managed threads automatically, but explicit attachment
            // is a no-op if already attached and prevents crashes on some IL2CPP configs.
            AndroidJNI.AttachCurrentThread();
            try
            {
                FeedInput(rgbaData);
                return DrainOutput();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[H264Encoder] Encode error: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        public void RequestKeyFrame()
        {
            if (!_ready) return;
            try
            {
                using var bundle = new AndroidJavaObject("android.os.Bundle");
                bundle.Call("putInt", "request-sync", 0);
                _codec.Call("setParameters", bundle);
            }
            catch { /* best-effort */ }
        }

        // ── FeedInput ─────────────────────────────────────────────────────────
        private void FeedInput(byte[] rgbaData)
        {
            int idx = _codec.Call<int>("dequeueInputBuffer", DEQUEUE_INPUT_TIMEOUT_US);
            if (idx < 0) return;

            using var buf = _codec.Call<AndroidJavaObject>("getInputBuffer", idx);
            if (buf == null) { _codec.Call("queueInputBuffer", idx, 0, 0, 0L, 0); return; }

            long ptsUs = _pts.ElapsedMilliseconds * 1000L;

            byte[] yuv = (_colorFormat == COLOR_Planar)
                ? RgbaToI420(rgbaData, _width, _height)
                : RgbaToNv12(rgbaData, _width, _height);

            // Write YUV into the native ByteBuffer directly — avoids marshaling ~780 KB
            // across the JNI boundary on every frame (which was causing the app freeze).
            IntPtr nativeAddr;
            unsafe { nativeAddr = (IntPtr)AndroidJNI.GetDirectBufferAddress(buf.GetRawObject()); }
            if (nativeAddr != IntPtr.Zero)
            {
                Marshal.Copy(yuv, 0, nativeAddr, yuv.Length);
            }
            else
            {
                // Fallback: ByteBuffer is not a direct buffer — use JNI put()
                buf.Call<AndroidJavaObject>("clear")?.Dispose();
                buf.Call<AndroidJavaObject>("put", yuv)?.Dispose();
            }

            _codec.Call("queueInputBuffer", idx, 0, yuv.Length, ptsUs, 0);
        }

        // ── DrainOutput ───────────────────────────────────────────────────────
        private byte[] DrainOutput()
        {
            using var ms = new MemoryStream();
            IntPtr codecRaw = _codec.GetRawObject();

            while (true)
            {
                // Call dequeueOutputBuffer via raw JNI so we can pass the raw _biObj
                var args = new jvalue[2];
                args[0].l = _biObj;
                args[1].j = 0L;
                int outIdx = AndroidJNI.CallIntMethod(codecRaw, _midDequeueOutput, args);

                if (outIdx == INFO_TRY_AGAIN_LATER) break;           // nothing ready — stop draining
                if (outIdx == INFO_OUTPUT_FORMAT_CHANGED) continue;   // format changed — keep draining
                if (outIdx < 0) break;                                // unexpected — stop

                // Read fields via raw JNI — safe because we created _biObj this way
                int flags  = AndroidJNI.GetIntField(_biObj, _fldFlags);
                int offset = AndroidJNI.GetIntField(_biObj, _fldOffset);
                int size   = AndroidJNI.GetIntField(_biObj, _fldSize);

                // Read output bytes via the direct-buffer native address.
                // IMPORTANT: Do NOT use ByteBuffer.get(byte[]) via AndroidJavaObject.Call —
                // Unity JNI marshals the byte[] parameter as a temporary Java array and does NOT
                // copy modifications back to the C# array (Call copies in, not out).
                // Using GetDirectBufferAddress + Marshal.Copy is the only reliable way to read data.
                byte[] chunk = null;
                if (size > 0)
                {
                    using var outBuf = _codec.Call<AndroidJavaObject>("getOutputBuffer", outIdx);
                    if (outBuf != null)
                    {
                        IntPtr nativeAddr;
                        unsafe { nativeAddr = (IntPtr)AndroidJNI.GetDirectBufferAddress(outBuf.GetRawObject()); }
                        if (nativeAddr != IntPtr.Zero)
                        {
                            chunk = new byte[size];
                            // offset into the buffer's start address before copying
                            Marshal.Copy(new IntPtr(nativeAddr.ToInt64() + offset), chunk, 0, size);
                        }
                        else
                        {
                            Debug.LogWarning("[H264Encoder] Output ByteBuffer is not direct — frame dropped.");
                        }
                    }
                }

                _codec.Call("releaseOutputBuffer", outIdx, false);

                bool isConfig   = (flags & BUFFER_FLAG_CODEC_CONFIG) != 0;
                bool isKeyFrame = (flags & BUFFER_FLAG_SYNC_FRAME)   != 0;

                if (isConfig) { if (chunk != null) ParseSPSPPS(chunk); continue; }
                if (chunk == null) continue;

                if (isKeyFrame && _spsBuffer != null && _ppsBuffer != null)
                {
                    WriteNal(ms, _spsBuffer);
                    WriteNal(ms, _ppsBuffer);
                }

                ms.Write(chunk, 0, chunk.Length);
            }

            return ms.Length > 0 ? ms.ToArray() : null;
        }

        // ── SPS/PPS ───────────────────────────────────────────────────────────
        private void ParseSPSPPS(byte[] data)
        {
            int len = data.Length, nseg = 0, p = 0;
            int[] starts = new int[4];
            while (p < len - 3 && nseg < 4)
            {
                if (data[p]==0 && data[p+1]==0 && data[p+2]==0 && data[p+3]==1)
                    { starts[nseg++] = p + 4; p += 4; }
                else p++;
            }
            if (nseg < 2) return;
            _spsBuffer = new byte[starts[1] - 4 - starts[0]];
            Array.Copy(data, starts[0], _spsBuffer, 0, _spsBuffer.Length);
            int ppsEnd = nseg > 2 ? starts[2] - 4 : len;
            _ppsBuffer = new byte[ppsEnd - starts[1]];
            Array.Copy(data, starts[1], _ppsBuffer, 0, _ppsBuffer.Length);
        }

        private static void WriteNal(Stream s, byte[] nal)
        { s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); s.WriteByte(1); s.Write(nal, 0, nal.Length); }

        // ── YUV conversion ────────────────────────────────────────────────────
        private static byte[] RgbaToNv12(byte[] rgba, int w, int h)
        {
            int fs = w * h; byte[] o = new byte[fs + fs / 2];
            int yi = 0, uvi = fs;
            for (int r = 0; r < h; r++) for (int c = 0; c < w; c++)
            {
                int i = (r*w+c)*4, R = rgba[i]&0xFF, G = rgba[i+1]&0xFF, B = rgba[i+2]&0xFF;
                o[yi++] = Y(R,G,B);
                if ((r&1)==0 && (c&1)==0) { o[uvi++]=U(R,G,B); o[uvi++]=V(R,G,B); }
            }
            return o;
        }

        private static byte[] RgbaToI420(byte[] rgba, int w, int h)
        {
            int fs = w * h; byte[] o = new byte[fs + fs / 2];
            int yi = 0, ui = fs, vi = fs + fs/4;
            for (int r = 0; r < h; r++) for (int c = 0; c < w; c++)
            {
                int i = (r*w+c)*4, R = rgba[i]&0xFF, G = rgba[i+1]&0xFF, B = rgba[i+2]&0xFF;
                o[yi++] = Y(R,G,B);
                if ((r&1)==0 && (c&1)==0) { o[ui++]=U(R,G,B); o[vi++]=V(R,G,B); }
            }
            return o;
        }

        private static byte Y(int r,int g,int b) => (byte)Math.Max(0,Math.Min(255,((66*r+129*g+25*b+128)>>8)+16));
        private static byte U(int r,int g,int b) => (byte)Math.Max(0,Math.Min(255,((-38*r-74*g+112*b+128)>>8)+128));
        private static byte V(int r,int g,int b) => (byte)Math.Max(0,Math.Min(255,((112*r-94*g-18*b+128)>>8)+128));


        // ── Dispose ───────────────────────────────────────────────────────────
        public void Dispose() { if (!_disposed) { _disposed = true; Release(); } }

        public void Release()
        {
            _ready = false;
            if (_codec != null)
            {
                try { _codec.Call("stop");    } catch { /**/ }
                try { _codec.Call("release"); } catch { /**/ }
                _codec.Dispose(); _codec = null;
            }
            FreeBufferInfo();
        }
    }

#else
    public sealed class H264Encoder : IDisposable
    {
        public static bool IsAvailable => false;
        public bool   Initialize(int w, int h, int bps, int fps) => false;
        public byte[] Encode(byte[] rgba)                        => null;
        public void   RequestKeyFrame()                           { }
        public void   Release()                                   { }
        public void   Dispose()                                   { }
    }
#endif

} // namespace GameViewStream
