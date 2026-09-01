using System;
using GTA;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// The engine's own police, and the single place they are switched off.
    ///
    /// THERE ARE TWO SEPARATE ENGINE SYSTEMS HERE AND SUPPRESSING ONLY THE FIRST IS WHY THE MOD
    /// DID NOT WORK.
    ///
    /// The first is the AMBIENT GENERATOR: a density target that creates squad cars out of view
    /// until an area's police presence is met. It is why one appears behind you on an empty road
    /// at three in the morning, and SET_CREATE_RANDOM_COPS turns it off.
    ///
    /// The second is DISPATCH, and it is the one that matters. The moment you have a wanted
    /// level the engine starts sending units at you -- created for the purpose, arriving having
    /// existed for four seconds, from wherever is convenient. That is precisely the behaviour
    /// this mod replaces, and it was left running: the Fleet would carefully reassign a car that
    /// was already three streets away while the engine conjured two more behind the player. Both
    /// responding, neither aware of the other, and the whole argument of the mod invisible
    /// underneath it.
    ///
    /// So every police dispatch service is off and the Fleet is the only source of police in the
    /// world. Fire and ambulance are deliberately left alone -- they are not police, and killing
    /// them breaks things that have nothing to do with this.
    ///
    /// RE-ASSERTED EVERY FEW SECONDS, NOT SET ONCE. The game resets these on a mission starting,
    /// a cutscene, a load, an area transition, and a few other things nothing gets told about.
    /// A suppression that quietly lapses looks exactly like a mod that never worked.
    /// </summary>
    internal static class AmbientCops
    {
        private const int ReassertMs = 3000;

        /// <summary>
        /// The dispatch services that send POLICE, by the game's own numbering.
        ///
        /// 1 police car, 2 police helicopter, 4 SWAT, 6 police bikes, 7 vehicle request,
        /// 8 road block, 9 and 10 the waiting/cruising units, 12 SWAT helicopter, 13 police
        /// boat, 14 army.
        ///
        /// NOT 3 (fire) and NOT 5 (ambulance). Those are emergency services rather than law
        /// enforcement, nothing in this mod replaces them, and switching them off would mean no
        /// fire engine ever attends anything -- a bug in a police mod that nobody would ever
        /// connect back to it. 11 (gangs) and 15 (biker backup) are left for the same reason.
        /// </summary>
        private static readonly int[] PoliceServices = { 1, 2, 4, 6, 7, 8, 9, 10, 12, 13, 14 };

        /// <summary>
        /// The two the severity of the crime is allowed to switch back on.
        ///
        /// Everything else stays off permanently while the mod owns dispatch. These two come
        /// back for a homicide or an officer down, because a helicopter and SWAT are things
        /// this mod does not model and would otherwise simply never happen -- and at that level
        /// they should.
        /// </summary>
        private const int Helicopter = 2;
        private const int Swat = 4;
        private const int SwatHelicopter = 12;

        /// <summary>The ordinary road police, which are what the failsafe hands back.</summary>
        private const int RoadCar = 1;
        private const int VehicleRequest = 7;
        private const int WaitPulledOver = 9;
        private const int WaitCruising = 10;

        private static int _lastPush;
        private static bool _suppressing;

        private static bool _allowAir;
        private static bool _allowSwat;

        /// <summary>
        /// Hands the engine's ordinary police dispatch back, temporarily.
        ///
        /// THE FAILSAFE THIS MOD SHOULD HAVE HAD FROM THE START. Switching off every dispatch
        /// service makes the Fleet the entire police force, and that is the intended design --
        /// right up until the Fleet cannot field anybody, at which point the player is at four
        /// stars in a city with no police in it and the mod has simply broken the game.
        ///
        /// There is no version of this mod's argument that is worth that. If nobody has
        /// answered a serious call for long enough, the engine is asked to help, and it keeps
        /// helping until the incident is over.
        /// </summary>
        private static bool _fallback;

        /// <summary>Whether the engine's police are currently held off.</summary>
        public static bool Suppressed => _suppressing;

        /// <summary>Whether the engine is currently being asked to help.</summary>
        public static bool FallingBack => _fallback;

        /// <summary>
        /// Asks the engine for help, or stops asking.
        ///
        /// Called by Manhunt when its own units have failed to answer a serious call. Pushed
        /// immediately rather than on the next beat, because the whole point is that the player
        /// has already been waiting.
        /// </summary>
        public static void Fallback(bool on)
        {
            if (on == _fallback) return;

            _fallback = on;
            _lastPush = 0;

            Log.Warn(on
                ? "No unit of ours could answer. Handing ordinary police dispatch back to the " +
                  "game for this incident."
                : "Taking dispatch back off the game.");
        }

        /// <summary>What the current incident justifies. Set by Manhunt, read on the next push.</summary>
        public static void Allow(bool air, bool swat)
        {
            if (air == _allowAir && swat == _allowSwat) return;

            _allowAir = air;
            _allowSwat = swat;

            // Pushed immediately rather than on the next beat. This changes when a crime
            // escalates, and waiting three seconds to put a helicopter up after somebody shoots
            // an officer is three seconds of nothing happening at the exact moment it should.
            _lastPush = 0;
        }

        /// <summary>
        /// Holds the engine's police off, and keeps holding them.
        ///
        /// Call every tick. It rate-limits itself, so the cost between pushes is a comparison.
        /// </summary>
        public static void Hold(bool ownDispatch)
        {
            _suppressing = true;

            var now = Game.GameTime;
            if (now - _lastPush < ReassertMs) return;
            _lastPush = now;

            Push(false, ownDispatch);
        }

        /// <summary>Gives them back. For the mod being switched off, and for teardown.</summary>
        public static void Release()
        {
            if (!_suppressing) return;

            _suppressing = false;
            _allowAir = false;
            _allowSwat = false;
            _fallback = false;

            Push(true, true);

            Log.Info("The game's own police handed back.");
        }

        private static void Push(bool allow, bool ownDispatch)
        {
            try
            {
                Function.Call(Hash.SET_CREATE_RANDOM_COPS, allow);
                Function.Call(Hash.SET_CREATE_RANDOM_COPS_NOT_ON_SCENARIOS, allow);

                // Scenario cops are the ones stood beside a car outside a station, or leaning
                // on a wall in Vespucci. Left ON deliberately: they are set dressing rather
                // than density, they do not drive, and removing them empties the stations this
                // mod spends its time pretending are staffed.
                Function.Call(Hash.SET_CREATE_RANDOM_COPS_ON_SCENARIOS, true);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set ambient police: " + ex.Message);
            }

            if (!ownDispatch && !allow) return;

            try
            {
                foreach (var service in PoliceServices)
                {
                    var on = allow;

                    if (!allow)
                    {
                        if (service == Helicopter) on = _allowAir;
                        else if (service == Swat || service == SwatHelicopter) on = _allowSwat;

                        // The ordinary road units, and only those, when nothing of ours could
                        // get there. Not the helicopter and not SWAT -- those are still gated
                        // on what was actually done, and a failsafe should restore policing
                        // rather than escalate it.
                        else if (_fallback && (service == RoadCar || service == VehicleRequest ||
                                               service == WaitPulledOver || service == WaitCruising))
                        {
                            on = true;
                        }
                    }

                    Function.Call(Hash.ENABLE_DISPATCH_SERVICE, service, on);
                }

                // AND THE ONE THAT IS NOT A SERVICE. Even with every service off the engine will
                // still decide the player warrants police attention through its own cop-request
                // path; this is the switch for that, and without it a few still arrive.
                Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, Game.Player.Handle,
                              allow || _fallback);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set dispatch: " + ex.Message);
            }
        }
    }
}
