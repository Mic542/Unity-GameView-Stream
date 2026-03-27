using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace GameViewStream
{
    // ── Data carrier ─────────────────────────────────────────────────────────────

    /// <summary>A fully-received, pool-backed frame payload from one client.</summary>
    public sealed class ClientFrameData
    {
        public ushort ClientId;
        public uint   FrameId;
        public NetworkProtocol.PacketType PacketType; // VideoFrame, H264Frame, or H264Fragment
        public byte[] PixelData;     // rented from ArrayPool<byte>.Shared — MUST be returned after use
        public int    PayloadLength; // valid bytes in PixelData (rented buffer is often larger)
    }

    // ── Server MonoBehaviour ─────────────────────────────────────────────────────

    /// <summary>
    /// Server-side component (PC).
    /// Listens for incoming client connections from Android VR clients,
    /// manages per-client receive threads, and exposes a thread-safe frame queue
    /// for <see cref="ViewDecoder"/> to drain on the main thread.
    ///
    /// Supports two transport modes selectable in the Inspector:
    ///   <b>TCP</b> (default): reliable, ordered stream — one background thread per client.
    ///   <b>UDP</b>: lower latency, best-effort — single receive thread, application-level
    ///       fragment reassembly for frames that exceed the UDP datagram limit.
    ///
    /// The chosen mode is advertised in the discovery reply so clients connect with
    /// the matching transport automatically.
    ///
    /// Setup:
    ///   1. Place on a persistent GameObject together with <see cref="ViewDecoder"/>.
    ///   2. Choose the transport mode and optionally override port / max clients.
    ///   3. Make sure the server firewall allows inbound traffic on the chosen port.
    /// </summary>
    public sealed class StreamServer : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Tooltip("Transport mode for the data channel.\n"
               + "TCP: reliable, ordered — one thread per client.\n"
               + "UDP: lower latency, best-effort — single receive thread with fragment reassembly.")]
        [SerializeField] private TransportMode transportMode = TransportMode.TCP;

        [Tooltip("Port to listen on (TCP or UDP). Must match ViewEncoder.serverPort on every client.")]
        [SerializeField] private int port       = NetworkProtocol.DefaultPort;
        [Tooltip("Hard cap on simultaneous connections. A new connection beyond this is rejected immediately.")]
        [SerializeField] private int maxClients = 50;

        [Tooltip("Respond to UDP broadcast discovery probes from clients on the LAN.")]
        [SerializeField] private bool enableDiscovery = true;
        [Tooltip("UDP port to listen on for discovery probes. Must match ViewEncoder.discoveryPort.")]
        [SerializeField] private int  discoveryPort   = NetworkProtocol.DiscoveryPort;

        [Tooltip("Seconds of idle silence before the server sends a heartbeat ping to confirm liveness.\n"
               + "Default 5 s: a ping is sent after 5 s without receiving any data.")]
        [SerializeField] private float heartbeatInterval = 5f;

        [Tooltip("Seconds of silence after which a client is considered dead and disconnected.\n"
               + "Must be greater than heartbeatInterval. Default 15 s gives 2 ping cycles before drop.")]
        [SerializeField] private float heartbeatTimeout  = 15f;

        [Tooltip("Maximum frames held in the shared queue waiting for decode workers. "
               + "Oldest frames are dropped when this is reached to prevent unbounded memory use "
               + "during long sessions. Raise if workers fall behind on high client counts.")]
        [SerializeField] private int frameQueueCap = 256;

        // ── Events (raised on the main thread via MainThreadDispatcher) ──────────

        /// <summary>Raised on the main thread when a new client connects.</summary>
        public event Action<ushort, IPAddress> OnClientConnected;
        /// <summary>Raised on the main thread when a client disconnects or errors.</summary>
        public event Action<ushort>            OnClientDisconnected;

        // ── Public state ─────────────────────────────────────────────────────────

        /// <summary>Frames received from all clients, ready to decode on the main thread.</summary>
        public readonly ConcurrentQueue<ClientFrameData> FrameQueue = new ConcurrentQueue<ClientFrameData>();

        /// <summary>Number of clients currently connected.</summary>
        public int ConnectedClientCount => _clients.Count;

        // ── Private state ─────────────────────────────────────────────────────────

        [SerializeField] private int _connectedClients;

        // Per-client state ————————————————————————————————————————————————————————
        private sealed class ConnectedClient
        {
            public readonly ushort        Id;
            public readonly IPAddress     Address;
            // Not volatile: long is 64-bit and volatile long is illegal in C#.
            // Use Interlocked.Exchange / Interlocked.Read for thread-safe access.
            public long                   LastReceivedMs;

            // ── TCP-mode fields (null in UDP mode) ───────────────────────────────
            public readonly TcpClient     Tcp;
            public readonly NetworkStream Stream;
            /// <summary>Serialises concurrent writes (heartbeat thread vs. any future server→client message).</summary>
            public readonly object        WriteLock = new object();

            // ── UDP-mode fields (null in TCP mode) ───────────────────────────────
            public readonly IPEndPoint    UdpEndPoint;

            public ConnectedClient(ushort id, TcpClient tcp, long nowMs)
            {
                Id             = id;
                Tcp            = tcp;
                Stream         = tcp.GetStream();
                Address        = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                LastReceivedMs = nowMs;
            }

            /// <summary>UDP-mode constructor (no persistent socket).</summary>
            public ConnectedClient(ushort id, IPEndPoint ep, long nowMs)
            {
                Id             = id;
                UdpEndPoint    = ep;
                Address        = ep.Address;
                LastReceivedMs = nowMs;
            }
        }

        private readonly ConcurrentDictionary<ushort, ConnectedClient> _clients =
            new ConcurrentDictionary<ushort, ConnectedClient>();

        private TcpListener _listener;
        private Thread      _acceptThread;
        private Thread      _heartbeatThread;
        private Thread      _discoveryThread;
        private UdpClient   _discoveryUdp;
        private CancellationTokenSource _cts;

        // ── UDP-mode state ────────────────────────────────────────────────────────
        private UdpClient   _udpReceiver;
        private Thread      _udpReceiveThread;
        private readonly object _udpWriteLock = new object();

        /// <summary>Maps sender endpoint string ("ip:port") → server-assigned client ID.</summary>
        private readonly ConcurrentDictionary<string, ushort> _endpointToClient =
            new ConcurrentDictionary<string, ushort>();

        /// <summary>Pending fragment reassembly buffers, keyed by (clientId, frameId).</summary>
        private readonly ConcurrentDictionary<(ushort, uint), FragmentBuffer> _fragmentBuffers =
            new ConcurrentDictionary<(ushort, uint), FragmentBuffer>();

        private sealed class FragmentBuffer
        {
            public readonly NetworkProtocol.PacketType OriginalType;
            public readonly int       TotalFragments;
            public readonly byte[][]  Chunks;
            public readonly int[]     ChunkLengths;
            public int                ReceivedCount;
            public readonly long      CreatedMs;

            public FragmentBuffer(NetworkProtocol.PacketType origType, int total, long nowMs)
            {
                OriginalType   = origType;
                TotalFragments = total;
                Chunks         = new byte[total][];
                ChunkLengths   = new int[total];
                CreatedMs      = nowMs;
            }
        }

        private ushort     _nextClientId = 1;
        private readonly object _idLock  = new object();

        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private static long NowMs => _clock.ElapsedMilliseconds;

        // ── Unity lifecycle ──────────────────────────────────────────────────────

        private void Start()
        {
            _cts = new CancellationTokenSource();

            if (transportMode == TransportMode.TCP)
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(backlog: maxClients);

                _acceptThread = new Thread(AcceptLoop)
                    { IsBackground = true, Name = "StreamServer-Accept" };
                _acceptThread.Start();

                Debug.Log($"[StreamServer] Listening on TCP *:{port} (max {maxClients} clients).");
            }
            else
            {
                _udpReceiver = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                _udpReceiveThread = new Thread(UdpReceiveLoop)
                    { IsBackground = true, Name = "StreamServer-UdpReceive" };
                _udpReceiveThread.Start();

                Debug.Log($"[StreamServer] Listening on UDP *:{port} (max {maxClients} clients).");
            }

            _heartbeatThread = new Thread(HeartbeatLoop)
                { IsBackground = true, Name = "StreamServer-Heartbeat" };
            _heartbeatThread.Start();

            if (enableDiscovery)
            {
                _discoveryUdp    = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
                _discoveryThread = new Thread(DiscoveryLoop)
                    { IsBackground = true, Name = "StreamServer-Discovery" };
                _discoveryThread.Start();
                Debug.Log($"[StreamServer] Discovery listening on UDP:{discoveryPort}.");
            }
        }

        private void Update()
        {
            _connectedClients = ConnectedClientCount;
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            // Stop TCP listener (if active) — unblocks AcceptTcpClient().
            _listener?.Stop();
            // Stop UDP receiver (if active) — unblocks UdpClient.Receive().
            _udpReceiver?.Close();
            _discoveryUdp?.Close();
            // Close all client sockets (TCP mode) — unblocks each ReadExact() via IOException.
            foreach (var kv in _clients)
                if (kv.Value.Tcp != null)
                    try { kv.Value.Tcp.Close(); } catch { }
            _clients.Clear();
            _endpointToClient.Clear();
            // Return any pool memory held by pending fragment buffers.
            foreach (var kv in _fragmentBuffers)
                for (int i = 0; i < kv.Value.TotalFragments; i++)
                    if (kv.Value.Chunks[i] != null)
                        ArrayPool<byte>.Shared.Return(kv.Value.Chunks[i]);
            _fragmentBuffers.Clear();
        }

        // ── Accept loop ──────────────────────────────────────────────────────────

        private void AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = _listener.AcceptTcpClient();
                }
                catch (SocketException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    if (!_cts.IsCancellationRequested)
                        Debug.LogWarning($"[StreamServer] Accept error: {e.Message}");
                    continue;
                }

                if (_clients.Count >= maxClients)
                {
                    Debug.LogWarning($"[StreamServer] Max clients ({maxClients}) reached — rejecting {tcp.Client.RemoteEndPoint}.");
                    tcp.Close();
                    continue;
                }

                // Disable Nagle's algorithm: real-time video frames must not be coalesced
                // with subsequent heartbeat packets, which would add up to 200 ms of latency.
                tcp.NoDelay           = true;
                tcp.ReceiveBufferSize = 2 * 1024 * 1024; // 2 MB per-client recv buffer

                ushort id;
                lock (_idLock) id = _nextClientId++;

                var client = new ConnectedClient(id, tcp, NowMs);
                _clients[id] = client;

                var receiveThread = new Thread(ClientReceiveLoop)
                    { IsBackground = true, Name = $"StreamServer-Client-{id}" };
                receiveThread.Start(client);

                Debug.Log($"[StreamServer] Client {id} connected from {client.Address}.");
                var capId   = id;
                var capAddr = client.Address;
                MainThreadDispatcher.Enqueue(() => OnClientConnected?.Invoke(capId, capAddr));
            }
        }

        // ── Per-client receive loop ──────────────────────────────────────────────

        /// <summary>
        /// Reads the continuous TCP stream for one client.
        /// Each message is: [15-byte header][N-byte payload].
        /// ReadExact guarantees we always read the exact number of bytes needed,
        /// compensating for TCP's partial-delivery behaviour.
        /// </summary>
        private void ClientReceiveLoop(object state)
        {
            var client    = (ConnectedClient)state;
            var headerBuf = new byte[NetworkProtocol.HeaderSize];

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    // ── Step 1: read the fixed-size header ───────────────────────────
                    if (!ReadExact(client.Stream, headerBuf, NetworkProtocol.HeaderSize))
                        break; // connection closed gracefully (Read returned 0)

                    if (!NetworkProtocol.TryParseHeader(headerBuf, 0,
                            out var type, out _, out uint frameId, out int payloadSize))
                    {
                        Debug.LogWarning($"[StreamServer] Client {client.Id}: bad header magic — disconnecting.");
                        break;
                    }

                    Interlocked.Exchange(ref client.LastReceivedMs, NowMs); // any valid packet counts as liveness

                    // ── Step 2: read payload (zero for control packets) ──────────────
                    byte[] payload = null;
                    if (payloadSize > 0)
                    {
                        payload = ArrayPool<byte>.Shared.Rent(payloadSize);
                        if (!ReadExact(client.Stream, payload, payloadSize))
                        {
                            ArrayPool<byte>.Shared.Return(payload);
                            break;
                        }
                    }

                    // ── Step 3: dispatch ──────────────────────────────────────────────
                    switch (type)
                    {
                        case NetworkProtocol.PacketType.VideoFrame:
                        case NetworkProtocol.PacketType.H264Frame:
                            if (payload != null)
                                EnqueueFrame(client.Id, frameId, type, payload, payloadSize);
                            break;

                        case NetworkProtocol.PacketType.Connect:
                            // Already registered on accept; Connect packet just confirms handshake.
                            if (payload != null) ArrayPool<byte>.Shared.Return(payload);
                            break;

                        case NetworkProtocol.PacketType.Disconnect:
                            // Graceful goodbye — remove and exit the loop cleanly.
                            if (payload != null) ArrayPool<byte>.Shared.Return(payload);
                            RemoveClient(client.Id, "sent disconnect");
                            return; // Skip the RemoveClient at the bottom.

                        case NetworkProtocol.PacketType.Heartbeat:
                            // Echo the ping back (server → client pong, same packet type, no payload).
                            if (payload != null) ArrayPool<byte>.Shared.Return(payload);
                            try
                            {
                                byte[] pong = NetworkProtocol.BuildHeartbeatPacket();
                                lock (client.WriteLock)
                                    client.Stream.Write(pong, 0, pong.Length);
                            }
                            catch { /* socket already closing — receive loop will exit on next read */ }
                            break;

                        default:
                            if (payload != null) ArrayPool<byte>.Shared.Return(payload);
                            break;
                    }
                }
            }
            catch (IOException)          { /* normal socket close / reset */ }
            catch (SocketException)      { /* reset by peer or local close */ }
            catch (ObjectDisposedException) { /* socket closed from our side (heartbeat timeout / OnDestroy) */ }
            catch (Exception e)
            {
                if (!_cts.IsCancellationRequested)
                    Debug.LogWarning($"[StreamServer] Client {client.Id} receive error: {e.Message}");
            }

            RemoveClient(client.Id, "disconnected");
        }

        // ── Heartbeat loop ───────────────────────────────────────────────────────

        /// <summary>
        /// Runs on a single background thread for all clients.
        /// Every half-interval it checks each client's idle time:
        ///   • idle &gt; heartbeatTimeout  → disconnect (close TCP socket / remove UDP client).
        ///   • TCP:  idle &gt; heartbeatInterval → send a Heartbeat ping.
        ///   • UDP:  always send a keepalive ping every cycle so the client can detect
        ///           server loss (UDP has no socket-level disconnect signal).
        /// Receiving ANY packet — including a data frame — resets the idle clock.
        /// Also cleans up stale fragment reassembly buffers in UDP mode.
        /// </summary>
        private void HeartbeatLoop()
        {
            long intervalMs = (long)(heartbeatInterval * 1000);
            long timeoutMs  = (long)(heartbeatTimeout  * 1000);
            int  sleepMs    = (int)Math.Max(500, intervalMs / 2);
            byte[] ping     = NetworkProtocol.BuildHeartbeatPacket();

            while (!_cts.IsCancellationRequested)
            {
                Thread.Sleep(sleepMs);

                long now = NowMs;
                foreach (var kv in _clients)
                {
                    ConnectedClient client = kv.Value;
                    long idleMs = now - Interlocked.Read(ref client.LastReceivedMs);

                    if (idleMs > timeoutMs)
                    {
                        if (transportMode == TransportMode.TCP)
                        {
                            Debug.Log($"[StreamServer] Client {client.Id} heartbeat timeout ({idleMs} ms idle) — closing.");
                            try { client.Tcp.Close(); } catch { }
                        }
                        else
                        {
                            RemoveClient(client.Id, $"heartbeat timeout ({idleMs} ms idle)");
                        }
                        continue;
                    }

                    if (transportMode == TransportMode.UDP)
                    {
                        // Always ping UDP clients so the client can monitor server liveness.
                        if (client.UdpEndPoint != null)
                        {
                            try
                            {
                                lock (_udpWriteLock)
                                    _udpReceiver.Send(ping, ping.Length, client.UdpEndPoint);
                            }
                            catch { /* receiver closed */ }
                        }
                    }
                    else if (idleMs > intervalMs)
                    {
                        // TCP: only ping idle clients.
                        try
                        {
                            lock (client.WriteLock)
                                client.Stream.Write(ping, 0, ping.Length);
                        }
                        catch { /* socket already closing */ }
                    }
                }

                // ── Stale fragment cleanup (UDP only) ────────────────────────────
                if (transportMode == TransportMode.UDP)
                {
                    const long staleMs = 2000; // 2 s — more than enough for LAN delivery
                    foreach (var kv in _fragmentBuffers)
                    {
                        if (now - kv.Value.CreatedMs > staleMs)
                        {
                            if (_fragmentBuffers.TryRemove(kv.Key, out var stale))
                                for (int i = 0; i < stale.TotalFragments; i++)
                                    if (stale.Chunks[i] != null)
                                        ArrayPool<byte>.Shared.Return(stale.Chunks[i]);
                        }
                    }
                }
            }
        }

        // ── Discovery loop ───────────────────────────────────────────────────────

        /// <summary>
        /// Responds to UDP broadcast probes with the listening port and transport mode
        /// so the client knows which transport to use: <c>GVST_HERE:port:tcp</c> or <c>GVST_HERE:port:udp</c>.
        /// </summary>
        private void DiscoveryLoop()
        {
            string transportStr = transportMode == TransportMode.UDP ? "udp" : "tcp";
            byte[] responseBytes = Encoding.ASCII.GetBytes(
                $"{NetworkProtocol.DiscoveryResponse}:{port}:{transportStr}");

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    IPEndPoint sender  = new IPEndPoint(IPAddress.Any, 0);
                    byte[]     data    = _discoveryUdp.Receive(ref sender);
                    string     message = Encoding.ASCII.GetString(data);

                    if (message == NetworkProtocol.DiscoveryRequest)
                    {
                        _discoveryUdp.Send(responseBytes, responseBytes.Length, sender);
                        Debug.Log($"[StreamServer] Discovery probe from {sender} — replied.");
                    }
                    else
                    {
                        Debug.LogWarning($"[StreamServer] Discovery: unexpected payload from {sender}: '{message}'");
                    }
                }
                catch (SocketException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    if (!_cts.IsCancellationRequested)
                        Debug.LogWarning($"[StreamServer] Discovery error: {e.Message}");
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        // ── UDP receive loop ─────────────────────────────────────────────────────

        /// <summary>
        /// Single-threaded UDP receive loop. Reads datagrams, maps sender endpoints to
        /// client IDs, reassembles fragments, and enqueues complete frames.
        /// </summary>
        private void UdpReceiveLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                byte[] data;
                IPEndPoint sender;
                try
                {
                    sender = new IPEndPoint(IPAddress.Any, 0);
                    data   = _udpReceiver.Receive(ref sender);
                }
                catch (SocketException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    if (!_cts.IsCancellationRequested)
                        Debug.LogWarning($"[StreamServer] UDP receive error: {e.Message}");
                    continue;
                }

                if (data.Length < NetworkProtocol.HeaderSize) continue;

                // ── Detect reliable sequence prefix ──────────────────────────────
                // Reliable datagram: [4-byte seqNum BE][GVST header + payload]
                // Unreliable:        [GVST header + payload]
                // Detection: GVST magic at offset 0 → unreliable; magic at offset 4 → reliable.
                uint reliableSeq = 0;
                bool isReliable  = false;

                bool magicAtZero = data[0] == NetworkProtocol.Magic[0]
                                && data[1] == NetworkProtocol.Magic[1]
                                && data[2] == NetworkProtocol.Magic[2]
                                && data[3] == NetworkProtocol.Magic[3];

                if (!magicAtZero
                    && data.Length >= NetworkProtocol.ReliableSeqSize + NetworkProtocol.HeaderSize
                    && data[4] == NetworkProtocol.Magic[0]
                    && data[5] == NetworkProtocol.Magic[1]
                    && data[6] == NetworkProtocol.Magic[2]
                    && data[7] == NetworkProtocol.Magic[3])
                {
                    isReliable  = true;
                    reliableSeq = (uint)((data[0] << 24) | (data[1] << 16)
                                       | (data[2] << 8)  |  data[3]);
                    // Strip the 4-byte prefix so all downstream code works with offset 0.
                    byte[] stripped = new byte[data.Length - NetworkProtocol.ReliableSeqSize];
                    Buffer.BlockCopy(data, NetworkProtocol.ReliableSeqSize,
                                    stripped, 0, stripped.Length);
                    data = stripped;
                }
                else if (!magicAtZero)
                {
                    continue; // unrecognised datagram
                }

                if (!NetworkProtocol.TryParseHeader(data, 0,
                        out var type, out _, out uint frameId, out int payloadSize))
                    continue;

                // Immediately acknowledge reliable datagrams so the client
                // can remove them from its retransmit buffer.
                if (isReliable)
                {
                    try
                    {
                        byte[] ack = NetworkProtocol.BuildAckPacket(reliableSeq);
                        lock (_udpWriteLock)
                            _udpReceiver.Send(ack, ack.Length, sender);
                    }
                    catch { /* receiver closed */ }
                }

                // ── Map endpoint → client ID ─────────────────────────────────────
                string epKey = sender.ToString();
                ushort clientId;

                if (!_endpointToClient.TryGetValue(epKey, out clientId))
                {
                    // Unknown endpoint — only accept Connect packets.
                    if (type != NetworkProtocol.PacketType.Connect) continue;

                    if (_clients.Count >= maxClients)
                    {
                        Debug.LogWarning($"[StreamServer] Max clients ({maxClients}) reached — ignoring UDP from {sender}.");
                        continue;
                    }

                    lock (_idLock) clientId = _nextClientId++;
                    var client = new ConnectedClient(clientId, sender, NowMs);
                    _clients[clientId] = client;
                    _endpointToClient[epKey] = clientId;

                    Debug.Log($"[StreamServer] UDP client {clientId} connected from {sender}.");
                    var capId   = clientId;
                    var capAddr = sender.Address;
                    MainThreadDispatcher.Enqueue(() => OnClientConnected?.Invoke(capId, capAddr));
                    continue; // Connect has no payload to process
                }

                // Update liveness
                if (_clients.TryGetValue(clientId, out var cc))
                    Interlocked.Exchange(ref cc.LastReceivedMs, NowMs);

                // ── Dispatch ──────────────────────────────────────────────────────
                switch (type)
                {
                    case NetworkProtocol.PacketType.VideoFrame:
                    case NetworkProtocol.PacketType.H264Frame:
                    {
                        int off = NetworkProtocol.HeaderSize;
                        int len = data.Length - off;
                        if (len > 0)
                        {
                            byte[] payload = ArrayPool<byte>.Shared.Rent(len);
                            Buffer.BlockCopy(data, off, payload, 0, len);
                            EnqueueFrame(clientId, frameId, type, payload, len);
                        }
                        break;
                    }

                    case NetworkProtocol.PacketType.Fragment:
                    {
                        int subOff = NetworkProtocol.HeaderSize;
                        if (!NetworkProtocol.TryParseFragmentSubHeader(data, subOff,
                                out var origType, out byte fragIndex, out byte fragTotal))
                            break;

                        int chunkOff = subOff + NetworkProtocol.FragSubHeaderSize;
                        int chunkLen = payloadSize - NetworkProtocol.FragSubHeaderSize;
                        if (chunkLen > 0)
                            HandleFragment(clientId, frameId, origType,
                                           fragIndex, fragTotal, data, chunkOff, chunkLen);
                        break;
                    }

                    case NetworkProtocol.PacketType.Disconnect:
                        RemoveClient(clientId, "sent disconnect");
                        break;

                    case NetworkProtocol.PacketType.Heartbeat:
                        try
                        {
                            byte[] pong = NetworkProtocol.BuildHeartbeatPacket();
                            lock (_udpWriteLock)
                                _udpReceiver.Send(pong, pong.Length, sender);
                        }
                        catch { /* receiver closed */ }
                        break;
                }
            }
        }

        /// <summary>
        /// Stores a single fragment chunk and, when all chunks for a frame arrive,
        /// reassembles them into a complete payload and enqueues it.
        /// </summary>
        private void HandleFragment(ushort clientId, uint frameId,
            NetworkProtocol.PacketType origType, byte fragIndex, byte fragTotal,
            byte[] data, int chunkOffset, int chunkLen)
        {
            var key = (clientId, frameId);
            var buf = _fragmentBuffers.GetOrAdd(key,
                _ => new FragmentBuffer(origType, fragTotal, NowMs));

            if (buf.TotalFragments != fragTotal || fragIndex >= fragTotal) return;
            if (buf.Chunks[fragIndex] != null) return; // duplicate

            byte[] chunk = ArrayPool<byte>.Shared.Rent(chunkLen);
            Buffer.BlockCopy(data, chunkOffset, chunk, 0, chunkLen);
            buf.Chunks[fragIndex]      = chunk;
            buf.ChunkLengths[fragIndex] = chunkLen;

            if (Interlocked.Increment(ref buf.ReceivedCount) == fragTotal)
            {
                // All fragments arrived — reassemble.
                _fragmentBuffers.TryRemove(key, out _);

                int totalLen = 0;
                for (int i = 0; i < fragTotal; i++) totalLen += buf.ChunkLengths[i];

                byte[] assembled = ArrayPool<byte>.Shared.Rent(totalLen);
                int offset = 0;
                for (int i = 0; i < fragTotal; i++)
                {
                    Buffer.BlockCopy(buf.Chunks[i], 0, assembled, offset, buf.ChunkLengths[i]);
                    offset += buf.ChunkLengths[i];
                    ArrayPool<byte>.Shared.Return(buf.Chunks[i]);
                }

                EnqueueFrame(clientId, frameId, buf.OriginalType, assembled, totalLen);
            }
        }

        // ── Frame / client helpers ───────────────────────────────────────────────

        private void EnqueueFrame(ushort clientId, uint frameId,
            NetworkProtocol.PacketType type, byte[] payload, int payloadSize)
        {
            // Safety cap: drop oldest decoded frame when queue is full.
            while (FrameQueue.Count >= frameQueueCap)
            {
                if (FrameQueue.TryDequeue(out ClientFrameData old) && old.PixelData != null)
                    ArrayPool<byte>.Shared.Return(old.PixelData);
            }
            FrameQueue.Enqueue(new ClientFrameData
            {
                ClientId      = clientId,
                FrameId       = frameId,
                PacketType    = type,
                PixelData     = payload,
                PayloadLength = payloadSize,
            });
        }

        private void RemoveClient(ushort clientId, string reason)
        {
            // TryRemove is atomic — only the first caller (receive thread vs. heartbeat) proceeds.
            if (!_clients.TryRemove(clientId, out ConnectedClient client)) return;

            // TCP cleanup
            if (client.Tcp != null)
                try { client.Tcp.Close(); } catch { }

            // UDP cleanup: endpoint mapping + pending fragment buffers
            if (client.UdpEndPoint != null)
            {
                _endpointToClient.TryRemove(client.UdpEndPoint.ToString(), out _);

                var staleKeys = new System.Collections.Generic.List<(ushort, uint)>();
                foreach (var key in _fragmentBuffers.Keys)
                    if (key.Item1 == clientId) staleKeys.Add(key);
                foreach (var key in staleKeys)
                    if (_fragmentBuffers.TryRemove(key, out var buf))
                        for (int i = 0; i < buf.TotalFragments; i++)
                            if (buf.Chunks[i] != null)
                                ArrayPool<byte>.Shared.Return(buf.Chunks[i]);
            }

            Debug.Log($"[StreamServer] Client {clientId} {reason}.");
            MainThreadDispatcher.Enqueue(() => OnClientDisconnected?.Invoke(clientId));
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes from <paramref name="stream"/> into
        /// <paramref name="buffer"/> starting at offset 0, retrying until all bytes arrive.
        /// Returns <c>false</c> if the connection closes before all bytes are read.
        /// TCP Read() is only guaranteed to return ≥1 byte — this wrapper handles partial reads.
        /// </summary>
        private static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n == 0) return false; // graceful close
                read += n;
            }
            return true;
        }

        /// <summary>
        /// Looks up the StreamServer client ID for a currently-connected client by IP address.
        /// Used by <see cref="ViewDecoder.SetAutoClaimForIP"/> to catch the case where the
        /// StreamServer TCP connection arrived before the caller had a chance to arm the decoder
        /// (common when the Netcode handshake follows the streaming TCP connect by >1 frame).
        /// Returns <c>true</c> and sets <paramref name="clientId"/> when a match is found.
        /// </summary>
        public bool TryGetClientIdByAddress(IPAddress address, out ushort clientId)
        {
            string target = address.ToString();
            foreach (var kv in _clients)
            {
                if (kv.Value.Address.ToString() == target)
                {
                    clientId = kv.Key;
                    return true;
                }
            }
            clientId = 0;
            return false;
        }
    }
}
