using System;
using System.IO;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>A frozen main thread is reported with where it stood, and the logs of earlier runs are kept.</summary>
    public sealed class StallDiagnosticsTests
    {
        [Test]
        public void AStallInsideASectionIsReportedWithTheSectionAndItsEnd()
        {
            var w = new StallWatch();
            Assert.Null(w.Beat(0, 100, true));
            w.Enter("apply", 0.1);
            w.Step("agent apply", 0.2);
            Assert.Null(w.Check(5), "5 s is no stall yet");
            string first = w.Check(10.5);
            Assert.NotNull(first);
            Assert.Contains("stalled: no frame for 10.5 s", first);
            Assert.Contains("section 'apply'", first);
            Assert.Contains("last timed step 'agent apply'", first);
            Assert.Null(w.Check(11), "not every second");
            Assert.Contains("still stalled", w.Check(30.5));
            w.Exit(40);
            string back = w.Beat(40, 101, true);
            Assert.Contains("responding again after 40.0 s", back);
            Assert.Contains("section 'apply'", back, "where it stood, not where it went on");
            Assert.Equal(1L, w.Stalls);
            Assert.Null(w.Check(45), "a fresh frame resets the watch");
        }

        [Test]
        public void AStallOutsideTheModNamesTheLastSectionLeft()
        {
            var w = new StallWatch();
            w.Beat(0, 1, true);
            w.Enter("capture", 0.1);
            w.Exit(0.2);
            string line = w.Check(12);
            Assert.Contains("outside the mod's code", line);
            Assert.Contains("last section 'capture'", line);
        }

        [Test]
        public void NothingIsReportedOutsideASession()
        {
            var w = new StallWatch();
            w.Beat(0, 1, false);
            Assert.Null(w.Check(100));
            Assert.Null(w.Beat(100, 2, true), "the gap began outside the session");
            Assert.Null(w.Beat(150, 3, false), "and a gap that ends it is a loading screen, not a stall");
        }

        [Test]
        public void TheLogsOfEarlierRunsAreKept()
        {
            string dir = Path.Combine(Path.GetTempPath(), "st-logrot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string log = Path.Combine(dir, "SkylineTogether.log");
                Assert.Null(LogRotation.Rotate(log, 3), "no log yet: nothing to do");
                for (int run = 1; run <= 5; run++)
                {
                    Assert.Null(LogRotation.Rotate(log, 3));
                    File.WriteAllText(log, "run " + run);
                }
                Assert.Equal("run 5", File.ReadAllText(log));
                Assert.Equal("run 4", File.ReadAllText(Path.Combine(dir, "SkylineTogether-1.log")));
                Assert.Equal("run 2", File.ReadAllText(Path.Combine(dir, "SkylineTogether-3.log")));
                Assert.False(File.Exists(Path.Combine(dir, "SkylineTogether-4.log")), "only three earlier runs");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
