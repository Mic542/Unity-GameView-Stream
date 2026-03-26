using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GameViewStream
{
    /// <summary>
    /// Thin P/Invoke wrapper around the native libturbojpeg.so (Android arm64-v8a / armeabi-v7a).
    ///
    /// The native library provides SIMD-accelerated (ARM NEON) JPEG encode/decode that is
    /// 5–15× faster than Unity's managed <c>ImageConversion.EncodeToJPG</c>.
    ///
    /// On non-Android platforms (Windows Editor / PC Server) TurboJpeg is not available;
    /// callers must use the managed fallback via <see cref="IsAvailable"/>.
    ///
    /// Pixel format used throughout: RGBA (4 bytes per pixel) matching
    /// <see cref="UnityEngine.TextureFormat.RGBA32"/> from <c>AsyncGPUReadback</c>.
    /// </summary>
    public static class TurboJpeg
    {
        // ── Constants ────────────────────────────────────────────────────────────

        /// <summary>RGBA pixel format (4 bytes/pixel). Matches Unity's RGBA32.</summary>
        public const int TJPF_RGBA = 7;

        /// <summary>4:2:0 chroma subsampling — best bandwidth/quality balance for streaming.</summary>
        public const int TJSAMP_420 = 2;
        /// <summary>4:2:2 subsampling — better colour accuracy, ~20% larger files.</summary>
        public const int TJSAMP_422 = 1;
        /// <summary>4:4:4 — no colour information discarded, largest files.</summary>
        public const int TJSAMP_444 = 0;

        /// <summary>Use fast (non-integer) DCT — small quality loss, significant speed gain on mobile.</summary>
        public const int TJFLAG_FASTDCT      = 2048;
        /// <summary>Use fast upsampling in decompressor.</summary>
        public const int TJFLAG_FASTUPSAMPLE = 256;

        // ── Availability ─────────────────────────────────────────────────────────

        // Runtime availability flag — lazily probed on Android so we never crash if
        // libturbojpeg.so was not shipped (e.g. when building H.264-only APKs).
        private static bool? _isAvailable;

        /// <summary>
        /// True when the native <c>turbojpeg</c> library is actually loadable on this platform.
        /// On Windows (Editor + Standalone) this is always true.
        /// On Android this is determined lazily at runtime by probing the native library —
        /// it returns false when libturbojpeg.so is absent from the APK (e.g. H.264-only builds).
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                return true; // DLL always ships with Windows builds
#elif UNITY_ANDROID
                if (_isAvailable.HasValue) return _isAvailable.Value;
                try
                {
                    // Probe: call a trivial native function to force the .so to load.
                    // If the library is absent this throws DllNotFoundException.
                    IntPtr h = tjInitCompress();
                    if (h != IntPtr.Zero) tjDestroy(h);
                    _isAvailable = true;
                }
                catch (DllNotFoundException)
                {
                    _isAvailable = false;
                    Debug.Log("[TurboJpeg] libturbojpeg.so not found in APK — JPEG encode unavailable (H.264 mode expected).");
                }
                catch (Exception e)
                {
                    _isAvailable = false;
                    Debug.LogWarning($"[TurboJpeg] Availability probe failed: {e.Message}");
                }
                return _isAvailable.Value;
#else
                return false;
#endif
            }
        }

        // ── P/Invoke declarations ─────────────────────────────────────────────────
        // Available on Windows (Editor + Standalone) and Android.
        // The native library name "turbojpeg" resolves to:
        //   Windows : Windows/x64/turbojpeg.dll  or  Windows/x86/turbojpeg.dll
        //   Android : Android/arm64-v8a/libturbojpeg.so  or  armeabi-v7a/libturbojpeg.so

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_ANDROID
        private const string Lib = "turbojpeg";

        [DllImport(Lib)] private static extern IntPtr tjInitCompress();
        [DllImport(Lib)] private static extern IntPtr tjInitDecompress();

        [DllImport(Lib)]
        private static extern int tjCompress2(
            IntPtr   handle,
            IntPtr   srcBuf,
            int      width,
            int      pitch,       // 0 = auto (width * pixel size)
            int      height,
            int      pixelFormat,
            ref IntPtr  jpegBuf,
            ref UIntPtr jpegSize,  // unsigned long: 4 bytes on ARM32, 8 bytes on ARM64 → UIntPtr
            int      jpegSubsamp,
            int      jpegQual,
            int      flags);

        [DllImport(Lib)]
        private static extern int tjDecompress2(
            IntPtr  handle,
            IntPtr  jpegBuf,
            UIntPtr jpegSize,     // unsigned long — same reasoning
            IntPtr  dstBuf,
            int     width,
            int     pitch,
            int     height,
            int     pixelFormat,
            int     flags);

        [DllImport(Lib)] private static extern int  tjDestroy(IntPtr handle);
        [DllImport(Lib)] private static extern void tjFree(IntPtr buffer);
        [DllImport(Lib)] private static extern IntPtr tjGetErrorStr2(IntPtr handle);

        [DllImport(Lib)]
        private static extern int tjDecompressHeader3(
            IntPtr   handle,
            IntPtr   jpegBuf,
            UIntPtr  jpegSize,
            out int  width,
            out int  height,
            out int  jpegSubsamp,
            out int  jpegColorspace);
