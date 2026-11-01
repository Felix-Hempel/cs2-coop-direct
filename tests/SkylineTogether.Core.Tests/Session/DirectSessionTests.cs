using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;
using SkylineTogether.Core.Transport.Direct;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The session layer for direct (IP) peers without a Steam identity: the host proof is always required, a refused join
    /// without a host password, a rejoin from the same IP replacing the ghost, and the fork's protocol number.
    /// </summary>
    public sealed class DirectSessionTests
    {
        private static SessionClient AddDirect(SessionRig rig, string address, string name, string password)
        {
            LoopbackTransport t = rig.HostTransport.CreateClient(new PeerIdentity(0, address, null, true));
            var info = new FakeInfo();
            var loader = new FakeLoader();
            var client = new SessionClient(t, info, loader, new ClientOptions { PlayerName = name, Password = password }, rig.Clock, rig.Log);
            client.Connect(new Endpoint());
            rig.Clients.Add((client, loader, info));
            rig.ClientTransports.Add(t);
            return client;
        }

        [Test]
        public void DirectTransportAlwaysRequiresHostProof()
        {
            using (var transport = new DirectTransport(NullLog.Instance, new ManualClock(0), "client"))
            {
                Assert.Equal(TransportKind.Direct, transport.Kind);
                var client = new SessionClient(transport, new FakeInfo(), new FakeLoader(), new ClientOptions { PlayerName = "A", ExpectPassword = false }, new ManualClock(0), NullLog.Instance);
                Assert.True(client.HostProofRequired, "a direct host always has a password and must prove it");
            }
        }

        [Test]
        public void DirectJoinWithoutHostPasswordIsRefused()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty" });
            SessionClient client = AddDirect(rig, "100.64.1.2:51000", "Direct", "");
            rig.Run(300);
            Assert.Equal(ClientEndKind.Refused, client.EndKind, client.EndReason);
            Assert.Equal(RefuseReason.NotInvited, client.RefusedFor);
        }

        [Test]
        public void DirectRejoinFromSameIpReplacesGhost()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            SessionClient a = AddDirect(rig, "100.64.1.2:51000", "Direct", "pw");
            rig.Run(400);
            Assert.Equal(ClientState.Live, a.State, a.EndReason);

            SessionClient b = AddDirect(rig, "100.64.1.2:51077", "Direct", "pw");
            rig.Run(600);

            Assert.Equal(ClientState.Live, b.State, b.EndReason);
            Assert.NotEqual(ClientState.Live, a.State);
            Assert.Equal(2, rig.Host.GetPlayers().Count, "the host and the new connection only");
            Assert.True(rig.Log.Contains("connected again; dropping the old connection"), "the replacement is logged like a Steam rejoin");
        }

        [Test]
        public void DirectPeersFromDifferentIpsCoexist()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            SessionClient a = AddDirect(rig, "100.64.1.2:51000", "One", "pw");
            SessionClient b = AddDirect(rig, "100.64.1.3:51000", "Two", "pw");
            rig.Run(600);
            Assert.Equal(ClientState.Live, a.State, a.EndReason);
            Assert.Equal(ClientState.Live, b.State, b.EndReason);
            Assert.Equal(3, rig.Host.GetPlayers().Count);
        }

        [Test]
        public void DirectKeyStripsPortAndBrackets()
        {
            Assert.Equal("100.64.1.2", SessionHost.DirectKey("100.64.1.2:51000"));
            Assert.Equal("fd7a::1", SessionHost.DirectKey("[fd7a::1]:5"));
        }

        [Test]
        public void ForkProtocolDiffersFromUpstream()
        {
            Assert.Equal(1009, ProtocolVersion.Current);
        }
    }
}
