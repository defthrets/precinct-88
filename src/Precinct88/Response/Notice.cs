using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Streets;

namespace Precinct88.Response
{
    /// <summary>
    /// One thing an officer could notice you doing, and what it takes to notice it.
    /// </summary>
    internal sealed class Misdeed
    {
        /// <summary>How it is said out loud. Goes on the ticker and into the log.</summary>
        public readonly string Name;

        /// <summary>How much of a response it is worth. 1 is a look, 3 is everybody near.</summary>
        public readonly int Weight;

        /// <summary>
        /// Heard rather than seen.
        ///
        /// The difference is line of sight. A gun held in the street has to be LOOKED at by
        /// somebody facing the right way; a gun fired does not, and an officer round the corner
        /// hears it exactly as well as one across the road. Getting this the wrong way round is
        /// what makes police feel either blind or psychic.
        /// </summary>
        public readonly bool Loud;

        /// <summary>How long before this same thing is worth reporting again.</summary>
        public readonly int CooldownMs;

        /// <summary>Whether it is happening right now. Nothing in here may throw.</summary>
        public readonly Func<Ped, bool> Happening;

        /// <summary>When it may next be reported. Not a setting; bookkeeping.</summary>
        public int NextAt;

        public Misdeed(string name, int weight, bool loud, int cooldownMs, Func<Ped, bool> happening)
        {
            Name = name;
            Weight = weight;
            Loud = loud;
            CooldownMs = cooldownMs;
            Happening = happening;
        }
    }

    /// <summary>
    /// What the police actually notice you doing.
    ///
    /// THE MISSING HALF. Patrol put police on the streets and they drove around beautifully
    /// ignoring everything, because the only thing in this build that could start a response
    /// was parked with the rest of the wanted rework. A police force that cannot be provoked is
    /// scenery with a light bar.
    ///
    /// WHY THIS IS NOT THE ENGINE'S WANTED SYSTEM, and the reason the request needed its own
    /// file at all: the things worth noticing are mostly not crimes the game has an opinion
    /// about. A burnout outside a school earns you nothing in vanilla. Standing in a crowd with
    /// a rifle out earns you nothing until you point it at somebody. Driving down the pavement
    /// earns you nothing at all. Every one of those is something a passing officer would
    /// obviously react to, and none of them will ever produce a star on their own -- so hooking
    /// this to the wanted level would have quietly dropped most of what was asked for.
    ///
    /// IT DELIBERATELY GIVES NO STARS. This reports what was seen; Callout sends somebody. The
    /// wanted level, the search, and what officers do when they arrive belong to Manhunt, which
    /// is still parked, and inventing a second half-version of it here is how the mod got into
    /// trouble in the first place.
    ///
    /// SOMEBODY OF OURS HAS TO BE THERE. There is no radius around the player inside which
    /// crime is magically known -- a report requires an actual officer, from the actual finite
    /// pool, who could actually see or hear it. That is the whole argument of the mod applied
    /// to noticing rather than to responding, and it means a burnout down an empty side street
    /// at four in the morning really does go unnoticed.
    /// </summary>
    internal sealed class Notice
    {
        private const int TickMs = 500;

        /// <summary>How far an officer can make out what you are doing.</summary>
        private const float SeenRange = 55f;

        /// <summary>And how far a gunshot carries. Generous, because gunshots do.</summary>
        private const float HeardRange = 130f;

        /// <summary>How recent one of the engine's "time since" answers counts as now.</summary>
        private const int JustNowMs = 1500;

        private readonly Settings _cfg;
        private readonly Fleet _fleet;
        private readonly Foot _foot;
        private readonly List<Misdeed> _list;

        private int _lastTick;

        /// <summary>Name, where, weight. Wired to Callout by Main.</summary>
        public Action<string, Vector3, int> Report;

        public Notice(Settings cfg, Fleet fleet, Foot foot)
        {
            _cfg = cfg;
            _fleet = fleet;
            _foot = foot;

            _list = Build();
        }

        /// <summary>
        /// Everything worth noticing, in the order it is checked.
        ///
        /// ROUGHLY WORST FIRST, because the first match on a tick wins and a man firing a gun
        /// out of a car doing a burnout should be reported as the gun.
        /// </summary>
        private static List<Misdeed> Build()
        {
            return new List<Misdeed>
            {
                // Heard, not seen -- see Misdeed.Loud.
                new Misdeed("shots fired", 3, true, 9000,
                            me => Function.Call<bool>(Hash.IS_PED_SHOOTING, me.Handle)),

                new Misdeed("somebody pointing a gun", 3, false, 11000,
                            me => Firearm(me) &&
                                  Function.Call<bool>(Hash.IS_PLAYER_FREE_AIMING,
                                                      Game.Player.Handle)),

                new Misdeed("a car being taken", 3, false, 18000,
                            me => Function.Call<bool>(Hash.IS_PED_JACKING, me.Handle)),

                new Misdeed("a fight in the street", 3, false, 14000,
                            me => Function.Call<bool>(Hash.IS_PED_IN_MELEE_COMBAT, me.Handle)),

                new Misdeed("a car driven at people", 3, false, 12000,
                            me => Driving(me) && Since(Hash.GET_TIME_SINCE_PLAYER_HIT_PED)),

                // The two the request actually named.
                new Misdeed("a gun out in the street", 2, false, 20000,
                            me => Firearm(me) && !me.IsInVehicle()),

                new Misdeed("a burnout", 2, false, 14000,
                            me => Driving(me) &&
                                  Function.Call<bool>(Hash.IS_VEHICLE_IN_BURNOUT,
                                                      me.CurrentVehicle.Handle)),

                new Misdeed("somebody swinging a bat about", 2, false, 20000,
                            me => Melee(me) && !me.IsInVehicle()),

                new Misdeed("a car up on the pavement", 1, false, 20000,
                            me => Driving(me) &&
                                  Since(Hash.GET_TIME_SINCE_PLAYER_DROVE_ON_PAVEMENT)),

                new Misdeed("a car on the wrong side", 1, false, 25000,
                            me => Driving(me) &&
                                  Since(Hash.GET_TIME_SINCE_PLAYER_DROVE_AGAINST_TRAFFIC)),
            };
        }