#endif

        // ── High-level API ───────────────────────────────────────────────────────

        /// <summary>
        /// Compresses a raw RGBA32 frame to a JPEG byte array using ARM NEON SIMD.
        ///
        /// <para>Called on the encode background thread — safe to call from any thread.</para>
        /// </summary>
        /// <param name="rgbaData">Raw RGBA32 pixel bytes from <c>AsyncGPUReadback</c>.</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="quality">JPEG quality 1–100. 75 is a good streaming default.</param>
        /// <param name="subsampling">Chroma subsampling constant (e.g. <see cref="TJSAMP_420"/>).</param>
        /// <returns>Compressed JPEG bytes, or <c>null</c> on failure.</returns>
        public static byte[] Encode(byte[] rgbaData, int width, int height,
                                    int quality, int subsampling = TJSAMP_420)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_ANDROID
            if (rgbaData == null || rgbaData.Length < width * height * 4)
            {
                Debug.LogWarning("[TurboJpeg] Encode: invalid input buffer.");
                return null;
            }

            IntPtr handle = tjInitCompress();
            if (handle == IntPtr.Zero)
            {
                Debug.LogError("[TurboJpeg] tjInitCompress failed.");
                return null;
            }

            // Pin the managed array so the GC cannot move it during the native call
            GCHandle srcPin = GCHandle.Alloc(rgbaData, GCHandleType.Pinned);
            try
            {
                IntPtr  jpegBuf  = IntPtr.Zero;
                UIntPtr jpegSize = UIntPtr.Zero;

                int result = tjCompress2(
                    handle,
                    srcPin.AddrOfPinnedObject(),
                    width,
                    0,              // pitch = 0 → width * sizeof(RGBA)
                    height,
                    TJPF_RGBA,
                    ref jpegBuf,
                    ref jpegSize,
                    subsampling,
                    quality,
                    TJFLAG_FASTDCT);

                if (result != 0)
                {
                    string err = Marshal.PtrToStringAnsi(tjGetErrorStr2(handle));
                    Debug.LogWarning($"[TurboJpeg] Encode error: {err}");
                    if (jpegBuf != IntPtr.Zero) tjFree(jpegBuf);
                    return null;
                }

                // Copy from unmanaged TurboJpeg buffer → managed byte[], then free native memory
                byte[] output = new byte[(int)jpegSize.ToUInt32()];
                Marshal.Copy(jpegBuf, output, 0, output.Length);
                tjFree(jpegBuf);
                return output;
            }
            finally
            {
                srcPin.Free();
                tjDestroy(handle);
            }
#else
            return null;
#endif
        }

        /// <summary>
        /// Decompresses JPEG bytes to a raw RGBA32 pixel array using ARM NEON SIMD.
        ///
        /// <para>Called on the decode background thread — safe to call from any thread.</para>
        /// </summary>
        /// <param name="jpegData">JPEG-compressed bytes received from the client.</param>
        /// <param name="width">Expected output width (must match the encoded frame).</param>
        /// <param name="height">Expected output height.</param>
        /// <param name="dstBuffer">Pre-allocated output buffer of at least width*height*4 bytes.</param>
        /// <returns>True on success.</returns>
        public static bool Decode(byte[] jpegData, int width, int height, byte[] dstBuffer)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_ANDROID
            if (jpegData == null || dstBuffer == null) return false;

            IntPtr handle = tjInitDecompress();
            if (handle == IntPtr.Zero) return false;

            GCHandle srcPin = GCHandle.Alloc(jpegData,  GCHandleType.Pinned);
            GCHandle dstPin = GCHandle.Alloc(dstBuffer, GCHandleType.Pinned);
            try
            {
                int result = tjDecompress2(
                    handle,
                    srcPin.AddrOfPinnedObject(),
                    (UIntPtr)jpegData.Length,
                    dstPin.AddrOfPinnedObject(),
                    width,
                    0,
                    height,
                    TJPF_RGBA,
                    TJFLAG_FASTUPSAMPLE | TJFLAG_FASTDCT);

                return result == 0;
            }
            finally
            {
                srcPin.Free();
                dstPin.Free();
                tjDestroy(handle);
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Reads the width and height of a JPEG image without fully decoding it.
        /// Safe to call from any thread.
        /// </summary>
        public static bool GetImageInfo(byte[] jpegData, out int width, out int height)
        {
            width  = 0;
            height = 0;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_ANDROID
            if (jpegData == null || jpegData.Length == 0) return false;

            IntPtr handle = tjInitDecompress();
            if (handle == IntPtr.Zero) return false;

            GCHandle pin = GCHandle.Alloc(jpegData, GCHandleType.Pinned);
            try
            {
                int result = tjDecompressHeader3(
                    handle,
                    pin.AddrOfPinnedObject(),
                    (UIntPtr)jpegData.Length,
                    out width, out height,
                    out _, out _);
                return result == 0;
            }
            finally
            {
                pin.Free();
                tjDestroy(handle);
            }
#else
            return false;
#endif
        }
    }
}
