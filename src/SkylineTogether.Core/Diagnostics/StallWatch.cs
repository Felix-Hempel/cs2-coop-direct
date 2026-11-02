using System;
using System.Globalization;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// Notices a main thread that stopped returning frames (an endless loop, a deadlock, a job the frame waits on forever)
    /// and says where it stopped (ARCHITECTURE.md#logs). The main thread calls <see cref="Beat"/> once per frame and marks
    /// the mod's sections with <see cref="Enter"/> / <see cref="Exit"/> and its timed steps with <see cref="Step"/>; a
    /// watcher thread calls <see cref="Check"/> about once per second and logs what it returns. A stall is reported after
    /// <see cref="FirstReportSeconds"/> and again at growing intervals; the first frame after it reports how long it lasted.
    /// Nothing is reported while the watch is not armed (outside a session). Pure: time is passed in. Thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class StallWatch
    {
        /// <summary>Seconds without a frame before the first report (loading a large city stalls a few seconds).</summary>
        public double FirstReportSeconds = 10;

        /// <summary>Later reports at these stall lengths, then every <see cref="RepeatSeconds"/>.</summary>
        private static readonly double[] LaterReports = { 30, 60, 120 };

        public double RepeatSeconds = 120;

        private readonly object _lock = new object();
        private bool _armed;
        private double _beatAt = double.NaN;
        private long _frame;
        private string _section;
        private double _sectionAt;
        private string _lastSection;
        private double _lastSectionLeftAt;
        private string _lastStep;
        private double _lastStepAt;
        private int _reports;
        private double _nextReportAt = double.NaN;
        private string _stallWhere;

        /// <summary>Stalls reported so far (diagnostics).</summary>
        public long Stalls { get; private set; }

        /// <summary>
        /// Main thread, once per frame: <paramref name="armed"/> is false outside a session. Returns the line that reports
        /// the end of a stall that lasted at least <see cref="FirstReportSeconds"/>, or null.
        /// </summary>
        public string Beat(double now, long frame, bool armed)
        {
            lock (_lock)
            {
                string line = null;
                double gap = now - _beatAt;
                if (_armed && armed && gap >= FirstReportSeconds)
                {
                    // where it stood is only known while it stands: the watcher's first report saw it
                    line = "main thread responding again after " + Seconds(gap) + " without a frame (frame " + _frame + " to " + frame + "); "
                           + (_stallWhere ?? "the watcher did not see it stand (where it stood is unknown)");
                    if (_reports == 0) Stalls++;
                }
                _armed = armed;
                _beatAt = now;
                _frame = frame;
                _reports = 0;
                _stallWhere = null;
                _nextReportAt = now + FirstReportSeconds;
                return line;
            }
        }

        /// <summary>Main thread: the mod entered a section (a system's update, the session tick).</summary>
        public void Enter(string section, double now)
        {
            lock (_lock)
            {
                _section = section;
                _sectionAt = now;
            }
        }

        /// <summary>Main thread: the section entered last was left.</summary>
        public void Exit(double now)
        {
            lock (_lock)
            {
                if (_section == null) return;
                _lastSection = _section;
                _lastSectionLeftAt = now;
                _section = null;
            }
        }

        /// <summary>Main thread: a timed step inside a section finished (the perf counters' component names).</summary>
        public void Step(string step, double now)
        {
            lock (_lock)
            {
                _lastStep = step;
                _lastStepAt = now;
            }
        }

        /// <summary>Watcher thread: the line to log when the main thread has stalled long enough for the next report; else null.</summary>
        public string Check(double now)
        {
            lock (_lock)
            {
                if (!_armed || double.IsNaN(_beatAt) || now < _nextReportAt) return null;
                double stalled = now - _beatAt;
                if (_reports == 0) Stalls++;
                _reports++;
                _nextReportAt = _beatAt + (_reports <= LaterReports.Length ? LaterReports[_reports - 1] : LaterReports[LaterReports.Length - 1] + (_reports - LaterReports.Length) * RepeatSeconds);
                string where = Where(now);
                if (_reports == 1) _stallWhere = "it stood " + where;
                return (_reports == 1 ? "main thread stalled: no frame for " : "main thread still stalled: no frame for ") + Seconds(stalled)
                       + " (last frame " + _frame + "); " + where;
            }
        }

        private string Where(double now)
        {
            string text = _section != null
                ? "inside the mod's section '" + _section + "' (entered " + Seconds(now - _sectionAt) + " before)"
                : "outside the mod's code (the game's systems or a job the frame waits for)"
                  + (_lastSection != null ? "; the mod's last section '" + _lastSection + "' was left " + Seconds(now - _lastSectionLeftAt) + " before" : "");
            if (_lastStep != null) text += "; last timed step '" + _lastStep + "' finished " + Seconds(now - _lastStepAt) + " before";
            return text;
        }

        private static string Seconds(double s) => Math.Max(0, s).ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }
}
