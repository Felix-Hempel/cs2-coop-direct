using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The referrer index stays bounded: despawned movers are removed, a background sweep prunes gone referrers.</summary>
    public sealed class ReferrerIndexGrowthTests
    {
        [Test]
        public void APairCanBeRemovedFromSmallAndLargeChains()
        {
            var index = new AgentReferrerIndex<int>();
            index.Add(1, 10);
            index.Add(1, 11);
            Assert.True(index.Remove(1, 10));
            Assert.False(index.Remove(1, 10));
            Assert.False(index.Contains(1, 10));
            Assert.True(index.Contains(1, 11));
            Assert.True(index.Remove(1, 11));
            Assert.Equal(0, index.Targets);
            for (int r = 100; r < 100 + AgentReferrerIndex<int>.LargeChain + 5; r++) index.Add(2, r);
            Assert.Equal(1, index.LargeTargets);
            Assert.True(index.Remove(2, 101));
            Assert.False(index.Contains(2, 101), "the large chain's set forgets it too");
            Assert.True(index.Add(2, 101), "and it can be added again");
        }

        [Test]
        public void ReAddingAKnownPairBehindAPrunedHeadKeepsTheChainIntact()
        {
            // chain of target 1: 11 -> 10; 11 is gone, 10 is added again (the sweep re-indexes every holder)
            var index = new AgentReferrerIndex<int>();
            index.Add(1, 10);
            index.Add(1, 11);
            Assert.False(index.Add(1, 10, r => r != 11), "the pair is known");
            // the node of 11 went back to the pool and is handed out again; target 1 must not still start at it
            index.Add(2, 20);
            var left = new List<int>();
            Assert.Equal(1, index.Get(1, left));
            Assert.Equal(10, left[0]);
            Assert.False(index.Contains(1, 20), "target 1 shares no node with target 2");
            Assert.Equal(2, index.Count);
            Assert.Equal(1, index.RemoveTarget(1));
            Assert.True(index.Contains(2, 20), "removing target 1 leaves target 2 alone");
        }

        [Test]
        public void ACycleClearsTheIndexInsteadOfWalkingForever()
        {
            var next = typeof(AgentReferrerIndex<int>).GetField("_next", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            AgentReferrerIndex<int> Cyclic()
            {
                var index = new AgentReferrerIndex<int>();
                index.Add(1, 10); // node 0
                index.Add(1, 11); // node 1 -> node 0
                ((int[])next.GetValue(index))[0] = 1; // node 0 -> node 1: a cycle
                return index;
            }
            var a = Cyclic();
            Assert.False(a.Contains(1, 99));
            Assert.Equal(1L, a.CorruptResets);
            Assert.Equal(0, a.Count, "cleared");
            var b = Cyclic();
            var output = new List<int>();
            Assert.Equal(0, b.Get(1, output));
            Assert.Equal(0, output.Count, "nothing half-walked is handed out");
            Assert.Equal(1L, b.CorruptResets);
            var c = Cyclic();
            c.RemoveTarget(1);
            Assert.Equal(1L, c.CorruptResets);
            var d = Cyclic();
            Assert.True(d.Add(1, 12, r => true), "added to the cleared index");
            Assert.True(d.Contains(1, 12));
            var e = Cyclic();
            e.Sweep(r => true, 10);
            Assert.Equal(1L, e.CorruptResets);
            Assert.False(e.Remove(1, 99));
        }

        [Test]
        public void TheSweepPrunesGoneReferrersAndTargetsInSlices()
        {
            var index = new AgentReferrerIndex<int>();
            var gone = new HashSet<int>();
            // a depot (target 1) and the vehicles it ever owned
            for (int v = 1000; v < 1200; v++) index.Add(1, v);
            for (int t = 2; t < 12; t++) index.Add(t, 5000 + t);
            for (int v = 1000; v < 1190; v++) gone.Add(v); // most of them are long gone
            gone.Add(5); // a gone target
            index.Add(5, 6000);
            bool Alive(int e) => !gone.Contains(e);
            int removed = 0;
            for (int i = 0; i < 12; i++) removed += index.Sweep(Alive, 1);
            Assert.Equal(192, removed, "190 gone vehicles and the gone target's two entries");
            Assert.Equal(10 + 9, index.Count);
            var left = new List<int>();
            index.Get(1, left);
            Assert.Equal(10, left.Count);
            Assert.Equal(0, index.Get(5, left = new List<int>()));
        }
    }
}
