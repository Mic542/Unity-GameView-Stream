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
    /// Listens for incoming TCP connections from Android VR clients,
    /// manages per-client receive threads, and exposes a thread-safe frame queue
    /// for <see cref="ViewDecoder"/> to drain on the main thread.
    ///
    /// TCP benefits over the previous UDP implementation:
    ///   • False disconnects eliminated — a socket exception, not a timer, indicates loss.
    ///   • No fragmentation needed — a single Write() carries any H.264 frame size.
    ///   • Heartbeat ping/pong detects silent drops (WiFi pull, power management) in ~15 s.
    ///   • Scales well to 50 clients: one background thread per client (~512 KB stack each).
    ///
    /// Setup:
    ///   1. Place on a persistent GameObject together with <see cref="ViewDecoder"/>.
    ///   2. Optionally override <see cref="port"/> and <see cref="maxClients"/> in the Inspector.
    ///   3. Make sure the server firewall allows inbound TCP on the chosen port.
    ///   4. The client-side ViewEncoder must also use TCP (connect + stream over the same port).
    /// </summary>
    public sealed class StreamServer : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Tooltip("TCP port to listen on. Must match ViewEncoder.serverPort on every client.")]
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
            public readonly TcpClient     Tcp;
            public readonly NetworkStream Stream;
            public readonly IPAddress     Address;
            // Not volatile: long is 64-bit and volatile long is illegal in C#.
            // Use Interlocked.Exchange / Interlocked.Read for thread-safe access.
            public long                   LastReceivedMs;
            /// <summary>Serialises concurrent writes (heartbeat thread vs. any future server→client message).</summary>
            public readonly object        WriteLock = new object();

            public ConnectedClient(ushort id, TcpClient tcp, long nowMs)
            {
                Id             = id;
                Tcp            = tcp;
                Stream         = tcp.GetStream();
                Address        = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
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

        private ushort     _nextClientId = 1;
        private readonly object _idLock  = new object();

        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private static long NowMs => _clock.ElapsedMilliseconds;

        // ── Unity lifecycle ──────────────────────────────────────────────────────

        private void Start()
        {
            _cts      = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(backlog: maxClients);

            _acceptThread = new Thread(AcceptLoop)
                { IsBackground = true, Name = "StreamServer-Accept" };
            _acceptThread.Start();

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

            Debug.Log($"[StreamServer] Listening on TCP *:{port} (max {maxClients} clients).");
        }

        private void Update()
        {
            _connectedClients = ConnectedClientCount;
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            // Stop the listener — unblocks AcceptTcpClient() which throws SocketException.
            _listener?.Stop();
            _discoveryUdp?.Close();
            // Close all client sockets — unblocks each ReadExact() via IOException.
            foreach (var kv in _clients)
                try { kv.Value.Tcp.Close(); } catch { }
            _clients.Clear();
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
        ///   • idle &gt; heartbeatTimeout  → close the socket (triggers receive-thread disconnect).
        ///   • idle &gt; heartbeatInterval → send a Heartbeat ping so the client knows we're watching.
        /// Receiving ANY packet — including a data frame — resets the idle clock.
        /// </summary>
        private void HeartbeatLoop()
        {
            long intervalMs = (long)(heartbeatInterval * 1000);
            long timeoutMs  = (long)(heartbeatTimeout  * 1000);
            // Sleep half the interval to stay responsive; e.g. 2.5 s sleep for 5 s interval.
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
                        // Silent disconnect: close the socket so ReadExact() throws → receive thread fires removal.
                        Debug.Log($"[StreamServer] Client {client.Id} heartbeat timeout ({idleMs} ms idle) — closing.");
                        try { client.Tcp.Close(); } catch { }
                    }
                    else if (idleMs > intervalMs)
                    {
                        // Send a ping. The client must echo it back (or just send any data) before timeoutMs.
                        try
                        {
                            lock (client.WriteLock)
                                client.Stream.Write(ping, 0, ping.Length);
                        }
                        catch { /* socket already closing */ }
                    }
                }
            }
        }

        // ── Discovery loop (UDP, unchanged) ─────────────────────────────────────

        private void DiscoveryLoop()
        {
            byte[] responseBytes = Encoding.ASCII.GetBytes($"{NetworkProtocol.DiscoveryResponse}:{port}");

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
            try { client.Tcp.Close(); } catch { }
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
