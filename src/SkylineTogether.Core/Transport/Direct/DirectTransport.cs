using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Transport.Direct
{
    /// <summary>
    /// Direct IP transport without Steam (fork, ARCHITECTURE.md#transport): one plain TCP connection per peer carries all
    /// three lanes (<see cref="DirectConnection"/>, wire format <see cref="DirectFraming"/>). Made for a private network
    /// such as Tailscale, so it needs no NAT traversal; the session password challenge authenticates the peer, which
    /// every connection reports as <see cref="PeerIdentity.Direct"/> with SteamID 0 and its <c>ip:port</c>.
    /// <para>
    /// Semantics follow <see cref="LoopbackTransport"/> and Steam Networking Sockets: <see cref="Connect"/> returns at
    /// once and a failure arrives as <see cref="ITransportEventSink.OnDisconnected"/>; both ends see
    /// <see cref="ITransportEventSink.OnConnected"/> before any message, messages arrive in send order per lane, and a
    /// connection one closes oneself reports nothing. A host refuses connections beyond
    /// <see cref="ListenOptions.Refusal"/> with a close frame that carries the refusal text, and closes strangers that
    /// do not speak the protocol (wrong preamble, oversized or malformed frames) before they reach the application.
    /// </para>
    /// <para>Thread-safe: Listen and Connect on the main thread, Poll on one network thread, connections from any thread.</para>
    /// </summary>
    public sealed class DirectTransport : ITransport
    {
        private const int Backlog = 16;
        private const int AcceptPollMicroseconds = 200000;
        private const int DisposeJoinMilliseconds = 1000;

        private enum EventKind : byte { Connected, Disconnected, Message }

        private struct DirectEvent
        {
            public EventKind Kind;
            public DirectConnection Connection;
            public Lane Lane;
            public byte[] Buffer;
            public int Count;
            public int Reason;
            public string Text;
        }

        private readonly ILog _log;
        private readonly string _localName;
        private readonly object _sync = new object();
        private readonly List<DirectConnection> _connections = new List<DirectConnection>();
        private readonly RingQueue<DirectEvent> _events = new RingQueue<DirectEvent>(64);
        private Socket _listener;
        private Thread _acceptThread;
        private ListenOptions _listen;
        private int _boundPort;
        private int _nextId;
        private int _strangerLogs;
        private volatile bool _disposed;

        public DirectTransport(ILog log, IClock clock, string localName)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _localName = localName ?? "direct";
        }

        public TransportKind Kind => TransportKind.Direct;

        /// <summary>The TCP port actually bound by <see cref="Listen"/> (0 before; a listen on port 0 picks a free one).</summary>
        public int BoundPort
        {
            get { lock (_sync) return _boundPort; }
        }

        internal IClock Clock { get; }

        internal ILog Log => _log;

        /// <summary>
        /// Opens a dual-mode (IPv6 and IPv4) TCP listen socket on <see cref="ListenOptions.IpPort"/>. Throws
        /// <see cref="DirectListenException"/> naming the port when it cannot be bound (usually: already in use).
        /// </summary>
        [ThreadAffinity(ThreadKind.Main)]
        public void Listen(ListenOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.MaxConnections <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxConnections must be positive");
            if (options.IpPort < 0 || options.IpPort > 65535) throw new ArgumentOutOfRangeException(nameof(options), "IpPort must be 0..65535");
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DirectTransport));
                if (_listener != null) throw new InvalidOperationException("DirectTransport already listens on port " + _boundPort.ToString(CultureInfo.InvariantCulture));
                Socket listener = OpenListener(options.IpPort);
                _listener = listener;
                _boundPort = ((IPEndPoint)listener.LocalEndPoint).Port;
                _listen = new ListenOptions
                {
                    IpPort = _boundPort,
                    AllowIp = true,
                    MaxConnections = options.MaxConnections,
                    MaxPendingConnections = options.MaxPendingConnections,
                    MaxPendingPerRemote = options.MaxPendingPerRemote
                };
                _acceptThread = new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "SkylineTogether direct accept" };
                _acceptThread.Start();
            }
            _log.Info(LogCategory.Net, _localName + ": listening for direct connections on TCP port " + BoundPort.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Connects to <see cref="Endpoint.Host"/>:<see cref="Endpoint.Port"/> in the background; the result arrives in <see cref="Poll"/>.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        public IConnection Connect(Endpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (string.IsNullOrEmpty(endpoint.Host)) throw new ArgumentException("a direct connection needs a host", nameof(endpoint));
            if (endpoint.Port <= 0 || endpoint.Port > 65535) throw new ArgumentException("a direct connection needs a port 1..65535", nameof(endpoint));
            var target = new Endpoint { Host = endpoint.Host.Trim(), Port = endpoint.Port };
            DirectConnection connection;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DirectTransport));
                connection = new DirectConnection(this, ++_nextId, new PeerIdentity(0, FormatAddress(target.Host, target.Port), null, true), target);
                _connections.Add(connection);
            }
            _log.Info(LogCategory.Net, _localName + ": connecting to " + connection.Peer);
            connection.Start();
            return connection;
        }

        /// <summary>
        /// Delivers the events that were pending when the call started, in order, and checks every connection's timers.
        /// A message buffer is valid only during its callback. Events of connections closed locally are dropped.
        /// </summary>
        [ThreadAffinity(ThreadKind.Network)]
        public void Poll(ITransportEventSink sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            DirectConnection[] open;
            int budget;
            lock (_sync)
            {
                if (_disposed) return;
                open = _connections.ToArray();
                budget = _events.Count;
            }
            long now = Clock.Ticks;
            foreach (DirectConnection connection in open) connection.CheckTimers(now);
            while (budget-- > 0)
            {
                DirectEvent e;
                lock (_sync)
                {
                    if (_disposed || _events.Count == 0) return;
                    e = _events.Dequeue();
                }
                try
                {
                    if (e.Connection.LocallyClosed) continue;
                    switch (e.Kind)
                    {
                        case EventKind.Connected:
                            sink.OnConnected(e.Connection);
                            break;
                        case EventKind.Disconnected:
                            sink.OnDisconnected(e.Connection, e.Reason, e.Text);
                            break;
                        default:
                            sink.OnMessage(e.Connection, e.Lane, e.Buffer, 0, e.Count);
                            break;
                    }
                }
                finally
                {
                    Release(ref e);
                }
            }
        }

        /// <summary>
        /// Stops listening and closes every connection (the other ends see <see cref="TransportCloseReason.TransportDisposed"/>),
        /// then waits a bounded time for their threads.
        /// </summary>
        public void Dispose()
        {
            Socket listener;
            Thread accept;
            DirectConnection[] open;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                listener = _listener;
                accept = _acceptThread;
                _listener = null;
                _listen = null;
                open = _connections.ToArray();
            }
            listener?.Dispose();
            accept?.Join(DisposeJoinMilliseconds);
            foreach (DirectConnection connection in open) connection.Close(TransportCloseReason.TransportDisposed, "the transport was disposed", false);
            // one shared deadline for the polite close, then the sockets are killed and the threads get one more
            long deadline = Clock.Ticks + DisposeJoinMilliseconds * TimeSpan.TicksPerMillisecond;
            foreach (DirectConnection connection in open)
            {
                if (!connection.Join(RemainingMilliseconds(deadline))) connection.Abort();
            }
            deadline = Clock.Ticks + DisposeJoinMilliseconds * TimeSpan.TicksPerMillisecond;
            foreach (DirectConnection connection in open)
            {
                if (!connection.Join(RemainingMilliseconds(deadline))) _log.Warn(LogCategory.Net, connection + ": threads did not stop");
            }
            var leftover = new List<DirectEvent>();
            lock (_sync)
            {
                while (_events.Count > 0) leftover.Add(_events.Dequeue());
                _connections.Clear();
            }
            for (int i = 0; i < leftover.Count; i++)
            {
                DirectEvent e = leftover[i];
                Release(ref e);   // outside the transport lock (Consumed takes the connection's)
            }
        }

        // ---- called by DirectConnection (it may hold its own lock: connection lock, then transport lock) ----

        internal void EnqueueConnected(DirectConnection connection)
        {
            lock (_sync) _events.Enqueue(new DirectEvent { Kind = EventKind.Connected, Connection = connection });
            _log.Info(LogCategory.Net, _localName + ": connected " + connection);
        }

        internal void EnqueueDisconnected(DirectConnection connection, int reason, string text)
        {
            lock (_sync) _events.Enqueue(new DirectEvent { Kind = EventKind.Disconnected, Connection = connection, Reason = reason, Text = text });
            _log.Info(LogCategory.Net, _localName + ": " + connection + " ended (" + reason.ToString(CultureInfo.InvariantCulture) + ": " + Diagnostics.AddressMask.InText(text) + ")");
        }

        internal void EnqueueMessage(DirectConnection connection, Lane lane, byte[] buffer, int count)
        {
            lock (_sync) _events.Enqueue(new DirectEvent { Kind = EventKind.Message, Connection = connection, Lane = lane, Buffer = buffer, Count = count });
        }

        internal void Remove(DirectConnection connection)
        {
            lock (_sync) _connections.Remove(connection);
        }

        /// <summary>Logs traffic from strangers (refusals, protocol violations) rate-limited: the first 20, then every 100th.</summary>
        internal void LogStranger(string message)
        {
            int n = Interlocked.Increment(ref _strangerLogs);
            if (n <= 20 || n % 100 == 0) _log.Info(LogCategory.Net, _localName + ": " + message + " (#" + n.ToString(CultureInfo.InvariantCulture) + ")");
        }

        // ---- internals ----

        private static Socket OpenListener(int port)
        {
            Socket socket = null;
            try
            {
                try
                {
                    socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
                    Bind(socket, new IPEndPoint(IPAddress.IPv6Any, port));
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported || ex.SocketErrorCode == SocketError.ProtocolNotSupported)
                {
                    // no IPv6 on this machine: IPv4 only
                    socket?.Dispose();
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    Bind(socket, new IPEndPoint(IPAddress.Any, port));
                }
                socket.Listen(Backlog);
                Socket result = socket;
                socket = null;
                return result;
            }
            catch (SocketException ex)
            {
                throw new DirectListenException(port, ex.SocketErrorCode, ex);
            }
            finally
            {
                socket?.Dispose();
            }
        }

        private static void Bind(Socket socket, IPEndPoint endpoint)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // Windows lets a second socket bind a port in use when it asks for address reuse; this one forbids that
                try
                {
                    socket.ExclusiveAddressUse = true;
                }
                catch (Exception ex) when (ex is SocketException || ex is NotSupportedException || ex is PlatformNotSupportedException)
                {
                    // not offered by this runtime
                }
            }
            socket.Bind(endpoint);
        }

        private void AcceptLoop(Socket listener)
        {
            while (!_disposed)
            {
                Socket accepted;
                try
                {
                    if (!listener.Poll(AcceptPollMicroseconds, SelectMode.SelectRead)) continue;
                    accepted = listener.Accept();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    if (_disposed) return;
                    LogStranger("accept failed (" + ex.SocketErrorCode.ToString() + ")");
                    continue;
                }
                try
                {
                    OnAccepted(accepted);
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Net, _localName + ": could not take a direct connection", ex);
                    accepted.Dispose();
                }
            }
        }

        private void OnAccepted(Socket socket)
        {
            socket.NoDelay = true;
            var remote = (IPEndPoint)socket.RemoteEndPoint;
            IPAddress ip = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
            string remoteKey = ip.ToString();
            var peer = new PeerIdentity(0, FormatAddress(remoteKey, remote.Port), null, true);
            DirectConnection connection;
            string refusal;
            lock (_sync)
            {
                if (_disposed || _listen == null)
                {
                    socket.Dispose();
                    return;
                }
                int admitted = 0, pending = 0, fromRemote = 0;
                foreach (DirectConnection c in _connections)
                {
                    if (!c.IsOpenIncoming) continue;
                    if (c.Admitted) admitted++;
                    else
                    {
                        pending++;
                        if (string.Equals(c.RemoteKey, remoteKey, StringComparison.Ordinal)) fromRemote++;
                    }
                }
                refusal = _listen.Refusal(admitted, pending, fromRemote);
                connection = new DirectConnection(this, ++_nextId, peer, socket, remoteKey, refusal);
                _connections.Add(connection);
                if (refusal != null) LogStranger("refused " + peer + " (" + refusal + ": " + admitted.ToString(CultureInfo.InvariantCulture) + " admitted, " + pending.ToString(CultureInfo.InvariantCulture) + " in the handshake)");
            }
            connection.Start();
        }

        /// <summary><c>ip:port</c>, an IPv6 address in brackets; a host name stays as given.</summary>
        private static string FormatAddress(string host, int port)
        {
            string p = port.ToString(CultureInfo.InvariantCulture);
            if (IPAddress.TryParse(host, out IPAddress ip))
            {
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                return ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + ip + "]:" + p : ip + ":" + p;
            }
            return host + ":" + p;
        }

        private int RemainingMilliseconds(long deadline) => (int)Math.Max(0, (deadline - Clock.Ticks) / TimeSpan.TicksPerMillisecond);

        private static void Release(ref DirectEvent e)
        {
            if (e.Buffer == null) return;
            e.Connection.Consumed(e.Count);
            BufferPool.Return(e.Buffer);
            e.Buffer = null;
        }
    }
}
