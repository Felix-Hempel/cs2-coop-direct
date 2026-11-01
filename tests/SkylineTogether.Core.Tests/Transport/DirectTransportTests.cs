using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;
using SkylineTogether.Core.Transport.Direct;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>
    /// DirectTransport (fork: TCP direct IP without Steam): real sockets on 127.0.0.1, port 0. Connection events, per-lane
    /// ordering, back-pressure, lane fairness behind the outbox, close semantics, keepalive timeout and the guards against
    /// foreign traffic before admission.
    /// </summary>
    public sealed class DirectTransportTests
    {
        private static readonly IClock Clock = new StopwatchClock();

        private sealed class Rig : IDisposable
        {
            public DirectTransport Host;
            public DirectTransport Client;
            public RecordingSink HostSink = new RecordingSink();
            public RecordingSink ClientSink = new RecordingSink();
            public IConnection HostConn;
            public IConnection ClientConn;

            public void PollBoth()
            {
                Host.Poll(HostSink);
                Client.Poll(ClientSink);
            }

            public void Dispose()
            {
                Client?.Dispose();
                Host?.Dispose();
            }
        }

        private static DirectTransport NewTransport(string name) => new DirectTransport(NullLog.Instance, Clock, name);

        private static DirectTransport ListeningHost(ListenOptions options = null)
        {
            DirectTransport host = NewTransport("host");
            ListenOptions o = options ?? new ListenOptions();
            o.IpPort = 0;
            o.AllowIp = true;
            host.Listen(o);
            return host;
        }

        private static Endpoint Local(int port) => new Endpoint { Host = "127.0.0.1", Port = port };

        /// <summary>A connected pair; the host admits the client unless told otherwise.</summary>
        private static Rig Connected(bool admit = true, ListenOptions options = null)
        {
            var rig = new Rig { Host = ListeningHost(options), Client = NewTransport("client") };
            rig.ClientConn = rig.Client.Connect(Local(rig.Host.BoundPort));
            Assert.True(PollUntil(() => rig.HostSink.Connected.Count == 1 && rig.ClientSink.Connected.Count == 1, 3000, rig.PollBoth), "both ends connect");
            rig.HostConn = rig.HostSink.Connected[0];
            Assert.Same(rig.ClientConn, rig.ClientSink.Connected[0]);
            if (admit) ((IAdmissionAware)rig.HostConn).OnAdmitted();
            return rig;
        }

        private static bool PollUntil(Func<bool> condition, int milliseconds, Action poll)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                poll?.Invoke();
                if (condition()) return true;
                if (watch.ElapsedMilliseconds > milliseconds) return false;
                Thread.Sleep(1);
            }
        }

        [Test]
        public void ConnectReportsBothEndsAsDirectPeers()
        {
            using (Rig rig = Connected(admit: false))
            {
                Assert.Equal(TransportKind.Direct, rig.Host.Kind);
                Assert.True(rig.Host.BoundPort > 0, "a real port was bound");
                foreach (IConnection c in new[] { rig.HostConn, rig.ClientConn })
                {
                    Assert.True(c.Peer.Direct, "direct peer");
                    Assert.Equal(0UL, c.Peer.SteamId);
                    Assert.True(c.Peer.Address.StartsWith("127.0.0.1:", StringComparison.Ordinal), "address " + c.Peer.Address);
                    Assert.True(c is IThreadSafeConnection && c is IConnectionTimeouts && c is IAdmissionAware);
                }
                Assert.Equal("127.0.0.1:" + rig.Host.BoundPort.ToString(System.Globalization.CultureInfo.InvariantCulture), rig.ClientConn.Peer.Address);
                Assert.SequenceEqual(new[] { "C" }, rig.HostSink.Events);
                Assert.SequenceEqual(new[] { "C" }, rig.ClientSink.Events);
            }
        }

        [Test]
        public void MessagesArriveInOrderPerLaneWithTheirLane()
        {
            using (Rig rig = Connected())
            {
                var lanes = new[] { Lane.Reliable, Lane.Unreliable, Lane.Bulk };
                var counters = new int[3];
                var random = new DeterministicRandom(11);
                for (int i = 0; i < 1000; i++)
                {
                    int l = random.Next(3);
                    Delivery d = lanes[l] == Lane.Unreliable ? Delivery.Unreliable : Delivery.ReliableOrdered;
                    byte[] m = TestMessages.Make((byte)l, counters[l]++, 5 + random.Next(200));
                    Assert.Equal(SendResult.Ok, rig.ClientConn.Send(lanes[l], d, m, 0, m.Length));
                }
                Assert.True(PollUntil(() => rig.HostSink.Messages.Count >= 1000, 5000, rig.PollBoth), "all 1000 arrive, got " + rig.HostSink.Messages.Count);
                var next = new int[3];
                foreach (ReceivedMessage m in rig.HostSink.Messages)
                {
                    int tag = TestMessages.Tag(m.Data);
                    Assert.Equal(lanes[tag], m.Lane, "the lane travels with the message");
                    Assert.Equal(next[tag]++, TestMessages.Sequence(m.Data), "send order per lane");
                    Assert.Same(rig.HostConn, m.Connection);
                }
                Assert.SequenceEqual(counters, next);
            }
        }

        [Test]
        public void MaxSizeMessageRoundTripsAndLargerIsTooLarge()
        {
            using (Rig rig = Connected())
            {
                byte[] big = TestMessages.Make(0, 42, Limits.MaxMessageBytes);
                Assert.Equal(SendResult.TooLarge, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[Limits.MaxMessageBytes + 1], 0, Limits.MaxMessageBytes + 1));
                Assert.Equal(SendResult.Ok, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, big, 0, big.Length));
                Assert.True(PollUntil(() => rig.HostSink.Messages.Count == 1, 5000, rig.PollBoth), "the largest message arrives");
                Assert.SequenceEqual(big, rig.HostSink.Messages[0].Data, "byte-identical");
            }
        }

        [Test]
        public void SendReturnsBusyAboveTheSendBuffer()
        {
            using (Rig rig = Connected())
            {
                // the client stops polling: its inbox and the socket buffers fill, then the host's send queue
                var chunk = new byte[Limits.MaxMessageBytes];
                long accepted = 0;
                SendResult last = SendResult.Ok;
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 10000)
                {
                    last = rig.HostConn.Send(Lane.Bulk, Delivery.ReliableOrdered, chunk, 0, chunk.Length);
                    if (last != SendResult.Ok) break;
                    accepted += chunk.Length;
                }
                Assert.Equal(SendResult.Busy, last);
                int sendBuffer = new OutboxConfig().SendBufferBytes;
                Assert.True(accepted >= sendBuffer - Limits.MaxMessageBytes, "accepted " + accepted);
                Assert.True(rig.HostConn.GetStats().PendingReliableBytes > sendBuffer - Limits.MaxMessageBytes, "the queue is reported as pending");
                Assert.Equal(SendResult.Ok, rig.HostConn.Send(Lane.Unreliable, Delivery.Unreliable, chunk, 0, 100), "unreliable is dropped silently");
            }
        }

        [Test]
        public void FullInboxPushesBackAndResumes()
        {
            DirectConnection hostEnd, clientEnd;
            long accepted = 0;
            using (Rig rig = Connected())
            {
                hostEnd = (DirectConnection)rig.HostConn;
                clientEnd = (DirectConnection)rig.ClientConn;
                // the client does not poll: its 32 MiB inbox fills, its reader pauses, TCP pushes back, the host's queue fills
                var chunk = new byte[Limits.MaxMessageBytes];
                var watch = Stopwatch.StartNew();
                long lastProgress = 0;
                while (watch.ElapsedMilliseconds - lastProgress < 300 && watch.ElapsedMilliseconds < 5000)
                {
                    SendResult r = rig.HostConn.Send(Lane.Bulk, Delivery.ReliableOrdered, chunk, 0, chunk.Length);
                    if (r == SendResult.Ok)
                    {
                        accepted += chunk.Length;
                        lastProgress = watch.ElapsedMilliseconds;
                    }
                    else
                    {
                        Assert.Equal(SendResult.Busy, r);
                        Thread.Sleep(1);
                    }
                }
                Assert.True(accepted >= (40L << 20), "the inbox (32 MiB) and the send buffer (16 MiB) both filled: " + accepted);
                Assert.True(accepted < (96L << 20), "but no more than that, plus the socket buffers: " + accepted);
                Assert.True(rig.HostConn.GetStats().PendingReliableBytes > (8 << 20), "the host's queue stays full");
                rig.ClientSink.KeepMessages = false;
                Assert.True(PollUntil(() => rig.ClientSink.ByteCount == accepted, 10000, rig.PollBoth), "everything arrives once the client polls again: " + rig.ClientSink.ByteCount);
                Assert.Equal(0, rig.ClientSink.Disconnected.Count + rig.HostSink.Disconnected.Count, "back-pressure is not a timeout");
            }
            Assert.True(hostEnd.Join(0) && clientEnd.Join(0), "dispose stopped the connections' threads");
        }

        [Test]
        public void ReliableIsNotStarvedByQueuedBulk()
        {
            using (Rig rig = Connected())
            {
                var config = new OutboxConfig();
                using (var outbox = new PeerOutbox(rig.HostConn, new PeerId(1), config, Clock, NullLog.Instance))
                {
                    const int chunkBytes = 256 * 1024;
                    var chunk = new byte[chunkBytes];
                    for (int i = 0; i < (40 << 20) / chunkBytes; i++) Assert.Equal(SendResult.Ok, outbox.Send(Lane.Bulk, Delivery.ReliableOrdered, chunk, 0, chunk.Length));
                    long bulkBytes = 0;
                    long bulkAtReliable = -1;
                    rig.ClientSink.KeepMessages = false;
                    rig.ClientSink.MessageHook = (c, lane, buffer, offset, count) =>
                    {
                        if (lane == Lane.Bulk) bulkBytes += count;
                        else if (lane == Lane.Reliable && bulkAtReliable < 0) bulkAtReliable = bulkBytes;
                    };
                    Action pump = () =>
                    {
                        outbox.Pump();
                        rig.Client.Poll(rig.ClientSink);
                    };
                    Assert.True(PollUntil(() => bulkBytes > 0, 3000, pump), "bulk flows");
                    byte[] r = TestMessages.Make(9, 1, 64);
                    Assert.Equal(SendResult.Ok, outbox.Send(Lane.Reliable, Delivery.ReliableOrdered, r, 0, r.Length));
                    Assert.True(PollUntil(() => bulkAtReliable >= 0, 5000, pump), "the reliable message arrives");
                    long bound = (long)config.BulkWindowMaxBytes + config.SendBufferBytes;
                    Assert.True(bulkAtReliable <= bound, "bulk bytes before R: " + bulkAtReliable + " (bound " + bound + ")");
                }
            }
        }

        [Test]
        public void CloseWithLingerDeliversQueuedThenReason()
        {
            using (Rig rig = Connected())
            {
                for (int i = 0; i < 5; i++)
                {
                    byte[] m = TestMessages.Make(0, i, 1000);
                    Assert.Equal(SendResult.Ok, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, m, 0, m.Length));
                }
                rig.ClientConn.Close(7, "bye", true);
                Assert.Equal(SendResult.Closed, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1], 0, 1));
                Assert.True(PollUntil(() => rig.HostSink.Disconnected.Count == 1, 3000, rig.PollBoth), "the host sees the close");
                Assert.SequenceEqual(new[] { "C", "M", "M", "M", "M", "M", "D" }, rig.HostSink.Events);
                for (int i = 0; i < 5; i++) Assert.Equal(i, TestMessages.Sequence(rig.HostSink.Messages[i].Data));
                Assert.Equal(7, rig.HostSink.Disconnected[0].Reason);
                Assert.Equal("bye", rig.HostSink.Disconnected[0].Text);
                Assert.Equal(0, rig.ClientSink.Disconnected.Count, "closing notifies only the other end");
            }
        }

        [Test]
        public void HalfOpenPeerTimesOut()
        {
            using (Rig rig = Connected())
            {
                ((DirectConnection)rig.HostConn).SuppressPongs = true;
                ((IConnectionTimeouts)rig.ClientConn).SetConnectedTimeout(1500);
                Assert.Equal(1500, ((IConnectionTimeouts)rig.ClientConn).ConnectedTimeoutMilliseconds);
                var watch = Stopwatch.StartNew();
                Assert.True(PollUntil(() => rig.ClientSink.Disconnected.Count == 1, 3000, rig.PollBoth), "the silent peer times out");
                Assert.True(watch.ElapsedMilliseconds >= 1000, "not before the timeout: " + watch.ElapsedMilliseconds + " ms");
                Assert.Equal("timeout", rig.ClientSink.Disconnected[0].Text);
                Assert.Equal(TransportCloseReason.Timeout, rig.ClientSink.Disconnected[0].Reason);
            }
        }

        [Test]
        public void ListenOnBusyPortThrowsWithPort()
        {
            using (DirectTransport first = ListeningHost())
            using (DirectTransport second = NewTransport("second"))
            {
                int port = first.BoundPort;
                var ex = Assert.Throws<DirectListenException>(() => second.Listen(new ListenOptions { IpPort = port, AllowIp = true }));
                Assert.Equal(port, ex.Port);
                Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
            }
        }

        [Test]
        public void GarbagePreambleIsClosed()
        {
            using (DirectTransport host = ListeningHost())
            using (var raw = new TcpClient())
            {
                var sink = new RecordingSink();
                raw.Connect(IPAddress.Loopback, host.BoundPort);
                raw.ReceiveTimeout = 2000;
                NetworkStream stream = raw.GetStream();
                byte[] garbage = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n");
                stream.Write(garbage, 0, garbage.Length);
                var watch = Stopwatch.StartNew();
                Assert.True(ReadsToEnd(stream), "the host closes the socket");
                Assert.True(watch.ElapsedMilliseconds < 2000, "closed within 2 s");
                PollUntil(() => false, 100, () => host.Poll(sink));
                Assert.Equal(0, sink.Connected.Count, "no OnConnected for a stranger");
                Assert.Equal(0, sink.Disconnected.Count);
            }
        }

        [Test]
        public void OversizedFrameBeforeAdmissionIsClosed()
        {
            using (DirectTransport host = ListeningHost())
            using (var raw = new TcpClient())
            {
                var sink = new RecordingSink();
                raw.Connect(IPAddress.Loopback, host.BoundPort);
                raw.ReceiveTimeout = 2000;
                NetworkStream stream = raw.GetStream();
                const int length = 64 * 1024 + 1;
                byte[] frame = { (byte)'S', (byte)'T', (byte)'D', (byte)'1', 0, 0, length & 0xFF, (length >> 8) & 0xFF, (length >> 16) & 0xFF, 0 };
                stream.Write(frame, 0, frame.Length);
                Assert.True(PollUntil(() => sink.Disconnected.Count == 1, 2000, () => host.Poll(sink)), "the host drops the connection");
                Assert.SequenceEqual(new[] { "C", "D" }, sink.Events);
                Assert.Equal(TransportCloseReason.ProtocolError, sink.Disconnected[0].Reason);
                var connection = (DirectConnection)sink.Connected[0];
                Assert.True(connection.LargestReceiveAllocation <= 64 * 1024, "allocated " + connection.LargestReceiveAllocation);
                Assert.True(ReadsToEnd(stream), "the socket is closed");
            }
        }

        [Test]
        public void PendingLimitRefusesWithText()
        {
            using (Rig rig = Connected(admit: false, options: new ListenOptions { MaxPendingConnections = 1 }))
            using (DirectTransport second = NewTransport("second"))
            {
                var sink = new RecordingSink();
                second.Connect(Local(rig.Host.BoundPort));
                Assert.True(PollUntil(() => sink.Disconnected.Count == 1, 3000, () =>
                {
                    rig.PollBoth();
                    second.Poll(sink);
                }), "the second client is refused");
                Assert.Equal("server busy", sink.Disconnected[0].Text);
                Assert.Equal(0, sink.Connected.Count, "a refused client never connects");
                Assert.Equal(1, rig.HostSink.Connected.Count, "the host reports only the first");
            }
        }

        [Test]
        public void WorksUnderNetSim()
        {
            var options = new NetSimOptions { LatencyMs = 80, JitterMs = 10 };
            using (var host = new NetSimTransport(NewTransport("host"), options, Clock))
            using (var client = new NetSimTransport(NewTransport("client"), options, Clock))
            {
                host.Listen(new ListenOptions { IpPort = 0, AllowIp = true });
                int port = ((DirectTransport)host.Inner).BoundPort;
                var hostSink = new RecordingSink();
                var clientSink = new RecordingSink();
                hostSink.MessageHook = (c, lane, buffer, offset, count) => c.Send(lane, Delivery.ReliableOrdered, buffer, offset, count);
                IConnection connection = client.Connect(Local(port));
                Action poll = () =>
                {
                    host.Poll(hostSink);
                    client.Poll(clientSink);
                };
                Assert.True(PollUntil(() => clientSink.Connected.Count == 1, 3000, poll), "connected");
                byte[] m = TestMessages.Make(3, 77, 300);
                Assert.Equal(SendResult.Ok, connection.Send(Lane.Reliable, Delivery.ReliableOrdered, m, 0, m.Length));
                Assert.True(PollUntil(() => clientSink.Messages.Count == 1, 3000, poll), "the echo arrives");
                Assert.SequenceEqual(m, clientSink.Messages[0].Data);
                Assert.True(connection.GetStats().PingMs >= 0);
            }
        }

        /// <summary>Reads until the peer closes (end of stream or reset); false when the read times out.</summary>
        private static bool ReadsToEnd(NetworkStream stream)
        {
            var buffer = new byte[4096];
            try
            {
                while (stream.Read(buffer, 0, buffer.Length) > 0)
                {
                }
                return true;
            }
            catch (IOException ex) when (ex.InnerException is SocketException se)
            {
                return se.SocketErrorCode != SocketError.TimedOut && se.SocketErrorCode != SocketError.WouldBlock;
            }
        }
    }
}
