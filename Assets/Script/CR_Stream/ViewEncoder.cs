using System;
using System.Collections.Concurrent;
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
    /// (ARM NEON SIMD on Android, SSE on Windows) and streams over a persistent TCP
    /// connection to the server.
    ///
    /// Encoding pipeline:
    ///   Main thread   : AsyncGPUReadback → raw RGBA32 bytes → _rawQueue
    ///   Encode thread : _rawQueue → TurboJpeg.Encode → _sendQueue
    ///   Send thread   : _sendQueue → TCP socket
    ///
    /// Setup:
    ///   1. Attach this component to the Camera you want to stream.
    ///   2. Enable <see cref="autoDiscover"/> (default on) — the server IP is found via UDP
    ///      broadcast automatically. No hard-coded IP needed on the same LAN.
    ///      Alternatively disable it and set <see cref="serverAddress"/> manually.
    ///   3. Build and deploy to the Android VR device.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class ViewEncoder : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("Server Connection")]
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

        [Header("Capture Settings")]
        [Tooltip("Width of the capture texture. Lower = less bandwidth (e.g. 480x270 for 10+ clients).")]
        [SerializeField] private int captureWidth  = 960;
        [Tooltip("Height of the capture texture.")]
        [SerializeField] private int captureHeight = 540;
        [Tooltip("Maximum frames per second to encode and send.")]
        [SerializeField, Range(5, 60)] private int targetFPS = 30;

        [Header("Encoding Settings")]
        [Tooltip("JPEG quality 1-100. 75 is a good streaming default (good quality, lower bandwidth).")]
        [SerializeField, Range(1, 100)] private int jpegQuality = 75;
        [Tooltip("Chroma subsampling. TJSAMP_420=2 (default). Use 0 for 4:4:4 (highest quality).")]
        [SerializeField] private int jpegSubsampling = TurboJpeg.TJSAMP_420;
        [Tooltip("Max raw frames held in queue for the encode thread. Older frames are dropped — latency beats delivery.")]
        [SerializeField, Range(1, 4)] private int rawQueueCapacity  = 2;
        [Tooltip("Max compressed packets queued for the send thread.")]
        [SerializeField, Range(1, 4)] private int sendQueueCapacity = 2;

        [Header("H.264 Settings (Android only)")]
        [Tooltip("Enable H.264 hardware encoding via Android MediaCodec instead of JPEG.\n"
               + "Results in ~3-5x lower bandwidth at comparable quality (recommended for WiFi LAN).\n"
               + "Requires 'Use H264' to also be enabled on the PC ViewDecoder.\n"
               + "Has no effect on Windows Editor (JPEG is used instead).")]
        [SerializeField] private bool useH264 = false;
        [Tooltip("H.264 target bitrate in Mbps. 2 Mbps is recommended for 960×540 @ 30fps on WiFi LAN.")]
        [SerializeField, Range(1, 20)] private int h264BitrateMbps = 2;

        // ── Private state ────────────────────────────────────────────────────────

        private Camera        _camera;
        private RenderTexture _captureRT;

        // H.264 encoder (Android only — null on Editor / Windows)
        private H264Encoder _h264Encoder;

        // Networking (UDP — connectionless, low-latency, tolerates lost frames)
        private UdpClient     _udp;
        private IPEndPoint    _serverEndPoint;
        private volatile bool _isConnected;
        private CancellationTokenSource _cts;

        // Three dedicated background threads
        private Thread _discoverThread;
        private Thread _encodeThread;
        private Thread _sendThread;

        // Raw frames waiting to be compressed: (frameId, rgba32 bytes)
        private readonly ConcurrentQueue<(uint id, byte[] raw)> _rawQueue
            = new ConcurrentQueue<(uint, byte[])>();

        // Compressed packets ready to send
        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();

        // Frame capture state (main thread only)
        private uint  _frameId;
        private float _captureInterval;
        private float _lastCaptureTime;
        private bool  _gpuReadbackPending;

        // Cached at Start() so encode thread never accesses Unity properties
        private int  _cachedWidth;
        private int  _cachedHeight;
        private int  _cachedQuality;
        private int  _cachedSubsampling;
        private bool _cachedUseH264;      // true  = H.264 path is active
        private bool _h264Intended;       // true  = user checked Use H264 (never fall back to JPEG)
        private int  _cachedH264Bitrate;

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
            _h264Intended      = useH264;  // remember the inspector intent — never fall back to JPEG if this is true
            _cachedUseH264     = useH264 && H264Encoder.IsAvailable;
            _cachedH264Bitrate = h264BitrateMbps * 1_000_000;

            // Initialise H.264 encoder on the main thread (safe for AndroidJavaObject)
            if (_cachedUseH264)
            {
                _h264Encoder = new H264Encoder();
                if (!_h264Encoder.Initialize(_cachedWidth, _cachedHeight, _cachedH264Bitrate, targetFPS))
                {
                    // Initialisation failed.  If the user intended H.264 we must NOT fall back to
                    // JPEG — libturbojpeg.so may not be in the APK and the DllImport would crash.
                    // Frames will simply be dropped until the encoder comes up.
                    Debug.LogError("[ViewEncoder] H264Encoder.Initialize failed. Frames will be dropped until resolved.");
                    _h264Encoder.Dispose();
                    _h264Encoder   = null;
                    _cachedUseH264 = false;
                    // _h264Intended stays true → EncodeLoop will skip frames instead of calling TurboJpeg
                }
                else
                {
                    Debug.Log($"[ViewEncoder] H.264 mode: {_cachedWidth}x{_cachedHeight} "
                             + $"@ {h264BitrateMbps} Mbps, {targetFPS} fps.");
                }
            }

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
            if (!_isConnected || _gpuReadbackPending) return;
            if (Time.unscaledTime - _lastCaptureTime < _captureInterval) return;

            _lastCaptureTime    = Time.unscaledTime;
            _gpuReadbackPending = true;

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
            if (_isConnected && _udp != null && _serverEndPoint != null)
            {
                try
                {
                    byte[] disc = NetworkProtocol.BuildDisconnectPacket(0);
                    _udp.Send(disc, disc.Length, _serverEndPoint);
                }
                catch { /* best-effort */ }
            }
            _isConnected = false;
            _udp?.Close();
            _h264Encoder?.Release();
            _h264Encoder?.Dispose();
            _h264Encoder = null;
            if (_captureRT != null) { _captureRT.Release(); Destroy(_captureRT); }
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
            byte[] raw = request.GetData<byte>().ToArray();

            if (_rawQueue.Count >= rawQueueCapacity)
                _rawQueue.TryDequeue(out _);

            _rawQueue.Enqueue((_frameId++, raw));
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
                    Thread.Sleep(1);
                    continue;
                }

                if (_cachedUseH264)
                    EncodeH264(item.id, item.raw);
                else if (_h264Intended)
                    { /* H.264 was requested but encoder not ready — drop frame, keepalive will maintain connection */ }
                else
                    EncodeJpeg(item.id, item.raw);
            }
        }

        /// <summary>Compress one RGBA32 frame to JPEG and enqueue the packet for sending.</summary>
        private void EncodeJpeg(uint frameId, byte[] raw)
        {
            byte[] jpeg = TurboJpeg.Encode(raw, _cachedWidth, _cachedHeight,
                                           _cachedQuality, _cachedSubsampling);

            if (jpeg == null)
            {
                Debug.LogWarning("[ViewEncoder] TurboJpeg.Encode returned null — frame dropped.");
                return;
            }

            byte[] pkt = NetworkProtocol.BuildFramePacket(0, frameId, jpeg);

            // UDP datagrams are capped at 65507 bytes. Drop oversized frames rather than crash.
            // Reduce quality or resolution if this warning fires frequently.
            if (pkt.Length > NetworkProtocol.MaxUdpPayload)
            {
                Debug.LogWarning($"[ViewEncoder] JPEG frame {frameId} too large for UDP "
                               + $"({pkt.Length} B > {NetworkProtocol.MaxUdpPayload}) — dropped. Lower quality or resolution.");
                return;
            }

            EnqueuePackets(new[] { pkt });
        }

        /// <summary>
        /// Encode one RGBA32 frame to H.264 via MediaCodec and enqueue all resulting packets.
        /// Large NAL sequences are automatically split into <see cref="NetworkProtocol.PacketType.H264Fragment"/>
        /// packets so each datagram stays within the UDP size limit.
        /// </summary>
        private void EncodeH264(uint frameId, byte[] raw)
        {
            if (_h264Encoder == null) return;

            byte[] h264 = _h264Encoder.Encode(raw);

            // It is normal for MediaCodec to buffer a few frames before producing output.
            if (h264 == null || h264.Length == 0) return;

            byte[][] pkts = NetworkProtocol.BuildH264Packets(0, frameId, h264, h264.Length);
            EnqueuePackets(pkts);
        }

        /// <summary>
        /// Enqueue an array of ready-to-send packets, dropping old packets as needed to fit the
        /// entire group.  Dropping is done atomically before any new packet is enqueued so that
        /// multi-fragment H.264 frames are never partially stranded in the queue.
        /// </summary>
        private void EnqueuePackets(byte[][] pkts)
        {
            // Clamp to 0 so we never loop infinitely when pkts is larger than the cap
            int maxExisting = Math.Max(0, sendQueueCapacity - pkts.Length);
            while (_sendQueue.Count > maxExisting)
                _sendQueue.TryDequeue(out _);

            foreach (byte[] pkt in pkts)
                _sendQueue.Enqueue(pkt);
        }

        // ── Background threads ───────────────────────────────────────────────────

        /// <summary>
        /// Continuously probes the LAN for a <see cref="StreamServer"/> via UDP broadcast.
        /// Once found, opens a UDP socket and streams frames until a send error occurs,
        /// then re-probes automatically.
        /// </summary>
        private void DiscoverLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                // ── Discover ─────────────────────────────────────────────────────
                if (autoDiscover)
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        Debug.Log("[ViewEncoder] Broadcasting discovery probe...");
                        if (DiscoverServer(out string foundAddress, out int foundPort))
                        {
                            _serverEndPoint = new IPEndPoint(IPAddress.Parse(foundAddress), foundPort);
                            Debug.Log($"[ViewEncoder] Server discovered at {foundAddress}:{foundPort}");
                            break;
                        }
                        Debug.Log($"[ViewEncoder] No server found — retrying in {discoveryTimeout} ms...");
                    }
                    if (_cts.IsCancellationRequested) break;
                }
                else
                {
                    _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverAddress), serverPort);
                }

                // ── Stream ─────────────────────────────────────────────────────
                try
                {
                    _udp         = new UdpClient();
                    _isConnected = true;
                    Debug.Log($"[ViewEncoder] Streaming UDP to {_serverEndPoint}.");

                    // Announce presence so the server registers us before the first video frame
                    byte[] conn = NetworkProtocol.BuildConnectPacket();
                    _udp.Send(conn, conn.Length, _serverEndPoint);

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
                    _udp?.Close();
                    _udp = null;
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
                        Debug.Log($"[ViewEncoder] Discovery: will probe subnet broadcast {bcAddr} (iface: {ni.Name})");
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
                        Debug.Log($"[ViewEncoder] Discovery probe sent to {target}:{discoveryPort}");
                    }

                    // Wait for the first reply from any server on the LAN
                    IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    byte[]     data   = udp.Receive(ref sender);
                    string     reply  = Encoding.ASCII.GetString(data);

                    Debug.Log($"[ViewEncoder] Discovery reply from {sender}: '{reply}'");

                    if (reply.StartsWith(NetworkProtocol.DiscoveryResponse + ":"))
                    {
                        string tcpPortStr = reply.Substring(NetworkProtocol.DiscoveryResponse.Length + 1);
                        if (int.TryParse(tcpPortStr, out int tcpPort))
                        {
                            address = sender.Address.ToString();
                            port    = tcpPort;
                            return true;
                        }
                    }

                    Debug.LogWarning($"[ViewEncoder] Discovery: unexpected reply format: '{reply}'");
                }
            }
            catch (SocketException se)
            {
                // 10060 = WSAETIMEDOUT (receive timed out — normal when no server present)
                Debug.Log($"[ViewEncoder] Discovery receive timed out (SocketError={se.SocketErrorCode}). No server found this probe.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ViewEncoder] Discovery error: {e.Message}");
            }

            return false;
        }

        /// <summary>
        /// Drains the send queue and fires each packet as a UDP datagram.
        /// When the queue is idle (e.g. during H.264 MediaCodec warmup or low-FPS scenes),
        /// sends a Connect heartbeat every second so the server does not time out the client.
        /// </summary>
        private void SendLoop()
        {
            long lastSentMs = 0;
            byte[] keepalive = NetworkProtocol.BuildConnectPacket();

            while (!_cts.IsCancellationRequested)
            {
                if (_isConnected && _udp != null && _sendQueue.TryDequeue(out byte[] packet))
                {
                    try
                    {
                        _udp.Send(packet, packet.Length, _serverEndPoint);
                        lastSentMs = NowMs;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ViewEncoder] Send error: {e.Message}");
                        _isConnected = false; // signals DiscoverLoop to re-probe
                    }
                }
                else
                {
                    // Send a keepalive heartbeat if nothing has been sent for 1 second.
                    // This prevents the server from timing out during H.264 encoder warmup
                    // (MediaCodec may take several frames before producing any output)
                    // or any other period of encode inactivity.
                    if (_isConnected && _udp != null && (NowMs - lastSentMs) >= 1000)
                    {
                        try
                        {
                            _udp.Send(keepalive, keepalive.Length, _serverEndPoint);
                            lastSentMs = NowMs;
                        }
                        catch { /* best-effort; will retry next loop */ }
                    }
                    Thread.Sleep(1);
                }
            }
        }

        private static long NowMs =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000L
            / System.Diagnostics.Stopwatch.Frequency;
    }
}

