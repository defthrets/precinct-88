using System;
using GTA;
using GTA.Math;
using Precinct88.Core;
using Precinct88.Streets;

namespace Precinct88.Response
{
    /// <summary>
    /// Somebody actually turning up.
    ///
    /// THIS IS THE HALF THE PATROL BUILD WAS MISSING. Notice says what an officer saw; this
    /// decides who goes, and it is deliberately the whole of the decision -- there is no
    /// wanted level involved anywhere in it.
    ///
    /// A RESPONSE IS A REASSIGNMENT, WHICH IS THE ENTIRE ARGUMENT OF THE MOD. Nothing is
    /// created because you did something. The car that turns up was already out driving that
    /// district, it took as long as it took to get to you, and when the call goes cold it goes
    /// back to what it was doing. The Surge below is the one exception and it is not a
    /// contradiction: it raises how many units the district WANTS, so the next car to go out
    /// goes out sooner and heads towards the call rather than towards a patrol route. It still
    /// has to drive.
    ///
    /// SO SOMETIMES NOBODY COMES, and that is correct rather than broken. Do a burnout in
    /// Paleto Bay at four in the morning with one unit on the road forty streets away and the
    /// honest answer is that nothing happens for a long time. The districts have different
    /// densities precisely so that this is different in Davis.
    ///
    /// It goes COLD rather than ending: every fresh sighting pushes the clock out, so a man
    /// who keeps doing it keeps the call alive, and one who stops has forty-five seconds of
    /// officers looking around before they give up. That is what makes driving away from
    /// something feel like getting away with it rather than like a switch being flipped.
    /// </summary>
    internal sealed class Callout
    {
        private const int TickMs = 900;

        /// <summary>How long after the last sighting before they stop looking.</summary>
        private const int ColdMs = 45000;

        /// <summary>How long between one unit being sent and the next.</summary>
        private const int SendGapMs = 2600;

        /// <summary>How far away a unit can be and still be worth sending.</summary>
        private const float SendFrom = 900f;

        private readonly Settings _cfg;
        private readonly Fleet _fleet;

        private string _what;
        private Vector3 _where;
        private int _weight;
        private int _freshAt;
        private int _lastSend;
        private int _lastTick;

        /// <summary>Puts a line on screen. Wired to Screen.Ticker by Main.</summary>
        public Action<string> Say;

        public Callout(Settings cfg, Fleet fleet)
        {
            _cfg = cfg;
            _fleet = fleet;
        }

        /// <summary>Whether anything is being answered right now.</summary>
        public bool Running => _what != null;

        /// <summary>What is being answered, for a status line. Null when nothing is.</summary>
        public string What => _what;

        /// <summary>
        /// Something was seen. Start a call, or feed the one already running.
        /// </summary>
        public void Report(string what, Vector3 where, int weight)
        {
            if (!_cfg.RespondToCrime) return;

            var now = Game.GameTime;

            var starting = _what == null;
            var worse = !starting && weight > _weight;

            // ALWAYS THE NEWEST POSITION. Officers head for where it was last seen rather than
            // where it started, which is the difference between a response and a shrine.
            _where = where;
            _freshAt = now;

            if (starting || worse)
            {
                _what = what;
                _weight = weight;

                if (Say != null)
                {
                    Say(starting ? "Reported: " + what + "." : "Now: " + what + ".");
                }

                Log.Info((starting ? "Call started: " : "Call escalated to: ") + what +
                         " (weight " + weight + ").");

                // Sent immediately on a new or worse call rather than waiting out the gap.
                _lastSend = 0;
            }

            // The district wants more cars out while this is live, and the next one out heads
            // here. Fleet reads both of these; neither creates anything on its own.
            _fleet.Surge = Extra(_weight);
            _fleet.SurgeTo = _where;
        }

        public void Update()
        {
            if (!_cfg.RespondToCrime)
            {
                if (Running) Clear("switched off");
                return;
            }

            if (!Running) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            try
            {
                if (now - _freshAt > ColdMs)
                {
                    Clear("nothing further");
                    return;
                }

                // Keep pointing the surge at the latest position -- Fleet reads SurgeTo when it
                // puts a car out, which can be several seconds after the last report.
                _fleet.SurgeTo = _where;

                if (now - _lastSend < SendGapMs) return;
                if (_fleet.OnCalls() >= Most(_weight)) return;

                var unit = Free();
                if (unit == null) return;

                _lastSend = now;
                unit.RespondTo(_where, _what);

                Log.Debug("Unit sent to " + _what + " (" + _fleet.OnCalls() + " on it).");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not run the call: " + ex.Message);
                Clear("something went wrong");
            }
        }

        /// <summary>
        /// The nearest unit that is not already busy with this.
        ///
        /// Fleet.NearestFree is not used because it counts a unit already on the call as
        /// available, which would have one car being re-tasked to the same place over and over
        /// while the second and third never left their patrol.
        /// </summary>
        private Unit Free()
        {
            Unit best = null;
            var bestDist = SendFrom;

            foreach (var unit in _fleet.Units)
            {
                if (!unit.Alive) continue;

                if (unit.Doing != Duty.Rolling && unit.Doing != Duty.Sitting) continue;

                var d = unit.Car.Position.DistanceTo(_where);
                if (d >= bestDist) continue;

                bestDist = d;
                best = unit;
            }

            return best;
        }

        /// <summary>
        /// How many may be on it at once.
        ///
        /// Small on purpose. More than this empties the district, the patrol stops existing,
        /// and every street around goes quiet for the duration -- which is the opposite of what
        /// a response should feel like.
        /// </summary>
        private static int Most(int weight)
        {
            if (weight >= 3) return 3;
            if (weight == 2) return 2;

            return 1;
        }

        /// <summary>Extra cars the district wants out while this is live.</summary>
        private static int Extra(int weight)
        {
            return weight >= 3 ? 2 : weight == 2 ? 1 : 0;
        }

        /// <summary>
        /// Stands everybody down and hands the district back to its patrol.
        ///
        /// EVERY UNIT IS PUT BACK BY HAND. Clearing the surge alone would leave whoever was
        /// driving to the call still driving to it, lights on, forever -- Fleet only re-steers
        /// a unit once it has arrived somewhere, and a unit heading to a call that no longer
        /// exists never arrives at anything.
        /// </summary>
        public void Clear(string why)
        {
            if (_what != null) Log.Info("Call over (" + why + "): " + _what + ".");

            _what = null;
            _weight = 0;
            _where = Vector3.Zero;
            _freshAt = 0;
            _lastSend = 0;

            try
            {
                _fleet.Surge = 0;
                _fleet.SurgeTo = Vector3.Zero;

                foreach (var unit in _fleet.Units)
                {
                    if (!unit.Alive) continue;
                    if (unit.Doing != Duty.Responding && unit.Doing != Duty.Searching) continue;

                    // Back to work from where he is standing. Fleet will give him somewhere
                    // proper to be on its next pass.
                    unit.BackToWork(unit.Car.Position);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not stand units down: " + ex.Message);
            }
        }
    }
}
