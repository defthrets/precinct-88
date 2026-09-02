using System;
using GTA;
using GTA.Math;
using Precinct88.Core;
using Precinct88.Response;
using Precinct88.Streets;

namespace Precinct88.Api
{
    /// <summary>
    /// The bridge. Everything another mod is allowed to touch, and nothing else.
    ///
    /// ONLY BCL TYPES CROSS THIS BOUNDARY -- string, int, float, bool, and Func/Action built out
    /// of those. That is not stylistic, it is the whole reason the bridge works. The other side
    /// calls in by REFLECTION, which means it has no reference to this assembly and cannot name
    /// any type declared in it; hand back a Vector3 or an Offence and the caller gets an object
    /// it can only poke at with more reflection. mscorlib is the one assembly both mods are
    /// guaranteed to agree about, so mscorlib is the vocabulary.
    ///
    /// WHY REFLECTION AND NOT A SHARED INTEROP DLL. A GTA scripts\ folder is one assembly
    /// resolution namespace. Two mods that both reference a third assembly must agree about its
    /// exact version forever -- and when they stop agreeing, the failure is a TypeLoadException
    /// at load with no log, because the thing that would have written the log is the thing that
    /// did not load. That fight has already been had on this machine over NAudio. Late binding
    /// cannot lose it: if Precinct 88 is not installed, Hoodrich finds nothing and carries on.
    ///
    /// EVERY METHOD HERE SWALLOWS ITS OWN EXCEPTIONS AND RETURNS SOMETHING SENSIBLE. An
    /// exception thrown across a reflection call surfaces at the other end as a
    /// TargetInvocationException wrapping something the caller has no type for -- so a throw
    /// here is a crash in a mod whose author cannot read the stack trace. Nothing thrown leaves
    /// this file.
    ///
    /// THIS IS A REWRITE AGAINST THE REBUILT MOD, NOT THE ORIGINAL FILE. The version under
    /// parked\ was written against Manhunt, and unparking it for the sake of one seizure
    /// handler would have dragged Manhunt, Radio, Profile, Record and Crime back in behind it.
    /// The SURFACE is unchanged -- still API v1, the same names and signatures -- so Hoodrich
    /// needs no rebuild and does not know the difference. What is behind each method now points
    /// at Callout, Fleet, LawHold and Search instead.
    ///
    /// Two things it can no longer answer honestly, and both say so rather than guessing: the
    /// hunt state is derived from whether a call is running instead of from a real search, and
    /// a report is weighted by keyword instead of by the severity table. Both come back when
    /// Manhunt does.
    /// </summary>
    public static class Dispatch
    {
        /// <summary>
        /// The contract version. Bumped when a signature here changes in a way that breaks.
        ///
        /// Read by the caller BEFORE anything else, and this is the reason the whole bridge is
        /// safe to change later: an old Hoodrich talking to a new Precinct 88 checks this
        /// number, does not like it, and quietly uses its own police instead of half-calling an
        /// API that has moved.
        /// </summary>
        public static int ApiVersion => Build.ApiVersion;

        /// <summary>The mod's version string, for the other side's log.</summary>
        public static string Version => Build.Version;

        // ---- what the host wires in --------------------------------------------

        /// <summary>
        /// Set by Main. Everything below is a thin, exception-proof shell over these.
        ///
        /// Nulls throughout are normal and are the state during load: SHVDN does not order
        /// script construction, so Hoodrich can and does find this class before Precinct 88's
        /// own Main has finished building. Ready() is how the other side finds that out.
        /// </summary>
        internal static Callout Calls;
        internal static Fleet Force;
        internal static Func<bool> InCustody;

        /// <summary>
        /// What the other mod does when the police search somebody.
        ///
        /// TAKE IT AND TELL ME WHAT YOU TOOK, in one call, rather than a query followed by a
        /// removal. Two calls with a window between them is a window in which the player is
        /// told he was robbed and was not, or is robbed twice.
        /// </summary>
        internal static Func<string, string> Seizer;

        /// <summary>Anything the other side wants told about. Optional.</summary>
        internal static Action<string> Events;

        // ---- what the other side calls -----------------------------------------

