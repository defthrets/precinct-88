using System;
using GTA;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Response;
using Precinct88.Streets;

namespace Precinct88.Contact
{
    /// <summary>
    /// Deciding that somebody is worth pulling over.
    ///
    /// THE SEPARATION THIS FILE EXISTS TO MAINTAIN: a CRIME goes to the manhunt, a CAUSE comes
    /// here. Shooting somebody is a crime -- it is reported, it is searched for, and there is
    /// nothing to discuss. Doing fifty through Vespucci is a cause: it gets you stopped, and
    /// what happens next depends on what you do about it.
    ///
    /// Vanilla collapses those into one thing, which is why driving badly in GTA V produces a
    /// pursuit. Keeping them apart is most of why a stop in this mod is a scene rather than the
    /// opening of a firefight -- and it is why running from one is interesting, because that is
    /// the moment a cause turns into a crime, in Stop.Ran, with the officer already stood in
    /// front of you.
    ///
    /// District attention is the dial. The same man doing the same thing gets stopped in
    /// Rockford Hills and ignored in Davis, and that is deliberate: it is the single clearest
    /// way the mod says the map is not uniform.
    /// </summary>
    internal sealed class Watch
    {
        private const int TickMs = 900;

        /// <summary>Over this, in metres a second, in front of an officer.</summary>
        private const float RecklessSpeed = 33f;

        /// <summary>How long the player has to be stood still for it to look like loitering.</summary>
        private const int LoiterMs = 25000;

        /// <summary>How still. Anything under this is stood about rather than walking past.</summary>
        private const float LoiterDrift = 6f;

        private readonly Settings _cfg;
        private readonly Fleet _fleet;
        private readonly Stop _stop;
        private readonly Random _rng = new Random();

        private int _lastTick;
        private int _stillSince;
        private GTA.Math.Vector3 _stillAt;

        /// <summary>
        /// Whether a screen is up and owns the controls.
        ///
        /// The panel disables every control and re-enables the six it uses, so the comply key
        /// cannot physically be pressed while it is open -- but the PROMPT would still be drawn
        /// over the top of it, telling the player to hold a button that does nothing. Set by
        /// Main, which is the only thing that knows what is on screen.
        /// </summary>
        public Func<bool> Occupied;

        public Watch(Settings cfg, Fleet fleet, Stop stop)
        {
            _cfg = cfg;
            _fleet = fleet;
            _stop = stop;
        }

        public void Update()
        {
            if (!_cfg.ContactEnabled) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            var me = Game.Player.Character;
            if (me == null || !me.Exists() || me.IsDead) return;

            Still(me, now);

            // Nothing new starts behind a panel. A stop that opens while the settings are up
            // is a scene the player misses the first ten seconds of.
            if (Occupied != null && Occupied()) return;

            if (!_stop.Possible()) return;

            // Only a unit THIS MOD PUT ON THE ROAD may start a stop. An ambient officer walking
            // out of a station is not something we own -- taking him over would leave his own
            // scenario half-finished, and worse, he could be another mod's.
            var unit = Looking(me);
            if (unit == null) return;

            var why = Reason(me, now);
            if (why == null) return;

            _stop.Begin(unit, why.Value);
        }

        /// <summary>The nearest of our units with somebody in it who can see the player.</summary>
        private Unit Looking(Ped me)
        {
            foreach (var unit in _fleet.Units)
            {
                if (!unit.Alive) continue;
                if (unit.Doing == Duty.Contact || unit.Doing == Duty.StandingDown) continue;

                foreach (var officer in unit.Everyone())
                {
                    if (Cops.Sees(officer, me, _cfg.NoticeRange)) return unit;
                }
            }

            return null;
        }

        /// <summary>
        /// What they would stop him for, or nothing.
        ///
        /// Ordered by how obvious it is from the outside, which is also the order an officer
        /// would notice them in: a gun is a gun, then how the car is being driven, then the car
        /// itself, then the man.
        /// </summary>
        private Why? Reason(Ped me, int now)
        {
            var attention = Districts.At(me.Position).Attention;

            if (_cfg.StopForWeapons && !me.IsInVehicle() && Cops.Armed(me))
            {
                // NOT ROLLED AGAINST ATTENTION. A gun in your hand in the street is the one
                // thing that stops a car everywhere in the map, and making it a dice roll in
                // Davis means the mod's most legible rule is one the player cannot rely on.
                return Why.Weapon;
            }

            if (me.IsInVehicle())
            {
                if (!_cfg.TrafficStops) return null;

                var car = me.CurrentVehicle;
                if (!Cops.Alive(car)) return null;
                if (car.Driver == null || car.Driver.Handle != me.Handle) return null;

                if (car.Speed > RecklessSpeed) return Why.Driving;

                // The plate. Rolled, because running every plate that goes past is not a
                // thing that happens -- and because being stopped in a stolen car every single
                // time would make stealing one pointless.
                if (Stolen(car) && Roll(attention * 0.7f)) return Why.Plate;

                return null;
            }

            // Stood about somewhere that notices. This is the one that makes Rockford Hills
            // feel different to everywhere else, and it is the only reason a player who has
            // done nothing at all ever gets spoken to.
            if (now - _stillSince > LoiterMs && Roll(attention * 0.5f))
            {
                _stillSince = now;   // reset, or it fires again on the next tick
                return Why.Suspicion;
            }

            return null;
        }

        /// <summary>Tracks how long the player has been in roughly one place.</summary>
        private void Still(Ped me, int now)
        {
            try
            {
                if (me.IsInVehicle() || _stillSince == 0 ||
                    me.Position.DistanceTo(_stillAt) > LoiterDrift)
                {
                    _stillSince = me.IsInVehicle() ? 0 : now;
                    _stillAt = me.Position;
                }
            }
            catch
            {
                _stillSince = 0;
            }
        }

        private bool Roll(float chance)
        {
            if (chance <= 0f) return false;
            return _rng.NextDouble() < chance;
        }

        private static bool Stolen(Vehicle car)
        {
            // IS_VEHICLE_STOLEN is the engine's own flag, set when the player takes a car that
            // was not his -- so it already knows about jacking somebody at a light, hotwiring
            // something parked, and the difference between those and a car he bought.
            // Reproducing that judgement by hand would get it wrong in a dozen small ways.
            try { return Function.Call<bool>(Hash.IS_VEHICLE_STOLEN, car.Handle); }
            catch { return false; }
        }
    }
}
