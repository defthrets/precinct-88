using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Streets;

namespace Precinct88.Contact
{
    /// <summary>Something an officer would pull you over for.</summary>
    internal enum Violation
    {
        Speeding,
        RedLight,
        WrongWay,
        Pavement,
        HitPed,
        Collision,
        NoHelmet,
        Wheelie,
        Drifting,
        Tailgating,
        Wreck,
        Phone,
        Burnout,

        /// <summary>
        /// Driving while your licence is gone.
        ///
        /// The teeth of the whole record. Without it a suspension is a word in a panel: you
        /// carry on driving the same car past the same officers and nothing about the world has
        /// changed. With it, being suspended makes the act of driving itself the offence, which
        /// is what a disqualification actually is.
        /// </summary>
        Disqualified,
    }

    /// <summary>
    /// How you are driving, watched continuously.
    ///
    /// THE CRIME/CAUSE LINE HOLDS HERE and it is the reason this file is in Contact and not in
    /// Response. Nothing detected here is reported to the police. A violation is a REASON TO BE
    /// STOPPED -- an officer who can see you doing it has grounds to pull you over, and that is
    /// all. What happens next is a conversation, and only refusing to have it turns any of this
    /// into a crime. Vanilla collapses the two, which is why driving badly in GTA V produces a
    /// pursuit rather than a ticket.
    ///
    /// FOUR OF THESE ARE THE ENGINE'S OWN BOOKKEEPING, not mine. GET_TIME_SINCE_PLAYER_-
    /// DROVE_AGAINST_TRAFFIC, _DROVE_ON_PAVEMENT, _HIT_PED and _HIT_VEHICLE are natives, and
    /// the game has been tracking all four the entire time. Every heuristic I could write for
    /// those would be a worse version of something already sitting there for free -- and would
    /// disagree with the game about what counts, which is the sort of thing players notice
    /// without being able to say why.
    ///
    /// THERE IS NO STOP SIGN AND THERE IS NOT GOING TO BE. Pull Me Over lists one; GTA V has no
    /// stop-sign data anywhere -- not in the node network, not as props with meaning. Detecting
    /// it would mean guessing which junctions have one, and a ticket for running a stop sign
    /// that was never there is worse than no feature. Left out on purpose rather than faked.
    /// </summary>
    internal sealed class Violations
    {
        private const int TickMs = 180;

        /// <summary>How long the same violation is left alone after being noticed.</summary>
        private const int RepeatCooldownMs = 30000;

        /// <summary>
        /// How recently a native must report something for it to count as happening NOW.
        ///
        /// These return milliseconds since the event, so "is he doing it" is really "did the
        /// engine see it in the last moment". Short, or wrong-way lingers for a minute after
        /// you have straightened up.
        /// </summary>
        private const int StillDoingItMs = 900;

        /// <summary>One-shot events. Long enough for an officer to have registered it.</summary>
        private const int JustHappenedMs = 4000;

        // ---- how long each must be sustained before it counts ------------------
        //
        // Taken from what a real stop needs to be fair. A wheel over a kerb for a fifth of a
        // second is not driving on the pavement, and a mod that tickets it is a mod nobody
        // finishes a journey in.

        private const int SpeedingGraceMs = 3000;
        private const int PavementGraceMs = 2000;
        private const int WrongWayGraceMs = 3000;
        private const int TailgateGraceMs = 2000;
        private const int DriftGraceMs = 900;

        /// <summary>Over the limit by this much before anybody cares, in metres a second.</summary>
        private const float SpeedTolerance = 2.3f;

        /// <summary>Body health under this and the car is visibly a wreck. 1000 is pristine.</summary>
        private const float WreckedUnder = 420f;

        /// <summary>Sideways enough to be a drift rather than a corner, in degrees.</summary>
        private const float DriftAngle = 24f;

        private const float DriftMinSpeed = 12f;

        /// <summary>Tailgating: this close, this fast, in front of you.</summary>
        private const float TailgateGap = 7.5f;
        private const float TailgateMinSpeed = 11f;

        /// <summary>How far to look for the traffic that is acting as our traffic light.</summary>
        private const float LightSensorRange = 34f;

        private readonly Settings _cfg;

        /// <summary>When each violation started being true. 0 when it is not.</summary>
        private readonly Dictionary<Violation, int> _since = new Dictionary<Violation, int>();

        /// <summary>When each was last handed to anybody, so it is not reported twice.</summary>
        private readonly Dictionary<Violation, int> _told = new Dictionary<Violation, int>();

        /// <summary>What is true right now and has been for long enough.</summary>
        private readonly List<Violation> _live = new List<Violation>();

        private int _lastTick;

