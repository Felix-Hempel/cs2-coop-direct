using System;
using System.Diagnostics;
using System.Threading;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// The game layer of <see cref="StallWatch"/>: a background thread that logs when the main thread stops returning
    /// frames during a session, with the mod section it stood in (or that it stood outside the mod's code). A freeze
    /// otherwise leaves only a log that ends. The driver beats once per frame; the mod's systems mark their sections with
    /// <see cref="Enter"/> / <see cref="Exit"/>, and <see cref="Perf.Stop"/> notes each timed step. The marks cost a lock
    /// and two field writes each.
    /// </summary>
    public static class HangWatch
    {
        private static readonly StallWatch Watch = new StallWatch();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static Thread _thread;
        private static volatile bool _stop;
        private static GameLog _log;

        private static double Now => Clock.Elapsed.TotalSeconds;

        /// <summary>Stalls seen this run (diagnostics).</summary>
        public static long Stalls => Watch.Stalls;

        public static void Start(GameLog log)
        {
            if (_thread != null) return;
            _log = log;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "SkylineTogether.HangWatch", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public static void Stop()
        {
            _stop = true;
            _thread = null;
        }

        /// <summary>Main thread, once per frame; <paramref name="inSession"/> arms the watch.</summary>
        public static void Beat(long frame, bool inSession)
        {
            string line = Watch.Beat(Now, frame, inSession);
            if (line != null) _log?.Warn(LogCategory.Forensics, line);
        }

        public static void Enter(string section) => Watch.Enter(section, Now);

        public static void Exit() => Watch.Exit(Now);

        public static void Step(string step) => Watch.Step(step, Now);

        private static void Run()
        {
            while (!_stop)
            {
                try
                {
                    Thread.Sleep(1000);
                    string line = Watch.Check(Now);
                    // GameLog is thread-safe (the flood guard and the game's logger lock; every line is flushed)
                    if (line != null) _log?.Warn(LogCategory.Forensics, line);
                }
                catch (ThreadInterruptedException)
                {
                    return;
                }
                catch (Exception)
                {
                    // never take the game down from a diagnostics thread
                }
            }
        }
    }
}