        /// <summary>Whether this mod is built and safe to talk to.</summary>
        public static bool Ready()
        {
            try
            {
                return Calls != null && Force != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tells the police something happened, somewhere.
        ///
        /// THE OFFENCE NAME IS MATCHED LOOSELY on purpose. The other side is a separate mod
        /// with its own vocabulary and no reference to any enum here, so a word that does not
        /// match exactly has to land somewhere sensible rather than be dropped. Anything
        /// unrecognised is a one-car call, which is the right way to be wrong.
        /// </summary>
        public static bool Report(string offence, float x, float y, float z)
        {
            try
            {
                if (Calls == null) return false;

                var name = string.IsNullOrEmpty(offence) ? "something reported" : offence;

                Calls.Report(name, new Vector3(x, y, z), Weigh(name));
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("A bridged report failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// How many cars a word is worth.
        ///
        /// KEYWORDS RATHER THAN A TABLE, and it is a stopgap that says so. Crime.cs holds the
        /// real severity model with a per-offence ceiling, and it is parked. Until it is back,
        /// gunfire words get two cars and everything else gets one -- which matches what this
        /// build does with its own offences and cannot be wildly wrong in either direction.
        /// </summary>
        private static int Weigh(string offence)
        {
            var s = offence.ToLowerInvariant();

            if (s.Contains("shot") || s.Contains("shoot") || s.Contains("gun") ||
                s.Contains("murder") || s.Contains("kill") || s.Contains("homicide"))
            {
                return 2;
            }

            return 1;
        }

        /// <summary>Calls everybody off, for a mod that has resolved its own scene.</summary>
        public static void ClearWanted(string why)
        {
            try
            {
                if (Calls != null) Calls.Clear(string.IsNullOrEmpty(why) ? "another mod" : why);

                var wanted = Game.Player.Wanted;

                if (wanted.WantedLevel > 0)
                {
                    wanted.SetWantedLevel(0, false);
                    wanted.ApplyWantedLevelChangeNow(false);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("A bridged clear failed: " + ex.Message);
            }
        }

        // ---- the law hold ------------------------------------------------------

        /// <summary>
        /// Holds the police off entirely while another mod runs a scene.
        ///
        /// This is the single most used thing on the bridge. Hoodrich holds the law for a gang
        /// war, a bike ride, a raid -- and a patrol car easing round the corner into a firefight
        /// it was not sent to is two officers walking into somebody else's scene.
        /// </summary>
        public static void HoldLaw(string who)
        {
            try { LawHold.Hold(who); }
            catch (Exception ex) { Log.Debug("A bridged hold failed: " + ex.Message); }
        }

        public static void ReleaseLaw(string who)
        {
            try { LawHold.Release(who); }
            catch (Exception ex) { Log.Debug("A bridged release failed: " + ex.Message); }
        }

        public static void CapLaw(int stars)
        {
            try { LawHold.Cap(stars); }
            catch (Exception ex) { Log.Debug("A bridged cap failed: " + ex.Message); }
        }

        public static void UncapLaw()
        {
            try { LawHold.Uncap(); }
            catch (Exception ex) { Log.Debug("A bridged uncap failed: " + ex.Message); }
        }

        public static bool LawIsHeld()
        {
            try { return LawHold.Held; }
            catch { return false; }
        }

        // ---- asking ------------------------------------------------------------

        /// <summary>
        /// Roughly what the police are doing: 0 nothing, 1 looking, 2 on to you.
        ///
        /// DERIVED RATHER THAN REPORTED, and the caller should treat it as a hint. The real
        /// article is Manhunt's own state -- tracked, searching, or seen -- and Manhunt is
        /// parked. A call being live is the closest honest answer this build can give.
        /// </summary>
        public static int HuntState()
        {
            try
            {
                if (Game.Player.Wanted.WantedLevel > 0) return 2;

                return Calls != null && Calls.Running ? 1 : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Whether the player is being dealt with and should not be interrupted.</summary>
        public static bool PlayerInCustody()
        {
            try
            {
                return InCustody != null && InCustody();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>How many of this mod's own units are on the road.</summary>
        public static int UnitsOut()
        {
            try { return Force == null ? 0 : Force.Count; }
            catch { return 0; }
        }

        /// <summary>
        /// Asks for a car at a particular place, for a mod running its own scene.
        ///
        /// Routed through Callout rather than tasking a unit directly, so a bridged request is
        /// subject to exactly the same finite pool, the same one-car-at-a-time, and the same
        /// drive time as anything this mod decides for itself. Returns false only when there is
        /// nothing to ask.
        /// </summary>
        public static bool SendUnit(float x, float y, float z, string reason)
        {
            try
            {
                if (Calls == null) return false;

                Calls.Report(string.IsNullOrEmpty(reason) ? "a request for a unit" : reason,
                             new Vector3(x, y, z), 1);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("A bridged unit request failed: " + ex.Message);
                return false;
            }
        }

        // ---- registering -------------------------------------------------------

        /// <summary>
        /// Registers what happens to somebody's pockets when the police search them.
        ///
        /// Func&lt;string,string&gt; crosses as itself: both sides get the delegate type from
        /// mscorlib, so the identity matches and nothing has to be reflected over to call it.
        /// The argument is why the search happened; the return is a short human phrase saying
        /// what was taken, or empty for nothing.
        /// </summary>
        public static void OnSeize(Func<string, string> handler)
        {
            try
            {
                Seizer = handler;
                Log.Info("A mod registered a seizure handler over the bridge.");
            }
            catch (Exception ex)
            {
                Log.Debug("A bridged seizure registration failed: " + ex.Message);
            }
        }

        /// <summary>Registers a sink for anything worth telling the other side about.</summary>
        public static void OnEvent(Action<string> handler)
        {
            try { Events = handler; }
            catch (Exception ex) { Log.Debug("A bridged event registration failed: " + ex.Message); }
        }

        /// <summary>Tells the other side something, if it asked to be told. Never throws.</summary>
        internal static void Said(string what)
        {
            var sink = Events;
            if (sink == null) return;

            try { sink(what); }
            catch (Exception ex) { Log.Debug("A bridged event handler threw: " + ex.Message); }
        }
    }
}
