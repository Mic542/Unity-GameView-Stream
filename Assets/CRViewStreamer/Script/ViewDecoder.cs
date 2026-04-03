// ViewDecoder.cs — Receives H.264 / MJPEG frames from StreamServer and displays them.
//
// H.264 path uses UniTask.RunOnThreadPool for DXVA hardware decode:
//   - Native GVST_Feed/GVST_GetFrame run on thread pool (no raw Thread objects)
//   - CancellationToken cancellation — OnDisable cancels token and returns instantly
//   - No Thread.Join, no Monitor, no blocking the main thread — ever
//   - Decoded RGBA pixels are applied to textures back on the main thread via
//     UniTask.SwitchToMainThread

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

using UnityEngine;
using UnityEngine.UI;

namespace GameViewStream
{
    public enum CodecMode { MJPEG = 0, H264 = 1 }

    public sealed class ViewDecoder : MonoBehaviour
    {
        [SerializeField] private StreamServer server;
        public StreamServer Server { get => server; set => server = value; }

        [SerializeField] private RawImage display;
        public RawImage Display { get => display; set => display = value; }

        [SerializeField] private ushort boundClientId = 0;
        public ushort ClientId { get => boundClientId; set => boundClientId = value; }

        [SerializeField, Range(1, 1024)] private int maxDecodePerFrame = 64;
        [SerializeField, Range(1, 32)] private int decodeWorkerCount = 8;
        [SerializeField, Range(8, 256)] private int readyQueueCap = 128;

        [SerializeField] private long _totalFramesDecoded;
        [SerializeField] private long _totalFramesDropped;
        [SerializeField] private CodecMode _activeCodec = CodecMode.MJPEG;
        public CodecMode ActiveCodec => _activeCodec;

        [Header("─── Diagnostics ───")]
        [SerializeField] private bool debugDumpReceivedH264 = false;
        [SerializeField] private int debugDumpReceivedMaxFrames = 0;

        private System.IO.FileStream _debugRecvH264File;
        private int _debugRecvH264Count;
        private readonly object _diagLock = new object();

        // ── Client views ──────────────────────────────────────────────────────────
        private sealed class ClientView
        {
            public Texture2D Texture;
            public RawImage  Display;
            public uint      LastFrameId;
            public int       DecodeCount;
            public IntPtr    LastTexPtr;  // last native texture pointer (zero-copy path)
        }

        private readonly Dictionary<ushort, ClientView> _views = new();
        private string   _autoClaimIP      = null;
        private RawImage _autoClaimDisplay = null;

        // ── Decoded frame queue (thread→main) ─────────────────────────────────────
        private struct DecodedFrame
        {
            public ushort ClientId;
            public uint   FrameId;
            public int    Width, Height;
            public byte[] RawPixels;
            public int    ValidBytes;
        }

        private readonly ConcurrentQueue<DecodedFrame> _readyQueue = new();

        // ── H264 per-client state ─────────────────────────────────────────────────
        private sealed class H264ClientState
        {
            public ushort ClientId;
            public readonly H264Decoder Decoder = new();
            public readonly ConcurrentQueue<ClientFrameData> Pending = new();
            public uint NextExpectedId;
            public bool HasReceivedAny;
            public bool WaitingForKeyFrame = true;
            public bool HasSentInitialKeyFrameRequest;
            public readonly SortedDictionary<uint, ClientFrameData> Holding = new();
            public readonly object HoldingLock = new object();
            public long LastKeyFrameRequestMs;
        }

        private readonly ConcurrentDictionary<ushort, H264ClientState> _h264Decoders = new();
        private readonly object _dequeueLock = new();

        // ── Cancellation ──────────────────────────────────────────────────────────
        private CancellationTokenSource _cts;
        private Thread[] _mjpegWorkers;      // MJPEG only — lightweight, no native GPU calls
        private StreamServer _server;
        private bool _started;
        private IntPtr _renderEventCallback; // GVST_GetRenderCallback() — issued each frame

        // Shared across all ViewDecoder instances so that OnRenderEvent is dispatched
        // exactly once per Unity frame even when multiple ViewDecoder components exist.
        // OnRenderEvent already iterates ALL decoders in the native map internally.
        private static IntPtr  s_sharedRenderCallback = IntPtr.Zero;
        private static int     s_lastPluginEventFrame  = -1;
        // ── Stats ─────────────────────────────────────────────────────────────────
        private long _h264RoutedCount, _h264DecodedCount, _h264DroppedKeyWait, _h264FeedFailed;
        private float _lastH264LogTime;

