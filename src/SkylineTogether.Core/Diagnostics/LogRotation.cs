using System;
using System.IO;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// Keeps the logs of the last runs (ARCHITECTURE.md#logs): the game's logger starts <c>SkylineTogether.log</c> anew with
    /// every game start, so a game that froze and was restarted lost the log that said why. Before the first line of a
    /// run, <c>X.log</c> becomes <c>X-1.log</c>, <c>X-1.log</c> becomes <c>X-2.log</c> and so on; the oldest goes.
    /// Never throws. Main thread, before the log is opened.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public static class LogRotation
    {
        /// <summary>The name of the <paramref name="index"/>-th previous run's log (1 = the last run).</summary>
        public static string PreviousPath(string path, int index)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "-" + index + Path.GetExtension(path));
        }

        /// <summary>
        /// Shifts <paramref name="path"/> and its previous runs by one, keeping <paramref name="keep"/> previous runs.
        /// Returns null when it worked (or there was nothing to keep), else what failed (no paths: the text is logged).
        /// </summary>
        public static string Rotate(string path, int keep)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || keep < 1 || !File.Exists(path)) return null;
                string oldest = PreviousPath(path, keep);
                if (File.Exists(oldest)) File.Delete(oldest);
                for (int i = keep - 1; i >= 1; i--)
                {
                    string from = PreviousPath(path, i);
                    if (File.Exists(from)) File.Move(from, PreviousPath(path, i + 1));
                }
                File.Move(path, PreviousPath(path, 1));
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }
    }
}
