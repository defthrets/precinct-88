using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace Precinct88.Core
{
    /// <summary>
    /// The handful of questions every system in this mod asks about police, answered once.
    ///
    /// These were spread across four files in the first cut and three of them disagreed about
    /// what counts as an officer -- which is how you end up with a mod that arrests you for a
    /// crime one system saw and another one did not.
    /// </summary>
    internal static class Cops
    {
        /// <summary>
        /// PED_TYPE values that wear a badge. 6 is COP, 27 SWAT, 29 ARMY.
        ///
        /// ARMY is in the list because at five stars it is the same authority as far as this
        /// mod is concerned -- a soldier who has you at gunpoint is not somebody you talk your
        /// way past, and leaving him out means the wanted system quietly stops working at the
        /// exact level it matters most.
        /// </summary>
        private static readonly int[] Badge = { 6, 27, 29 };

        /// <summary>Marked squad cars. Unmarked and federal are deliberately not in here.</summary>
        public static readonly string[] Cars = { "police", "police2", "police3", "sheriff" };

        /// <summary>Uniforms. Two models, because a force of one face is worse than no force.</summary>
        public static readonly string[] Uniforms = { "s_m_y_cop_01", "s_f_y_cop_01" };

        public static bool IsCop(Ped ped)
        {
            try
            {
                if (!Alive(ped)) return false;

                var type = Function.Call<int>(Hash.GET_PED_TYPE, ped.Handle);
                return Array.IndexOf(Badge, type) >= 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Alive, existing, and safe to give an order to.</summary>
        public static bool Alive(Entity who)
        {
            try { return who != null && who.Exists() && !who.IsDead; }
            catch { return false; }
        }

        /// <summary>
        /// Whether this officer can actually SEE the player right now.
        ///
        /// Range alone is not sight, and the difference is the whole mod. An officer the other
        /// side of a wall has not seen anything, and a system that books you through a
        /// building is one nobody trusts the second time it fires.
        ///
        /// HAS_ENTITY_CLEAR_LOS_TO_ENTITY rather than a raycast, because it is the game's own
        /// answer to the question and it already accounts for the things a naive ray does not.
        /// </summary>
        public static bool Sees(Ped officer, Ped target, float range)
        {
            try
            {
                if (!Alive(officer) || !Alive(target)) return false;
                if (officer.Position.DistanceTo(target.Position) > range) return false;

                if (!Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                                         officer.Handle, target.Handle, 17))
                {
                    return false;
                }

                // In front of him, not behind. The clear-line test does not care which way an
                // officer is facing, so without this a car passing you is a car that saw you.
                return Function.Call<bool>(Hash.IS_ENTITY_AT_ENTITY,
                                           officer.Handle, target.Handle,
                                           range, range, range, false, true, 0)
                       || InFront(officer, target);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Within roughly a 140-degree cone off the front of him.</summary>
        public static bool InFront(Ped officer, Entity target)
        {
            try
            {
                var to = target.Position - officer.Position;
                to.Z = 0f;

                if (to.Length() < 0.01f) return true;

                to.Normalize();

                var facing = officer.ForwardVector;
                facing.Z = 0f;
                facing.Normalize();

                return Vector3.Dot(facing, to) > 0.34f;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Officers near a point, cheapest first: the game's own list, filtered.</summary>
        public static List<Ped> Near(Vector3 where, float range)
        {
            var found = new List<Ped>();

            try
            {
                foreach (var ped in World.GetNearbyPeds(where, range))
                {
                    if (IsCop(ped)) found.Add(ped);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not scan for officers: " + ex.Message);
            }

            return found;
        }

        /// <summary>
        /// Whether the player has an actual weapon out.
        ///
        /// Unarmed is not a weapon, and neither is a phone -- but the obvious test, "is he
        /// armed", says yes to both on some builds. Asked by hash so there is nothing to get
        /// wrong.
        /// </summary>
        /// <summary>
        /// A GUN, as opposed to a bat, a phone, a parachute or empty hands.
        ///
        /// THE DISTINCTION THE WHOLE FORCE LADDER TURNS ON. Armed() answers "is he holding
        /// something", which is the right question for whether an officer looks twice and the
        /// wrong one for whether he draws. A man with a baseball bat gets tased; a man with a
        /// pistol does not, whatever his star count says.
        ///
        /// It lived privately in two files before this and was about to live in a third, which
        /// is how a rule ends up meaning three slightly different things.
        /// </summary>
        public static bool HasGun(Ped who)
        {
            try
            {
                if (!Armed(who)) return false;

                var group = who.Weapons.Current.Group;

                return group != WeaponGroup.Unarmed &&
                       group != WeaponGroup.Melee &&
                       group != WeaponGroup.Parachute &&
                       group != WeaponGroup.PetrolCan &&
                       group != WeaponGroup.FireExtinguisher &&
                       group != WeaponGroup.DigiScanner &&
                       group != WeaponGroup.NightVision;
            }
            catch
            {
                return false;
            }
        }

        public static bool Armed(Ped who)
        {
            try
            {
                if (!Alive(who)) return false;

                var now = Function.Call<uint>(Hash.GET_SELECTED_PED_WEAPON, who.Handle);
                if (now == 0) return false;

                return now != Function.Call<uint>(Hash.GET_HASH_KEY, "WEAPON_UNARMED") &&
                       now != Function.Call<uint>(Hash.GET_HASH_KEY, "WEAPON_MOBILEPHONE");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Speech through the car rather than out of a man stood next to it.</summary>
        public static void Megaphone(Ped who, string speech)
        {
            try
            {
                if (!Alive(who) || string.IsNullOrEmpty(speech)) return;

                Function.Call(Hash.PLAY_PED_AMBIENT_SPEECH_NATIVE, who.Handle, speech,
                              "SPEECH_PARAMS_FORCE_MEGAPHONE");
            }
            catch
            {
                // A voice that has not got this line simply does not play it, and a patrol car
                // that is quiet for one pass is not worth a line of code to prevent.
            }
        }

        /// <summary>An ordinary line, out of the man.</summary>
        public static void Say(Ped who, string speech)
        {
            try
            {
                if (!Alive(who) || string.IsNullOrEmpty(speech)) return;

                Function.Call(Hash.PLAY_PED_AMBIENT_SPEECH_NATIVE, who.Handle, speech,
                              "SPEECH_PARAMS_FORCE");
            }
            catch
            {
                // As above.
            }
        }

        /// <summary>
        /// Loads a model and hands it back, or null.
        ///
        /// Every spawn in this mod goes through here so that no spawn anywhere can hang the
        /// game waiting on a model that will never arrive.
        /// </summary>
        public static Model? Load(string name, int waitMs = 1200)
        {
            return Load(name, null, waitMs);
        }

        /// <summary>
        /// The same, when the caller already has the Model and only wants it streamed in.
        ///
        /// Agencies resolve and cache their own valid models, so re-deriving one from a name
        /// there would throw away the validity check that was the whole point of caching it.
        /// The name is still taken, purely so a failure has something readable to log.
        /// </summary>
        public static Model? Load(string name, Model? known, int waitMs = 1200)
        {
            try
            {
                var model = known ?? new Model(name);
                if (!model.IsValid) return null;

                model.Request();

                var until = Game.GameTime + waitMs;
                while (!model.IsLoaded && Game.GameTime < until) Script.Yield();

                return model.IsLoaded ? model : (Model?)null;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not load " + name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Lets a ped or car go back to the game rather than deleting it on screen.</summary>
        public static void LetGo(Entity what)
        {
            try
            {
                if (what == null || !what.Exists()) return;

                what.MarkAsNoLongerNeeded();
            }
            catch
            {
                // Teardown. Nothing left to tell.
            }
        }
    }
}