        public Violations(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>Everything currently committed. Live list; do not hold it.</summary>
        public IReadOnlyList<Violation> Live => _live;

        public bool Any => _live.Count > 0;

        public void Update()
        {
            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            _live.Clear();

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists() || me.IsDead) { _since.Clear(); return; }

                if (!me.IsInVehicle()) { _since.Clear(); return; }

                var car = me.CurrentVehicle;
                if (!Cops.Alive(car)) { _since.Clear(); return; }

                // Not your car, not your problem. A passenger is not driving.
                if (car.Driver == null || car.Driver.Handle != me.Handle) { _since.Clear(); return; }

                if (!Enforced(car)) { _since.Clear(); return; }

                Look(me, car, now);
            }
            catch (Exception ex)
            {
                Log.Debug("Violation check failed: " + ex.Message);
            }
        }

        /// <summary>Whether this kind of vehicle is enforced at all.</summary>
        private bool Enforced(Vehicle car)
        {
            try
            {
                var model = car.Model;

                if (Function.Call<bool>(Hash.IS_THIS_MODEL_A_BICYCLE, model.Hash))
                {
                    return _cfg.EnforceBicycles;
                }

                if (Function.Call<bool>(Hash.IS_THIS_MODEL_A_BIKE, model.Hash))
                {
                    return _cfg.EnforceBikes;
                }

                return _cfg.EnforceCars;
            }
            catch
            {
                return true;
            }
        }

        // ---- the checks --------------------------------------------------------

        private void Look(Ped me, Vehicle car, int now)
        {
            var speed = car.Speed;

            Sustained(Violation.Speeding, Speeding(car, speed), SpeedingGraceMs, now);
            Sustained(Violation.WrongWay, Recent(Hash.GET_TIME_SINCE_PLAYER_DROVE_AGAINST_TRAFFIC),
                      WrongWayGraceMs, now);
            Sustained(Violation.Pavement, Recent(Hash.GET_TIME_SINCE_PLAYER_DROVE_ON_PAVEMENT),
                      PavementGraceMs, now);
            Sustained(Violation.Drifting, Drifting(car, speed), DriftGraceMs, now);
            Sustained(Violation.Tailgating, Tailgating(car, speed), TailgateGraceMs, now);

            // One-shots. No grace -- they either happened or they did not.
            Instant(Violation.HitPed, Within(Hash.GET_TIME_SINCE_PLAYER_HIT_PED, JustHappenedMs), now);
            Instant(Violation.Collision,
                    Within(Hash.GET_TIME_SINCE_PLAYER_HIT_VEHICLE, JustHappenedMs), now);

            // States. True for as long as they are true, no timer needed.
            Instant(Violation.NoHelmet, NoHelmet(me, car), now);
            Instant(Violation.Wheelie, Wheelie(car, speed), now);
            Instant(Violation.Wreck, Wrecked(car), now);
            Instant(Violation.Phone, OnThePhone(me), now);
            Instant(Violation.Burnout, Burnout(car), now);

            Instant(Violation.RedLight, RanTheLight(car, speed), now);

            // DISQUALIFIED IS NOT CHECKED HERE YET, and the enum member is kept rather than
            // deleted so that nothing downstream has to be rewritten when it comes back.
            //
            // It is the only violation that is a state of YOU rather than of the car or of the
            // driving -- you cannot stop committing it by driving better -- and it needs
            // Licence, which is still parked. Restore this line with Licence and nothing else
            // in this file changes:
            //
            //     Instant(Violation.Disqualified, _licence != null && _licence.IsSuspended, now);
        }

        private bool Speeding(Vehicle car, float speed)
        {
            var limit = Limits.For(car.Position);
            return speed > limit + SpeedTolerance;
        }

        /// <summary>
        /// Sideways rather than round a corner.
        ///
        /// The angle between where the car is POINTING and where it is actually GOING. A car
        /// taking a bend has those nearly aligned; a car drifting does not, and the number is
        /// the same whichever way it is sliding.
        /// </summary>
        private static bool Drifting(Vehicle car, float speed)
        {
            try
            {
                if (speed < DriftMinSpeed) return false;
                if (!car.IsOnAllWheels) return false;

                var going = car.Velocity;
                going.Z = 0f;

                if (going.Length() < 1f) return false;

                going.Normalize();

                var facing = car.ForwardVector;
                facing.Z = 0f;
                facing.Normalize();

                var dot = Vector3.Dot(facing, going);
                if (dot < 0f) return false;   // reversing is not drifting

                if (dot > 1f) dot = 1f;

                var angle = (float)(Math.Acos(dot) * 180d / Math.PI);
                return angle > DriftAngle;
            }
            catch
            {
                return false;
            }
        }

