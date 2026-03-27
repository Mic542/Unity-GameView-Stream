using System;

namespace GameViewStream
{
    /// <summary>
    /// Shared binary protocol for frame streaming between Android VR client and PC server.
    ///
    /// Packet layout (15-byte header + payload):
    /// [0..3]  Magic bytes  : 0x47 0x56 0x53 0x54  ("GVST")
    /// [4]     PacketType   : 1 byte
    /// [5..6]  ClientId     : uint16 big-endian
    /// [7..10] FrameId      : uint32 big-endian (wraps around)
    /// [11..14]PayloadSize  : int32  big-endian
    /// [15..N] Payload      : data (see PacketType)
    ///
    /// VideoFrame payload : raw JPEG bytes (TurboJpeg / ImageConversion).
    /// H264Frame payload  : complete H.264 Annex-B NAL unit sequence.
    /// Connect / Disconnect / Heartbeat: no payload.
    /// </summary>
    public static class NetworkProtocol
    {
        // ── Constants ───────────────────────────────────────────────────────────

        public const int  DefaultPort    = 9000;
        public const int  DiscoveryPort  = 9001;   // UDP port used for server auto-discovery
        public const int  HeaderSize     = 15;     // 4 magic + 1 type + 2 id + 4 frameId + 4 size
        public const int  MaxFrameSize   = 8 * 1024 * 1024; // 8 MB sanity cap per reassembled frame

        /// <summary>UDP datagram the client broadcasts to find a server.</summary>
        public const string DiscoveryRequest  = "GVST_DISC";
        /// <summary>Prefix the server sends back; full message is "GVST_HERE:&lt;port&gt;".</summary>
        public const string DiscoveryResponse = "GVST_HERE";

        public static readonly byte[] Magic = { 0x47, 0x56, 0x53, 0x54 }; // "GVST"

        // ── Packet types ────────────────────────────────────────────────────────

        public enum PacketType : byte
        {
            Connect      = 0x00,  // client→server: announce presence (no payload)
            VideoFrame   = 0x01,  // JPEG frame (TurboJpeg compressed)
            Disconnect   = 0x02,  // client→server: graceful goodbye
            H264Frame    = 0x03,  // complete H.264 Annex-B NAL sequence
            Heartbeat    = 0x05,  // server→client ping / client→server pong (no payload)
        }

        // ── Builder helpers ──────────────────────────────────────────────────────

        private static void WriteHeader(byte[] packet, PacketType type, ushort clientId, uint frameId, int payloadSize)
        {
            packet[0] = Magic[0]; packet[1] = Magic[1]; packet[2] = Magic[2]; packet[3] = Magic[3];
            packet[4] = (byte)type;
            packet[5] = (byte)(clientId >> 8);
            packet[6] = (byte)(clientId & 0xFF);
            packet[7]  = (byte)(frameId >> 24);
            packet[8]  = (byte)(frameId >> 16);
            packet[9]  = (byte)(frameId >> 8);
            packet[10] = (byte)(frameId & 0xFF);
            packet[11] = (byte)(payloadSize >> 24);
            packet[12] = (byte)(payloadSize >> 16);
            packet[13] = (byte)(payloadSize >> 8);
            packet[14] = (byte)(payloadSize & 0xFF);
        }

        // ── Builders ─────────────────────────────────────────────────────────────

        /// <summary>Serialise a JPEG frame into a fully framed packet ready to send.</summary>
        public static byte[] BuildFramePacket(ushort clientId, uint frameId, byte[] jpegData)
        {
            if (jpegData == null) throw new ArgumentNullException(nameof(jpegData));

            byte[] packet = new byte[HeaderSize + jpegData.Length];
            WriteHeader(packet, PacketType.VideoFrame, clientId, frameId, jpegData.Length);
            Buffer.BlockCopy(jpegData, 0, packet, HeaderSize, jpegData.Length);
            return packet;
        }

        /// <summary>Serialise an H.264 Annex-B NAL unit sequence into a single framed packet.</summary>
        public static byte[] BuildH264Packet(ushort clientId, uint frameId, byte[] h264Data, int length)
        {
            if (h264Data == null) throw new ArgumentNullException(nameof(h264Data));

            byte[] pkt = new byte[HeaderSize + length];
            WriteHeader(pkt, PacketType.H264Frame, clientId, frameId, length);
            Buffer.BlockCopy(h264Data, 0, pkt, HeaderSize, length);
            return pkt;
        }

        /// <summary>Serialise a Connect packet (no payload). Sent by the client on startup.</summary>
        public static byte[] BuildConnectPacket()
        {
            byte[] packet = new byte[HeaderSize];
            WriteHeader(packet, PacketType.Connect, 0, 0, 0);
            return packet;
        }

        /// <summary>Serialise a Disconnect packet (no payload).</summary>
        public static byte[] BuildDisconnectPacket(ushort clientId)
        {
            byte[] packet = new byte[HeaderSize];
            WriteHeader(packet, PacketType.Disconnect, clientId, 0, 0);
            return packet;
        }

        /// <summary>
        /// Serialise a Heartbeat packet (no payload).
        /// Sent by the server to ping idle clients; the client echoes it back as a pong.
        /// Receiving ANY packet from the client — including a data frame — also counts as liveness.
        /// </summary>
        public static byte[] BuildHeartbeatPacket()
        {
            byte[] packet = new byte[HeaderSize];
            WriteHeader(packet, PacketType.Heartbeat, 0, 0, 0);
            return packet;
        }

        // ── Parser ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Parse a header from <paramref name="buffer"/> at <paramref name="offset"/>.
        /// Returns <c>false</c> if the magic bytes don't match or the buffer is too short.
        /// </summary>
        public static bool TryParseHeader(
            byte[]      buffer,
            int         offset,
            out PacketType type,
            out ushort  clientId,
            out uint    frameId,
            out int     payloadSize)
        {
            type        = PacketType.VideoFrame;
            clientId    = 0;
            frameId     = 0;
            payloadSize = 0;

            if (buffer == null || offset + HeaderSize > buffer.Length)
                return false;

            // Validate magic
            for (int i = 0; i < 4; i++)
                if (buffer[offset + i] != Magic[i]) return false;

            type        = (PacketType)buffer[offset + 4];
            clientId    = (ushort)((buffer[offset + 5] << 8) | buffer[offset + 6]);
            frameId     = (uint)  ((buffer[offset + 7] << 24) | (buffer[offset + 8] << 16)
                                 | (buffer[offset + 9] << 8)  |  buffer[offset + 10]);
            payloadSize = (buffer[offset + 11] << 24) | (buffer[offset + 12] << 16)
                        | (buffer[offset + 13] << 8)  |  buffer[offset + 14];

            return payloadSize >= 0 && payloadSize <= MaxFrameSize;
        }
    }
}
