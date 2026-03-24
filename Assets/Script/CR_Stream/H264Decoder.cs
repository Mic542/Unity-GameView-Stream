// H264Decoder.cs  –  per-client stateful H.264 decoder wrapping uNvPipe NVDEC (Windows PC).
//
// Design:
//   • One H264Decoder instance per connected client (managed by ViewDecoder.ClientView).
//   • NOT thread-safe for concurrent calls.  ViewDecoder workers must lock before calling Decode().
//   • Fragments are reassembled here via a simple FragmentBuffer.  Call FeedFragment() for each
//     H264Fragment packet; call Decode() only when a full H264Frame arrives OR when FeedFragment()
//     signals that all fragments have been received.
//   • IDisposable: calls uNvPipe.Lib.DeleteDecoder() to free GPU resources.
//
// Platform guard: only compiled + active on Windows (standalone + Editor).
// On Android the encoder sends H264; the corresponding decoded pixels arrive at the PC server.

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GameViewStream
{

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN

    /// <summary>
    /// Per-client H.264 decoder backed by uNvPipe NVDEC.
    /// Call <see cref="Initialize"/> once, then <see cref="Decode"/> for complete frames
    /// or <see cref="FeedFragment"/> for fragmented frames.
    ///
    /// <para>Thread safety: NOT safe for concurrent calls to the same instance — use an external lock.</para>
    /// <para>Dispose to free native GPU resources.</para>
    /// </summary>
    public sealed class H264Decoder : IDisposable
    {
        // ── State ────────────────────────────────────────────────────────────────

        private int  _id       = -1;
        private bool _disposed = false;

        // ── Fragment reassembly ───────────────────────────────────────────────────

        private sealed class FragmentBuffer
        {
            public uint    FrameId;         // which logical frame these fragments belong to
            public byte[]  Data;            // assembled NAL bytes (grows as needed)
            public int     TotalBytes;      // total expected bytes after all fragments are combined
            public bool[]  ReceivedFlags;   // which fragment indices have arrived
            public int     ReceivedCount;
            public int     TotalFragments;
            public long    LastUpdateMs;    // for stale-buffer cleanup
        }

        private FragmentBuffer _fragBuffer;

        // ── Properties ───────────────────────────────────────────────────────────

        public int  Width    { get; private set; }
        public int  Height   { get; private set; }
        public bool IsReady  => _id >= 0 && !_disposed;

        // ── Availability ─────────────────────────────────────────────────────────

        private static bool? _isAvailable;

        /// <summary>
        /// True when uNvPipe is loaded and the platform supports NVDEC (NVIDIA GPU, Windows).
        /// Determined lazily on first access by attempting to load the native library.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable.HasValue) return _isAvailable.Value;

                try
                {
                    int testId = uNvPipe.Lib.CreateDecoder();
                    if (testId >= 0) uNvPipe.Lib.DeleteDecoder(testId);
                    _isAvailable = true;
                }
                catch (DllNotFoundException)
                {
                    _isAvailable = false;
                    Debug.LogWarning("[H264Decoder] uNvPipe DLL not found — H.264 decode unavailable. Falling back to JPEG.");
                }
                catch (Exception e)
                {
                    _isAvailable = false;
                    Debug.LogWarning($"[H264Decoder] Could not probe uNvPipe: {e.Message} — H.264 decode unavailable.");
                }
                return _isAvailable.Value;
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Create and initialise the uNvPipe NVDEC H.264 decoder for the given frame dimensions.
        /// Returns false (and logs an error) if initialisation fails.
        /// </summary>
        public bool Initialize(int width, int height)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(H264Decoder));

            Width  = width;
            Height = height;

            _id = uNvPipe.Lib.CreateDecoder();
            if (_id < 0)
            {
                Debug.LogError("[H264Decoder] CreateDecoder returned invalid id.");
                return false;
            }

            uNvPipe.Lib.SetDecoderWidth (_id, width);
            uNvPipe.Lib.SetDecoderHeight(_id, height);
            uNvPipe.Lib.SetDecoderFormat(_id, uNvPipe.Format.RGBA32);
            uNvPipe.Lib.SetDecoderCodec (_id, uNvPipe.Codec.H264);

            if (!uNvPipe.Lib.InitializeDecoder(_id))
            {
                string err = uNvPipe.Lib.DecoderGetError(_id);
                Debug.LogError($"[H264Decoder] InitializeDecoder failed: {err}");
                uNvPipe.Lib.DeleteDecoder(_id);
                _id = -1;
                return false;
            }

            return true;
        }

        // ── Fragment reassembly ───────────────────────────────────────────────────

        private static long NowMs =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>
        /// Feed one H264Fragment packet payload (after the 15-byte GVST header).
        /// The payload starts with the 2-byte fragment sub-header [frag_idx][frag_total]
        /// followed by the H.264 chunk bytes.
        ///
        /// Returns a rented <see cref="ArrayPool{T}"/> RGBA32 buffer (size = Width*Height*4)
        /// when the last fragment completes the frame and decode succeeds; otherwise null.
        /// The caller MUST return the buffer to <see cref="ArrayPool{byte}.Shared"/> when done.
        /// </summary>
        public byte[] FeedFragment(byte[] payload, int payloadLength, uint frameId)
        {
            if (!IsReady) return null;

            if (payloadLength < NetworkProtocol.FragSubHeaderSize)
            {
                Debug.LogWarning("[H264Decoder] Fragment payload too small.");
                return null;
            }

            byte fragIdx   = payload[0];
            byte fragTotal = payload[1];

            if (fragTotal == 0) return null;

            // Reset buffer if this is a new frame or the old buffer is stale (> 2 sec old)
            if (_fragBuffer == null
                || _fragBuffer.FrameId != frameId
                || (NowMs - _fragBuffer.LastUpdateMs) > 2000)
            {
                _fragBuffer = new FragmentBuffer
                {
                    FrameId        = frameId,
                    Data           = ArrayPool<byte>.Shared.Rent(fragTotal * NetworkProtocol.MaxH264ChunkSize),
                    TotalBytes     = 0,
                    ReceivedFlags  = new bool[fragTotal],
                    ReceivedCount  = 0,
                    TotalFragments = fragTotal,
                    LastUpdateMs   = NowMs,
                };
            }

            _fragBuffer.LastUpdateMs = NowMs;

            if (fragIdx >= _fragBuffer.TotalFragments) return null;  // out-of-range index

            // Write this fragment's data into the correct position in the assembled buffer
            int chunkSize   = payloadLength - NetworkProtocol.FragSubHeaderSize;
            int destOffset  = fragIdx * NetworkProtocol.MaxH264ChunkSize;

            if (destOffset + chunkSize > _fragBuffer.Data.Length)
            {
                Debug.LogWarning("[H264Decoder] Fragment data exceeds reassembly buffer — dropped.");
                return null;
            }

            Buffer.BlockCopy(payload, NetworkProtocol.FragSubHeaderSize, _fragBuffer.Data, destOffset, chunkSize);

            if (!_fragBuffer.ReceivedFlags[fragIdx])
            {
                _fragBuffer.ReceivedFlags[fragIdx] = true;
                _fragBuffer.ReceivedCount++;
                // Track total valid bytes as the maximum contiguous end
                _fragBuffer.TotalBytes = Math.Max(_fragBuffer.TotalBytes, destOffset + chunkSize);
            }

            if (_fragBuffer.ReceivedCount < _fragBuffer.TotalFragments)
                return null; // frame not complete yet

            // All fragments received — decode the assembled NAL
            byte[] rgba = Decode(_fragBuffer.Data, 0, _fragBuffer.TotalBytes);

            // Return the fragment assembly buffer to the pool
            ArrayPool<byte>.Shared.Return(_fragBuffer.Data);
            _fragBuffer = null;

            return rgba;
        }

        /// <summary>
        /// Decode a complete H.264 Annex-B NAL byte sequence (from an H264Frame packet, or
        /// assembled from fragments internally by <see cref="FeedFragment"/>).
        ///
        /// Returns a rented RGBA32 buffer (size = Width*Height*4) on success, or null on failure.
        /// The caller MUST return the buffer to <see cref="ArrayPool{byte}.Shared"/> when done.
        /// </summary>
        public byte[] Decode(byte[] data, int offset, int length)
        {
            if (!IsReady || data == null || length <= 0) return null;

            // Pin and pass the H.264 NAL bytes to uNvPipe NVDEC
            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            bool decodeOk;
            try
            {
                IntPtr ptr = new IntPtr(pin.AddrOfPinnedObject().ToInt64() + offset);
                decodeOk = uNvPipe.Lib.DecoderDecode(_id, ptr, length);
            }
            finally
            {
                pin.Free();
            }

            if (!decodeOk)
            {
                string err = uNvPipe.Lib.DecoderGetError(_id);
                Debug.LogWarning($"[H264Decoder] DecoderDecode failed: {err}");
                return null;
            }

            IntPtr outPtr  = uNvPipe.Lib.GetDecoderDecodedData(_id);
            int    outSize = uNvPipe.Lib.GetDecoderDecodedSize(_id);

            if (outPtr == IntPtr.Zero || outSize <= 0) return null;

            int    validBytes = Width * Height * 4;
            int    copyBytes  = Math.Min(outSize, validBytes);
            byte[] buf        = ArrayPool<byte>.Shared.Rent(validBytes);

            // Copy from uNvPipe's internal native buffer to managed memory.
            // The native buffer is valid only until the next Decode() call on this instance.
            Marshal.Copy(outPtr, buf, 0, copyBytes);

            // H.264 has no alpha channel — NVDEC leaves the A byte at 0 in the output buffer.
            // Force every alpha byte to 255 (fully opaque) so the RawImage is not transparent.
            for (int i = 3; i < copyBytes; i += 4)
                buf[i] = 255;

            return buf;
        }

        // ── IDisposable ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_id >= 0)
            {
                uNvPipe.Lib.DeleteDecoder(_id);
                _id = -1;
            }
            // Return any in-progress fragment buffer
            if (_fragBuffer?.Data != null)
                ArrayPool<byte>.Shared.Return(_fragBuffer.Data);
            _fragBuffer = null;
        }
    }

#else

    /// <summary>
    /// Stub: H.264 decoding via uNvPipe NVDEC is only supported on Windows (PC server).
    /// On Android the device is the encoder, not the decoder.
    /// </summary>
    public sealed class H264Decoder : IDisposable
    {
        public static bool IsAvailable => false;
        public bool IsReady  => false;
        public int  Width    { get; private set; }
        public int  Height   { get; private set; }

        public bool   Initialize(int width, int height)                              => false;
        public byte[] Decode(byte[] data, int offset, int length)                   => null;
        public byte[] FeedFragment(byte[] payload, int payloadLength, uint frameId) => null;
        public void   Dispose() { }
    }

#endif

} // namespace GameViewStream
