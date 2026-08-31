using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Precinct88.Core
{
    /// <summary>
    /// Where this mod reads and writes.
    ///
    /// Assembly.Location is NOT usable here. ScriptHookVDotNet shadow-copies every script into
    /// the .NET download cache before running it, so Location reports somewhere under
    /// AppData\Local\assembly\dl3 -- a folder that has never held an ini and never will. A mod
    /// that trusts it looks for its settings beside the COPY, finds nothing, and runs on
    /// built-in defaults while printing a line that reads like a missing file rather than a mod
    /// looking in the wrong place. That cost Hoodrich several days; it is not repeated here.
    ///
    /// So several candidates are tested against files we know we shipped, and the first that
    /// actually holds them wins.
    /// </summary>
    internal static class Paths
    {
        private static string _scripts;

        /// <summary>The scripts folder the game loaded this dll from.</summary>
        public static string Scripts
        {
            get
            {
                if (_scripts != null) return _scripts;

                var candidates = new List<string>();

                // SHVDN builds its script AppDomain with the scripts folder as the base.
                TryAdd(candidates, SafeGet(() => AppDomain.CurrentDomain.BaseDirectory));

                var cwd = SafeGet(Directory.GetCurrentDirectory);
                if (!string.IsNullOrEmpty(cwd))
                {
                    TryAdd(candidates, Path.Combine(cwd, "scripts"));
                    TryAdd(candidates, cwd);
                }

                // CodeBase survives a shadow copy where Location does not.
                TryAdd(candidates, SafeGet(() =>
                {
                    var code = Assembly.GetExecutingAssembly().CodeBase;
                    return string.IsNullOrEmpty(code)
                        ? null
                        : Path.GetDirectoryName(new Uri(code).LocalPath);
                }));

                TryAdd(candidates, SafeGet(() =>
                {
                    var loc = Assembly.GetExecutingAssembly().Location;
                    return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
                }));

                foreach (var dir in candidates)
                {
                    if (LooksLikeOurFolder(dir)) { _scripts = dir; return _scripts; }
                }

                _scripts = candidates.Count > 0 ? candidates[0] : cwd ?? ".";
                return _scripts;
            }
        }

        /// <summary>True when this folder holds the files the deploy puts down.</summary>
        private static bool LooksLikeOurFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, "Precinct88.ini"))) return true;

                var data = Path.Combine(dir, "Precinct88");
                return Directory.Exists(data) &&
                       File.Exists(Path.Combine(data, "stations.json"));
            }
            catch
            {
                return false;
            }
        }

        private static void TryAdd(List<string> list, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                dir = Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar));
                if (Directory.Exists(dir) && !list.Contains(dir)) list.Add(dir);
            }
            catch
            {
                // Unusable path; skip it.
            }
        }

        private static string SafeGet(Func<string> get)
        {
            try { return get(); }
            catch { return null; }
        }

        /// <summary>The shipped data files. Read-only as far as we care.</summary>
        public static string Data
        {
            get
            {
                var d = Path.Combine(Scripts, "Precinct88");
                EnsureDir(d);
                return d;
            }
        }

        private static string _writable;

        /// <summary>
        /// Where the log and the custody record go.
        ///
        /// The game is normally installed under Program Files, which is NOT writable by an
        /// unelevated process -- and GTA5.exe is unelevated. Reads work fine, so the shipped
        /// data loads, but every write silently fails: no log and no save, with nothing on
        /// screen to say so.
        ///
        /// A save that already exists wins over a folder that merely happens to be writable.
        /// Permissions move -- somebody runs the game as administrator once, or an installer
        /// resets an ACL -- and choosing purely on "can I write here" then finds no save in
        /// the newly-writable folder and starts them clean, with the real record sitting
        /// untouched in Documents forever.
        /// </summary>
        public static string Writable
        {
            get
            {
                if (_writable != null) return _writable;

                var preferred = Path.Combine(Scripts, "Precinct88");
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Precinct88");

                if (IsWritable(preferred))
                {
                    if (!HasSave(preferred) && HasSave(fallback))
                    {
                        // ASSIGNED BEFORE IT IS LOGGED. Log.Info asks for Paths.LogFile, which
                        // asks for Paths.Writable -- so logging first re-enters this getter
                        // with _writable still null, takes this same branch and logs again.
                        // That is not an exception anything can catch, it is a stack overflow,
                        // and it lands on exactly the installs this branch exists to rescue.
                        _writable = fallback;

                        Log.Info("Record found in " + fallback + " rather than beside the dll. " +
                                 "Using it, so the permissions on the game folder cannot move " +
                                 "a playthrough.");

                        return _writable;
                    }

                    _writable = preferred;
                    return _writable;
                }

                try
                {
                    if (!Directory.Exists(fallback)) Directory.CreateDirectory(fallback);
                }
                catch
                {
                    fallback = Path.Combine(Path.GetTempPath(), "Precinct88");
                    try { if (!Directory.Exists(fallback)) Directory.CreateDirectory(fallback); }
                    catch { /* nothing left to try */ }
                }

                _writable = fallback;
                return _writable;
            }
        }

        /// <summary>
        /// Whether a record lives here.
        ///
        /// The backup counts. A file mid-write is still a record, and the whole point of the
        /// .bak is that it is the one still readable when the other one is not.
        /// </summary>
        private static bool HasSave(string dir)
        {
            try
            {
                return File.Exists(Path.Combine(dir, "record.json")) ||
                       File.Exists(Path.Combine(dir, "record.json.bak"));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                EnsureDir(dir);

                var probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureDir(string dir)
        {
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { /* the caller finds out when it writes */ }
        }

        public static string Ini => Path.Combine(Scripts, "Precinct88.ini");
        public static string LogFile => Path.Combine(Writable, "Precinct88.log");
        public static string SaveFile => Path.Combine(Writable, "record.json");
        public static string StationsFile => Path.Combine(Data, "stations.json");

        /// <summary>
        /// The HUD art, beside the data rather than loose in scripts\.
        ///
        /// Read-only as far as this mod is concerned, so it hangs off Data and not Writable --
        /// a player whose game folder is unwritable still has the icons, because the deploy put
        /// them there.
        /// </summary>
        public static string Icons => Path.Combine(Data, "icons");
    }
}