        private static bool Tailgating(Vehicle car, float speed)
        {
            try
            {
                if (speed < TailgateMinSpeed) return false;

                foreach (var other in World.GetNearbyVehicles(car.Position, TailgateGap + 4f))
                {
                    if (other == null || !other.Exists()) continue;
                    if (other.Handle == car.Handle) continue;

                    var to = other.Position - car.Position;
                    to.Z = 0f;

                    var gap = to.Length();
                    if (gap > TailgateGap || gap < 0.5f) continue;

                    to.Normalize();

                    var facing = car.ForwardVector;
                    facing.Z = 0f;
                    facing.Normalize();

                    // Directly in front, in a narrow cone. Beside you in the next lane is not
                    // tailgating, and a wide cone makes every dual carriageway an offence.
                    if (Vector3.Dot(facing, to) < 0.92f) continue;

                    // And going the same way, or it is oncoming traffic rather than somebody
                    // you are sitting behind.
                    if (Vector3.Dot(facing, other.ForwardVector) < 0.5f) continue;

                    return true;
                }
            }
            catch
            {
                // Treated as not tailgating.
            }

            return false;
        }

        private static bool NoHelmet(Ped me, Vehicle car)
        {
            try
            {
                if (!Function.Call<bool>(Hash.IS_THIS_MODEL_A_BIKE, car.Model.Hash)) return false;

                return !Function.Call<bool>(Hash.IS_PED_WEARING_HELMET, me.Handle);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A wheel off the ground on a bike, without being airborne.
        ///
        /// IsOnAllWheels goes false for a wheelie, a stoppie AND a jump, so the height check is
        /// what separates riding badly from riding off something. Half a metre is generous --
        /// a wheelie lifts the front, not the whole bike.
        /// </summary>
        private static bool Wheelie(Vehicle car, float speed)
        {
            try
            {
                if (speed < 4f) return false;
                if (!Function.Call<bool>(Hash.IS_THIS_MODEL_A_BIKE, car.Model.Hash)) return false;
                if (car.IsOnAllWheels) return false;

                return Function.Call<float>(Hash.GET_ENTITY_HEIGHT_ABOVE_GROUND, car.Handle) < 0.6f;
            }
            catch
            {
                return false;
            }
        }

        private static bool Wrecked(Vehicle car)
        {
            try { return car.BodyHealth < WreckedUnder; }
            catch { return false; }
        }

        private static bool OnThePhone(Ped me)
        {
            try
            {
                return Function.Call<bool>(Hash.IS_PED_RUNNING_MOBILE_PHONE_TASK, me.Handle) ||
                       Function.Call<bool>(Hash.IS_MOBILE_PHONE_CALL_ONGOING);
            }
            catch
            {
                return false;
            }
        }

        private static bool Burnout(Vehicle car)
        {
            try { return car.IsInBurnout; }
            catch { return false; }
        }

        /// <summary>
        /// Running a red, worked out from the traffic rather than from the light.
        ///
        /// THE GAME WILL NOT TELL YOU WHAT COLOUR A LIGHT IS. There is a native to OVERRIDE a
        /// junction's lights and none at all to read one, so a mod cannot ask. What it can ask
        /// is IS_VEHICLE_STOPPED_AT_TRAFFIC_LIGHTS about any car it likes -- and the AI obeys
        /// the lights perfectly.
        ///
        /// So the traffic IS the sensor. If cars pointing the same way as you are sitting
        /// stopped at a light, the light in your direction is red, and if you are going through
        /// it at speed you have just run it. Facing is what makes this work rather than
        /// misfire: cars stopped on the CROSS street are stopped at a different light, and
        /// counting those would ticket you for every junction you legally drove through on
        /// green.
        ///
        /// Two of them, because one car stopped in your direction might simply be parked, or
        /// turning, or stuck.
        /// </summary>
        private static bool RanTheLight(Vehicle car, float speed)
        {
            try
            {
                // Slow enough and you are stopping for it, whatever the light says.
                if (speed < 8f) return false;

                // If our own car is registering as stopped at lights we are plainly not
                // running one.
                if (Function.Call<bool>(Hash.IS_VEHICLE_STOPPED_AT_TRAFFIC_LIGHTS, car.Handle))
                {
                    return false;
                }

                var facing = car.ForwardVector;
                facing.Z = 0f;

                if (facing.Length() < 0.01f) return false;
                facing.Normalize();

                var waiting = 0;

                foreach (var other in World.GetNearbyVehicles(car.Position, LightSensorRange))
                {
                    if (other == null || !other.Exists()) continue;
                    if (other.Handle == car.Handle) continue;

                    if (!Function.Call<bool>(Hash.IS_VEHICLE_STOPPED_AT_TRAFFIC_LIGHTS,
                                             other.Handle))
                    {
                        continue;
                    }

                    var theirs = other.ForwardVector;
                    theirs.Z = 0f;

                    if (theirs.Length() < 0.01f) continue;
                    theirs.Normalize();

                    // Pointing the same way we are, so waiting at OUR light rather than the
                    // one on the cross street.
                    if (Vector3.Dot(facing, theirs) < 0.7f) continue;

                    if (++waiting >= 2) return true;
                }
            }
            catch
            {
                // No opinion.
            }

            return false;
        }

        // ---- the engine's own timers -------------------------------------------

        /// <summary>Whether one of the game's own counters says this is happening right now.</summary>
        private static bool Recent(Hash which)
        {
            return Within(which, StillDoingItMs);
        }

        private static bool Within(Hash which, int ms)
        {
            try
            {
                var since = Function.Call<int>(which);

                // A negative or absurd value means it has never happened this session, which
                // the natives signal inconsistently -- so anything outside a sane window is
                // treated as never rather than trusted.
                return since >= 0 && since < ms;
            }
            catch
            {
                return false;
            }
        }

        // ---- bookkeeping -------------------------------------------------------

        /// <summary>Something that has to be kept up for a while before it counts.</summary>
        private void Sustained(Violation what, bool happening, int graceMs, int now)
        {
            if (!happening)
            {
                _since.Remove(what);
                return;
            }

            int began;
            if (!_since.TryGetValue(what, out began))
            {
                _since[what] = now;
                return;
            }

            if (now - began < graceMs) return;

            Add(what, now);
        }

        /// <summary>Something that counts the moment it is true.</summary>
        private void Instant(Violation what, bool happening, int now)
        {
            if (!happening) return;

            Add(what, now);
        }

        private void Add(Violation what, int now)
        {
            if (!_live.Contains(what)) _live.Add(what);
        }

        /// <summary>
        /// Hands over what an officer has grounds to stop you for, and marks it told.
        ///
        /// The cooldown lives HERE rather than in the detection, so the HUD and anything else
        /// that wants to know what you are currently doing wrong still sees it -- only the
        /// grounds for a fresh stop go quiet.
        /// </summary>
        public List<Violation> Take(int cooldownMs = RepeatCooldownMs)
        {
            var now = Game.GameTime;
            var got = new List<Violation>();

            foreach (var v in _live)
            {
                int told;
                if (_told.TryGetValue(v, out told) && now - told < cooldownMs) continue;

                _told[v] = now;
                got.Add(v);
            }

            return got;
        }

        /// <summary>Forgets every cooldown. For an arrest, a death, or a reload.</summary>
        public void Forget()
        {
            _since.Clear();
            _told.Clear();
            _live.Clear();
        }

        // ---- what they are called ----------------------------------------------

        /// <summary>What an officer would say he stopped you for.</summary>
        public static string Called(Violation what)
        {
            switch (what)
            {
                case Violation.Speeding: return "speeding";
                case Violation.RedLight: return "running a red light";
                case Violation.WrongWay: return "driving against traffic";
                case Violation.Pavement: return "driving on the pavement";
                case Violation.HitPed: return "hitting a pedestrian";
                case Violation.Collision: return "a collision";
                case Violation.NoHelmet: return "riding without a helmet";
                case Violation.Wheelie: return "pulling a wheelie in traffic";
                case Violation.Drifting: return "driving without due care";
                case Violation.Tailgating: return "tailgating";
                case Violation.Wreck: return "an unroadworthy vehicle";
                case Violation.Phone: return "using a phone at the wheel";
                case Violation.Burnout: return "a burnout in a public street";
                case Violation.Disqualified: return "driving while disqualified";
                default: return "a traffic offence";
            }
        }

        /// <summary>
        /// How seriously it is taken, 1 to 3.
        ///
        /// Not a fine and not licence points -- both of those come later and both are built on
        /// this. What it decides now is which violation an officer mentions when you have
        /// managed several at once, which you very often have.
        /// </summary>
        public static int Weight(Violation what)
        {
            switch (what)
            {
                // Above everything. A disqualified driver is the one thing on this list that
                // is never a conversation.
                case Violation.Disqualified: return 4;

                case Violation.HitPed: return 3;
                case Violation.RedLight: return 3;
                case Violation.WrongWay: return 3;
                case Violation.Pavement: return 2;
                case Violation.Speeding: return 2;
                case Violation.Collision: return 2;
                case Violation.Drifting: return 2;
                case Violation.Burnout: return 2;
                case Violation.Phone: return 2;
                default: return 1;
            }
        }

        /// <summary>The one he leads with. Null when there is nothing.</summary>
        public static Violation? Worst(IReadOnlyList<Violation> of)
        {
            Violation? best = null;
            var bestWeight = -1;

            for (var i = 0; i < of.Count; i++)
            {
                var w = Weight(of[i]);
                if (w <= bestWeight) continue;

                bestWeight = w;
                best = of[i];
            }

            return best;
        }
    }
}
