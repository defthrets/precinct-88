using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;
using Precinct88.Core;
using Precinct88.UI;

namespace Precinct88.Contact
{
    /// <summary>
    /// Cars taken off you, and kept off you.
    ///
    /// TWELVE POINTS HAD NO TEETH. The licence counted up, the panel said SUSPENDED, and
    /// absolutely nothing happened -- you carried on driving the same car past the same
    /// officers. A suspension that costs nothing is a number going up.
    ///
    /// So the car goes. You are made to get out, it is locked against you specifically, and it
    /// stays locked until your licence comes back. Pull Me Over does the same and is honest
    /// about the limit -- "locked for you, no towing for now" -- and that limit is the right
    /// call for a script mod: actually removing somebody's vehicle means deciding where it went
    /// and whether it still exists, and a car that has been deleted out from under a player is
    /// unrecoverable in a way a locked one never is.
    ///
    /// LOCKED FOR THE PLAYER, NOT LOCKED OUTRIGHT. SET_VEHICLE_DOORS_LOCKED_FOR_PLAYER is the
    /// precise native and the difference matters: a car locked outright is a prop that nobody in
    /// the world can use, which looks wrong the moment an officer wants to move it. This one is
    /// yours and specifically not yours any more.
    ///
    /// SESSION ONLY, and deliberately so. Vehicle handles do not survive a reload and the
    /// game does not keep an arbitrary car in the world across one, so a seizure that claimed to
    /// persist would be claiming something it cannot deliver. What DOES persist is the licence
    /// -- so a suspended player who reloads is still suspended, and the next car he is stopped
    /// in goes the same way.
    /// </summary>
    internal sealed class Impound
    {
        private const int TickMs = 900;

        /// <summary>How far away a seized car stops being worth re-locking.</summary>
        private const float MindRange = 120f;

        private readonly Settings _cfg;
        private readonly Licence _licence;

        /// <summary>Handles of cars taken. Live objects are looked up each tick.</summary>
        private readonly HashSet<int> _taken = new HashSet<int>();

        private int _lastTick;
        private bool _wasSuspended;

        public Impound(Settings cfg, Licence licence)
        {
            _cfg = cfg;
            _licence = licence;
        }

        public int Count => _taken.Count;

        /// <summary>Whether this particular car has been taken off the player.</summary>
        public bool IsTaken(Vehicle car)
        {
            try { return Cops.Alive(car) && _taken.Contains(car.Handle); }
            catch { return false; }
        }

        // ---- taking one --------------------------------------------------------

        /// <summary>
        /// Takes the car the player is in.
        ///
        /// Called from a stop, so there is an officer standing there -- which is what makes
        /// this a seizure rather than the mod arbitrarily locking a door.
        /// </summary>
        public void Seize(Vehicle car, Ped me)
        {
            if (!_cfg.SeizeOnSuspension) return;
            if (!Cops.Alive(car)) return;

            try
            {
                _taken.Add(car.Handle);

                // OUT FIRST. Locking a car with somebody sitting in it locks them IN, which is
                // the opposite of a seizure and reads as the mod having glitched.
                if (Cops.Alive(me) && me.IsInVehicle(car))
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, me.Handle, car.Handle, 0);
                }

                Lock(car);

                Screen.Ticker("Vehicle seized. Your licence is suspended.");

                Log.Info("Seized " + car.DisplayName + " (" + _taken.Count + " held).");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not seize a vehicle: " + ex.Message);
            }
        }

        private static void Lock(Vehicle car)
        {
            try
            {
                // 2 is locked. FOR_PLAYER rather than outright, so the world can still use it.
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_PLAYER,
                              car.Handle, Game.Player.Handle, true);

                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, car.Handle, 2);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not lock a seized vehicle: " + ex.Message);
            }
        }

        private static void Unlock(Vehicle car)
        {
            try
            {
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_PLAYER,
                              car.Handle, Game.Player.Handle, false);

                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, car.Handle, 1);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not unlock a vehicle: " + ex.Message);
            }
        }

        // ---- per-tick ----------------------------------------------------------

        public void Update()
        {
            if (!_cfg.SeizeOnSuspension) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            try
            {
                var suspended = _licence != null && _licence.IsSuspended;

                // THE LICENCE COMING BACK IS WHAT GIVES THE CAR BACK. Charges expire, or an
                // arrest wipes them, and either way the suspension ends -- and a car that
                // stayed locked after that would be a punishment with no way out, which is the
                // one thing a decaying record exists to avoid.
                if (_wasSuspended && !suspended)
                {
                    Release("the suspension ended");
                }

                // Told once, at the moment it happens, rather than every tick.
                if (suspended && !_wasSuspended)
                {
                    Screen.Ticker("Your licence is suspended. Driving now is an offence.");
                    Log.Info("Licence suspended.");
                }

                _wasSuspended = suspended;

                if (_taken.Count > 0) Mind();
            }
            catch (Exception ex)
            {
                Log.Debug("Impound check failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Keeps the locks on.
        ///
        /// RE-ASSERTED, because the game resets door lock state on plenty of things nothing gets
        /// told about -- a mission, a cutscene, the vehicle streaming out and back. A lock
        /// pushed once and never again is a seizure that quietly lapses the first time you walk
        /// round a corner, which is the same failure the dispatch suppression had.
        /// </summary>
        private void Mind()
        {
            var me = Game.Player.Character;
            if (me == null || !me.Exists()) return;

            var gone = new List<int>();

            foreach (var handle in _taken)
            {
                var car = (Vehicle)Entity.FromHandle(handle);

                if (!Cops.Alive(car))
                {
                    // Streamed out or destroyed. Nothing left to hold.
                    gone.Add(handle);
                    continue;
                }

                if (car.Position.DistanceTo(me.Position) > MindRange) continue;

                Lock(car);
            }

            foreach (var handle in gone) _taken.Remove(handle);
        }

        /// <summary>Gives everything back. For the suspension ending, and for teardown.</summary>
        public void Release(string why)
        {
            if (_taken.Count == 0) return;

            foreach (var handle in _taken)
            {
                var car = (Vehicle)Entity.FromHandle(handle);
                if (Cops.Alive(car)) Unlock(car);
            }

            Log.Info("Released " + _taken.Count + " seized vehicle(s): " + why + ".");

            _taken.Clear();

            Screen.Ticker("Your licence is back. Your vehicle has been released.");
        }
    }
}
