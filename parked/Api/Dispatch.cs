using System;
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
    /// resolution namespace. Two mods that both reference a third assembly are two mods that
    /// must agree about its exact version forever -- and when they stop agreeing, the failure is
    /// a TypeLoadException at load with no log, because the thing that would have written the
    /// log is the thing that did not load. That fight has already been had on this machine over
    /// NAudio. Late binding cannot lose it: if Precinct 88 is not installed, Hoodrich finds
    /// nothing and carries on; if it is a different version, ApiVersion says so and Hoodrich
    /// carries on.
    ///
    /// EVERY METHOD HERE SWALLOWS ITS OWN EXCEPTIONS AND RETURNS SOMETHING SENSIBLE. An
    /// exception thrown across a reflection call surfaces at the other end as a
    /// TargetInvocationException wrapping something the caller has no type for -- so a throw
    /// here is a crash in a mod whose author cannot read the stack trace. Nothing thrown leaves
    /// this file.
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
        internal static Manhunt Chase;
        internal static Fleet Force;
        internal static Func<bool> InCustody;

        /// <summary>Whether the mod is actually up and its systems are wired.</summary>
        public static bool Ready()
        {
            try { return Chase != null && Force != null; }
            catch { return false; }
        }

        // ---- telling us about a crime ------------------------------------------

        /// <summary>
        /// Reports something to the police at a point in the world.
        ///
        /// The name is matched loosely against this mod's own offence list -- see Crime.Parse,
        /// which understands both our names and the words another mod is likelier to reach for
        /// ("drugs", "gun", "murder"). An unrecognised name is treated as the mildest thing
        /// there is rather than rejected, because a bridge that returns false for a typo is a
        /// bridge where crimes silently do not happen.
        ///
        /// Returns whether it was recognised, so the caller can log its own mistake.
        /// </summary>
        public static bool Report(string offence, float x, float y, float z)
        {
            try
            {
                if (Chase == null) return false;

                Offence what;
                var known = Crime.Parse(offence, out what);

                // DESCRIBED, because a mod reporting a crime is reporting one it watched
                // happen. Hoodrich's busts are called in by an undercover officer stood in
                // front of you; defaulting to "nobody could describe him" would quietly make
                // every bust in that mod unsolvable.
                //
                // Kept out of the signature deliberately -- the caller binds Report by its
                // exact parameter types, and adding one would be an API break for a default
                // that is right in every case anybody has yet.
                Chase.Report(what, new Vector3(x, y, z),
                             Known.Face | Known.Clothes | Known.Vehicle);

                if (!known) Log.Warn("Bridge: unknown offence '" + offence + "'; treated as " + what + ".");

                return known;
            }
            catch (Exception ex)
            {
                Log.Debug("Bridge report failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Drops whatever is running. For a scene starting, or a mission taking over.</summary>
        public static void ClearWanted(string why)
        {
            try { if (Chase != null) Chase.Clear(string.IsNullOrEmpty(why) ? "another mod asked" : why); }
            catch (Exception ex) { Log.Debug("Bridge clear failed: " + ex.Message); }
        }

        // ---- the shared law hold -----------------------------------------------

        /// <summary>
        /// Holds the police off, counted, on behalf of the caller.
        ///
        /// THIS IS THE MOST IMPORTANT METHOD ON THE BRIDGE. Hoodrich has its own counted hold
        /// for gang wars and bike rides, and this mod has one for bookings. Two counted holds
        /// that do not know about each other is precisely the bug either of them was written to
        /// prevent, one layer up: whichever finishes first hands the police back to the other,
        /// so a raid on your own block brings a helicopter.
        ///
        /// With this wired, Hoodrich's LawHold delegates here and there is one arbiter for
        /// both mods. The token is the caller's own name for whatever is holding.
        /// </summary>
        public static void HoldLaw(string who)
        {
            try { LawHold.Hold(who); }
            catch (Exception ex) { Log.Debug("Bridge hold failed: " + ex.Message); }
        }

        public static void ReleaseLaw(string who)
        {
            try { LawHold.Release(who); }
            catch (Exception ex) { Log.Debug("Bridge release failed: " + ex.Message); }
        }

        /// <summary>Lids the wanted level without switching the police off.</summary>
        public static void CapLaw(int stars)
        {
            try { LawHold.Cap(stars); }
            catch (Exception ex) { Log.Debug("Bridge cap failed: " + ex.Message); }
        }

        public static void UncapLaw()
        {
            try { LawHold.Uncap(); }
            catch (Exception ex) { Log.Debug("Bridge uncap failed: " + ex.Message); }
        }

        public static bool LawIsHeld()
        {
            try { return LawHold.Held; }
            catch { return false; }
        }

        // ---- asking us what is going on ----------------------------------------

        /// <summary>
        /// 0 nothing, 1 they can see you, 2 they are searching for you.
        ///
        /// An int rather than the enum, because the enum is a type in this assembly and the
        /// caller cannot name it. Ugly, and the alternative is uglier.
        /// </summary>
        public static int HuntState()
        {
            try
            {
                if (Chase == null) return 0;

                return Chase.State == Response.Hunt.Searching ? 2
                     : Chase.State == Response.Hunt.Seen ? 1
                     : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Whether the player is cuffed, in the back of a car, or in a cell.</summary>
        public static bool PlayerInCustody()
        {
            try { return InCustody != null && InCustody(); }
            catch { return false; }
        }

        /// <summary>How many units this mod has on the road right now.</summary>
        public static int UnitsOut()
        {
            try { return Force == null ? 0 : Force.Count; }
            catch { return 0; }
        }

        /// <summary>
        /// Sends the nearest available unit to a point, and says whether one was free.
        ///
        /// FALSE IS A REAL ANSWER AND CALLERS MUST HANDLE IT. This is the whole reason the
        /// bridge exists rather than the other mod spawning its own car: police in this mod
        /// come out of a finite pool, and a quiet district at four in the morning genuinely has
        /// nobody to send. A caller that spawns its own car when this returns false has thrown
        /// away the only thing it gained by asking.
        /// </summary>
        public static bool SendUnit(float x, float y, float z, string reason)
        {
            try
            {
                if (Force == null) return false;

                var to = new Vector3(x, y, z);

                var unit = Force.NearestFree(to);
                if (unit == null) return false;

                unit.RespondTo(to, string.IsNullOrEmpty(reason) ? "a call" : reason);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Bridge dispatch failed: " + ex.Message);
                return false;
            }
        }

        // ---- what we ask of the host -------------------------------------------

        /// <summary>
        /// The host's chance to take contraband off the player when he is searched or booked.
        ///
        /// Func and string are both mscorlib types, so the delegate has the same identity in
        /// both assemblies and this can be handed straight across the bridge with no reflection
        /// on the calling side beyond the one Invoke.
        ///
        /// The contract: the handler is given the reason for the search and must ALREADY HAVE
        /// TAKEN whatever it is going to take by the time it returns. What it returns is the
        /// line shown to the player -- "84g and $1,200" -- or empty for nothing found. It is a
        /// seizure, not a query, deliberately: two calls with a window between them is a window
        /// in which the player walks off having been told he was robbed and not having been.
        /// </summary>
        public static void OnSeize(Func<string, string> handler)
        {
            try
            {
                Seizer = handler;
                Log.Info("Bridge: a mod has registered a seizure handler.");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not register a seizure handler: " + ex.Message);
            }
        }

        internal static Func<string, string> Seizer;

        /// <summary>
        /// Something the host wants to be told about. Set by the host, called by us.
        ///
        /// One string, and the mod on the other end decides what to do with it -- a ticker
        /// line, a post to its social feed, an entry in its own log. Beats defining an event
        /// type neither side can name.
        /// </summary>
        public static void OnEvent(Action<string> handler)
        {
            try
            {
                Listener = handler;
                Log.Info("Bridge: a mod has registered an event listener.");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not register an event listener: " + ex.Message);
            }
        }

        internal static Action<string> Listener;

        /// <summary>Tells whoever is listening. Never throws, whatever the handler does.</summary>
        internal static void Tell(string what)
        {
            try { if (Listener != null) Listener(what); }
            catch (Exception ex) { Log.Debug("An event listener threw: " + ex.Message); }
        }
    }
}
