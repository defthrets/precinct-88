using System;
using GTA;
using GTA.Native;

namespace Precinct88.Core
{
    /// <summary>
    /// Playing a clip on somebody, and never hanging the game to do it.
    ///
    /// ANIMATION DICTIONARY NAMES ARE THE MOST FRAGILE STRINGS IN A GTA MOD. They are not
    /// checked by the compiler, they are not listed anywhere official, they differ between the
    /// two editions in places, and a wrong one fails silently -- REQUEST_ANIM_DICT on a name
    /// that does not exist never loads and never errors. The usual way this is written is a
    /// `while (!HAS_ANIM_DICT_LOADED) Yield();` loop, which on a wrong name is an infinite loop
    /// inside a script tick: the game hangs, with no log line, and it looks like a crash.
    ///
    /// So every clip in this mod goes through here, everything is time-boxed, and a dictionary
    /// that does not arrive is skipped. The cost of a wrong name is then an officer who stands
    /// there instead of miming a search -- cosmetic, findable in the log, and not a hang.
    /// </summary>
    internal static class Anim
    {
        /// <summary>Long enough for a real load off disk, short enough not to be a stall.</summary>
        private const int LoadWaitMs = 900;

        /// <summary>Loads a dictionary, or gives up. True when it is ready to play from.</summary>
        public static bool Ready(string dict)
        {
            try
            {
                if (string.IsNullOrEmpty(dict)) return false;

                if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict)) return true;

                Function.Call(Hash.REQUEST_ANIM_DICT, dict);

                var until = Game.GameTime + LoadWaitMs;

                while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict) &&
                       Game.GameTime < until)
                {
                    Script.Yield();
                }

                var ok = Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict);

                if (!ok) Log.Warn("Animation dictionary did not load: " + dict);

                return ok;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not request " + dict + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Plays a clip. Returns whether it actually started.
        ///
        /// Flag 49 is the one worth knowing: loop, plus upper-body only, plus allow player
        /// control -- which is what makes hands-up something a player is doing rather than
        /// something being done to them. Flag 1 is a plain loop for when they should be held.
        /// </summary>
        public static bool Play(Ped who, string dict, string clip, int flags, int ms = -1)
        {
            try
            {
                if (!Cops.Alive(who)) return false;

                // ALREADY PLAYING IS NOT A REASON TO PLAY IT AGAIN, and this one line is the
                // whole of the surrender twitch.
                //
                // Every caller of this sits in a per-tick loop and calls it unconditionally --
                // hands up, cuffed, the officer's inspect idle. TASK_PLAY_ANIM does not check
                // whether the clip is running; it RESTARTS it. So the pose was being restarted
                // ten times a second and the ped juddered on the spot, which reads as the
                // animation being broken rather than as the mod re-issuing it.
                //
                // Cheap to ask, and it makes "call it every tick" the correct way to hold a
                // pose -- which is what every call site already assumed it was.
                if (IsPlaying(who, dict, clip)) return true;

                if (!Ready(dict)) return false;

                Function.Call(Hash.TASK_PLAY_ANIM, who.Handle, dict, clip,
                              8f, -8f, ms, flags, 0f, false, false, false);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not play " + dict + "/" + clip + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Whether this ped is already running this exact clip.
        ///
        /// Task flag 3 covers both the ordinary and the secondary task slots, which is what
        /// upper-body clips like hands-up actually run in.
        /// </summary>
        public static bool IsPlaying(Ped who, string dict, string clip)
        {
            try
            {
                if (!Cops.Alive(who)) return false;

                return Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM,
                                           who.Handle, dict, clip, 3);
            }
            catch
            {
                // Cannot tell, so let it be re-issued. A twitch is better than a pose that
                // never starts.
                return false;
            }
        }

        /// <summary>Stops whatever clip is running, without clearing the rest of their tasks.</summary>
        public static void Stop(Ped who, string dict, string clip)
        {
            try
            {
                if (!Cops.Alive(who)) return;

                Function.Call(Hash.STOP_ANIM_TASK, who.Handle, dict, clip, 3f);
            }
            catch
            {
                // Nothing worth saying. The clip ends when the ped is re-tasked anyway.
            }
        }

        // ---- the clips this mod uses -------------------------------------------

        /// <summary>Hands up, standing, and still able to move. What a stop looks like.</summary>
        public const string HandsUpDict = "random@mugging3";
        public const string HandsUpClip = "handsup_standing_base";

        /// <summary>Cuffed. Hands behind the back, and genuinely stuck.</summary>
        public const string CuffedDict = "mp_arresting";
        public const string CuffedClip = "idle";

        /// <summary>An officer looking at something, for the length of a search.</summary>
        public const string InspectDict = "amb@code_human_police_investigate@idle_a";
        public const string InspectClip = "idle_a";
    }
}