        public void Update()
        {
            if (!_cfg.RespondToCrime) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            if (Report == null) return;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists() || me.IsDead) return;

                // WORKED OUT AT MOST TWICE A TICK, NOT ONCE PER THING NOTICED. Whether an
                // officer is there does not depend on WHICH offence is being checked -- only on
                // whether it has to be seen or merely heard. Asking per offence meant a player
                // driving badly (pavement, wrong side, and a burnout all at once) paid for
                // three full line-of-sight sweeps of every officer on the map every half
                // second, for one answer repeated three times.
                bool? seen = null;
                bool? heard = null;

                foreach (var what in _list)
                {
                    if (now < what.NextAt) continue;

                    if (!Ask(what, me)) continue;

                    if (what.Loud)
                    {
                        if (heard == null) heard = Anybody(me, true);
                        if (!heard.Value) continue;
                    }
                    else
                    {
                        if (seen == null) seen = Anybody(me, false);
                        if (!seen.Value) continue;
                    }

                    what.NextAt = now + what.CooldownMs;

                    Log.Info("Noticed: " + what.Name + ".");
                    Report(what.Name, me.Position, what.Weight);

                    // One a tick. Reporting four things at once from one moment produces four
                    // ticker lines and one enormous response to what was really a single event.
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not check what the police can see: " + ex.Message);
            }
        }

        /// <summary>
        /// Runs one test, and swallows whatever it does.
        ///
        /// Every check in the list touches the game and any of them can throw on a frame where
        /// the player is mid-transition -- getting into a car, dying, being teleported by
        /// another mod. One bad frame must not stop the other nine being asked.
        /// </summary>
        private static bool Ask(Misdeed what, Ped me)
        {
            try
            {
                return what.Happening(me);
            }
            catch
            {
                return false;
            }
        }

        // ---- who was there -----------------------------------------------------

        /// <summary>
        /// Whether any officer of ours could have seen or heard it.
        ///
        /// Foot officers are included and it matters more than it looks: they are the only
        /// police in this mod who are ever on a pavement, in a crowd, or up an alley -- which
        /// is exactly where the things in the list above tend to happen.
        /// </summary>
        private bool Anybody(Ped me, bool loud)
        {
            if (_fleet != null)
            {
                foreach (var unit in _fleet.Units)
                {
                    if (!unit.Alive) continue;

                    foreach (var officer in unit.Everyone())
                    {
                        if (Noticed(officer, me, loud)) return true;
                    }
                }
            }

            if (_foot != null)
            {
                foreach (var walker in _foot.Walkers)
                {
                    if (!walker.Alive) continue;

                    if (Noticed(walker.Who, me, loud)) return true;
                }
            }

            return false;
        }

        private static bool Noticed(Ped officer, Ped me, bool loud)
        {
            try
            {
                if (!Cops.Alive(officer)) return false;

                if (loud)
                {
                    return officer.Position.DistanceTo(me.Position) <= HeardRange;
                }

                return Cops.Sees(officer, me, SeenRange);
            }
            catch
            {
                return false;
            }
        }

        // ---- the tests ---------------------------------------------------------

        /// <summary>Driving, rather than a passenger in something.</summary>
        private static bool Driving(Ped me)
        {
            if (!me.IsInVehicle()) return false;

            var car = me.CurrentVehicle;
            if (!Cops.Alive(car)) return false;

            return car.Driver != null && car.Driver.Handle == me.Handle;
        }

        /// <summary>
        /// Whether one of the engine's own "time since" counters just tripped.
        ///
        /// These return milliseconds since the thing last happened, and a very large number if
        /// it never has -- so "recently" is simply a small answer. The engine keeps this
        /// bookkeeping for its own wanted system whether or not anything reads it, which makes
        /// it far better than trying to work out from a heading and a road node whether
        /// somebody is on the wrong side of the carriageway.
        /// </summary>
        private static bool Since(Hash what)
        {
            var ms = Function.Call<int>(what, Game.Player.Handle);

            return ms >= 0 && ms < JustNowMs;
        }

        /// <summary>A gun, as opposed to a bat, a phone, or empty hands.</summary>
        private static bool Firearm(Ped me)
        {
            if (!Cops.Armed(me)) return false;

            var group = me.Weapons.Current.Group;

            return group != WeaponGroup.Unarmed &&
                   group != WeaponGroup.Melee &&
                   group != WeaponGroup.Parachute &&
                   group != WeaponGroup.PetrolCan &&
                   group != WeaponGroup.FireExtinguisher &&
                   group != WeaponGroup.DigiScanner &&
                   group != WeaponGroup.NightVision;
        }

        /// <summary>Something in your hands that is not a gun and is not nothing.</summary>
        private static bool Melee(Ped me)
        {
            if (!Cops.Armed(me)) return false;

            return me.Weapons.Current.Group == WeaponGroup.Melee;
        }
    }
}
