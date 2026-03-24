using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

namespace GameViewStream
{
    /// <summary>
    /// Server-side decode engine (PC).
    /// Drains <see cref="StreamServer.FrameQueue"/> every frame, JPEG-decodes each frame,
    /// and paints the result onto registered <see cref="RawImage"/> targets.
    ///
    /// ── Two ways to use this ────────────────────────────────────────────────────
    ///
    /// 1. Inspector (simple):
    ///    Assign <see cref="display"/> and set <see cref="boundClientId"/>.
    ///    ViewDecoder registers itself automatically — no code required.
    ///    Leave <see cref="boundClientId"/> at 0 to claim the next connecting client.
    ///
    /// 2. Code (flexible):
    ///    Leave <see cref="display"/> empty and call
    ///    <see cref="RegisterClient"/> / <see cref="UnregisterClient"/> at runtime.
    ///    Both approaches can coexist on separate ViewDecoder instances.
    ///
    /// ── Setup ───────────────────────────────────────────────────────────────────
    ///   1. Place on any GameObject.
    ///   2. Assign <see cref="server"/> (or leave empty to auto-find on same GameObject).
    ///   3. Assign <see cref="display"/> and optionally set <see cref="boundClientId"/>.
    /// </summary>
    public sealed class ViewDecoder : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("Server")]
        [Tooltip("StreamServer to read frames from. Leave empty to auto-find on this GameObject.")]
        [SerializeField] private StreamServer server;

        [Header("Display")]
        [Tooltip("RawImage this decoder paints frames onto. Can also be set at runtime via RegisterClient().")]
        [SerializeField] private RawImage display;
        [Tooltip("Client ID this decoder is bound to when using the Inspector display.\n"
               + "Set to 0 to automatically claim the next connecting client.")]
        [SerializeField] private ushort boundClientId = 0;
        public ushort ClientId => boundClientId;

        [Header("Decode Settings")]
        [Tooltip("Max frames to decode per Update tick.")]
        [SerializeField, Range(1, 60)] private int maxDecodePerFrame = 16;
        [Tooltip("True  → destroy the internal Texture2D when a client disconnects.\n"
               + "False → only remove the texture reference from the RawImage (display stays intact).")]
        [SerializeField] private bool destroyDisplayOnDisconnect = true;
        [Header("Performance")]
        [Tooltip("Background threads that JPEG-decode incoming frames in parallel (TurboJpeg only).\n"
               + "Recommended: 4 for \u226416 clients, 8 for \u226450 clients.\n"
               + "Falls back to single main-thread decode when TurboJpeg is unavailable.")]
        [SerializeField, Range(1, 16)] private int decodeWorkerCount = 4;
        [Tooltip("Maximum decoded-but-unapplied frames held in the ready queue. "
               + "Oldest are dropped and their pooled buffers returned when this is reached. "
               + "Prevents unbounded memory use if the main thread stalls during long sessions.")]
        [SerializeField, Range(8, 256)] private int readyQueueCap = 64;

        [Header("H.264 Settings")]
        [Tooltip("Enable to decode H.264 frames from clients using the H264 codec path (uNvPipe NVDEC, Windows only).\n"
               + "When disabled or unavailable, the decoder falls back to JPEG for all frames.")]
        [SerializeField] private bool useH264 = false;
        [Tooltip("Expected width of H.264 stream. Must match ViewEncoder.captureWidth on the client.")]
        [SerializeField] private int h264StreamWidth  = 960;
        [Tooltip("Expected height of H.264 stream. Must match ViewEncoder.captureHeight on the client.")]
        [SerializeField] private int h264StreamHeight = 540;

        [Header("Debug (read-only)")]
        [SerializeField] private long _totalFramesDecoded;

        // ── Per-client state ─────────────────────────────────────────────────────

        private sealed class ClientView
        {
            public Texture2D Texture;
            public RawImage  Display;
            public uint      LastFrameId;
            public int       DecodeCount;
        }

        private readonly Dictionary<ushort, ClientView> _views = new Dictionary<ushort, ClientView>();

        // ── Thread-pool decode state ──────────────────────────────────────────────

        /// <summary>A frame that has been JPEG-decoded to raw RGBA32 pixels on a worker thread.</summary>
        private struct DecodedFrame
        {
            public ushort ClientId;
            public uint   FrameId;
            public int    Width;
            public int    Height;
            public byte[] RawPixels;  // rented from ArrayPool<byte>.Shared — MUST be returned after use
            public int    ValidBytes; // Width*Height*4; rented buffer may be larger
        }

        // Workers write here; Update() reads and applies to textures on the main thread
        private readonly ConcurrentQueue<DecodedFrame> _readyQueue = new ConcurrentQueue<DecodedFrame>();
        private CancellationTokenSource _workerCts;
        private Thread[]                _workers;

        // Per-client H.264 decoders (uNvPipe NVDEC, Windows only).
        // Each entry holds the decoder + a per-client lock (H264Decoder is NOT thread-safe).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, (H264Decoder Decoder, object Lock)>
            _h264Decoders = new System.Collections.Concurrent.ConcurrentDictionary<ushort, (H264Decoder, object)>();

        // ── Dependencies ─────────────────────────────────────────────────────────

        private StreamServer _server;

        // ── Unity lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _server = server != null ? server : GetComponent<StreamServer>();

            if (_server == null)
            {
                Debug.LogError("[ViewDecoder] No StreamServer found. Assign it in the Inspector.");
                return;
            }

            // If a display is assigned in the Inspector, register it now.
            // boundClientId 0 means "auto-claim" — we subscribe to the connect event instead.
            if (display != null && boundClientId != 0)
                RegisterClient(boundClientId, display);
        }

        private void OnEnable()
        {
            if (_server == null) return;
            _server.OnClientConnected    += OnClientConnected;
            _server.OnClientDisconnected += OnClientDisconnected;

            // Start background decode workers when TurboJpeg is available (JPEG path)
            // or when H.264 decode is requested (H264Decoder/uNvPipe path).
            bool startWorkers = TurboJpeg.IsAvailable || (useH264 && H264Decoder.IsAvailable);
            if (startWorkers)
            {
                _workerCts = new CancellationTokenSource();
                _workers   = new Thread[decodeWorkerCount];
                for (int i = 0; i < decodeWorkerCount; i++)
                {
                    var t = new Thread(DecodeWorkerLoop)
                        { IsBackground = true, Name = $"ViewDecoder-Worker-{i}" };
                    _workers[i] = t;
                    t.Start();
                }
                string mode = TurboJpeg.IsAvailable ? "TurboJpeg" : "";
                if (useH264 && H264Decoder.IsAvailable) mode += (mode.Length > 0 ? " + " : "") + "H264/uNvPipe";
                Debug.Log($"[ViewDecoder] Started {decodeWorkerCount} decode worker(s) ({mode}).");
            }
        }

        private void OnDisable()
        {
            if (_server == null) return;
            _server.OnClientConnected    -= OnClientConnected;
            _server.OnClientDisconnected -= OnClientDisconnected;

            _workerCts?.Cancel();
            _workerCts = null;
            _workers   = null;

            // Return any decoded-but-unapplied pixel buffers so the pool stays clean.
            DrainReadyQueue();
        }

        private void Update()
        {
            if (_server == null) return;

            bool useWorkers = TurboJpeg.IsAvailable || (useH264 && H264Decoder.IsAvailable);

            if (useWorkers)
            {
                // Workers decoded frames off the main thread.
                // Here we only upload pixel data to the GPU (fast: no decompression, just a memcpy).
                for (int i = 0; i < maxDecodePerFrame; i++)
                {
                    if (!_readyQueue.TryDequeue(out DecodedFrame df)) break;
                    ApplyDecoded(df);
                }
            }
            else
            {
                // Fallback: full JPEG decode on main thread (slow; only when TurboJpeg unavailable)
                for (int i = 0; i < maxDecodePerFrame; i++)
                {
                    if (!_server.FrameQueue.TryDequeue(out ClientFrameData frame)) break;
                    DecodeFrameMainThread(frame);
                }
            }
        }

        private void OnDestroy()
        {
            DrainReadyQueue();
            foreach (var view in _views.Values)
                if (view.Texture != null) Destroy(view.Texture);
            _views.Clear();

            // Dispose all H264Decoders to release NVDEC resources
            foreach (var kv in _h264Decoders)
                kv.Value.Decoder?.Dispose();
            _h264Decoders.Clear();
        }

        /// <summary>Dispose and remove the H264Decoder for the given client, if any.</summary>
        private void DisposeH264Decoder(ushort clientId)
        {
            if (_h264Decoders.TryRemove(clientId, out var entry))
            {
                lock (entry.Lock)
                    entry.Decoder?.Dispose();
            }
        }

        /// <summary>Returns all pooled pixel buffers still sitting in <see cref="_readyQueue"/> to <see cref="ArrayPool{T}"/>.
        /// Call this whenever workers are stopped or the component is destroyed.</summary>
        private void DrainReadyQueue()
        {
            while (_readyQueue.TryDequeue(out DecodedFrame df))
                if (df.RawPixels != null)
                    ArrayPool<byte>.Shared.Return(df.RawPixels);
        }

        // ── Auto-wiring from server events ───────────────────────────────────────

        private void OnClientConnected(ushort clientId, IPAddress address)
        {
            if (display == null) return;   // no inspector display — let code handle it

            if (boundClientId == 0)
            {
                // Auto-claim mode: bind this display to the first client we haven't seen yet
                if (!_views.ContainsKey(clientId))
                {
                    RegisterClient(clientId, display);
                    boundClientId = clientId; // remember which client we claimed
                    Debug.Log($"[ViewDecoder] Auto-claimed client {clientId} → {display.name}");
                }
            }
            // If boundClientId != 0 the display was already registered in Awake
        }

        private void OnClientDisconnected(ushort clientId)
        {
            if (destroyDisplayOnDisconnect)
                UnregisterClient(clientId);  // destroys Texture2D
            else
                DetachDisplay(clientId);     // nulls the RawImage.texture reference only

            // Reset auto-claim so the next connecting client re-uses this display slot
            if (clientId == boundClientId && display != null)
                boundClientId = 0;
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Bind a <see cref="RawImage"/> to <paramref name="clientId"/>.
        /// Frames from that client are decoded onto the image immediately.
        /// Safe to call again with a different image to swap the display at runtime.
        /// </summary>
        public void RegisterClient(ushort clientId, RawImage rawImage)
        {
            if (!_views.TryGetValue(clientId, out ClientView view))
            {
                view = new ClientView
                {
                    Texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
                    {
                        filterMode = FilterMode.Bilinear,
                        wrapMode   = TextureWrapMode.Clamp,
                    },
                    LastFrameId = 0,
                    DecodeCount = 0,
                };
                _views[clientId] = view;
            }

            view.Display = rawImage;

            if (rawImage != null)
            {
                rawImage.texture = view.Texture;
                rawImage.uvRect  = new UnityEngine.Rect(0f, 0f, 1f, 1f);
            }
        }

        /// <summary>
        /// Removes the texture reference from the client's <see cref="RawImage"/> without
        /// destroying anything. The display GameObject remains intact and shows nothing.
        /// </summary>
        public void DetachDisplay(ushort clientId)
        {
            if (!_views.TryGetValue(clientId, out ClientView view)) return;
            if (view.Display != null)
                view.Display.texture = null;
            _views.Remove(clientId);
        }

        /// <summary>
        /// Destroys the internal <see cref="Texture2D"/> and removes the client binding.
        /// The caller is responsible for destroying the <see cref="RawImage"/> if needed.
        /// </summary>
        public void UnregisterClient(ushort clientId)
        {
            if (!_views.TryGetValue(clientId, out ClientView view)) return;
            if (view.Texture != null) Destroy(view.Texture);
            _views.Remove(clientId);

            // Dispose the per-client H264Decoder if present
            DisposeH264Decoder(clientId);
        }

        /// <summary>Swap the <see cref="RawImage"/> for an already-registered client.</summary>
        public void SetDisplay(ushort clientId, RawImage rawImage)
        {
            if (!_views.TryGetValue(clientId, out ClientView view))
            {
                Debug.LogWarning($"[ViewDecoder] SetDisplay: client {clientId} not registered.");
                return;
            }
            view.Display = rawImage;
            if (rawImage != null)
            {
                rawImage.texture = view.Texture;
                rawImage.uvRect  = new UnityEngine.Rect(0f, 0f, 1f, 1f);
            }
        }

        /// <summary>Returns the live decoded <see cref="Texture2D"/> for a client, or null.</summary>
        public Texture2D GetClientTexture(ushort clientId)
            => _views.TryGetValue(clientId, out ClientView v) ? v.Texture : null;

        /// <summary>Returns decode statistics for every registered client.</summary>
        public IEnumerable<(ushort id, int frames)> GetStats()
        {
            foreach (var kv in _views)
                yield return (kv.Key, kv.Value.DecodeCount);
        }

        // ── Thread-pool decode worker ────────────────────────────────────────────

        private void DecodeWorkerLoop()
        {
            while (_workerCts != null && !_workerCts.IsCancellationRequested)
            {
                if (_server == null || !_server.FrameQueue.TryDequeue(out ClientFrameData frame))
                {
                    Thread.Sleep(1);
                    continue;
                }

                // ── Route by codec type ───────────────────────────────────────────

                switch (frame.PacketType)
                {
                    case NetworkProtocol.PacketType.VideoFrame:
                        DecodeWorkerJpeg(frame);
                        break;

                    case NetworkProtocol.PacketType.H264Frame:
                    case NetworkProtocol.PacketType.H264Fragment:
                        DecodeWorkerH264(frame);
                        break;

                    default:
                        // Unknown or non-video packet — return pool buffer and skip
                        if (frame.PixelData != null)
                            ArrayPool<byte>.Shared.Return(frame.PixelData);
                        break;
                }
            }
        }

        // ── JPEG decode path (worker thread) ─────────────────────────────────────

        private void DecodeWorkerJpeg(ClientFrameData frame)
        {
            byte[] jpegData = frame.PixelData;
            int    jpegLen  = frame.PayloadLength;

            if (jpegData == null || jpegLen == 0)
            {
                if (jpegData != null) ArrayPool<byte>.Shared.Return(jpegData);
                return;
            }

            if (!TurboJpeg.GetImageInfo(jpegData, out int w, out int h))
            {
                ArrayPool<byte>.Shared.Return(jpegData);
                Debug.LogWarning($"[ViewDecoder] Worker: could not read JPEG info for client {frame.ClientId} — dropped.");
                return;
            }

            int    validBytes  = w * h * 4;
            byte[] pixelBuffer = ArrayPool<byte>.Shared.Rent(validBytes);

            if (!TurboJpeg.Decode(jpegData, w, h, pixelBuffer))
            {
                ArrayPool<byte>.Shared.Return(pixelBuffer);
                ArrayPool<byte>.Shared.Return(jpegData);
                Debug.LogWarning($"[ViewDecoder] Worker: TurboJpeg.Decode failed for client {frame.ClientId} — dropped.");
                return;
            }

            // JPEG source no longer needed — return it immediately
            ArrayPool<byte>.Shared.Return(jpegData);

            EnqueueDecoded(frame.ClientId, frame.FrameId, w, h, pixelBuffer, validBytes);
        }

        // ── H.264 decode path (worker thread) ────────────────────────────────────

        private void DecodeWorkerH264(ClientFrameData frame)
        {
            byte[] payload    = frame.PixelData;
            int    payloadLen = frame.PayloadLength;

            if (payload == null || payloadLen == 0)
            {
                if (payload != null) ArrayPool<byte>.Shared.Return(payload);
                return;
            }

            // Lazily create and initialise an H264Decoder for this client
            var entry = _h264Decoders.GetOrAdd(frame.ClientId,
                _ => (new H264Decoder(), new object()));

            byte[] rgba = null;
            lock (entry.Lock)
            {
                if (!entry.Decoder.IsReady)
                {
                    if (!entry.Decoder.Initialize(h264StreamWidth, h264StreamHeight))
                    {
                        Debug.LogError($"[ViewDecoder] H264Decoder.Initialize failed for client {frame.ClientId}.");
                        ArrayPool<byte>.Shared.Return(payload);
                        return;
                    }
                }

                if (frame.PacketType == NetworkProtocol.PacketType.H264Frame)
                    rgba = entry.Decoder.Decode(payload, 0, payloadLen);
                else
                    rgba = entry.Decoder.FeedFragment(payload, payloadLen, frame.FrameId);
            }

            // H264 payload no longer needed
            ArrayPool<byte>.Shared.Return(payload);

            if (rgba == null) return; // fragment not yet complete, or decode failed

            int validBytes = h264StreamWidth * h264StreamHeight * 4;
            EnqueueDecoded(frame.ClientId, frame.FrameId, h264StreamWidth, h264StreamHeight, rgba, validBytes);
        }

        // ── Shared ready-queue enqueue helper ────────────────────────────────────

        private void EnqueueDecoded(ushort clientId, uint frameId, int w, int h, byte[] pixels, int validBytes)
        {
            // Cap the ready queue to protect memory during long sessions.
            // When full, drop the oldest decoded frame and return its pooled buffer.
            while (_readyQueue.Count >= readyQueueCap)
            {
                if (_readyQueue.TryDequeue(out DecodedFrame old) && old.RawPixels != null)
                    ArrayPool<byte>.Shared.Return(old.RawPixels);
            }

            _readyQueue.Enqueue(new DecodedFrame
            {
                ClientId   = clientId,
                FrameId    = frameId,
                Width      = w,
                Height     = h,
                RawPixels  = pixels,
                ValidBytes = validBytes,
            });
        }

        // ── Apply decoded frame (main thread) ────────────────────────────────────

        /// <summary>Uploads a pre-decoded RGBA32 buffer to the client Texture2D. Must run on the main thread.</summary>
        private void ApplyDecoded(DecodedFrame df)
        {
            if (!_views.TryGetValue(df.ClientId, out ClientView view))
            {
                // Client unregistered before this frame was applied — return buffer and bail.
                ArrayPool<byte>.Shared.Return(df.RawPixels);
                return;
            }

            // Drop stale frames (tolerate uint32 wrap-around)
            if (df.FrameId < view.LastFrameId && (view.LastFrameId - df.FrameId) < 1000)
            {
                ArrayPool<byte>.Shared.Return(df.RawPixels);
                return;
            }

            // Resize texture if stream resolution changed
            if (view.Texture.width != df.Width || view.Texture.height != df.Height)
                view.Texture.Reinitialize(df.Width, df.Height, TextureFormat.RGBA32, false);

            // Use LoadRawTextureData(IntPtr, int) to pass only the valid byte range.
            // This overload has been available since Unity 5.6, and correctly handles
            // rented buffers that are larger than the exact pixel data size.
            GCHandle pin = GCHandle.Alloc(df.RawPixels, GCHandleType.Pinned);
            try
            {
                view.Texture.LoadRawTextureData(pin.AddrOfPinnedObject(), df.ValidBytes);
            }
            finally
            {
                pin.Free();
                // Always return the rented buffer, even if LoadRawTextureData threw.
                ArrayPool<byte>.Shared.Return(df.RawPixels);
            }

            view.Texture.Apply(updateMipmaps: false);

            if (view.Display != null)
                view.Display.texture = view.Texture;

            view.LastFrameId = df.FrameId;
            view.DecodeCount++;
            _totalFramesDecoded++;
        }

        // ── Fallback main-thread decode ───────────────────────────────────────────

        /// <summary>Decode via Unity's managed ImageConversion — only used when TurboJpeg is unavailable.</summary>
        private void DecodeFrameMainThread(ClientFrameData frame)
        {
            byte[] data = frame.PixelData;
            if (data == null || frame.PayloadLength == 0)
            {
                if (data != null) ArrayPool<byte>.Shared.Return(data);
                return;
            }

            if (!_views.TryGetValue(frame.ClientId, out ClientView view))
            {
                ArrayPool<byte>.Shared.Return(data);
                return;
            }

            if (frame.FrameId < view.LastFrameId && (view.LastFrameId - frame.FrameId) < 1000)
            {
                ArrayPool<byte>.Shared.Return(data);
                return;
            }

            // ImageConversion.LoadImage reads only what it needs based on the JPEG header,
            // so an oversized rented buffer is safe here.
            if (ImageConversion.LoadImage(view.Texture, data, markNonReadable: false))
            {
                if (view.Display != null)
                    view.Display.texture = view.Texture;

                view.LastFrameId = frame.FrameId;
                view.DecodeCount++;
                _totalFramesDecoded++;
            }
            else
            {
                Debug.LogWarning($"[ViewDecoder] Failed to decode frame from client {frame.ClientId}.");
            }

            ArrayPool<byte>.Shared.Return(data);
        }
    }
}