        // ══════════════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════════════

        private void Start()
        {
            _started = true;
            _server = server != null ? server : GetComponent<StreamServer>();
            if (_server != null && display != null && boundClientId != 0)
                RegisterClient(boundClientId, display);
            BeginListening();
        }

        private void OnEnable() { if (_started) BeginListening(); }

        private void BeginListening()
        {
            if (_server == null) return;
            _server.OnClientConnected    += OnClientConnected;
            _server.OnClientDisconnected += OnClientDisconnected;

            Debug.Log($"[ViewDecoder] BeginListening: TurboJpeg={TurboJpeg.IsAvailable}, H264={H264Decoder.IsAvailable}");

            _cts = new CancellationTokenSource();

            // One-time grab of the render callback pointer (safe after DLL is loaded).
            // GL.IssuePluginEvent uses this to schedule GPU texture uploads on the render thread.
            if (_renderEventCallback == IntPtr.Zero)
            {
                _renderEventCallback = H264Decoder.GetRenderCallback();
                s_sharedRenderCallback = _renderEventCallback;  // same pointer for all instances
            }

            // MJPEG workers (lightweight — no native GPU calls, safe to join)
            var mjpegToken = _cts.Token;
            _mjpegWorkers = new Thread[decodeWorkerCount];
            for (int i = 0; i < decodeWorkerCount; i++)
            {
                var t = new Thread(MjpegWorkerLoop) { IsBackground = true, Name = $"MJPEG-{i}" };
                _mjpegWorkers[i] = t;
                t.Start(mjpegToken);
            }

#if UNITASK_PRESENT
            // H264 decode loop — UniTask on thread pool, cancelled via token
            H264DecodeLoopAsync(_cts.Token).Forget();
#else
            throw new Exception("[ViewDecoder] UniTask is required for H264 hardware decode but is not installed. Use Window → Package Manager → Add package from git URL to install com.cysharp.unitask.");
#endif
        }

        /// <summary>
        /// The UniTask H264 loop sees the cancelled token and exits on its own.
        /// </summary>
        private void OnDisable()
        {
            if (_server == null) return;
            _server.OnClientConnected    -= OnClientConnected;
            _server.OnClientDisconnected -= OnClientDisconnected;

            // 1. Signal native decoders to abort (lock-free, instant)
            foreach (var kv in _h264Decoders)
                kv.Value.Decoder?.Abort();

            // 2. Cancel — the UniTask loop and MJPEG workers check this token
            _cts?.Cancel();

            // 3. Wake any MJPEG worker sleeping in FrameReady.Wait so it sees the
            //    cancellation immediately instead of waiting up to 50 ms on its own.
            _server?.FrameReady.Set();

            // 4. Join MJPEG workers.  We allow 500 ms — enough for a single JPEG decode
            //    to complete.  Workers receive the CancellationToken directly so they
            //    do NOT touch _cts after this point.  _cts.Dispose() is deferred until
            //    after the join so the token stays valid if a worker is mid-Wait.
            if (_mjpegWorkers != null)
            {
                foreach (var t in _mjpegWorkers)
                {
                    if (t != null && !t.Join(500))
                        Debug.LogWarning($"[ViewDecoder] MJPEG worker '{t.Name}' did not exit within 500 ms — it will be orphaned.");
                }
                _mjpegWorkers = null;
            }

            // 5. Dispose CTS only after workers have stopped (or timed out).
            //    Disposing earlier causes ObjectDisposedException inside Wait(ct)
            //    in any still-running worker → unhandled thread exception + leak.
            _cts?.Dispose();
            _cts = null;

            // 6. Dispose decoders + drain queues.
            //    The UniTask loop is already cancelled (token) and native calls
            //    will bail out quickly (aborting flag).  No contention.
            foreach (var kv in _h264Decoders)
            {
                DrainClientState(kv.Value);
                kv.Value.Decoder?.Dispose();
            }
            _h264Decoders.Clear();

            // Reset LastTexPtr on all views — native textures were just freed.
            foreach (var view in _views.Values)
                view.LastTexPtr = IntPtr.Zero;

            // 7. Final drain — workers may have enqueued frames during the join window.
            CloseDiagDump();
            DrainReadyQueue();
        }

