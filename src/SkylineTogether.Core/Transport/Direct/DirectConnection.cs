using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Transport.Direct
{
    /// <summary>
    /// One TCP connection of <see cref="DirectTransport"/>: all three lanes share the stream, the lane travels in each
    /// frame (<see cref="DirectFraming"/>). A writer thread owns the socket's life (connect, preamble, frames, keepalive,
    /// close) and picks queued messages round-robin weighted R 4 : U 2 : B 1, the weights of the Steam lanes; a reader
    /// thread parses frames into the transport's inbox, which <see cref="DirectTransport.Poll"/> drains.
    /// <para>
    /// Send answers <see cref="SendResult.Busy"/> while the queued reliable bytes plus the message exceed the send buffer
    /// (<see cref="OutboxConfig.SendBufferBytes"/>), drops unreliable messages (answering <see cref="SendResult.Ok"/>)
    /// while more than 256 KiB are queued, and refuses anything above <see cref="Limits.MaxMessageBytes"/>. Before the
    /// host admits the peer (<see cref="IAdmissionAware"/>) a frame may carry at most 64 KiB and the inbox holds at most
    /// 1 MiB; afterwards <see cref="Limits.MaxMessageBytes"/> and 32 MiB (the receive buffer the game gives a Steam
    /// connection). A full inbox stops the reader, so TCP pushes back on the sender.
    /// </para>
    /// <para>
    /// The keepalive pings every second; when no byte arrives for the connected timeout (<see cref="IConnectionTimeouts"/>,
    /// default <see cref="ConnectionTimeouts.NormalMilliseconds"/>) the connection ends with "timeout". The writer checks
    /// it, and so does every poll of the transport (a writer stuck on a dead link cannot). Thread-safe: every field below
    /// is guarded by <c>_sync</c> unless marked otherwise. Lock order: connection, then transport.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    internal sealed class DirectConnection : IThreadSafeConnection, IConnectionTimeouts, IAdmissionAware
    {
        private const int UnreliableDropBytes = 256 * 1024;
        private const int PendingInboxBytes = 1 << 20;
        private const int AdmittedInboxBytes = 32 << 20;
        private const long LingerTicks = 2 * TimeSpan.TicksPerSecond;
        private const long PingTicks = TimeSpan.TicksPerSecond;
        private const long WakeTicks = 100 * TimeSpan.TicksPerMillisecond;
        private const int ConnectTimeoutMilliseconds = 10000;
        private const int JoinMilliseconds = 2000;
        private const int IoBufferBytes = 64 * 1024;

        private static readonly int SendBufferBytes = new OutboxConfig().SendBufferBytes;
        private static readonly byte[] Schedule = { 0, 0, 0, 0, 1, 1, 2 };   // weighted round robin R 4 : U 2 : B 1

        private struct OutFrame
        {
            public Delivery Delivery;
            public byte[] Buffer;
            public int Count;
        }

        private readonly DirectTransport _owner;
        private readonly IClock _clock;
        private readonly object _sync = new object();
        private readonly RingQueue<OutFrame>[] _queues = { new RingQueue<OutFrame>(64), new RingQueue<OutFrame>(16), new RingQueue<OutFrame>(64) };
        private readonly Endpoint _target;      // outgoing connections only
        private readonly string _refusal;       // a refused incoming connection: the text of its close frame
        private Socket _socket;
        private Thread _writer;
        private Thread _reader;

        private volatile bool _ended;           // failed, timed out, closed by the peer or aborted: no more events
        private volatile bool _locallyClosed;
        private volatile bool _admitted;
        private volatile bool _suppressPongs;
        private bool _connectedReported;
        private bool _notifyPeer;               // ended by a protocol violation: the writer still sends the close frame
        private int _closeReason;
        private string _closeText;
        private bool _closeLinger;
        private long _closeDeadline;
        private long _queuedReliable;
        private long _queuedUnreliable;
        private long _inboxBytes;
        private bool _readerPaused;
        private bool _pongDue;
        private long _pongStamp;
        private long _nextPing;
        private int _turn;
        private int _timeoutMs;
        private int _pingMs;
        private long _lastReceive;              // Interlocked
        private long _largestAllocation;        // Interlocked

        // writer-thread state
        private readonly byte[] _out = new byte[IoBufferBytes];
        private int _outCount;

        // reader-thread state
        private readonly byte[] _in = new byte[IoBufferBytes];
        private int _inStart, _inEnd;

        /// <summary>An accepted connection (<paramref name="refusal"/> non-null: refuse it with that text).</summary>
        internal DirectConnection(DirectTransport owner, int id, PeerIdentity peer, Socket socket, string remoteKey, string refusal)
        {
            _owner = owner;
            _clock = owner.Clock;
            Id = id;
            Peer = peer;
            _socket = socket;
            RemoteKey = remoteKey;
            _refusal = refusal;
            Incoming = true;
        }

        /// <summary>An outgoing connection to <paramref name="target"/>; the client end counts as admitted.</summary>
        internal DirectConnection(DirectTransport owner, int id, PeerIdentity peer, Endpoint target)
        {
            _owner = owner;
            _clock = owner.Clock;
            Id = id;
            Peer = peer;
            _target = target;
            _admitted = true;
        }

        public int Id { get; }

        public PeerIdentity Peer { get; }

        internal bool Incoming { get; }

        /// <summary>The remote IP address (for <see cref="ListenOptions.MaxPendingPerRemote"/>).</summary>
        internal string RemoteKey { get; }

        /// <summary>Counts toward the listener's limits: an incoming connection that is open and not being refused.</summary>
        internal bool IsOpenIncoming => Incoming && _refusal == null && !_ended && !_locallyClosed;

        internal bool Admitted => _admitted;

        internal bool LocallyClosed => _locallyClosed;

        /// <summary>Test hook: this end sends no keepalive frames (neither pings nor pongs), like a peer gone half-open.</summary>
        internal bool SuppressPongs
        {
            get => _suppressPongs;
            set => _suppressPongs = value;
        }

        /// <summary>Test hook: the largest payload buffer the reader asked for.</summary>
        internal long LargestReceiveAllocation => Interlocked.Read(ref _largestAllocation);

        public int ConnectedTimeoutMilliseconds
        {
            get { lock (_sync) return _timeoutMs; }
        }

        public void SetConnectedTimeout(int milliseconds)
        {
            if (milliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "must be positive");
            lock (_sync)
            {
                _timeoutMs = milliseconds;
                Monitor.PulseAll(_sync);
            }
        }

        public void OnAdmitted()
        {
            lock (_sync)
            {
                _admitted = true;
                Monitor.PulseAll(_sync);   // a reader waiting for inbox space gets the larger one
            }
        }

        public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the buffer");
            if ((byte)lane > (byte)Lane.Bulk || (byte)delivery > (byte)Delivery.Unreliable) return SendResult.Failed;
            if (count > Limits.MaxMessageBytes) return SendResult.TooLarge;
            lock (_sync)
            {
                if (_ended || _locallyClosed) return SendResult.Closed;
                if (delivery == Delivery.Unreliable)
                {
                    if (_queuedReliable + _queuedUnreliable > UnreliableDropBytes) return SendResult.Ok;
                }
                else if (_queuedReliable + count > SendBufferBytes)
                {
                    return SendResult.Busy;
                }
                byte[] copy = BufferPool.Rent(count);
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                _queues[(int)lane].Enqueue(new OutFrame { Delivery = delivery, Buffer = copy, Count = count });
                if (delivery == Delivery.ReliableOrdered) _queuedReliable += count;
                else _queuedUnreliable += count;
                Monitor.PulseAll(_sync);
                return SendResult.Ok;
            }
        }

        public ConnectionStats GetStats()
        {
            lock (_sync)
            {
                bool open = !_ended && !_locallyClosed;
                return new ConnectionStats
                {
                    PingMs = _pingMs,
                    Quality = open ? 1f : 0f,
                    PendingReliableBytes = Clamp(_queuedReliable),
                    PendingUnreliableBytes = Clamp(_queuedUnreliable)
                };
            }
        }

        public void Close(int reason, string text, bool linger)
        {
            lock (_sync)
            {
                if (_locallyClosed) return;
                _locallyClosed = true;
                _closeReason = reason;
                _closeText = text;
                _closeLinger = linger;
                _closeDeadline = _clock.Ticks + LingerTicks;
                if (!linger) DropQueuesLocked();
                Monitor.PulseAll(_sync);
            }
        }

        public override string ToString() => "direct#" + Id.ToString(CultureInfo.InvariantCulture) + " to " + Peer;

        // ---- lifecycle (called by DirectTransport) ----

        internal void Start()
        {
            _writer = new Thread(WriterMain) { IsBackground = true, Name = "SkylineTogether direct writer #" + Id.ToString(CultureInfo.InvariantCulture) };
            _writer.Start();
        }

        /// <summary>Waits for the connection's threads; false when they are still running after <paramref name="milliseconds"/>.</summary>
        internal bool Join(int milliseconds)
        {
            Thread writer = _writer;
            return writer == null || writer.Join(milliseconds);
        }

        /// <summary>Message bytes left the inbox (delivered or dropped by the poll).</summary>
        internal void Consumed(int count)
        {
            lock (_sync)
            {
                _inboxBytes -= count;
                if (_readerPaused) Monitor.PulseAll(_sync);
            }
        }

        /// <summary>Called by each poll: the keepalive timeout and the linger deadline, for a writer stuck in a send.</summary>
        internal void CheckTimers(long now)
        {
            lock (_sync)
            {
                if (_ended)
                {
                    if (_notifyPeer && now > _closeDeadline) AbortLocked();
                    return;
                }
                if (_locallyClosed)
                {
                    if (now > _closeDeadline + LingerTicks) AbortLocked();
                    return;
                }
                CheckTimeoutLocked(now);
            }
        }

        /// <summary>Ends the connection at once without telling the application (transport disposal).</summary>
        internal void Abort()
        {
            lock (_sync) AbortLocked();
        }

        // ---- writer thread ----

        private void WriterMain()
        {
            bool graceful = false;
            try
            {
                if (_target != null && !ConnectOutgoing()) return;
                Interlocked.Exchange(ref _lastReceive, _clock.Ticks);
                lock (_sync) _nextPing = _clock.Ticks + PingTicks;
                if (_refusal != null)
                {
                    AppendFrame(DirectFrameType.Close, 0, DirectFraming.EncodeClose(TransportCloseReason.ListenerFull, _refusal), 0, -1);
                    Flush();
                    DrainAndClose();
                    return;
                }
                DirectFraming.WritePreamble(_out, 0);
                _outCount = DirectFraming.PreambleBytes;
                Flush();
                _reader = new Thread(ReaderMain) { IsBackground = true, Name = "SkylineTogether direct reader #" + Id.ToString(CultureInfo.InvariantCulture) };
                _reader.Start();
                graceful = WriteLoop();
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                Fail(TransportCloseReason.SocketError, "connection lost");
            }
            catch (Exception ex)
            {
                _owner.Log.Warn(LogCategory.Net, this + ": writer failed", ex);
                Fail(TransportCloseReason.SocketError, "connection lost");
            }
            finally
            {
                Teardown(graceful);
            }
        }

        /// <summary>Writes frames until the connection ends; true when it ended by a local close (close frame sent).</summary>
        private bool WriteLoop()
        {
            while (true)
            {
                OutFrame frame = default;
                int frameLane = -1;
                bool flush = false, closing = false;
                DirectFrameType control = DirectFrameType.Data;
                long stamp = 0;
                lock (_sync)
                {
                    while (true)
                    {
                        long now = _clock.Ticks;
                        if (_ended)
                        {
                            if (!_notifyPeer) return false;
                            _notifyPeer = false;
                            DropQueuesLocked();
                            closing = true;
                            break;
                        }
                        if (_locallyClosed)
                        {
                            if (!_closeLinger || TotalQueuedLocked() == 0 || now >= _closeDeadline)
                            {
                                DropQueuesLocked();
                                closing = true;
                                break;
                            }
                        }
                        else if (CheckTimeoutLocked(now))
                        {
                            return false;
                        }
                        if (_pongDue && !_suppressPongs)
                        {
                            _pongDue = false;
                            control = DirectFrameType.Pong;
                            stamp = _pongStamp;
                            break;
                        }
                        if (now >= _nextPing)
                        {
                            _nextPing = now + PingTicks;
                            if (!_suppressPongs && !_locallyClosed)
                            {
                                control = DirectFrameType.Ping;
                                stamp = now;
                                break;
                            }
                        }
                        frameLane = PickLaneLocked();
                        if (frameLane >= 0)
                        {
                            frame = _queues[frameLane].Dequeue();
                            break;
                        }
                        if (_outCount > 0)
                        {
                            flush = true;
                            break;
                        }
                        long wait = Math.Min(Math.Max(_nextPing - now, 0), WakeTicks);
                        Monitor.Wait(_sync, (int)Math.Max(1, wait / TimeSpan.TicksPerMillisecond));
                    }
                }
                if (closing)
                {
                    AppendFrame(DirectFrameType.Close, 0, DirectFraming.EncodeClose(_closeReason, _closeText), 0, -1);
                    Flush();
                    return true;
                }
                if (flush)
                {
                    Flush();
                }
                else if (frameLane >= 0)
                {
                    try
                    {
                        AppendFrame(DirectFrameType.Data, (byte)frameLane, frame.Buffer, 0, frame.Count);
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            if (frame.Delivery == Delivery.ReliableOrdered) _queuedReliable -= frame.Count;
                            else _queuedUnreliable -= frame.Count;
                        }
                        BufferPool.Return(frame.Buffer);
                    }
                }
                else
                {
                    var payload = new byte[DirectFraming.PingPayloadBytes];
                    DirectFraming.WriteInt64(payload, 0, stamp);
                    AppendFrame(control, 0, payload, 0, payload.Length);
                }
            }
        }

        private bool ConnectOutgoing()
        {
            IPAddress[] addresses;
            if (IPAddress.TryParse(_target.Host, out IPAddress parsed))
            {
                addresses = new[] { parsed };
            }
            else
            {
                try
                {
                    addresses = Dns.GetHostAddresses(_target.Host);
                }
                catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
                {
                    Fail(TransportCloseReason.ResolveFailed, "could not resolve " + Diagnostics.AddressMask.Host(_target.Host));
                    return false;
                }
            }
            SocketError last = SocketError.HostNotFound;
            foreach (IPAddress address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                lock (_sync)
                {
                    if (_ended || _locallyClosed)
                    {
                        socket.Dispose();
                        Fail(TransportCloseReason.NoListener, "the connection attempt was cancelled");
                        return false;
                    }
                    _socket = socket;
                }
                try
                {
                    IAsyncResult pending = socket.BeginConnect(new IPEndPoint(address, _target.Port), null, null);
                    long deadline = _clock.Ticks + ConnectTimeoutMilliseconds * TimeSpan.TicksPerMillisecond;
                    while (!pending.AsyncWaitHandle.WaitOne(50))
                    {
                        if (_ended || _locallyClosed || _clock.Ticks > deadline) break;
                    }
                    if (pending.IsCompleted)
                    {
                        socket.EndConnect(pending);
                        socket.NoDelay = true;
                        return true;
                    }
                    last = SocketError.TimedOut;
                }
                catch (SocketException ex)
                {
                    last = ex.SocketErrorCode;
                }
                catch (ObjectDisposedException)
                {
                    last = SocketError.OperationAborted;
                }
                lock (_sync) _socket = null;
                socket.Dispose();
            }
            Fail(TransportCloseReason.NoListener, "could not connect to " + Diagnostics.AddressMask.Address(_target.ToString()) + " (" + last.ToString() + ")");
            return false;
        }

        /// <summary>Appends a frame to the write buffer (payload length -1: the whole array), sending what does not fit.</summary>
        private void AppendFrame(DirectFrameType type, byte lane, byte[] payload, int offset, int count)
        {
            if (count < 0) count = payload.Length - offset;
            if (_outCount + DirectFraming.HeaderBytes + count > _out.Length) Flush();
            DirectFraming.WriteHeader(_out, _outCount, type, lane, count);
            _outCount += DirectFraming.HeaderBytes;
            if (count <= _out.Length - _outCount)
            {
                Buffer.BlockCopy(payload, offset, _out, _outCount, count);
                _outCount += count;
            }
            else
            {
                Flush();
                SendAll(payload, offset, count);
            }
        }

        private void Flush()
        {
            if (_outCount == 0) return;
            int n = _outCount;
            _outCount = 0;
            SendAll(_out, 0, n);
        }

        private void SendAll(byte[] buffer, int offset, int count)
        {
            Socket socket = _socket ?? throw new ObjectDisposedException(nameof(DirectConnection));
            while (count > 0)
            {
                int n = socket.Send(buffer, offset, count, SocketFlags.None);
                offset += n;
                count -= n;
            }
        }

        /// <summary>After the refusal frame: half-close, read whatever the stranger still sends (no reset), then close.</summary>
        private void DrainAndClose()
        {
            Socket socket = _socket;
            try
            {
                socket.Shutdown(SocketShutdown.Send);
                long deadline = _clock.Ticks + TimeSpan.TicksPerSecond;
                while (_clock.Ticks < deadline && !_ended)
                {
                    if (!socket.Poll(50000, SelectMode.SelectRead)) continue;
                    if (socket.Receive(_in, 0, _in.Length, SocketFlags.None) == 0) break;
                }
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
            {
                // the stranger is gone already
            }
        }

        private void Teardown(bool graceful)
        {
            Socket socket;
            lock (_sync) socket = _socket;
            if (socket != null && graceful)
            {
                // half-close after the close frame; the peer closes once it read everything, which ends our reader
                try
                {
                    socket.Shutdown(SocketShutdown.Send);
                }
                catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
                {
                    // already closed
                }
                _reader?.Join(JoinMilliseconds);
            }
            lock (_sync)
            {
                _ended = true;
                DropQueuesLocked();
                Monitor.PulseAll(_sync);
            }
            CloseSocket(socket);
            _reader?.Join(JoinMilliseconds);
            _owner.Remove(this);
        }

        // ---- reader thread ----

        private void ReaderMain()
        {
            try
            {
                var header = new byte[DirectFraming.HeaderBytes];
                if (!ReadExact(header, 0, DirectFraming.PreambleBytes))
                {
                    Fail(TransportCloseReason.SocketError, "the connection was closed during the handshake");
                    return;
                }
                if (!DirectFraming.IsPreamble(header, 0))
                {
                    if (!Incoming && header[0] == (byte)DirectFrameType.Close && header[1] == 0 && ReadExact(header, DirectFraming.PreambleBytes, 2))
                    {
                        ReadFrame(header);   // the host refused us with a close frame
                        return;
                    }
                    Violation("not a direct-connect peer");
                    return;
                }
                lock (_sync)
                {
                    if (_ended) return;
                    _connectedReported = true;
                    _owner.EnqueueConnected(this);
                }
                while (ReadExact(header, 0, DirectFraming.HeaderBytes))
                {
                    if (!ReadFrame(header)) return;
                }
                Fail(TransportCloseReason.SocketError, "the connection was closed");
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                Fail(TransportCloseReason.SocketError, "connection lost");
            }
            catch (Exception ex)
            {
                _owner.Log.Warn(LogCategory.Net, this + ": reader failed", ex);
                Fail(TransportCloseReason.SocketError, "connection lost");
            }
        }

        /// <summary>Reads one frame's payload and handles it; false when the connection ended.</summary>
        private bool ReadFrame(byte[] header)
        {
            DirectFraming.ReadHeader(header, 0, out DirectFrameType type, out byte lane, out int length);
            if (!DirectFraming.IsValid(type, lane, length, _admitted))
            {
                Violation("invalid frame (type " + ((int)type).ToString(CultureInfo.InvariantCulture) + ", " + length.ToString(CultureInfo.InvariantCulture) + " bytes)");
                return false;
            }
            if (length > Interlocked.Read(ref _largestAllocation)) Interlocked.Exchange(ref _largestAllocation, length);
            byte[] payload = BufferPool.Rent(length);
            if (!ReadExact(payload, 0, length))
            {
                BufferPool.Return(payload);
                Fail(TransportCloseReason.SocketError, "the connection was closed");
                return false;
            }
            switch (type)
            {
                case DirectFrameType.Data:
                    return Deliver((Lane)lane, payload, length);
                case DirectFrameType.Ping:
                    lock (_sync)
                    {
                        _pongDue = true;
                        _pongStamp = DirectFraming.ReadInt64(payload, 0);
                        Monitor.PulseAll(_sync);
                    }
                    break;
                case DirectFrameType.Pong:
                {
                    long rtt = _clock.Ticks - DirectFraming.ReadInt64(payload, 0);
                    if (rtt >= 0 && rtt < int.MaxValue * TimeSpan.TicksPerMillisecond)
                    {
                        lock (_sync) _pingMs = (int)(rtt / TimeSpan.TicksPerMillisecond);
                    }
                    break;
                }
                default:
                    DirectFraming.DecodeClose(payload, length, out int reason, out string text);
                    BufferPool.Return(payload);
                    Fail(reason, text);
                    return false;
            }
            BufferPool.Return(payload);
            return true;
        }

        /// <summary>Puts a message into the inbox, waiting while it is full; false when the connection ended.</summary>
        private bool Deliver(Lane lane, byte[] payload, int count)
        {
            lock (_sync)
            {
                while (!_ended && !_locallyClosed && _inboxBytes > (_admitted ? AdmittedInboxBytes : PendingInboxBytes))
                {
                    _readerPaused = true;
                    Monitor.Wait(_sync, 100);
                }
                if (_readerPaused)
                {
                    _readerPaused = false;
                    Interlocked.Exchange(ref _lastReceive, _clock.Ticks);   // the pause was ours, not the peer's silence
                }
                if (_ended)
                {
                    BufferPool.Return(payload);
                    return false;
                }
                if (_locallyClosed)
                {
                    BufferPool.Return(payload);   // closing: drain the stream, deliver nothing
                    return true;
                }
                _inboxBytes += count;
                _owner.EnqueueMessage(this, lane, payload, count);
                return true;
            }
        }

        private bool ReadExact(byte[] buffer, int offset, int count)
        {
            Socket socket = _socket;
            while (count > 0)
            {
                int available = _inEnd - _inStart;
                if (available > 0)
                {
                    int n = Math.Min(available, count);
                    Buffer.BlockCopy(_in, _inStart, buffer, offset, n);
                    _inStart += n;
                    offset += n;
                    count -= n;
                    continue;
                }
                int received;
                if (count >= _in.Length)
                {
                    received = socket.Receive(buffer, offset, count, SocketFlags.None);
                    offset += received;
                    count -= received;
                }
                else
                {
                    received = socket.Receive(_in, 0, _in.Length, SocketFlags.None);
                    _inStart = 0;
                    _inEnd = received;
                }
                if (received <= 0) return false;
                Interlocked.Exchange(ref _lastReceive, _clock.Ticks);
            }
            return true;
        }

        /// <summary>Ends the connection with <see cref="TransportCloseReason.ProtocolError"/>; the writer still tells the peer (close frame).</summary>
        private void Violation(string what)
        {
            _owner.LogStranger(this + ": protocol violation, " + what);
            lock (_sync)
            {
                if (_ended) return;
                _closeReason = TransportCloseReason.ProtocolError;
                _closeText = "protocol violation";
                _closeDeadline = _clock.Ticks + LingerTicks;
                _notifyPeer = true;
                FailLocked(TransportCloseReason.ProtocolError, "protocol violation: " + what, false);
            }
        }

        // ---- state (callers hold _sync where the name says so) ----

        private void Fail(int reason, string text)
        {
            lock (_sync) FailLocked(reason, text);
        }

        /// <summary>The connection ended for a reason other than a local close: report it (once) and kill the socket.</summary>
        private void FailLocked(int reason, string text, bool closeSocket = true)
        {
            if (_ended) return;
            _ended = true;
            if (!_locallyClosed && _refusal == null && (!Incoming || _connectedReported)) _owner.EnqueueDisconnected(this, reason, text);
            DropQueuesLocked();
            Monitor.PulseAll(_sync);
            if (closeSocket) CloseSocket(_socket);
        }

        /// <summary>Kills the socket now (whatever state the connection is in); reports nothing.</summary>
        private void AbortLocked()
        {
            _ended = true;
            _notifyPeer = false;
            DropQueuesLocked();
            Monitor.PulseAll(_sync);
            CloseSocket(_socket);
        }

        private bool CheckTimeoutLocked(long now)
        {
            if (_readerPaused) return false;
            long timeout = (_timeoutMs > 0 ? _timeoutMs : ConnectionTimeouts.NormalMilliseconds) * TimeSpan.TicksPerMillisecond;
            if (now - Interlocked.Read(ref _lastReceive) <= timeout) return false;
            _owner.Log.Info(LogCategory.Net, this + ": no data for " + (timeout / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture) + " ms, disconnecting");
            FailLocked(TransportCloseReason.Timeout, "timeout");
            return true;
        }

        private int PickLaneLocked()
        {
            for (int i = 0; i < Schedule.Length; i++)
            {
                int slot = (_turn + i) % Schedule.Length;
                int lane = Schedule[slot];
                if (_queues[lane].Count == 0) continue;
                _turn = (slot + 1) % Schedule.Length;
                return lane;
            }
            return -1;
        }

        private long TotalQueuedLocked() => _queuedReliable + _queuedUnreliable;

        private void DropQueuesLocked()
        {
            foreach (RingQueue<OutFrame> queue in _queues)
            {
                while (queue.Count > 0) BufferPool.Return(queue.Dequeue().Buffer);
            }
            _queuedReliable = 0;
            _queuedUnreliable = 0;
        }

        private static void CloseSocket(Socket socket)
        {
            if (socket == null) return;
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
            {
                // not connected or already closed
            }
            socket.Dispose();
        }

        private static int Clamp(long value) => value <= 0 ? 0 : value >= int.MaxValue ? int.MaxValue : (int)value;
    }
}
