using System;
using System.Globalization;
using System.Net.Sockets;

namespace SkylineTogether.Core.Transport.Direct
{
    /// <summary>
    /// <see cref="DirectTransport.Listen"/> could not open its TCP port, usually because another program (or an old mod
    /// server) already listens on it. <see cref="Port"/> names the port so the game can tell the player which one.
    /// </summary>
    public sealed class DirectListenException : Exception
    {
        public DirectListenException(int port, SocketError error, Exception inner)
            : base(Describe(port, error), inner)
        {
            Port = port;
            Error = error;
        }

        /// <summary>The port that could not be opened.</summary>
        public int Port { get; }

        /// <summary>The socket error of the failed bind or listen.</summary>
        public SocketError Error { get; }

        private static string Describe(int port, SocketError error)
        {
            string p = port.ToString(CultureInfo.InvariantCulture);
            return error == SocketError.AddressAlreadyInUse
                ? "TCP port " + p + " is already in use (another program or an older co-op server listens on it)"
                : "could not listen on TCP port " + p + " (" + error.ToString() + ")";
        }
    }
}
