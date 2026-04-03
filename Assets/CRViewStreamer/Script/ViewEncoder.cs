using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameViewStream
{
    /// <summary>
    /// Client-side component (Android VR device).
    /// Captures the attached camera each frame, compresses to JPEG via TurboJpeg
    /// (ARM NEON SIMD on Android, SSE on Windows) or H.264 via MediaCodec,
    /// and streams over TCP or UDP to the server.
    ///
    /// The transport mode is chosen by the server and communicated during
    /// auto-discovery. The client connects with the matching transport type.
    ///
    /// Encoding pipeline:
    ///   Main thread   : AsyncGPUReadback → raw RGBA32 bytes → _rawQueue
    ///   Encode thread : _rawQueue → TurboJpeg.Encode / H264Encoder.Encode → _sendQueue
    ///   Send thread   : _sendQueue → TCP stream or UDP datagrams
    ///   Receive thread: TCP stream or UDP socket → heartbeat ping echo
    ///
    /// Setup:
    ///   1. Attach this component to the Camera you want to stream.
    ///   2. Enable <see cref="autoDiscover"/> (default on) — the server IP and transport
    ///      mode are found via UDP broadcast automatically.
    ///   3. Build and deploy to the Android VR device.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class ViewEncoder : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Tooltip("When enabled, the encoder sends a UDP broadcast on start to locate the server "
               + "automatically. ServerAddress is ignored while auto-discover finds a server. "
               + "Falls back to ServerAddress only if no server is found within the timeout.")]
        [SerializeField] private bool autoDiscover     = true;
        [Tooltip("UDP port to broadcast discovery probes on. Must match StreamServer.discoveryPort.")]
        [SerializeField] private int  discoveryPort    = NetworkProtocol.DiscoveryPort;
        [Tooltip("How long (ms) to wait for a discovery reply before falling back to ServerAddress.")]
        [SerializeField] private int  discoveryTimeout = 5000;
        [Tooltip("Fallback IP address used when Auto Discover is off or times out.")]
        [SerializeField] private string serverAddress = "192.168.1.100";
        [SerializeField] private int    serverPort    = NetworkProtocol.DefaultPort;
        [Tooltip("Transport mode to use when Auto Discover is off.\n"
               + "Must match the server's Transport Mode setting.\n"
               + "When Auto Discover is on, this is ignored \u2014 the server's reply decides.")]
        [SerializeField] private TransportMode manualTransportMode = TransportMode.TCP;

        [Tooltip("Width of the capture texture. Lower = less bandwidth (e.g. 480x270 for 10+ clients).")]
        [SerializeField] private int captureWidth  = 480;
        [Tooltip("Height of the capture texture.")]
        [SerializeField] private int captureHeight = 272;
        [Tooltip("Maximum frames per second to encode and send.")]
        [SerializeField, Range(5, 60)] private int targetFPS = 15;

        [Tooltip("JPEG quality 1-100. 75 is a good streaming default (good quality, lower bandwidth).")]
        [SerializeField, Range(1, 100)] private int jpegQuality = 75;
        [Tooltip("Chroma subsampling. TJSAMP_420=2 (default). Use 0 for 4:4:4 (highest quality).")]
        [SerializeField] private int jpegSubsampling = TurboJpeg.TJSAMP_420;
        [Tooltip("Max raw frames held in queue for the encode thread. Older frames are dropped — latency beats delivery.")]
        [SerializeField, Range(1, 4)] private int rawQueueCapacity  = 2;
        [Tooltip("Max compressed packets queued for the send thread.\n"
               + "For H.264, every frame is critical (P-frames reference previous frames).\n"
               + "A queue that is too small DROPS frames, corrupting ALL subsequent H.264\n"
               + "output until the next IDR keyframe. 512 = ~15 s buffer at 30 fps.")]
        [SerializeField, Range(1, 1024)] private int sendQueueCapacity = 512;

        [Tooltip("Choose the encoding codec.\n"
               + "MJPEG: CPU encode via TurboJpeg. Works on all platforms including Windows Editor.\n"
               + "H264: GPU encode via Android MediaCodec. Lower bandwidth, Android device only.")]
        [SerializeField] private CodecMode codecMode = CodecMode.MJPEG;
        [Tooltip("H.264 target bitrate in Mbps (CBR). This is the actual network bandwidth.\n"
               + "0.1 Mbps = ~100 kbps — aggressive, low-res/low-fps.\n"
               + "0.3 Mbps = ~300 kbps — good balance for 320×240–540p.\n"
               + "1.0 Mbps = ~1000 kbps — high quality 960×540.\n"
               + "Set lower if bandwidth is limited.")]
        [SerializeField, Range(0.05f, 20f)] private float h264BitrateMbps = 0.8f;

        [Tooltip("Seconds between IDR keyframes (I-frame interval).\n"
               + "0   = every frame is an I-frame (recommended — no motion smearing).\n"
               + "0.1 = IDR every 100 ms (~1-2 P-frames at 15 fps).\n"
               + "0.5 = IDR every 500 ms (~7 P-frames at 15 fps).\n"
               + "1   = IDR every 1 s (~15 P-frames at 15 fps).\n\n"
               + "Values > 0 allow P-frames for better compression but may cause\n"
               + "motion-smearing on some Android MediaCodec implementations.\n"
               + "All-I mode (0) is still ~30-50% smaller than MJPEG.")]
        [SerializeField, Range(0f, 5f)] private float h264IFrameInterval = 0f;

        [Tooltip("When enabled and the server selects UDP transport, every outgoing datagram gets a\n"
               + "sequence number and the server sends back an ACK. Un-ACKed packets are re-sent\n"
               + "after Retransmit Ms, up to Max Retries times. Prevents packet loss on busy WiFi.")]
        [SerializeField] private bool sendReliable    = true;
        [Tooltip("Milliseconds to wait before retransmitting an un-ACKed reliable datagram.\n"
               + "LAN round-trip is typically <2 ms — 200 ms gives headroom without retransmit storms.")]
        [SerializeField, Range(10, 2000)] private int retransmitMs = 200;
        [Tooltip("Maximum retransmission attempts per datagram before giving up.\n"
               + "After this many retries the datagram is dropped (heartbeat/reconnect handles total loss).")]
        [SerializeField, Range(1, 30)] private int maxRetries = 5;

        // ── Diagnostics ──────────────────────────────────────────────────────────

        [Header("─── Diagnostics ───")]

        [Tooltip("Save the raw H.264 Annex-B bitstream to a file on the device.\n"
               + "Pull with: adb pull /sdcard/Android/data/<pkg>/files/debug_h264.bin\n"
               + "Play with: ffplay -f h264 debug_h264.bin\n"
               + "If the file looks clean in VLC/ffplay, the encoder is correct and\n"
               + "the problem is downstream (transport or decoder).")]
        [SerializeField] private bool debugDumpH264 = false;

        [Tooltip("Maximum number of H.264 frames to dump (0 = unlimited).\n"
               + "300 frames ≈ 10 seconds at 30 fps. The file is closed when this limit is reached.")]
        [SerializeField] private int debugDumpMaxFrames = 0;

        // ── Events ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised on the **main thread** once the encoder discovers (or connects to) a
        /// <see cref="StreamServer"/> and begins streaming.
        /// Arguments: server IP string (e.g. "192.168.1.42"), server <see cref="IPEndPoint"/>.
        /// </summary>
        public event Action<string, IPEndPoint> OnServerDiscovered;

        // ── Private state ────────────────────────────────────────────────────────

        private Camera        _camera;
        private RenderTexture _captureRT;
        private byte[] _gpuStagingBuffer;

        // H.264 encoder (Android only — null on Editor / Windows)
        private H264Encoder _h264Encoder;

        // Networking
        private TcpClient     _tcp;
        private NetworkStream _tcpStream;
        private UdpClient     _udpSender;       // used in UDP mode (null in TCP mode)
        private IPEndPoint    _serverEndPoint;
        private volatile bool _isConnected;
        private volatile TransportMode _transportMode = TransportMode.TCP;
        // Serialises concurrent writes from SendLoop and the heartbeat pong in ReceiveServerMessages.
        private readonly object _tcpWriteLock = new object();
        private CancellationTokenSource _cts;

        // Reliable UDP state (sequence + ACK + retransmit)
        private int  _reliableSeqNum;
        private bool _cachedSendReliable;
        private int  _cachedRetransmitMs;
        private int  _cachedMaxRetries;

        private readonly ConcurrentDictionary<uint, ReliablePending> _pendingAcks
            = new ConcurrentDictionary<uint, ReliablePending>();

        private sealed class ReliablePending
        {
            public byte[] Packet;   // full datagram (seqNum prefix + GVST packet)
            public long   SentMs;   // timestamp of last send / re-send
            public int    Retries;  // how many re-sends so far
        }

        // Three dedicated background threads (receive thread is spawned per-connection inside DiscoverLoop)
        private Thread _discoverThread;
        private Thread _encodeThread;
        private Thread _sendThread;

        // Raw frames waiting to be compressed: (frameId, rgba32 bytes)
        private readonly ConcurrentQueue<(uint id, byte[] raw)> _rawQueue
            = new ConcurrentQueue<(uint, byte[])>();

        // Compressed packets ready to send
        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();

        // Signal wake events — replace Thread.Sleep(1) whose 15.6 ms Windows timer resolution
        // is the primary source of encode/send jitter in busy projects.
        private readonly ManualResetEventSlim _rawReady  = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _sendReady = new ManualResetEventSlim(false);

        // Frame capture state (main thread only)
        private uint  _frameId;
        private float _captureInterval;
        private float _lastCaptureTime;
        private bool  _gpuReadbackPending;
        private float _gpuReadbackRequestTime; // watchdog: when readback was issued, to detect stuck readbacks
        private int   _connectionGeneration;   // incremented on each TCP connect to detect stale recv threads

        // Cached at Start() so encode thread never accesses Unity properties
        private int  _cachedWidth;
        private int  _cachedHeight;
        private int  _cachedQuality;
        private int  _cachedSubsampling;
        private bool _cachedUseH264;      // true  = H.264 path is active
        private bool _h264Intended;       // true  = user checked Use H264 (never fall back to JPEG)
        private int  _cachedH264Bitrate;

        // Bandwidth measurement — logs encoder output rate and wire rate every 5 s
        private long _bwEncoderBytes;      // total H.264 bytes produced by encoder since last log
        private long _bwWireBytes;         // total bytes written to TCP/UDP since last log
        private long _bwLastLogMs;

        // ── Diagnostic state ──────────────────────────────────────────────────
        private FileStream _debugH264File;
        private int        _debugH264FrameCount;

        // ── Reliable UDP helpers ──────────────────────────────────────────────

        /// <summary>
        /// Returns the next reliable sequence number, skipping the one value
        /// (0x47565354) whose big-endian encoding matches the GVST magic bytes
        /// to keep the server’s reliable-prefix detector unambiguous.
        /// </summary>
        private uint NextReliableSeq()
        {
            uint seq = unchecked((uint)Interlocked.Increment(ref _reliableSeqNum));
            if (seq == 0x47565354u)
                seq = unchecked((uint)Interlocked.Increment(ref _reliableSeqNum));
            return seq;
        }

        /// <summary>
        /// Re-sends every pending reliable datagram whose last-send timestamp
        /// exceeds <see cref="_cachedRetransmitMs"/>, up to <see cref="_cachedMaxRetries"/>.
        /// Called from <see cref="SendLoop"/> on its idle path.
        /// </summary>
        private void RetransmitPending()
        {
            // Cap: if too many pending ACKs accumulate (e.g. server unreachable),
            // clear them all — those frames are stale for real-time streaming anyway.
            if (_pendingAcks.Count > 100)
            {
                _pendingAcks.Clear();
                return;
            }

            long now = NowMs;
            foreach (var kv in _pendingAcks)
            {
                var p = kv.Value;
                if (now - p.SentMs < _cachedRetransmitMs) continue;

                if (p.Retries >= _cachedMaxRetries)
                {
                    _pendingAcks.TryRemove(kv.Key, out _);
                    continue;
                }

                try
                {
                    _udpSender.Send(p.Packet, p.Packet.Length);
                    p.SentMs  = now;
                    p.Retries++;
                }
                catch
                {
                    _isConnected = false;
                    break;
                }
            }
        }

        // ── Unity lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _camera          = GetComponent<Camera>();
            _captureInterval = 1f / targetFPS;

            // Off-screen render target — ARGB32 on GPU, read back as RGBA32 on CPU
            // 24-bit depth is required for correct 3-D rendering (depth testing / occlusion).
            _captureRT = new RenderTexture(captureWidth, captureHeight, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                filterMode   = FilterMode.Bilinear,
            };
            _captureRT.Create();
        }

        private void Start()
        {
            // Cache on main thread — encode thread must NOT access Unity properties
            _cachedWidth       = captureWidth;
            _cachedHeight      = captureHeight;
            _cachedQuality     = jpegQuality;
            _cachedSubsampling = jpegSubsampling;
            _cachedH264Bitrate  = (int)(h264BitrateMbps * 1_000_000);
            _cachedSendReliable = sendReliable;
            _cachedRetransmitMs = retransmitMs;
            _cachedMaxRetries   = maxRetries;

            // Client decides codec — apply the Inspector setting now regardless of discover mode.
            ApplyCodecMode(codecMode);

            _cts            = new CancellationTokenSource();
            _discoverThread = new Thread(DiscoverLoop) { IsBackground = true, Name = "ViewEncoder-Discover" };
            _encodeThread   = new Thread(EncodeLoop)   { IsBackground = true, Name = "ViewEncoder-Encode"   };
            _sendThread     = new Thread(SendLoop)     { IsBackground = true, Name = "ViewEncoder-Send"     };
            _discoverThread.Start();
            _encodeThread.Start();
            _sendThread.Start();
        }

        private void Update()
        {
            // Watchdog: if GPU readback has been pending for > 3 s, force-reset it.
            // This handles rare Unity edge cases where AsyncGPUReadback never calls back
            // (e.g. RenderTexture destroyed, device lost, app focus lost on some platforms).
            if (_gpuReadbackPending && (Time.unscaledTime - _gpuReadbackRequestTime) > 3f)
            {
                // Callback never fired (RT destroyed, device lost, focus lost, etc.).
                // Just clear the flag so the next Update can issue a fresh request.
                // Do NOT call WaitAllRequests() here — that blocks the render thread
                // and causes the GPU readback queue to back up, which is the leak.
                Debug.LogWarning("[ViewEncoder] GPU readback stuck for 3 s — clearing flag.");
                _gpuReadbackPending = false;
            }

            if (!_isConnected || _gpuReadbackPending) return;
            if (Time.unscaledTime - _lastCaptureTime < _captureInterval) return;

            _lastCaptureTime        = Time.unscaledTime;
            _gpuReadbackPending     = true;
            _gpuReadbackRequestTime = Time.unscaledTime;

            // Render camera to offscreen RT (doesn't affect the scene's main camera)
            RenderTexture prev    = _camera.targetTexture;
            _camera.targetTexture = _captureRT;
            _camera.Render();
            _camera.targetTexture = prev;

            // Non-blocking GPU→CPU transfer; callback fires on next main-thread tick
            AsyncGPUReadback.Request(_captureRT, 0, TextureFormat.RGBA32, OnGPUReadbackComplete);
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            if (_isConnected)
            {
                // Best-effort graceful disconnect.
                try
                {
                    byte[] disc = NetworkProtocol.BuildDisconnectPacket(0);
                    if (_transportMode == TransportMode.UDP && _udpSender != null)
                        _udpSender.Send(disc, disc.Length);
                    else if (_tcpStream != null)
                        lock (_tcpWriteLock) _tcpStream.Write(disc, 0, disc.Length);
                }
                catch { /* best-effort */ }
            }
            _isConnected = false;
            _pendingAcks.Clear();
            _tcp?.Close();
            _udpSender?.Close();
            _h264Encoder?.Release();
            _h264Encoder?.Dispose();
            _h264Encoder = null;
            if (_debugH264File != null)
            {
                try { _debugH264File.Flush(); _debugH264File.Close(); }
                catch { /* best-effort */ }
                _debugH264File = null;
            }
            // Drain any in-flight AsyncGPUReadback requests BEFORE releasing the RT.
            // If the RT is destroyed with requests still referencing it, those requests
            // can never complete and stay in Unity's internal queue permanently —
            // accumulating across play sessions and starving the GPU of video decode
            // resources (causes MF_E_HW_MFT_FAILED_START_STREAMING / 0xC00D4A3E).
            if (_gpuReadbackPending)
            {
                AsyncGPUReadback.WaitAllRequests();
                _gpuReadbackPending = false;
            }
            if (_captureRT != null) { _captureRT.Release(); Destroy(_captureRT); _captureRT = null; }
            // Drain _rawQueue — return rented ArrayPool buffers.
            while (_rawQueue.TryDequeue(out var item))
                System.Buffers.ArrayPool<byte>.Shared.Return(item.raw);
        }

        // ── GPU readback callback (main thread) ──────────────────────────────────

        /// <summary>
        /// Copies NativeArray pixels to a managed byte[] and enqueues for the encode thread.
        /// No compression happens here — main thread is never blocked.
        /// </summary>
        private void OnGPUReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _gpuReadbackPending = false;
            if (request.hasError || !_isConnected) return;

            // .ToArray() = single memcpy, stays on main thread, NativeArray is valid here
            if (_gpuStagingBuffer == null || _gpuStagingBuffer.Length != request.layerCount * request.width * request.height * 4)
                _gpuStagingBuffer = new byte[request.width * request.height * 4];
            
            var data = request.GetData<byte>();
            data.CopyTo(_gpuStagingBuffer);
            byte[] raw = System.Buffers.ArrayPool<byte>.Shared.Rent(_gpuStagingBuffer.Length);
            System.Buffer.BlockCopy(_gpuStagingBuffer, 0, raw, 0, _gpuStagingBuffer.Length);

            if (_rawQueue.Count >= rawQueueCapacity)
            {
                if (_rawQueue.TryDequeue(out var dropped))
                    System.Buffers.ArrayPool<byte>.Shared.Return(dropped.raw);
            }

            _rawQueue.Enqueue((_frameId++, raw));
            _rawReady.Set();   // wake encode thread immediately
        }

        // ── Encode thread ────────────────────────────────────────────────────────

        /// <summary>
        /// Drains <see cref="_rawQueue"/> and compresses each raw RGBA32 frame to JPEG
        /// via <see cref="TurboJpeg.Encode"/> (native SIMD — SSE on Windows, ARM NEON on Android).
        ///
        /// Payload: raw JPEG bytes (self-describing — decoder uses ImageConversion.LoadImage).
        /// </summary>
        private void EncodeLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!_rawQueue.TryDequeue(out var item))
                {
                    _rawReady.Reset();
                    // Double-check after reset to avoid missed-signal race
                    if (!_rawQueue.TryDequeue(out item))
                    {
                        _rawReady.Wait(50);  // 50 ms cap so we notice cancellation promptly
                        continue;
                    }
                }

                try
                {
                    if (_cachedUseH264)
                        EncodeH264(item.id, item.raw);
                    else if (_h264Intended)
                        { /* H.264 was requested but encoder not ready — drop frame */ }
                    else
                        EncodeJpeg(item.id, item.raw);
                }
                finally
                {
                    // Return the rented RGBA buffer to the pool (was leaked before this fix)
                    System.Buffers.ArrayPool<byte>.Shared.Return(item.raw);
                }
            }
        }

        /// <summary>Compress one RGBA32 frame to JPEG and enqueue the packet(s) for sending.</summary>
        private void EncodeJpeg(uint frameId, byte[] raw)
        {
            byte[] jpeg = TurboJpeg.Encode(raw, _cachedWidth, _cachedHeight,
                                           _cachedQuality, _cachedSubsampling);

            if (jpeg == null)
            {
                Debug.LogWarning("[ViewEncoder] TurboJpeg.Encode returned null — frame dropped.");
                return;
            }

            if (_transportMode == TransportMode.UDP)
            {
                byte[][] pkts = NetworkProtocol.BuildUdpPackets(
                    NetworkProtocol.PacketType.VideoFrame, 0, frameId, jpeg, jpeg.Length);
                EnqueuePackets(pkts);
            }
            else
            {
                byte[] pkt = NetworkProtocol.BuildFramePacket(0, frameId, jpeg);
                EnqueuePacket(pkt);
            }
        }

        /// <summary>
        /// Apply a codec mode at runtime — called from <see cref="Start"/>.
        /// The H264Encoder itself is initialised lazily on the first <see cref="EncodeH264"/> call
        /// so this method is safe to call from any thread.
        /// </summary>
        private void ApplyCodecMode(CodecMode mode)
        {
            bool wantsH264 = mode == CodecMode.H264 && H264Encoder.IsAvailable;
            if (mode == CodecMode.H264 && !wantsH264 && TurboJpeg.IsAvailable)
                Debug.LogWarning("[ViewEncoder] H.264 requested but H264Encoder is not available on this device — falling back to MJPEG.");
            _h264Intended  = mode == CodecMode.H264;
            _cachedUseH264 = wantsH264;
        }

        /// <summary>
        /// Encode one RGBA32 frame to H.264 via MediaCodec and enqueue the resulting packet(s).
        /// In UDP mode, large NAL sequences are split into fragment datagrams.
        /// </summary>
        private void EncodeH264(uint frameId, byte[] raw)
        {
            // Lazy-initialise encoder on first call. H264Encoder uses AndroidJavaObject which
            // is safe from background threads in Unity 2021+ (auto JVM attachment).
            if (_h264Encoder == null)
            {
                _h264Encoder = new H264Encoder();
                if (!_h264Encoder.Initialize(_cachedWidth, _cachedHeight, _cachedH264Bitrate, targetFPS,
                                              h264IFrameInterval))
                {
                    _h264Encoder.Dispose();
                    _h264Encoder   = null;
                    _cachedUseH264 = false;
                    _h264Intended  = false;
                    Debug.LogWarning("[ViewEncoder] H264Encoder.Initialize failed — switching to MJPEG.");
                    return; // this frame dropped; next frame will use MJPEG path
                }
                Debug.Log($"[ViewEncoder] H.264 encoder active: {_cachedWidth}x{_cachedHeight} "
                        + $"@ {_cachedH264Bitrate / 1000} kbps, {targetFPS} fps"
                        + $", iFrameInterval={h264IFrameInterval:F2}s.");

                // Open H.264 dump file if enabled
                if (debugDumpH264)
                {
                    try
                    {
                        string path = System.IO.Path.Combine(Application.persistentDataPath, "debug_h264.bin");
                        _debugH264File = new FileStream(path, FileMode.Create, FileAccess.Write);
                        _debugH264FrameCount = 0;
                        Debug.Log($"[ViewEncoder] H.264 dump: {path}");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ViewEncoder] Cannot open H.264 dump file: {e.Message}");
                    }
                }
            }

            byte[] h264 = _h264Encoder.Encode(raw);

            // It is normal for MediaCodec to buffer a few frames before producing output.
            if (h264 == null || h264.Length == 0) return;

            // Write raw Annex-B H.264 to dump file for offline verification (ffplay -f h264 file.bin)
            if (_debugH264File != null)
            {
                if (debugDumpMaxFrames <= 0 || _debugH264FrameCount < debugDumpMaxFrames)
                {
                    try { _debugH264File.Write(h264, 0, h264.Length); }
                    catch { /* best-effort */ }
                    _debugH264FrameCount++;
                }
                else if (_debugH264FrameCount == debugDumpMaxFrames)
                {
                    _debugH264FrameCount++;
                    try { _debugH264File.Flush(); _debugH264File.Close(); }
                    catch { /* best-effort */ }
                    _debugH264File = null;
                    Debug.Log($"[ViewEncoder] H.264 dump complete ({debugDumpMaxFrames} frames).");
                }
            }

            Interlocked.Add(ref _bwEncoderBytes, h264.Length);

            if (_transportMode == TransportMode.UDP)
            {
                byte[][] pkts = NetworkProtocol.BuildUdpPackets(
                    NetworkProtocol.PacketType.H264Frame, 0, frameId, h264, h264.Length);
                EnqueuePackets(pkts);
            }
            else
            {
                byte[] pkt = NetworkProtocol.BuildH264Packet(0, frameId, h264, h264.Length);
                EnqueuePacket(pkt);
            }
        }

        /// <summary>Enqueue a single packet, dropping the oldest if the queue is full.</summary>
        private void EnqueuePacket(byte[] pkt)
        {
            while (_sendQueue.Count >= sendQueueCapacity)
                _sendQueue.TryDequeue(out _);

            _sendQueue.Enqueue(pkt);
            _sendReady.Set();
        }

        /// <summary>Enqueue multiple packets (e.g. UDP fragments), dropping oldest on overflow.</summary>
        private void EnqueuePackets(byte[][] pkts)
        {
            foreach (var pkt in pkts)
                EnqueuePacket(pkt);
        }

        // ── Background threads ───────────────────────────────────────────────────

        /// <summary>
        /// Continuously probes the LAN for a <see cref="StreamServer"/> via UDP broadcast.
        /// Once found, opens a TCP or UDP connection (matching the server's advertised
        /// transport mode) and streams frames until the connection errors,
        /// then re-probes automatically.
        /// </summary>
        private void DiscoverLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                // ── Discover ──────────────────────────────────────────────────────
                if (autoDiscover)
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        if (DiscoverServer(out string foundAddress, out int foundPort))
                        {
                            _serverEndPoint = new IPEndPoint(IPAddress.Parse(foundAddress), foundPort);
                            Debug.Log($"[ViewEncoder] Server discovered at {foundAddress}:{foundPort} transport={_transportMode}");
                            break;
                        }
                        // No extra log here — DiscoverServer already logs the timeout reason.
                    }
                    if (_cts.IsCancellationRequested) break;
                }
                else
                {
                    _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverAddress), serverPort);
                    _transportMode  = manualTransportMode;
                }

                Debug.Log($"[ViewEncoder] Transport mode: {_transportMode}, target: {_serverEndPoint}");

                // ── Connect ───────────────────────────────────────────────────────
                if (_transportMode == TransportMode.TCP)                {
                    // ── TCP path ──────────────────────────────────────────────────
                    try
                    {
                        _tcp             = new TcpClient();
                        _tcp.NoDelay     = true;
                        _tcp.SendBufferSize = 2 * 1024 * 1024;
                        _tcp.SendTimeout    = 5000;   // unblock Write after 5 s
                        _tcp.ReceiveTimeout = 10000;  // unblock Read after 10 s so recv thread can check liveness

                        // Aggressive OS-level TCP keepalive to detect half-open connections
                        // caused by WiFi drops, NAT table expiry, etc.
                        // Uses IOControl(KeepAliveValues) for compatibility with Mono/.NET Standard 2.1.
                        try
                        {
                            _tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                            // struct tcp_keepalive { u_long onoff; u_long keepalivetime; u_long keepaliveinterval; }
                            byte[] keepAlive = new byte[12];
                            System.BitConverter.GetBytes((uint)1).CopyTo(keepAlive, 0);     // onoff
                            System.BitConverter.GetBytes((uint)5000).CopyTo(keepAlive, 4);  // 5 s idle before first probe
                            System.BitConverter.GetBytes((uint)1000).CopyTo(keepAlive, 8);  // 1 s between probes
                            _tcp.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);
                        }
                        catch (Exception) { /* platform may not support IOControl keepalive */ }

                        _tcp.Connect(_serverEndPoint);
                        _tcpStream     = _tcp.GetStream();
                        _isConnected   = true;
                        int gen = Interlocked.Increment(ref _connectionGeneration);
                        Debug.Log($"[ViewEncoder] TCP connected to {_serverEndPoint}.");

                        var ep = _serverEndPoint;
                        MainThreadDispatcher.Enqueue(() => OnServerDiscovered?.Invoke(ep.Address.ToString(), ep));

                        byte[] conn = NetworkProtocol.BuildConnectPacket();
                        lock (_tcpWriteLock) _tcpStream.Write(conn, 0, conn.Length);

                        var recvThread = new Thread(() => ReceiveServerMessagesTcp(gen))
                            { IsBackground = true, Name = "ViewEncoder-Receive" };
                        recvThread.Start();

                        while (_isConnected && !_cts.IsCancellationRequested)
                            Thread.Sleep(100);

                        // Wait for the receive thread to exit before we close the socket
                        // and potentially start a new connection. This prevents the old
                        // recv thread's _isConnected = false from clobbering the new one.
                        recvThread.Join(2000);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ViewEncoder] TCP connect/stream error: {e.Message}");
                    }
                    finally
                    {
                        _isConnected = false;
                        _tcpStream   = null;
                        _tcp?.Close();
                        _tcp = null;
                    }
                }
                else
                {
                    // ── UDP path ──────────────────────────────────────────────────
                    try
                    {
                        _udpSender = new UdpClient();
                        _udpSender.Connect(_serverEndPoint);
                        _isConnected = true;
                        Debug.Log($"[ViewEncoder] UDP connected to {_serverEndPoint}.");

                        var ep = _serverEndPoint;
                        MainThreadDispatcher.Enqueue(() => OnServerDiscovered?.Invoke(ep.Address.ToString(), ep));

                        // Route through sendQueue so SendLoop applies reliable wrapping.
                        byte[] conn = NetworkProtocol.BuildConnectPacket();
                        EnqueuePacket(conn);

                        var recvThread = new Thread(ReceiveServerMessagesUdp)
                            { IsBackground = true, Name = "ViewEncoder-Receive" };
                        recvThread.Start();

                        while (_isConnected && !_cts.IsCancellationRequested)
                            Thread.Sleep(100);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ViewEncoder] UDP error: {e.Message}");
                    }
                    finally
                    {
                        _isConnected = false;
                        _pendingAcks.Clear();
                        _udpSender?.Close();
                        _udpSender = null;
                    }
                }

                if (!_cts.IsCancellationRequested)
                {
                    Debug.Log("[ViewEncoder] Retrying in 3 s...");
                    Thread.Sleep(3000);
                }
            }
        }

        /// <summary>
        /// Broadcasts a UDP discovery probe on every active network interface and waits up to
        /// <see cref="discoveryTimeout"/> ms for a reply from a <see cref="StreamServer"/>.
        /// Uses subnet-directed broadcast addresses (e.g. 192.168.1.255) rather than the
        /// limited 255.255.255.255 broadcast, which is silently dropped on many networks.
        /// </summary>
        private bool DiscoverServer(out string address, out int port)
        {
            address = serverAddress;
            port    = serverPort;

            // Collect subnet-directed broadcast addresses from every active IPv4 interface.
            // Also include 255.255.255.255 as a last-resort fallback.
            var targets = new System.Collections.Generic.List<IPAddress>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] ip   = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        byte[] bc   = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                        var bcAddr = new IPAddress(bc);
                        targets.Add(bcAddr);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ViewEncoder] Discovery: failed to enumerate interfaces — {e.Message}");
            }

            if (targets.Count == 0)
            {
                // Fallback: no interfaces found, try the limited broadcast
                targets.Add(IPAddress.Broadcast);
                Debug.LogWarning("[ViewEncoder] Discovery: no active interfaces found, falling back to 255.255.255.255.");
            }

            try
            {
                // Explicitly bind to any port so Receive() works on every platform
                using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                {
                    udp.EnableBroadcast    = true;
                    udp.Client.ReceiveTimeout = discoveryTimeout;

                    byte[] probe = Encoding.ASCII.GetBytes(NetworkProtocol.DiscoveryRequest);

                    foreach (var target in targets)
                    {
                        udp.Send(probe, probe.Length, new IPEndPoint(target, discoveryPort));
                    }

                    // Wait for the first reply from any server on the LAN
                    IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    byte[]     data   = udp.Receive(ref sender);
                    string     reply  = Encoding.ASCII.GetString(data);

                    if (reply.StartsWith(NetworkProtocol.DiscoveryResponse + ":"))
                    {
                        // Format: "GVST_HERE:<port>" or "GVST_HERE:<port>:<tcp|udp>"
                        string afterPrefix = reply.Substring(NetworkProtocol.DiscoveryResponse.Length + 1);
                        string[] parts = afterPrefix.Split(':');
                        if (parts.Length >= 1 && int.TryParse(parts[0], out int serverTcpPort))
                        {
                            address = sender.Address.ToString();
                            port    = serverTcpPort;

                            // Parse transport mode (defaults to TCP for backward compat).
                            _transportMode = (parts.Length >= 2 && parts[1] == "udp")
                                ? TransportMode.UDP
                                : TransportMode.TCP;

                            return true;
                        }
                    }

                    Debug.LogWarning($"[ViewEncoder] Discovery: unexpected reply format: '{reply}'");
                }
            }
            catch (SocketException)
            {
                // Receive timed out — normal when no server is running. Silent.
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ViewEncoder] Discovery error: {e.Message}");
            }

            return false;
        }

        /// <summary>
        /// Drains the send queue and writes each packet to the transport (TCP stream or UDP socket).
        /// When idle, sends a Heartbeat every second so both sides know the connection is alive
        /// (important during H.264 MediaCodec warmup when no frames are produced yet).
        /// Sets <see cref="_isConnected"/> to false on any write error so <see cref="DiscoverLoop"/>
        /// automatically reconnects.
        /// </summary>
        private void SendLoop()
        {
            long lastSentMs = 0;
            byte[] heartbeat = NetworkProtocol.BuildHeartbeatPacket();

            bool reliableUdp = _cachedSendReliable; // captured once for the loop

            while (!_cts.IsCancellationRequested)
            {
                if (_isConnected && _sendQueue.TryDequeue(out byte[] packet))
                {
                    try
                    {
                        if (_transportMode == TransportMode.UDP)
                        {
                            if (reliableUdp)
                            {
                                uint seq = NextReliableSeq();
                                byte[] wrapped = NetworkProtocol.PrependReliableSeq(packet, seq);
                                _pendingAcks[seq] = new ReliablePending
                                    { Packet = wrapped, SentMs = NowMs, Retries = 0 };
                                _udpSender.Send(wrapped, wrapped.Length);
                                Interlocked.Add(ref _bwWireBytes, wrapped.Length);
                            }
                            else
                            {
                                _udpSender.Send(packet, packet.Length);
                                Interlocked.Add(ref _bwWireBytes, packet.Length);
                            }
                        }
                        else
                        {
                            lock (_tcpWriteLock) _tcpStream.Write(packet, 0, packet.Length);
                            Interlocked.Add(ref _bwWireBytes, packet.Length);
                        }
                        lastSentMs = NowMs;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ViewEncoder] Send error: {e.Message}");
                        _isConnected = false;
                    }
                }
                else
                {
                    // Heartbeat when idle for >= 1 second — prevents server heartbeat-timeout
                    // during H.264 warmup or HMD proximity-sensor sleep.
                    // Heartbeats are sent unreliably (loss is harmless; the next one follows in 1 s).
                    if (_isConnected && (NowMs - lastSentMs) >= 1000)
                    {
                        try
                        {
                            if (_transportMode == TransportMode.UDP)
                                _udpSender.Send(heartbeat, heartbeat.Length);
                            else
                                lock (_tcpWriteLock) _tcpStream.Write(heartbeat, 0, heartbeat.Length);
                            lastSentMs = NowMs;
                        }
                        catch { /* receive thread will detect the error */ }
                    }

                    // Retransmit un-ACKed reliable datagrams.
                    if (reliableUdp && _transportMode == TransportMode.UDP && _isConnected)
                        RetransmitPending();

                    // Periodic bandwidth log (every 5 s)
                    long nowBw = NowMs;
                    long elapsed = nowBw - _bwLastLogMs;
                    if (_isConnected && elapsed >= 5000)
                    {
                        long encBytes  = Interlocked.Exchange(ref _bwEncoderBytes, 0);
                        long wireBytes = Interlocked.Exchange(ref _bwWireBytes, 0);
                        _bwLastLogMs = nowBw;
                        float encKbps  = encBytes  * 8f / elapsed;
                        float wireKbps = wireBytes * 8f / elapsed;
                        Debug.Log($"[ViewEncoder] BW: encoder={encKbps:F0} kbps, wire={wireKbps:F0} kbps (target={_cachedH264Bitrate / 1000} kbps)");
                    }

                    _sendReady.Reset();
                    if (_sendQueue.IsEmpty)
                        _sendReady.Wait(reliableUdp
                            ? Math.Min(50, _cachedRetransmitMs)
                            : 50);
                }
            }
        }

        // ── TCP receive ──────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the server→client half of the TCP stream for one connection lifetime.
        /// Main job: echo heartbeat pings back so the server's idle clock stays reset.
        /// </summary>
        private void ReceiveServerMessagesTcp(int myGeneration)
        {
            NetworkStream stream = _tcpStream;
            if (stream == null) return;

            byte[] headerBuf = new byte[NetworkProtocol.HeaderSize];
            try
            {
                while (_isConnected && !_cts.IsCancellationRequested)
                {
                    // Read fixed-size header (partial reads are normal on TCP)
                    if (!ReadExact(stream, headerBuf, NetworkProtocol.HeaderSize, _cts))
                        break; // graceful close from server or cancellation

                    if (!NetworkProtocol.TryParseHeader(headerBuf, 0,
                            out NetworkProtocol.PacketType type, out _, out _, out int payloadSize))
                    {
                        Debug.LogWarning("[ViewEncoder] Bad header from server — disconnecting.");
                        break;
                    }

                    // Drain any payload bytes (future-proofing; server currently sends none)
                    if (payloadSize > 0)
                    {
                        byte[] discard = new byte[payloadSize];
                        if (!ReadExact(stream, discard, payloadSize, _cts)) break;
                    }

                    if (type == NetworkProtocol.PacketType.RequestKeyFrame)
                    {
                        _h264Encoder?.RequestKeyFrame();
                    }
                    else if (type == NetworkProtocol.PacketType.Heartbeat)
                    {
                        // Echo pong back so the server's LastReceivedMs is reset.
                        // Any data frame the encoder sends also resets it, so this only matters
                        // during pauses (e.g. proximity sensor sleep, encode warmup).
                        byte[] pong = NetworkProtocol.BuildHeartbeatPacket();
                        try
                        {
                            lock (_tcpWriteLock)
                                stream.Write(pong, 0, pong.Length);
                        }
                        catch { break; }
                    }
                }
            }
            catch (IOException)            { /* socket closed — normal */ }
            catch (SocketException)        { /* reset by peer */ }
            catch (ObjectDisposedException) { /* closed from our side */ }
            catch (Exception e)
            {
                if (!_cts.IsCancellationRequested)
                    Debug.LogWarning($"[ViewEncoder] Receive error: {e.Message}");
            }

            // Only clear _isConnected if we're still the active connection generation.
            // Otherwise a new connection has already started and we'd clobber its state.
            if (Volatile.Read(ref _connectionGeneration) == myGeneration)
                _isConnected = false; // unblocks DiscoverLoop's sleep loop → triggers reconnect
        }

        // ── UDP receive ──────────────────────────────────────────────────────────

        /// <summary>
        /// Listens for server→client UDP packets (heartbeat pings) and echoes them back.
        /// Also monitors server liveness — if no server packet is received for 15 seconds,
        /// the connection is considered lost and <see cref="DiscoverLoop"/> re-probes.
        /// Unlike TCP, there is no socket-level disconnect signal for UDP, so continuous
        /// server keepalive pings (sent by the server's HeartbeatLoop) are the only indicator.
        /// </summary>
        private void ReceiveServerMessagesUdp()
        {
            UdpClient udp = _udpSender;
            if (udp == null) return;

            udp.Client.ReceiveTimeout = 5000; // 5 s per Receive() call
            long lastServerPacketMs = NowMs;

            try
            {
                while (_isConnected && !_cts.IsCancellationRequested)
                {
                    try
                    {
                        IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = udp.Receive(ref sender);
                        lastServerPacketMs = NowMs;

                        if (data.Length < NetworkProtocol.HeaderSize) continue;
                        if (!NetworkProtocol.TryParseHeader(data, 0,
                                out var type, out _, out uint ackSeq, out _))
                            continue;

                        if (type == NetworkProtocol.PacketType.RequestKeyFrame)
                        {
                            _h264Encoder?.RequestKeyFrame();
                        }
                        else if (type == NetworkProtocol.PacketType.Ack)
                        {
                            // Server acknowledged a reliable datagram — remove from retransmit buffer.
                            _pendingAcks.TryRemove(ackSeq, out _);
                        }
                        else if (type == NetworkProtocol.PacketType.RequestKeyFrame)
                    {
                        _h264Encoder?.RequestKeyFrame();
                    }
                    else if (type == NetworkProtocol.PacketType.Heartbeat)
                        {
                            byte[] pong = NetworkProtocol.BuildHeartbeatPacket();
                            try { udp.Send(pong, pong.Length); } catch { break; }
                        }
                    }
                    catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
                    {
                        // No data within 5 s — check server liveness.
                        if (NowMs - lastServerPacketMs > 15_000)
                        {
                            Debug.LogWarning("[ViewEncoder] No server response for 15 s — reconnecting.");
                            break;
                        }
                    }
                }
            }
            catch (SocketException) when (_cts?.IsCancellationRequested == true) { }
            catch (ObjectDisposedException) { }
            catch (Exception e)
            {
                if (_cts != null && !_cts.IsCancellationRequested)
                    Debug.LogWarning($"[ViewEncoder] UDP receive error: {e.Message}");
            }

            _isConnected = false;
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes from <paramref name="stream"/>.
        /// Returns false if the server closes the connection before all bytes arrive.
        /// TCP Read() only guarantees ≥1 byte — this wrapper handles partial reads.
        /// </summary>
        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes.  Returns false on graceful close (Read=0)
        /// or cancellation.  On ReceiveTimeout it re-enters the loop internally so partial reads
        /// are preserved, but checks <paramref name="cts"/> so the thread can exit for shutdown.
        /// </summary>
        private static bool ReadExact(NetworkStream stream, byte[] buffer, int count,
                                       CancellationTokenSource cts = null)
        {
            int read = 0;
            while (read < count)
            {
                if (cts != null && cts.IsCancellationRequested) return false;
                int n;
                try
                {
                    n = stream.Read(buffer, read, count - read);
                }
                catch (IOException ex) when (ex.InnerException is SocketException se
                    && se.SocketErrorCode == SocketError.TimedOut)
                {
                    // ReceiveTimeout fired — not a real disconnect.
                    // Re-enter the loop so partial reads are preserved.
                    continue;
                }
                if (n == 0) return false; // graceful close
                read += n;
            }
            return true;
        }

        private static long NowMs =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000L
            / System.Diagnostics.Stopwatch.Frequency;
    }
}

