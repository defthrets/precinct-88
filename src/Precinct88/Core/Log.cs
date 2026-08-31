using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Precinct88.Core
{
    internal enum LogLevel
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3
    }

    /// <summary>
    /// File logger for Precinct88.log.
    ///
    /// Every method swallows its own exceptions. A logger that can throw takes the whole
    /// script down from inside a Tick handler, which is precisely the moment the log is the
    /// only thing that would have told you why.
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 2 * 1024 * 1024;

        private static readonly object Gate = new object();
        private static bool _started;

        public static LogLevel Level = LogLevel.Info;

        public static void Error(string message, Exception ex = null) => Write(LogLevel.Error, message, ex);
        public static void Warn(string message) => Write(LogLevel.Warn, message, null);
        public static void Info(string message) => Write(LogLevel.Info, message, null);
        public static void Debug(string message) => Write(LogLevel.Debug, message, null);

        private static void Write(LogLevel level, string message, Exception ex)
        {
            if (level > Level) return;

            try
            {
                lock (Gate)
                {
                    var path = Paths.LogFile;

                    if (!_started)
                    {
                        RollIfLarge(path);
                        _started = true;
                        AppendLine(path, "");
                        AppendLine(path, "=== " + Build.Name + " " + Build.Version + " by " + Build.By +
                                         " started " +
                                         DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                                                               CultureInfo.InvariantCulture) + " ===");
                    }

                    var sb = new StringBuilder();
                    sb.Append('[')
                      .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                      .Append("] ");
                    sb.Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(' ');
                    sb.Append(message);

                    if (ex != null)
                    {
                        sb.AppendLine();
                        sb.Append("    ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            sb.AppendLine();
                            sb.Append(ex.StackTrace);
                        }

                        if (ex.InnerException != null)
                        {
                            sb.AppendLine();
                            sb.Append("    inner: ").Append(ex.InnerException.GetType().Name)
                              .Append(": ").Append(ex.InnerException.Message);
                        }
                    }

                    AppendLine(path, sb.ToString());
                }
            }
            catch
            {
                // Logging must never be the reason a script dies.
            }
        }

        private static void AppendLine(string path, string line)
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }

        private static void RollIfLarge(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;

                var old = path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            catch
            {
                // A locked or unrollable log is not worth failing over.
            }
        }
    }

    /// <summary>
    /// What this thing is called, in the one place anything is allowed to ask.
    ///
    /// The file names are a separate matter and stay as they are: Precinct88.dll,
    /// Precinct88.ini, Precinct88.log and the folder beside them are PATHS, and renaming a
    /// path breaks every installation that exists. This is the word people read.
    /// </summary>
    internal static class Build
    {
        /// <summary>
        /// 0.1.0 -- the first build with all four systems in it.
        ///
        /// Ambient patrol on a beat, a wanted system that searches rather than knows, contact
        /// that starts with a reason, and custody that ends somewhere other than a hospital
        /// bill.
        /// </summary>
        public const string Version = "0.1.0";

        public const string Name = "Precinct 88";
        public const string By = "spitmux";

        /// <summary>
        /// The name Hoodrich looks for over the reflection seam, and the version it checks.
        ///
        /// Both mods are shipped separately and either can be updated without the other, so
        /// the bridge is version-gated rather than assumed. See Api.Dispatch.
        /// </summary>
        public const int ApiVersion = 1;
    }
}
