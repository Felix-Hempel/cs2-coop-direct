using System;
using System.Text;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Transport.Direct
{
    /// <summary>Frame types of the direct TCP stream (<see cref="DirectFraming"/>).</summary>
    internal enum DirectFrameType : byte
    {
        /// <summary>One application message; the header's lane byte is its <see cref="Lane"/>.</summary>
        Data = 0,
        /// <summary>Keepalive with the sender's u64 clock ticks; answered by <see cref="Pong"/>.</summary>
        Ping = 1,
        /// <summary>The echoed ticks of a <see cref="Ping"/> (round-trip time).</summary>
        Pong = 2,
        /// <summary>The sender ends the connection: i32 reason, UTF-8 text of at most 256 bytes.</summary>
        Close = 3
    }

    /// <summary>
    /// Wire format of <see cref="DirectTransport"/> (fork: TCP direct IP without Steam). Both ends first send the 4-byte
    /// ASCII preamble <c>STD1</c>; a host that refuses a connection sends a <see cref="DirectFrameType.Close"/> frame
    /// instead. Then frames follow in both directions: <c>u8 type | u8 lane | u32 LE length | payload</c>. Control frames
    /// carry lane 0. A receiver checks the header against <see cref="MaxPayloadBytes"/> before it allocates or reads the
    /// payload. Pure; thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    internal static class DirectFraming
    {
        public const int PreambleBytes = 4;
        public const int HeaderBytes = 6;
        public const int PingPayloadBytes = 8;
        public const int MaxCloseTextBytes = 256;
        public const int MaxClosePayloadBytes = 4 + MaxCloseTextBytes;

        /// <summary>
        /// Largest frame a host reads from a connection before the session admitted it: the size of a <c>Hello</c>
        /// (<see cref="Limits.MaxBodyBytes"/> of <see cref="MessageKind.Hello"/>, 64 KiB; Limits has no named constant).
        /// </summary>
        public const int PreAdmissionMaxFrameBytes = 64 * 1024;

        private static readonly byte[] PreambleValue = { (byte)'S', (byte)'T', (byte)'D', (byte)'1' };

        public static void WritePreamble(byte[] buffer, int offset) => Buffer.BlockCopy(PreambleValue, 0, buffer, offset, PreambleBytes);

        public static bool IsPreamble(byte[] buffer, int offset)
        {
            for (int i = 0; i < PreambleBytes; i++)
            {
                if (buffer[offset + i] != PreambleValue[i]) return false;
            }
            return true;
        }

        public static void WriteHeader(byte[] buffer, int offset, DirectFrameType type, byte lane, int length)
        {
            buffer[offset] = (byte)type;
            buffer[offset + 1] = lane;
            WriteInt32(buffer, offset + 2, length);
        }

        public static void ReadHeader(byte[] buffer, int offset, out DirectFrameType type, out byte lane, out int length)
        {
            type = (DirectFrameType)buffer[offset];
            lane = buffer[offset + 1];
            length = ReadInt32(buffer, offset + 2);
        }

        /// <summary>
        /// The largest payload a frame with this header may carry, or -1 when the header is invalid (unknown type, a lane
        /// out of range, a control frame on a lane). Data frames get <see cref="PreAdmissionMaxFrameBytes"/> until the
        /// connection is admitted, then <see cref="Limits.MaxMessageBytes"/>.
        /// </summary>
        public static int MaxPayloadBytes(DirectFrameType type, byte lane, bool admitted)
        {
            switch (type)
            {
                case DirectFrameType.Data:
                    if (lane > (byte)Lane.Bulk) return -1;
                    return admitted ? Limits.MaxMessageBytes : PreAdmissionMaxFrameBytes;
                case DirectFrameType.Ping:
                case DirectFrameType.Pong:
                    return lane == 0 ? PingPayloadBytes : -1;
                case DirectFrameType.Close:
                    return lane == 0 ? MaxClosePayloadBytes : -1;
                default:
                    return -1;
            }
        }

        /// <summary>Whether <paramref name="length"/> is a valid payload size for the header (the fixed sizes of control frames included).</summary>
        public static bool IsValid(DirectFrameType type, byte lane, int length, bool admitted)
        {
            int max = MaxPayloadBytes(type, lane, admitted);
            if (max < 0 || length < 0 || length > max) return false;
            if (type == DirectFrameType.Ping || type == DirectFrameType.Pong) return length == PingPayloadBytes;
            if (type == DirectFrameType.Close) return length >= 4;
            return true;
        }

        /// <summary>The payload of a close frame; the text is cut at a character boundary to fit <see cref="MaxCloseTextBytes"/>.</summary>
        public static byte[] EncodeClose(int reason, string text)
        {
            string t = text ?? "";
            byte[] utf8 = Encoding.UTF8.GetBytes(t);
            int length = t.Length;
            while (utf8.Length > MaxCloseTextBytes && length > 0)
            {
                length--;
                if (length > 0 && char.IsLowSurrogate(t[length])) length--;
                utf8 = Encoding.UTF8.GetBytes(t.Substring(0, length));
            }
            var payload = new byte[4 + utf8.Length];
            WriteInt32(payload, 0, reason);
            Buffer.BlockCopy(utf8, 0, payload, 4, utf8.Length);
            return payload;
        }

        public static void DecodeClose(byte[] payload, int count, out int reason, out string text)
        {
            reason = ReadInt32(payload, 0);
            text = Encoding.UTF8.GetString(payload, 4, count - 4);
        }

        public static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        public static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
        }

        public static void WriteInt64(byte[] buffer, int offset, long value)
        {
            WriteInt32(buffer, offset, (int)value);
            WriteInt32(buffer, offset + 4, (int)(value >> 32));
        }

        public static long ReadInt64(byte[] buffer, int offset)
        {
            return (uint)ReadInt32(buffer, offset) | ((long)ReadInt32(buffer, offset + 4) << 32);
        }
    }
}
