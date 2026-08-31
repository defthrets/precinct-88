using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Response
{
    /// <summary>
    /// One switch for the police, shared by everything that needs them out of the way.
    ///
    /// COUNTED RATHER THAN BOOLEAN, for the same reason a lock is: the last one out restores
    /// it, not the first. Hoodrich learned this by having two systems each turning the wanted
    /// system off and back on with no idea the other existed -- a gang war starting during a
    /// bike ride meant whichever finished first turned the police back on for the other, so
    /// either a straightener on a basketball court brought a helicopter or a raid on your own
    /// block did.
    ///
    /// THE SAME PROBLEM NOW EXISTS ACROSS TWO MODS, which is why this class is deliberately the
    /// same shape as Hoodrich's. With Precinct 88 installed, Hoodrich's LawHold delegates here
    /// over the bridge and this becomes the single arbiter for both. Without it, Hoodrich keeps
    /// using its own and nothing changes. Two counted holds that do not know about each other
    /// is precisely the bug either of them was written to prevent, one layer up.
    ///
    /// The level to go back to is read from the game at the FIRST hold rather than assumed to
    /// be five, so an install that has been set to something else keeps it.
    /// </summary>
    internal static class LawHold
    {
        private static readonly HashSet<string> Holders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static int _wasMax = 5;

        /// <summary>Whether anybody is currently holding the police off.</summary>
        public static bool Held => Holders.Count > 0;

        /// <summary>Who, for the log and for anything that wants to say why.</summary>
        public static IEnumerable<string> Who => Holders;

        /// <summary>
        /// Takes the police off, and puts them off again every time it is asked.
        ///
        /// RE-ASKING RE-APPLIES, and that is not a detail. The obvious first line here is
        /// `if (Holders.Contains(who)) return;` -- and it is wrong, because callers that hold
        /// through a long scene call this every tick specifically to re-assert. The game resets
        /// the max wanted level on a mission finishing, a cutscene, an area reload; with an
        /// early return the natives are pushed once at the start and whatever happens to them
        /// after that stands. From the street that looks exactly like "we keep getting stars
        /// during the raid".
        ///
        /// The one thing that must NOT happen twice is reading the level to go back to. Read it
        /// again while it is held and it reads zero, and the police never come back at all.
        /// </summary>
        public static void Hold(string who)
        {
            if (string.IsNullOrEmpty(who)) return;

            var first = Holders.Count == 0;

            Holders.Add(who);

            if (first)
            {
                try
                {
                    _wasMax = Function.Call<int>(Hash.GET_MAX_WANTED_LEVEL);
                    if (_wasMax <= 0) _wasMax = 5;

                    Log.Info("Law: off, held by " + who + ".");
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not read the wanted ceiling: " + ex.Message);
                    _wasMax = 5;
                }
            }

            try
            {
                // Cleared as well as capped. A star already showing when the hold starts would
                // otherwise sit there for the whole scene with the ceiling quietly stopping it
                // going any higher -- suppressed, and still on screen.
                Game.Player.Wanted.SetWantedLevel(0, false);
                Game.Player.Wanted.ApplyWantedLevelChangeNow(false);

                Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player.Handle, true);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not hold the law: " + ex.Message);
            }
        }

        /// <summary>
        /// Puts a lid on how far it can go, without turning the police off.
        ///
        /// A hold is "none of this is a police matter". A cap is "this IS a police matter and
        /// it is worth exactly this much" -- which is what every entry in the Crime table
        /// amounts to. A hold outranks a cap and simply wins: nothing is more capped than off.
        /// </summary>
        public static void Cap(int stars)
        {
            if (Held) return;

            try { Function.Call(Hash.SET_MAX_WANTED_LEVEL, stars < 0 ? 0 : stars); }
            catch (Exception ex) { Log.Debug("Could not cap the law: " + ex.Message); }
        }

        /// <summary>Back to whatever the ceiling was before anybody touched it.</summary>
        public static void Uncap()
        {
            if (Held) return;

            try { Function.Call(Hash.SET_MAX_WANTED_LEVEL, _wasMax); }
            catch (Exception ex) { Log.Debug("Could not lift the cap: " + ex.Message); }
        }

        public static void Release(string who)
        {
            if (string.IsNullOrEmpty(who) || !Holders.Remove(who)) return;
            if (Holders.Count > 0) return;

            Restore();
        }

        /// <summary>
        /// Puts it back regardless of who was holding it.
        ///
        /// For teardown only. Leaving the player permanently un-arrestable because a script
        /// unloaded mid-scene is far worse than any amount of litter, so this does not care
        /// about the count.
        /// </summary>
        public static void ReleaseAll()
        {
            Holders.Clear();
            Restore();
        }

        private static void Restore()
        {
            try
            {
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, _wasMax);
                Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player.Handle, false);

                Log.Info("Law: back on.");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not put the law back: " + ex.Message);
            }
        }
    }
}