        private void OnDestroy()
        {
            DrainReadyQueue();
            foreach (var view in _views.Values)
                if (view.Texture != null) Destroy(view.Texture);
            _views.Clear();
        }

        // ══════════════════════════════════════════════════════════════════════════
        // H264 decode loop — runs on thread pool via UniTask
        // ══════════════════════════════════════════════════════════════════════════

#if UNITASK_PRESENT
        private async UniTaskVoid H264DecodeLoopAsync(CancellationToken ct)
        {
            // Move to thread pool immediately — all native DXVA calls happen here.
            await UniTask.SwitchToThreadPool();

            var clientIds = new List<ushort>();

            while (!ct.IsCancellationRequested)
            {
                bool didWork = false;

                clientIds.Clear();
                clientIds.AddRange(_h264Decoders.Keys);

                foreach (var clientId in clientIds)
                {
                    if (ct.IsCancellationRequested) return;
                    if (!_h264Decoders.TryGetValue(clientId, out var state)) continue;

                    // Process up to 64 pending frames per client per iteration
                    for (int i = 0; i < 64 && state.Pending.TryDequeue(out ClientFrameData f); i++)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            if (f.PixelData != null) ArrayPool<byte>.Shared.Return(f.PixelData);
                            return;
                        }

                        didWork = true;

                        try
                        {
                            if (!state.HasReceivedAny)
                            {
                                state.NextExpectedId = f.FrameId;
                                state.HasReceivedAny = true;
                            }

                            bool isUdp = _server != null && _server.Transport == TransportMode.UDP;

                            if (isUdp && f.FrameId > state.NextExpectedId && (f.FrameId - state.NextExpectedId) < 1000)
                            {
                                lock (state.HoldingLock)
                                {
                                    if (!state.Holding.ContainsKey(f.FrameId)) state.Holding[f.FrameId] = f;
                                    else if (f.PixelData != null) ArrayPool<byte>.Shared.Return(f.PixelData);
                                }
                            }
                            else if (isUdp && f.FrameId < state.NextExpectedId && (state.NextExpectedId - f.FrameId) < 1000)
                            {
                                if (f.PixelData != null) ArrayPool<byte>.Shared.Return(f.PixelData);
                            }
                            else
                            {
                                DecodeAndFlushH264(state, f, ct);
                            }

                            ClientFrameData holdOverflow = default;
                            lock (state.HoldingLock)
                            {
                                if (state.Holding.Count > 15)
                                {
                                    RequestKeyFrameThrottled(state);
                                    var enumerator = state.Holding.GetEnumerator();
                                    if (enumerator.MoveNext())
                                    {
                                        uint nextId = enumerator.Current.Key;
                                        state.NextExpectedId = nextId;
                                        holdOverflow = enumerator.Current.Value;
                                        state.Holding.Remove(nextId);
                                    }
                                }
                            }
                            if (holdOverflow.PixelData != null)
                                DecodeAndFlushH264(state, holdOverflow, ct);
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[ViewDecoder] H264 decode error (client {clientId}): {ex}");
                            state.WaitingForKeyFrame = true;
                            state.HasReceivedAny = false;
                            lock (state.HoldingLock) { state.Holding.Clear(); }
                        }
                    }
                }

