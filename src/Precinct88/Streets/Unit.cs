using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>What a unit is doing at this moment.</summary>
    internal enum Duty
    {
        /// <summary>Driving the beat. The default, and most of what you ever see.</summary>
        Rolling,

        /// <summary>Pulled up somewhere with the light on, watching a street.</summary>
        Sitting,

        /// <summary>Been given a call and is driving to it.</summary>
        Responding,

        /// <summary>Arrived at a call and looking for whoever it was about.</summary>
        Searching,

        /// <summary>Dealing with the player directly. The Contact system is driving, not this.</summary>
        Contact,

        /// <summary>Round finished. Driving off to be let go somewhere off screen.</summary>
        StandingDown
    }

    /// <summary>
    /// One car, the two in it, and the orders it is currently under.
    ///
    /// A unit is a THING THAT PERSISTS, which is the whole difference between this and the
    /// vanilla generator. The car that pulls you over on Innocence Boulevard is a car that has
    /// been driving that beat for four minutes, and when the call it is sent to turns out to be
    /// nothing it goes back to driving it. Nothing in here is created in response to the player
    /// and nothing is deleted the moment he looks away.
    /// </summary>
    internal sealed class Unit
    {
        /// <summary>Slow. This is a car looking at things rather than going somewhere.</summary>
        private const float CruiseSpeed = 9f;

        /// <summary>On a call. Fast enough to matter, not fast enough to be comic.</summary>
        private const float ResponseSpeed = 24f;

        /// <summary>Normal road use: stop at lights, overtake, avoid people.</summary>
        private const int CruiseStyle = 786603;

        /// <summary>Lights, siren, and the rules relaxed. Used only when responding.</summary>
        private const int UrgentStyle = 786469;

        private const float ArrivedRange = 15f;

        /// <summary>Not moved this far in this long and something is wrong with the route.</summary>
        private const float StuckMoved = 3.5f;
        private const int StuckAfterMs = 9000;
        private const int MaxNudges = 2;

        public Vehicle Car;
        public Ped Driver;
        public readonly List<Ped> Crew = new List<Ped>();

        public Duty Doing = Duty.Rolling;

        /// <summary>The district this unit was put out to cover.</summary>
        public District Beat;

        /// <summary>Where it is headed, and whether it stops when it gets there.</summary>
        public Vector3 Target;
        public bool StopThere;

        /// <summary>When it stops sitting and moves on.</summary>
        public int MoveOnAt;

        /// <summary>When this unit has finished its round.</summary>
        public int OffDutyAt;

        /// <summary>What it was sent to, if anything. Null while it is just driving.</summary>
        public string CallReason;

        private Vector3 _wasAt;
        private int _lookedAt;
        private int _nudges;

        public bool Alive => Cops.Alive(Car) && Cops.Alive(Driver);

        /// <summary>Everybody in it, driver included.</summary>
        public IEnumerable<Ped> Everyone()
        {
            if (Cops.Alive(Driver)) yield return Driver;

            foreach (var p in Crew)
            {
                if (Cops.Alive(p)) yield return p;
            }
        }

        // ---- orders ------------------------------------------------------------

        /// <summary>Drive there at a beat pace, and stop when you get there if told to.</summary>
        public void Roll(Vector3 to, bool stop)
        {
            Target = to;
            StopThere = stop;
            Doing = Duty.Rolling;
            CallReason = null;

            Drive(to, CruiseSpeed, CruiseStyle);
            Lights(false);
        }

        /// <summary>
        /// Drop what you are doing and get to this.
        ///
        /// The unit keeps its beat -- it is not reassigned, it is diverted -- so when the call
        /// clears it goes back to the district it belongs to rather than wandering off to
        /// patrol wherever the last shout happened to be.
        /// </summary>
        public void RespondTo(Vector3 to, string reason)
        {
            Target = to;
            StopThere = true;
            Doing = Duty.Responding;
            CallReason = reason;

            Drive(to, ResponseSpeed, UrgentStyle);
            Lights(true);

            Cops.Megaphone(Driver, "CHASE_SOLO");
        }

        /// <summary>Stop where you are and hold. The Contact system takes it from here.</summary>
        public void HandOver()
        {
            Doing = Duty.Contact;

            try
            {
                if (Cops.Alive(Driver)) Driver.Task.ClearAll();
                if (Cops.Alive(Car)) Car.IsSirenActive = false;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not hand a unit over: " + ex.Message);
            }
        }

        /// <summary>Back to the beat after a call came to nothing.</summary>
        public void BackToWork(Vector3 to)
        {
            CallReason = null;
            Roll(to, false);
        }

        /// <summary>
        /// Pulls in and waits, instead of stopping dead where it happens to be.
        ///
        /// THE OLD VERSION CLEARED THE DRIVER'S TASKS, which is a hard stop wherever the car is
        /// standing -- and since it was driving to a vehicle NODE, wherever it was standing was
        /// the middle of the carriageway. A squad car across a live lane with its light bar on
        /// and traffic backed up behind it, for up to forty-six seconds.
        ///
        /// TASK_VEHICLE_PARK does the geometry properly. Mode 3 is the pull-in-and-stop one and
        /// the radius gives it room to find something workable; the engine is left running,
        /// because an officer sitting watching a street does not switch off.
        ///
        /// If the park task will not take, the fallback is to keep DRIVING rather than to stop
        /// dead. A car still moving is never the bug this is fixing.
        /// </summary>
        public void PullIn(Vector3 kerb, float heading, int until)
        {
            Doing = Duty.Sitting;
            MoveOnAt = until;
            Target = kerb;

            Lights(false);

            try
            {
                if (!Alive) return;

                Function.Call(Hash.TASK_VEHICLE_PARK,
                              Driver.Handle, Car.Handle,
                              kerb.X, kerb.Y, kerb.Z, heading,
                              3, 20f, true);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not park a unit: " + ex.Message);
            }
        }

        public void StandDown(Vector3 goHome)
        {
            Doing = Duty.StandingDown;
            Target = goHome;
            CallReason = null;

            Drive(goHome, ResponseSpeed, CruiseStyle);
            Lights(false);
        }

        // ---- the tick ----------------------------------------------------------

        /// <summary>
        /// Keeps the car moving, and reports whether it is still worth having.
        ///
        /// Returns false when this unit should be let go -- crashed, shot up, hopelessly stuck,
        /// or simply finished. The fleet does the letting go; a unit does not delete itself,
        /// because half the point of the pool is that something else may still be holding a
        /// reference to it.
        /// </summary>
        public bool Update(Vector3 playerAt, Random rng)
        {
            if (!Alive) return false;

            // Contact means somebody else is driving. Do not touch the tasks, do not re-issue
            // the route, do not decide the round is over -- a unit yanked out from under the
            // system that is mid-scene with it is a bug in the other file.
            if (Doing == Duty.Contact) return true;

            var now = Game.GameTime;

            if (Doing != Duty.StandingDown && now > OffDutyAt) return false;

            switch (Doing)
            {
                case Duty.Sitting:
                    if (now > MoveOnAt) return true;   // the fleet gives it somewhere to go
                    break;

                case Duty.Responding:
                    if (Car.Position.DistanceTo(Target) < ArrivedRange)
                    {
                        Doing = Duty.Searching;
                        MoveOnAt = now + 20000 + rng.Next(15000);

                        Lights(true);
                        Cops.Megaphone(Driver, "SURROUNDED");
                    }
                    break;
            }

            Unstick(now);
            return true;
        }

        /// <summary>Whether it has reached where it was going.</summary>
        public bool Arrived() =>
            Cops.Alive(Car) && Car.Position.DistanceTo(Target) < ArrivedRange;

        /// <summary>
        /// A car that has not moved is a car with a route it cannot drive.
        ///
        /// Two nudges, then it is somebody else's problem -- re-issuing a task to a car wedged
        /// between a bin and a wall forever is how a patrol system ends up with three of its
        /// four units parked in an alley for the whole session.
        /// </summary>
        private void Unstick(int now)
        {
            if (_lookedAt == 0)
            {
                _lookedAt = now;
                _wasAt = Car.Position;
                return;
            }

            if (now - _lookedAt < StuckAfterMs) return;

            var moved = Car.Position.DistanceTo(_wasAt);

            _lookedAt = now;
            _wasAt = Car.Position;

            if (moved > StuckMoved || Doing == Duty.Sitting || Doing == Duty.Searching)
            {
                _nudges = 0;
                return;
            }

            if (++_nudges > MaxNudges)
            {
                // Marked as finished rather than deleted. It drives off if it can and gets
                // released when it is out of sight either way.
                OffDutyAt = 0;
                return;
            }

            Log.Debug("Unit stuck; re-routing (" + _nudges + ").");
            Drive(Target, Doing == Duty.Responding ? ResponseSpeed : CruiseSpeed,
                  Doing == Duty.Responding ? UrgentStyle : CruiseStyle);
        }

        // ---- the car -----------------------------------------------------------

        private void Drive(Vector3 to, float speed, int style)
        {
            try
            {
                if (!Alive) return;

                Driver.Task.ClearAll();

                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                              Driver.Handle, Car.Handle, to.X, to.Y, to.Z,
                              speed, style, StopThere ? 8f : 20f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not route a unit: " + ex.Message);
            }
        }

        /// <summary>
        /// Lights on, and whether the noise comes with them.
        ///
        /// A beat car sitting on a corner has the bar lit and nothing else -- that is what
        /// SET_VEHICLE_HAS_MUTED_SIRENS is for, and without it every parked unit in the city
        /// is howling. The noise is turned back on only for a real response.
        /// </summary>
        public void Lights(bool urgent)
        {
            try
            {
                if (!Cops.Alive(Car)) return;

                Function.Call(Hash.SET_VEHICLE_HAS_MUTED_SIRENS, Car.Handle, !urgent);
                Car.IsSirenActive = true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set the light bar: " + ex.Message);
            }
        }

        public void Dark()
        {
            try
            {
                if (Cops.Alive(Car)) Car.IsSirenActive = false;
            }
            catch
            {
                // Not worth a line in the log.
            }
        }

        /// <summary>Hands the car and everybody in it back to the game.</summary>
        public void Release()
        {
            foreach (var p in Everyone()) Cops.LetGo(p);
            Cops.LetGo(Car);
        }
    }
}
