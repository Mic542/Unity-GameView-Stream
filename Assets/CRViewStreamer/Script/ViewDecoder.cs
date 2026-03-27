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
    public enum CodecMode { MJPEG = 0, H264 = 1 }

    public sealed class ViewDecoder : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Tooltip("StreamServer to read frames from. Leave empty to auto-find on this GameObject.")]
        [SerializeField] private StreamServer server;
        /// <summary>StreamServer this decoder reads from. Set before the component is enabled.</summary>
        public StreamServer Server { get => server; set => server = value; }

        [Tooltip("RawImage this decoder paints frames onto. Can also be set at runtime via RegisterClient().")]
        [SerializeField] private RawImage display;
        /// <summary>RawImage this decoder paints frames onto. Can be set before enabling or via RegisterClient().</summary>
        public RawImage Display { get => display; set => display = value; }
        [Tooltip("Client ID this decoder is bound to when using the Inspector display.\n"
               + "Set to 0 to automatically claim the next connecting client.")]
        [SerializeField] private ushort boundClientId = 0;
        /// <summary>Client ID this decoder is bound to. Set to 0 for auto-claim. Can be set at runtime before enabling.</summary>
        public ushort ClientId { get => boundClientId; set => boundClientId = value; }

        [Tooltip("Max frames to decode per Update tick. Applies to both MJPEG and H264 paths.")]
        [SerializeField, Range(1, 60)] private int maxDecodePerFrame = 16;
        [Tooltip("Background threads used for decoding. Applies to both MJPEG (TurboJpeg) and H264 (GVSTDecoder).\n"
               + "Recommended: 4 for \u226416 clients, 8 for \u226450 clients.\n"
               + "Falls back to single main-thread decode when no hardware/library is available.")]
        [SerializeField, Range(1, 16)] private int decodeWorkerCount = 4;
        [Tooltip("Maximum decoded-but-unapplied frames held per-client ready queue. Applies to both MJPEG and H264.\n"
               + "Oldest are dropped and their pooled buffers returned when this is reached. "
               + "Prevents unbounded memory use if the main thread stalls during long sessions.")]
        [SerializeField, Range(8, 256)] private int readyQueueCap = 64;

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

        // ── Externally-managed auto-claim state ───────────────────────────────────
        // Set by SetAutoClaimForIP(). When non-null, the next StreamServer client whose
        // IP matches _autoClaimIP is registered automatically, so frames are keyed by
        // the StreamServer ID instead of an independent Netcode client ID.
        private string   _autoClaimIP      = null;
        private RawImage _autoClaimDisplay = null;

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

        // Per-client H.264 decoders (GVSTDecoder MFT, Windows only).
        // Each entry holds the decoder + a per-client lock (H264Decoder is NOT thread-safe).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, (H264Decoder Decoder, object Lock)>
            _h264Decoders = new System.Collections.Concurrent.ConcurrentDictionary<ushort, (H264Decoder, object)>();

        // ── Dependencies ─────────────────────────────────────────────────────────

        private StreamServer _server;
        private bool _started;   // true after Start() has run — guards OnEnable on first activation

        // ── Unity lifecycle ──────────────────────────────────────────────────────

        /// <summary>
        /// Resolves <see cref="_server"/> and performs initial registration.
        /// Called once from <see cref="Start"/> so that runtime <c>AddComponent</c>
        /// users can set <see cref="Server"/>, <see cref="Display"/>, etc.
        /// before initialisation runs.
        /// </summary>
        private void ResolveAndRegister()
        {
            _server = server != null ? server : GetComponent<StreamServer>();

            if (_server == null)
            {
                Debug.LogError("[ViewDecoder] No StreamServer found. Assign one via the Inspector "
                             + "or set the Server property before enabling this component.");
                return;
            }

            // If a display is assigned in the Inspector, register it now.
            // boundClientId 0 means "auto-claim" — we subscribe to the connect event instead.
            if (display != null && boundClientId != 0)
                RegisterClient(boundClientId, display);
        }

        private void Start()
        {
            _started = true;
            ResolveAndRegister();
            BeginListening();
        }

        private void OnEnable()
        {
            // First activation: Start() hasn't run yet — it will call BeginListening().
            // This lets AddComponent callers configure properties before init.
            if (!_started) return;
            BeginListening();
        }

        /// <summary>
        /// Subscribes to server events and spins up background decode workers.
        /// Called from <see cref="Start"/> (first time) and <see cref="OnEnable"/> (re-enable).
        /// </summary>
        private void BeginListening()
        {
            if (_server == null) return;
            _server.OnClientConnected    += OnClientConnected;
            _server.OnClientDisconnected += OnClientDisconnected;

            // Start background decode workers when any decode backend is available.
            // Codec is detected automatically from the incoming packet type (VideoFrame vs H264Frame).
            bool canDecode = TurboJpeg.IsAvailable || H264Decoder.IsAvailable;
            if (canDecode)
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
                string backends = "";
                if (TurboJpeg.IsAvailable)     backends += "TurboJpeg";
                if (H264Decoder.IsAvailable)   backends += (backends.Length > 0 ? " + " : "") + "GVSTDecoder";
                Debug.Log($"[ViewDecoder] Started {decodeWorkerCount} decode worker(s) ({backends}).");
            }
        }

        private void OnDisable()
        {
            if (_server == null) return;
            _server.OnClientConnected    -= OnClientConnected;
            _server.OnClientDisconnected -= OnClientDisconnected;

            _workerCts?.Cancel();

            // Wait for all worker threads to finish before we dispose decoders.
            // Without this, workers may still be inside GVST_Feed (holding the per-decoder lock)
            // when we call Dispose → DeleteDecoder — causing a permanent deadlock/freeze.
            if (_workers != null)
            {
                foreach (var t in _workers)
                    t?.Join(2000); // 2-second safety timeout per thread
                _workers = null;
            }

            _workerCts = null;

            // Return any decoded-but-unapplied pixel buffers so the pool stays clean.
            DrainReadyQueue();

            // Dispose all H264Decoders NOW (after threads are joined, so no concurrent access).
            // Previously this was only in OnDestroy, which could race with worker threads or
            // with MainThreadDispatcher callbacks still firing between OnDisable and OnDestroy.
            foreach (var kv in _h264Decoders)
                kv.Value.Decoder?.Dispose();
            _h264Decoders.Clear();
        }

        private void Update()
        {
            if (_server == null) return;

            bool useWorkers = TurboJpeg.IsAvailable || H264Decoder.IsAvailable;

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

            // Safety net: dispose any H264Decoders that survived (normally already done in OnDisable).
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
            if (display != null)
            {
                // Inspector-wired mode: claim if auto-claim or matching re-connect.
                if (boundClientId == 0 || boundClientId == clientId)
                {
                    RegisterClient(clientId, display);
                    boundClientId = clientId;
                    Debug.Log($"[ViewDecoder] Auto-claimed client {clientId} → {display.name}");
                }
                return;
            }

            // Externally-managed mode: claim if this client's IP matches the expected device.
            // _autoClaimIP and _autoClaimDisplay are set by SetAutoClaimForIP() below.
            // This ensures ViewDecoder is always keyed by the StreamServer's own client ID,
            // not by an independent Netcode client ID — eliminating the ID mismatch that
            // caused frames to be silently dropped after reconnect.
            if (_autoClaimIP != null && address.ToString() == _autoClaimIP)
            {
                RegisterClient(clientId, _autoClaimDisplay);
                boundClientId = clientId;
                Debug.Log($"[ViewDecoder] IP-claimed StreamServer client {clientId} ({address}) → "
                        + (_autoClaimDisplay != null ? _autoClaimDisplay.name : "null display"));
            }
        }

        private void OnClientDisconnected(ushort clientId)
        {
            if (_views.TryGetValue(clientId, out ClientView view))
            {
                if (display != null)
                {
                    // Inspector-wired mode: null the texture (caller expects blank on disconnect).
                    if (view.Display != null)
                        view.Display.texture = null;
                }
                else
                {
                    // Externally-managed mode: preserve the last decoded frame on screen.
                    // The disconnect mask (shown by the caller) already signals loss of connection.
                    // Save the display ref so the next StreamServer connect re-claims it automatically.
                    _autoClaimDisplay = view.Display;
                }
                _views.Remove(clientId);
            }

            // Re-arm for the next StreamServer connect event for this device.
            // Do NOT dispose the H264Decoder — it will be reused or cleaned up in OnDisable.
            if (clientId == boundClientId)
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

        /// <summary>
        /// Arms this decoder to auto-claim the next <see cref="StreamServer"/> client whose
        /// IP address equals <paramref name="ipAddress"/>.
        /// Use this instead of <see cref="RegisterClient"/> when operating under
        /// <c>ServerComponent</c>: it binds the view to the StreamServer's own client ID
        /// rather than an independent Netcode client ID, so frame lookup in
        /// <c>ApplyDecoded</c> always succeeds — even when the two systems assign different IDs.
        /// Safe to call multiple times; each call re-arms for the next connect event.
        /// </summary>
        public void SetAutoClaimForIP(string ipAddress, RawImage rawImage)
        {
            _autoClaimIP      = ipAddress;
            _autoClaimDisplay = rawImage;
            boundClientId     = 0;   // re-arm so OnClientConnected will claim the next match

            // Catch-up: the StreamServer TCP connection may have arrived and fired OnClientConnected
            // BEFORE this method was called (e.g. the streaming TCP connect completes one frame
            // before the Netcode "Connected" named message is processed by ServerComponent).
            // In that case OnClientConnected already ran with _autoClaimIP == null and was ignored.
            // Check now whether the matching client is already connected and register immediately.
            // Use `server` (the inspector/public field) as a fallback because this method may be
            // called immediately after AddComponent — before Start() has resolved `_server`.
            StreamServer activeServer = _server ?? server;
            if (activeServer != null && IPAddress.TryParse(ipAddress, out IPAddress addr))
            {
                if (activeServer.TryGetClientIdByAddress(addr, out ushort existingId))
                {
                    RegisterClient(existingId, rawImage);
                    boundClientId = existingId;
                    Debug.Log($"[ViewDecoder] SetAutoClaimForIP: catch-up registered already-connected "
                            + $"client {existingId} ({ipAddress})");
                }
            }
        }

        /// <summary>Returns decode statistics for every registered client.</summary>
        public IEnumerable<(ushort id, int frames)> GetStats()
        {
            foreach (var kv in _views)
                yield return (kv.Key, kv.Value.DecodeCount);
        }

        // ── Thread-pool decode worker ────────────────────────────────────────────

        private void DecodeWorkerLoop()
        {
            // SpinWait yields with increasing backoff (busy-spin → Thread.Yield → Thread.Sleep(0/1)).
            // This avoids the fixed ~15 ms stall that Thread.Sleep(1) imposes on Windows due to the
            // default 15.6 ms timer resolution, which is the primary cause of decode jitter under
            // bursty network conditions.
            SpinWait spin = default;
            while (_workerCts != null && !_workerCts.IsCancellationRequested)
            {
                if (_server == null || !_server.FrameQueue.TryDequeue(out ClientFrameData frame))
                {
                    spin.SpinOnce();
                    continue;
                }
                spin.Reset();

                // ── Route by codec type ───────────────────────────────────────────

                switch (frame.PacketType)
                {
                    case NetworkProtocol.PacketType.VideoFrame:
                        DecodeWorkerJpeg(frame);
                        break;

                    case NetworkProtocol.PacketType.H264Frame:
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

            // Lazily create and initialise an H264Decoder for this client.
            // The init hint (960×540) is only used to size the first staging buffer;
            // the MFT reads the actual resolution from the H.264 SPS header and
            // H264Decoder.Decode auto-reallocates if needed.
            const int initHintW = 960;
            const int initHintH = 540;

            var entry = _h264Decoders.GetOrAdd(frame.ClientId,
                _ => (new H264Decoder(), new object()));

            // H.264 NAL units MUST be fed in strict FIFO order per client.
            // With N workers, two can dequeue frames K and K+1 simultaneously.
            // If we used a blocking lock, worker-B could win the lock first and feed K+1
            // before K — corrupting decoder state until the next IDR.
            // Instead, TryEnter: if another worker is already decoding for this client,
            // re-enqueue the frame so it's retried later in the correct order.
            if (!Monitor.TryEnter(entry.Lock))
            {
                _server.FrameQueue.Enqueue(frame);  // put it back for retry
                return;
            }

            byte[] rgba = null;
            int decodedW, decodedH;
            try
            {
                if (!entry.Decoder.IsReady)
                {
                    if (!entry.Decoder.Initialize(initHintW, initHintH))
                    {
                        Debug.LogError($"[ViewDecoder] H264Decoder.Initialize failed for client {frame.ClientId}.");
                        ArrayPool<byte>.Shared.Return(payload);
                        return;
                    }
                }

                rgba = entry.Decoder.Decode(payload, 0, payloadLen);
                // Read actual dimensions from the decoder (set by MFT after parsing SPS)
                decodedW = entry.Decoder.Width;
                decodedH = entry.Decoder.Height;
            }
            finally
            {
                Monitor.Exit(entry.Lock);
            }

            // H264 payload no longer needed
            ArrayPool<byte>.Shared.Return(payload);

            if (rgba == null) return; // MFT still buffering or decode failed

            int validBytes = decodedW * decodedH * 4;
            EnqueueDecoded(frame.ClientId, frame.FrameId, decodedW, decodedH, rgba, validBytes);
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