                // Yield when idle instead of spinning
                if (!didWork)
                {
                    try { await UniTask.Delay(5, cancellationToken: ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
#endif
        private readonly Dictionary<ushort, DecodedFrame> _tickFrames = new();

        private void Update()
        {
            if (_server == null) return;

            // ── Zero-copy H264: dispatch NV12→RGBA compute shader on the render thread ───
            // OnRenderEvent iterates ALL decoders in the native map, so we only need one
            // dispatch per Unity frame.  A static frame counter prevents N×M redundant
            // dispatches when multiple ViewDecoder components are active simultaneously.
            // Key off whether any H264 decoders are active, NOT _activeCodec — the codec
            // field reflects the last packet type and can be MJPEG even when H264 clients
            // are connected (e.g. mixed-codec session or temporarily idle H264 stream).
            if (s_sharedRenderCallback != IntPtr.Zero && _h264Decoders.Count > 0)
            {
                int frame = Time.frameCount;
                if (s_lastPluginEventFrame != frame)
                {
                    s_lastPluginEventFrame = frame;
                    GL.IssuePluginEvent(s_sharedRenderCallback, 0);
                }
            }

            // ── Sync external textures (pointer changes on first frame + resolution change) ─
            SyncH264ExternalTextures();

            // Periodic diagnostic log
            if (Time.unscaledTime - _lastH264LogTime >= 5f && _h264Decoders.Count > 0)
            {
                _lastH264LogTime = Time.unscaledTime;
                int pendingTotal = 0;
                string nativeStats = "";
                foreach (var kv in _h264Decoders)
                {
                    pendingTotal += kv.Value.Pending.Count;
                    if (string.IsNullOrEmpty(nativeStats))
                        nativeStats = kv.Value.Decoder?.GetStats() ?? "n/a";
                }
                Debug.Log($"[ViewDecoder] H264 stats: routed={_h264RoutedCount} decoded={_h264DecodedCount} keyWait={_h264DroppedKeyWait} feedFail={_h264FeedFailed} pending={pendingTotal} ready={_readyQueue.Count} views={_views.Count} applied={_totalFramesDecoded} dropped={_totalFramesDropped}");
                Debug.Log($"[ViewDecoder] Native MFT: {nativeStats}");
            }

            // Drain ready queue → apply to textures
            int dequeued = 0;
            while (dequeued < maxDecodePerFrame && _readyQueue.TryDequeue(out DecodedFrame df))
            {
                dequeued++;
                if (_tickFrames.TryGetValue(df.ClientId, out DecodedFrame old))
                {
                    if (df.FrameId >= old.FrameId || (old.FrameId - df.FrameId) > 1000)
                    {
                        if (old.RawPixels != null) { ArrayPool<byte>.Shared.Return(old.RawPixels); _totalFramesDropped++; }
                        _tickFrames[df.ClientId] = df;
                    }
                    else
                    {
                        if (df.RawPixels != null) { ArrayPool<byte>.Shared.Return(df.RawPixels); _totalFramesDropped++; }
                    }
                }
                else
                {
                    _tickFrames[df.ClientId] = df;
                }
            }

            foreach (var kv in _tickFrames)
                ApplyDecoded(kv.Value);
            _tickFrames.Clear();
        }

        /// <summary>
        /// Called each frame to detect when the native texture pointer changes
        /// (first frame produced, or resolution change) and update Unity's
        /// external Texture2D accordingly.  No CPU pixel copy — zero-overhead.
        /// </summary>
        private void SyncH264ExternalTextures()
        {
            if (_h264Decoders.Count == 0) return;

            foreach (var kv in _views)
            {
                ushort     cid  = kv.Key;
                ClientView view = kv.Value;
                if (!_h264Decoders.TryGetValue(cid, out var state)) continue;

                IntPtr ptr = state.Decoder.GetTexturePtr();
                if (ptr == IntPtr.Zero || ptr == view.LastTexPtr) continue;

                int w = state.Decoder.Width;
                int h = state.Decoder.Height;
                if (w <= 0 || h <= 0) continue;

                if (view.LastTexPtr == IntPtr.Zero)
                {
                    // First frame — create Texture2D wrapper around the native D3D11 texture.
                    if (view.Texture != null) Destroy(view.Texture);
                    view.Texture = Texture2D.CreateExternalTexture(
                        w, h, TextureFormat.RGBA32, false, false, ptr);
                    if (view.Display != null) view.Display.texture = view.Texture;
                    Debug.Log($"[ViewDecoder] H264 zero-copy texture created: {w}x{h} client={cid}");
                }
                else
                {
                    // Resolution change — update the existing external texture.
                    view.Texture.UpdateExternalTexture(ptr);
                    Debug.Log($"[ViewDecoder] H264 zero-copy texture updated: {w}x{h} client={cid}");
                }

                view.LastTexPtr = ptr;
                view.DecodeCount++;
                _totalFramesDecoded++;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // MJPEG worker loop (background thread — no native GPU calls, safe to join)
        // ══════════════════════════════════════════════════════════════════════════

        private void MjpegWorkerLoop(object tokenObj)
        {
            var ct = (CancellationToken)tokenObj;
            while (!ct.IsCancellationRequested)
            {
                while (TryDequeueAndRoute(out var frame))
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        if (frame != null && frame.PacketType == NetworkProtocol.PacketType.VideoFrame)
                        {
                            _activeCodec = CodecMode.MJPEG;
                            DecodeWorkerJpeg(frame);
                        }
                        else if (frame?.PixelData != null)
                        {
                            ArrayPool<byte>.Shared.Return(frame.PixelData);
                        }
                    }
                    catch { }
                }

                _server?.FrameReady.Reset();
                if (_server != null && _server.FrameQueue.IsEmpty)
                {
                    try { _server.FrameReady.Wait(50, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Routing — dequeue from server, route H264 vs MJPEG
        // ══════════════════════════════════════════════════════════════════════════

        private bool TryDequeueAndRoute(out ClientFrameData frame)
        {
            frame = null;
            lock (_dequeueLock)
            {
                if (_server == null || !_server.FrameQueue.TryDequeue(out var f))
                    return false;

                if (f.PacketType == NetworkProtocol.PacketType.H264Frame)
                {
                    _activeCodec = CodecMode.H264;
                    if (f.PixelData == null || f.PayloadLength == 0)
                    {
                        if (f.PixelData != null) ArrayPool<byte>.Shared.Return(f.PixelData);
                        return true;
                    }

                    var state = _h264Decoders.GetOrAdd(f.ClientId, id => new H264ClientState { ClientId = id });

                    // Cap pending queue — drop old frames on burst arrival
                    while (state.Pending.Count > 10)
                    {
                        if (state.Pending.TryDequeue(out var stale))
                            if (stale.PixelData != null) ArrayPool<byte>.Shared.Return(stale.PixelData);
                    }

                    state.Pending.Enqueue(f);
                    Interlocked.Increment(ref _h264RoutedCount);
                    return true;
                }
                else
                {
                    frame = f;
                    return true;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // H264 decode helpers (called from thread pool via UniTask)
        // ══════════════════════════════════════════════════════════════════════════

        private void DecodeAndFlushH264(H264ClientState state, ClientFrameData f, CancellationToken ct)
        {
            DecodeSingleH264(state, f);
            state.NextExpectedId = f.FrameId + 1;
            while (true)
            {
                ClientFrameData nextFrame;
                lock (state.HoldingLock)
                {
                    if (!state.Holding.TryGetValue(state.NextExpectedId, out nextFrame))
                        break;
                    state.Holding.Remove(state.NextExpectedId);
                }
                if (ct.IsCancellationRequested)
                {
                    if (nextFrame.PixelData != null) ArrayPool<byte>.Shared.Return(nextFrame.PixelData);
                    break;
                }
                DecodeSingleH264(state, nextFrame);
                state.NextExpectedId++;
            }
        }

        private void DecodeSingleH264(H264ClientState state, ClientFrameData frame)
        {
            byte[] payload    = frame.PixelData;
            int    payloadLen = frame.PayloadLength;
            H264Decoder decoder = state.Decoder;

            // Diagnostic dump
            if (debugDumpReceivedH264 && payload != null && payloadLen > 0)
                WriteDiagDump(payload, payloadLen);

            try
            {
                if (!decoder.IsReady) if (!decoder.Initialize(16, 16)) return;

                if (state.WaitingForKeyFrame && !state.HasSentInitialKeyFrameRequest)
                {
                    state.HasSentInitialKeyFrameRequest = true;
                    RequestKeyFrameThrottled(state);
                }

                bool isKey = NetworkProtocol.IsKeyFrame(payload, payloadLen);
                if (isKey)
                {
                    state.WaitingForKeyFrame = false;
                    state.NextExpectedId = frame.FrameId;
                    lock (state.HoldingLock) { state.Holding.Clear(); }
                }

                if (state.WaitingForKeyFrame)
                {
                    Interlocked.Increment(ref _h264DroppedKeyWait);
                    return;
                }

                if (!decoder.Feed(payload, 0, payloadLen))
                {
                    Interlocked.Increment(ref _h264FeedFailed);
                    state.WaitingForKeyFrame = true;
                    state.HasReceivedAny = false;
                    lock (state.HoldingLock) { state.Holding.Clear(); }
                    RequestKeyFrameThrottled(state);
                    return;
                }

                while (true)
                {
                    byte[] rgba = decoder.GetNextReadyFrame();
                    if (rgba == null) break;
                    Interlocked.Increment(ref _h264DecodedCount);
                    EnqueueDecoded(frame.ClientId, frame.FrameId,
                                   decoder.Width, decoder.Height, rgba,
                                   decoder.Width * decoder.Height * 4);
                }
            }
            finally
            {
                if (payload != null) ArrayPool<byte>.Shared.Return(payload);
            }
        }

        private void RequestKeyFrameThrottled(H264ClientState state)
        {
            long nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
            if (nowMs - state.LastKeyFrameRequestMs < 500) return;
            state.LastKeyFrameRequestMs = nowMs;
            byte[] req = new byte[15];
            req[0] = 0x47; req[1] = 0x56; req[2] = 0x53; req[3] = 0x54; req[4] = 0x07;
            _server?.SendPacketToClient(state.ClientId, req);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // MJPEG decode (called from MJPEG worker thread)
        // ══════════════════════════════════════════════════════════════════════════

        private void DecodeWorkerJpeg(ClientFrameData frame)
        {
            byte[] data = frame.PixelData;
            if (TurboJpeg.GetImageInfo(data, out int w, out int h))
            {
                byte[] pix = ArrayPool<byte>.Shared.Rent(w * h * 4);
                if (TurboJpeg.Decode(data, w, h, pix))
                    EnqueueDecoded(frame.ClientId, frame.FrameId, w, h, pix, w * h * 4);
                else
                    ArrayPool<byte>.Shared.Return(pix);
            }
            ArrayPool<byte>.Shared.Return(data);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Ready queue + texture apply
        // ══════════════════════════════════════════════════════════════════════════

        private void EnqueueDecoded(ushort clientId, uint frameId, int w, int h, byte[] pixels, int validBytes)
        {
            while (_readyQueue.Count >= readyQueueCap)
            {
                if (_readyQueue.TryDequeue(out DecodedFrame old) && old.RawPixels != null)
                {
                    ArrayPool<byte>.Shared.Return(old.RawPixels);
                    _totalFramesDropped++;
                }
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

        private void ApplyDecoded(DecodedFrame df)
        {
            if (!_views.TryGetValue(df.ClientId, out ClientView view))
            {
                ArrayPool<byte>.Shared.Return(df.RawPixels);
                return;
            }

            if (df.FrameId < view.LastFrameId && (view.LastFrameId - df.FrameId) < 1000)
            {
                ArrayPool<byte>.Shared.Return(df.RawPixels);
                return;
            }

            // Texture can be null transiently: after Destroy() on disconnect and before
            // CreateExternalTexture() fires on the next SyncH264ExternalTextures pass.
            if (view.Texture == null)
            {
                ArrayPool<byte>.Shared.Return(df.RawPixels);
                return;
            }

            // Skip CPU pixel upload for clients using zero-copy GPU texture.
            // External textures don't have CPU-side memory — LoadRawTextureData is UB.
            if (view.LastTexPtr != IntPtr.Zero)
            {
                if (df.RawPixels != null) ArrayPool<byte>.Shared.Return(df.RawPixels);
                return;
            }

            if (view.Texture.width != df.Width || view.Texture.height != df.Height)
                view.Texture.Reinitialize(df.Width, df.Height, TextureFormat.RGBA32, false);

            GCHandle pin = GCHandle.Alloc(df.RawPixels, GCHandleType.Pinned);
            try { view.Texture.LoadRawTextureData(pin.AddrOfPinnedObject(), df.ValidBytes); }
            finally { pin.Free(); ArrayPool<byte>.Shared.Return(df.RawPixels); }

            view.Texture.Apply(false);
            if (view.Display != null) view.Display.texture = view.Texture;
            view.LastFrameId = df.FrameId;
            view.DecodeCount++;
            _totalFramesDecoded++;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Client management
        // ══════════════════════════════════════════════════════════════════════════

        private void OnClientConnected(ushort clientId, IPAddress address)
        {
            if (display != null && (boundClientId == 0 || boundClientId == clientId))
            {
                RegisterClient(clientId, display);
                boundClientId = clientId;
            }
            else if (_autoClaimIP != null && address.ToString() == _autoClaimIP)
            {
                RegisterClient(clientId, _autoClaimDisplay);
                boundClientId = clientId;
            }
        }

        private void OnClientDisconnected(ushort clientId)
        {
            if (_views.TryGetValue(clientId, out ClientView view))
            {
                if (display == null) _autoClaimDisplay = view.Display;
                _views.Remove(clientId);
            }
            if (_h264Decoders.TryRemove(clientId, out var state))
            {
                // Abort first — tells the native decoder to exit GVST_Feed quickly
                // so the UniTask decode loop can't be mid-Feed when Dispose runs.
                state.Decoder?.Abort();
                DrainClientState(state);
                state.Decoder?.Dispose();
            }
            if (clientId == boundClientId) boundClientId = 0;
        }

        public void RegisterClient(ushort clientId, RawImage rawImage)
        {
            if (!_views.TryGetValue(clientId, out ClientView view))
            {
                view = new ClientView
                {
                    Texture = new Texture2D(2, 2, TextureFormat.RGBA32, false),
                    LastFrameId = 0
                };
                _views[clientId] = view;
            }
            view.Display = rawImage;
            if (rawImage != null) rawImage.texture = view.Texture;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════════════════

        private static void DrainClientState(H264ClientState state)
        {
            while (state.Pending.TryDequeue(out var f))
                if (f.PixelData != null) ArrayPool<byte>.Shared.Return(f.PixelData);
            lock (state.HoldingLock)
            {
                foreach (var f in state.Holding.Values)
                    if (f.PixelData != null) ArrayPool<byte>.Shared.Return(f.PixelData);
                state.Holding.Clear();
            }
        }

        private void DrainReadyQueue()
        {
            while (_readyQueue.TryDequeue(out DecodedFrame df))
                if (df.RawPixels != null) ArrayPool<byte>.Shared.Return(df.RawPixels);
        }

        private void WriteDiagDump(byte[] payload, int payloadLen)
        {
            lock (_diagLock)
            {
                if (_debugRecvH264File == null && (debugDumpReceivedMaxFrames <= 0 || _debugRecvH264Count < debugDumpReceivedMaxFrames))
                {
                    try
                    {
                        string path = System.IO.Path.Combine(Application.persistentDataPath, "debug_received_h264.bin");
                        _debugRecvH264File = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
                        Debug.Log($"[ViewDecoder] H.264 receive dump: {path}");
                    }
                    catch (Exception e) { Debug.LogWarning($"[ViewDecoder] Cannot open dump: {e.Message}"); }
                }
                if (_debugRecvH264File != null)
                {
                    if (debugDumpReceivedMaxFrames <= 0 || _debugRecvH264Count < debugDumpReceivedMaxFrames)
                    {
                        try { _debugRecvH264File.Write(payload, 0, payloadLen); } catch { }
                        _debugRecvH264Count++;
                    }
                    else if (_debugRecvH264Count == debugDumpReceivedMaxFrames)
                    {
                        _debugRecvH264Count++;
                        CloseDiagDumpLocked();
                        Debug.Log($"[ViewDecoder] H.264 dump complete ({debugDumpReceivedMaxFrames} frames).");
                    }
                }
            }
        }

        private void CloseDiagDump()
        {
            lock (_diagLock) { CloseDiagDumpLocked(); }
        }

        private void CloseDiagDumpLocked()
        {
            if (_debugRecvH264File != null)
            {
                try { _debugRecvH264File.Flush(); _debugRecvH264File.Close(); } catch { }
                _debugRecvH264File = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Public API
        // ══════════════════════════════════════════════════════════════════════════

        public void DetachDisplay(ushort clientId) { if (_views.TryGetValue(clientId, out var v)) if (v.Display != null) v.Display.texture = null; _views.Remove(clientId); }
        public void UnregisterClient(ushort clientId) { if (_views.TryGetValue(clientId, out var v)) { if (v.Texture != null) Destroy(v.Texture); _views.Remove(clientId); } if (_h264Decoders.TryRemove(clientId, out var s)) { s.Decoder?.Abort(); DrainClientState(s); s.Decoder?.Dispose(); } }
        public void SetDisplay(ushort clientId, RawImage rawImage) { if (_views.TryGetValue(clientId, out var v)) { v.Display = rawImage; if (rawImage != null) rawImage.texture = v.Texture; } }
        public Texture2D GetClientTexture(ushort clientId) => _views.TryGetValue(clientId, out var v) ? v.Texture : null;
        public void SetAutoClaimForIP(string ip, RawImage img) { _autoClaimIP = ip; _autoClaimDisplay = img; boundClientId = 0; StreamServer s = _server ?? server; if (s != null && IPAddress.TryParse(ip, out var addr)) if (s.TryGetClientIdByAddress(addr, out var id)) { RegisterClient(id, img); boundClientId = id; } }
        public IEnumerable<(ushort id, int frames)> GetStats() { foreach (var kv in _views) yield return (kv.Key, kv.Value.DecodeCount); }
    }
}