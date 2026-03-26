using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// Setup:
    ///   1. Place on a persistent GameObject together with <see cref="ViewDecoder"/>.
    ///   2. Optionally override <see cref="port"/> and <see cref="maxClients"/> in the Inspector.
    ///   3. Make sure the server's firewall allows inbound TCP on the chosen port.
    /// </summary>
    public sealed class StreamServer : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Tooltip("UDP port to listen on. Must match ViewEncoder.serverPort on every client.")]
        [SerializeField] private int port       = NetworkProtocol.DefaultPort;
        [Tooltip("Hard cap on simultaneous connections. A new connection beyond this is rejected.")]
        [SerializeField] private int maxClients = 16;

        [Tooltip("Respond to UDP broadcast discovery probes from clients on the LAN.")]
        [SerializeField] private bool enableDiscovery  = true;
        [Tooltip("UDP port to listen on for discovery probes. Must match ViewEncoder.discoveryPort.")]
        [SerializeField] private int  discoveryPort    = NetworkProtocol.DiscoveryPort;
        [Tooltip("Seconds of silence before a client is considered disconnected. "
               + "UDP has no connection state so this is the only way to detect a gone client.\n"
               + "Set higher (10-15s) for mobile clients that may pause briefly during WiFi roam.")]
        [SerializeField] private float clientTimeout   = 10f;

        [Tooltip("Maximum frames held in the shared queue waiting for decode workers. "
               + "Oldest frames are dropped when this is reached to prevent unbounded memory use "
               + "during long sessions. Raise if workers fall behind on high client counts.")]
        [SerializeField] private int   frameQueueCap   = 256;

        // ── Events (raised on the main thread via MainThreadDispatcher) ──────────

        /// <summary>Raised on the main thread when a new client connects.</summary>
        public event Action<ushort, IPAddress> OnClientConnected;
        /// <summary>Raised on the main thread when a client disconnects or errors.</summary>
        public event Action<ushort>            OnClientDisconnected;

        // ── Public state ─────────────────────────────────────────────────────────

        /// <summary>Frames received from all clients, ready to decode on the main thread.</summary>
        public readonly ConcurrentQueue<ClientFrameData> FrameQueue = new ConcurrentQueue<ClientFrameData>();

        /// <summary>Number of clients currently connected.</summary>
        public int ConnectedClientCount
        {
            get { lock (_clientsLock) return _endpointToId.Count; }
        }

        // ── Private state ──────────────────────────────────────────────────────────

        [SerializeField] private int _connectedClients;

        private UdpClient _udpServer;
        private Thread    _receiveThread;
        private Thread    _discoveryThread;
        private UdpClient _discoveryUdp;
        private CancellationTokenSource _cts;

        // Clients identified by UDP source endpoint
        private readonly Dictionary<string, ushort>     _endpointToId = new Dictionary<string, ushort>();
        private readonly Dictionary<ushort, IPEndPoint> _idToEndpoint = new Dictionary<ushort, IPEndPoint>();
        private readonly Dictionary<ushort, long>       _lastSeenTick = new Dictionary<ushort, long>();
        private readonly object _clientsLock = new object();
        private ushort _nextClientId = 1;

        // Monotonic ms clock usable from any thread on all Unity runtime versions.
        // Stopwatch has been in .NET since 2.0 — unlike TickCount64 it is universally available.
        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private static long NowMs => _clock.ElapsedMilliseconds;

        // ── Unity lifecycle ──────────────────────────────────────────────────────

        private void Start()
        {
            _cts       = new CancellationTokenSource();
            _udpServer = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            // Default OS buffer (~256 KB) drops burst packets from many simultaneous clients.
            // 16 MB absorbs ~100 back-to-back 960×540 JPEG frames safely.
            _udpServer.Client.ReceiveBufferSize = 16 * 1024 * 1024;

            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "StreamServer-Receive" };
            _receiveThread.Start();

            if (enableDiscovery)
            {
                _discoveryUdp    = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
                _discoveryThread = new Thread(DiscoveryLoop) { IsBackground = true, Name = "StreamServer-Discovery" };
                _discoveryThread.Start();
                Debug.Log($"[StreamServer] Discovery listening on UDP:{discoveryPort}.");
            }

            Debug.Log($"[StreamServer] Listening on UDP *:{port} (max {maxClients} clients).");
        }

        private void Update()
        {
            _connectedClients = ConnectedClientCount;

            // Timeout clients that have stopped sending
            long          now      = NowMs;
            long          limitMs  = (long)(clientTimeout * 1000);
            List<ushort>  timedOut = null;
            lock (_clientsLock)
            {
                foreach (var kv in _lastSeenTick)
                {
                    if (now - kv.Value > limitMs)
                    {
                        if (timedOut == null) timedOut = new List<ushort>();
                        timedOut.Add(kv.Key);
                    }
                }
            }
            if (timedOut != null)
                foreach (ushort id in timedOut)
                    RemoveClient(id, "timed out");
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _udpServer?.Close();
            _discoveryUdp?.Close();
            lock (_clientsLock)
            {
                _endpointToId.Clear();
                _idToEndpoint.Clear();
                _lastSeenTick.Clear();
            }
        }

        // ── Discovery loop (UDP) ─────────────────────────────────────────────────

        /// <summary>
        /// Listens for UDP broadcast probes (<see cref="NetworkProtocol.DiscoveryRequest"/>)
        /// and replies with <c>GVST_HERE:&lt;tcpPort&gt;</c> so clients can find the server
        /// without a hard-coded IP address.
        /// </summary>
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
                        Debug.Log($"[StreamServer] Discovery probe received from {sender} — sent reply '{Encoding.ASCII.GetString(responseBytes)}'.");
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

        // ── Receive loop (UDP) ──────────────────────────────────────────────────────

        /// <summary>
        /// Single receive thread for all clients. Clients are identified by UDP source endpoint.
        /// The first datagram from a new endpoint fires <see cref="OnClientConnected"/>.
        /// </summary>
        private void ReceiveLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[]     data   = _udpServer.Receive(ref remote);

                    if (!NetworkProtocol.TryParseHeader(
                            data, 0,
                            out NetworkProtocol.PacketType type,
                            out ushort packetClientId,  // clientId embedded by the encoder
                            out uint   frameId,
                            out int    payloadSize))
                    {
                        Debug.LogWarning($"[StreamServer] Bad packet from {remote} ({data.Length} B) — ignored.");
                        continue;
                    }

                    string endpointKey = remote.ToString();
                    ushort clientId;
                    bool   isNew = false;

                    lock (_clientsLock)
                    {
                        if (!_endpointToId.TryGetValue(endpointKey, out clientId))
                        {
                            // New endpoint.  Check if the in-packet clientId matches an existing
                            // client (same device, different source port — common after WiFi roam
                            // or mobile NAT rebind).  If so, migrate the old endpoint → new one
                            // instead of allocating a fresh clientId.  This prevents a false
                            // disconnect/reconnect cycle that would destroy the H264Decoder and
                            // leave the display blank until a new keyframe arrives.
                            if (packetClientId != 0 && _idToEndpoint.ContainsKey(packetClientId))
                            {
                                // Migrate: remove old endpoint mapping, add new one
                                IPEndPoint oldEp = _idToEndpoint[packetClientId];
                                _endpointToId.Remove(oldEp.ToString());
                                _endpointToId[endpointKey]      = packetClientId;
                                _idToEndpoint[packetClientId]   = remote;
                                _lastSeenTick[packetClientId]   = NowMs;
                                clientId = packetClientId;
                                // Not "new" — no connect event needed, just endpoint migration
                            }
                            else
                            {
                                if (_endpointToId.Count >= maxClients)
                                {
                                    Debug.LogWarning($"[StreamServer] Max clients reached — ignoring {remote}.");
                                    continue;
                                }
                                clientId = _nextClientId++;
                                _endpointToId[endpointKey] = clientId;
                                _idToEndpoint[clientId]    = remote;
                                _lastSeenTick[clientId]    = NowMs;
                                isNew = true;
                            }
                        }
                        else
                        {
                            _lastSeenTick[clientId] = NowMs;
                        }
                    }

                    if (isNew)
                    {
                        Debug.Log($"[StreamServer] Client {clientId} connected from {remote}.");
                        var capId   = clientId;
                        var capAddr = remote.Address;
                        MainThreadDispatcher.Enqueue(() => OnClientConnected?.Invoke(capId, capAddr));
                    }

                    switch (type)
                    {
                        case NetworkProtocol.PacketType.VideoFrame:
                        case NetworkProtocol.PacketType.H264Frame:
                        case NetworkProtocol.PacketType.H264Fragment:
                        {
                            if (payloadSize <= 0 || NetworkProtocol.HeaderSize + payloadSize > data.Length)
                                break;

                            // Cap the queue to protect memory during long sessions.
                            // When full, drop the oldest frame and return its pooled buffer before enqueueing.
                            while (FrameQueue.Count >= frameQueueCap)
                            {
                                if (FrameQueue.TryDequeue(out ClientFrameData old) && old.PixelData != null)
                                    ArrayPool<byte>.Shared.Return(old.PixelData);
                            }

                            byte[] payload = ArrayPool<byte>.Shared.Rent(payloadSize);
                            Buffer.BlockCopy(data, NetworkProtocol.HeaderSize, payload, 0, payloadSize);
                            FrameQueue.Enqueue(new ClientFrameData
                            {
                                ClientId      = clientId,
                                FrameId       = frameId,
                                PacketType    = type,
                                PixelData     = payload,
                                PayloadLength = payloadSize,
                            });
                            break;
                        }

                        case NetworkProtocol.PacketType.Connect:
                            break; // Registration already handled above.

                        case NetworkProtocol.PacketType.Disconnect:
                            RemoveClient(clientId, "sent disconnect");
                            break;
                    }
                }
                catch (SocketException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    if (!_cts.IsCancellationRequested)
                        Debug.LogWarning($"[StreamServer] Receive error: {e.Message}");
                }
            }
        }

        // ── Client lifecycle ─────────────────────────────────────────────────────────

        private void RemoveClient(ushort clientId, string reason)
        {
            lock (_clientsLock)
            {
                if (!_idToEndpoint.TryGetValue(clientId, out IPEndPoint ep)) return;
                _endpointToId.Remove(ep.ToString());
                _idToEndpoint.Remove(clientId);
                _lastSeenTick.Remove(clientId);
            }
            Debug.Log($"[StreamServer] Client {clientId} {reason}.");
            MainThreadDispatcher.Enqueue(() => OnClientDisconnected?.Invoke(clientId));
        }
    }
}