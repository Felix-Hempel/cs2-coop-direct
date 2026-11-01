using System;
using System.Diagnostics;
using System.Threading;

namespace SkylineTogether.Core.Transport.Direct
{
    /// <summary>
    /// The direct-mode self-test (fork, probe <c>P-DIRECT</c>, in place of the Steam socket pair): two
    /// <see cref="DirectTransport"/>s on 127.0.0.1, the host on port 0 (any free port), connect, then one message on every
    /// <see cref="Lane"/> from the client to the host and back, each checked byte for byte. Blocks the calling thread until
    /// it passed or <c>timeoutMilliseconds</c> ran out (both transports are disposed either way). Returns null when it
    /// passed, else what failed (English, for the probe line).
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public static class DirectSelfTest
    {
        private static readonly Lane[] Lanes = { Lane.Reliable, Lane.Unreliable, Lane.Bulk };

        public static string Run(ILog log, IClock clock, int timeoutMilliseconds)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            var watch = Stopwatch.StartNew();
            using (var host = new DirectTransport(log, clock, "self-test host"))
            using (var client = new DirectTransport(log, clock, "self-test client"))
            {
                host.Listen(new ListenOptions { IpPort = 0, AllowIp = true, MaxConnections = 1 });
                var hostSink = new Sink();
                var clientSink = new Sink();
                IConnection toHost = client.Connect(new Endpoint { Host = "127.0.0.1", Port = host.BoundPort });
                Func<bool> expired = () => watch.ElapsedMilliseconds > timeoutMilliseconds;
                if (!PollUntil(host, hostSink, client, clientSink, () => hostSink.Connection != null && clientSink.Connection != null, expired))
                {
                    return hostSink.Failure ?? clientSink.Failure ?? "no loopback connection within " + timeoutMilliseconds + " ms";
                }
                // a stranger gets small limits until the session admits it; this pair is trusted
                (hostSink.Connection as IAdmissionAware)?.OnAdmitted();
                foreach (Lane lane in Lanes)
                {
                    byte[] message = Message(lane);
                    Delivery delivery = lane == Lane.Unreliable ? Delivery.Unreliable : Delivery.ReliableOrdered;
                    SendResult sent = toHost.Send(lane, delivery, message, 0, message.Length);
                    if (sent != SendResult.Ok) return "sending on lane " + lane + " failed: " + sent;
                    if (!PollUntil(host, hostSink, client, clientSink, () => hostSink.Received(lane) != null, expired))
                    {
                        return hostSink.Failure ?? clientSink.Failure ?? "the host received nothing on lane " + lane + " within " + timeoutMilliseconds + " ms";
                    }
                    if (!Same(hostSink.Received(lane), message)) return "the host received other bytes on lane " + lane;
                    sent = hostSink.Connection.Send(lane, delivery, message, 0, message.Length);
                    if (sent != SendResult.Ok) return "the host's reply on lane " + lane + " failed: " + sent;
                    if (!PollUntil(host, hostSink, client, clientSink, () => clientSink.Received(lane) != null, expired))
                    {
                        return hostSink.Failure ?? clientSink.Failure ?? "the client received nothing on lane " + lane + " within " + timeoutMilliseconds + " ms";
                    }
                    if (!Same(clientSink.Received(lane), message)) return "the client received other bytes on lane " + lane;
                }
                return null;
            }
        }

        private static byte[] Message(Lane lane)
        {
            var message = new byte[64 + (int)lane];
            for (int i = 0; i < message.Length; i++) message[i] = (byte)(i * 31 + (int)lane * 7 + 1);
            return message;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static bool PollUntil(DirectTransport host, Sink hostSink, DirectTransport client, Sink clientSink, Func<bool> done, Func<bool> expired)
        {
            while (true)
            {
                host.Poll(hostSink);
                client.Poll(clientSink);
                if (done()) return true;
                if (hostSink.Failure != null || clientSink.Failure != null || expired()) return false;
                Thread.Sleep(1);
            }
        }

        private sealed class Sink : ITransportEventSink
        {
            private readonly byte[][] _received = new byte[3][];

            public IConnection Connection { get; private set; }

            public string Failure { get; private set; }

            public byte[] Received(Lane lane) => _received[(int)lane];

            public void OnConnected(IConnection connection) => Connection = connection;

            public void OnDisconnected(IConnection connection, int reason, string text) => Failure = "the loopback connection closed (" + reason + (string.IsNullOrEmpty(text) ? "" : ": " + text) + ")";

            public void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
            {
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                _received[(int)lane] = copy;
            }
        }
    }
}
