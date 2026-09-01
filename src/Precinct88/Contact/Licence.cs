using System;
using System.Collections.Generic;
using GTA;
using Precinct88.Core;

namespace Precinct88.Contact
{
    /// <summary>One thing they wrote down about you, and when.</summary>
    internal sealed class Charge
    {
        public Violation What;
        public int Points;
        public int Fine;

        /// <summary>Real milliseconds since the session began. See Licence for why not a date.</summary>
        public int At;
    }

    /// <summary>
    /// What is on your licence.
    ///
    /// THIS IS THE THING THAT MAKES A SECOND STOP DIFFERENT FROM A FIRST. Without it every
    /// traffic stop in the mod is the same traffic stop: the officer has no idea whether he is
    /// talking to somebody who has never been pulled over or somebody he stopped twenty minutes
    /// ago for the same thing. A record is the cheapest possible way to give the police a
    /// memory, and memory is most of what makes them read as people.
    ///
    /// TWELVE POINTS AND YOU ARE OFF THE ROAD, which is the British convention and a good one
    /// for a game: it is a small enough number to be reachable in an evening of bad driving and
    /// large enough that no single stop gets you there.
    ///
    /// CHARGES EXPIRE, and they have to. A record that only ever grows is a save file the player
    /// eventually has to abandon, and the whole point of the decay is that driving properly for
    /// a while is a real way back. Pull Me Over does the same and makes the window configurable;
    /// so does this.
    ///
    /// TIMES ARE Game.GameTime, NOT DATES. That is a deliberate limitation and worth being
    /// honest about: the clock resets when the game restarts, so charges are effectively
    /// per-session unless the save is reloaded within one. The alternative is wall-clock dates,
    /// which means a player who leaves the game for a week comes back to a clean licence
    /// regardless of what they did -- and that is a worse lie than the one this tells.
    /// </summary>
    internal sealed class Licence
    {
        /// <summary>Off the road at this many.</summary>
        public const int Suspended = 12;

        private readonly List<Charge> _charges = new List<Charge>();

        private bool _dirty;

        /// <summary>How long a charge stays on the licence, in real minutes.</summary>
        public float ExpireMinutes = 20f;

        public bool Dirty => _dirty;

        public void Clean() => _dirty = false;

        /// <summary>Live points, expired charges already dropped.</summary>
        public int Points
        {
            get
            {
                Prune();

                var n = 0;
                for (var i = 0; i < _charges.Count; i++) n += _charges[i].Points;

                return n;
            }
        }

        public int Count
        {
            get { Prune(); return _charges.Count; }
        }

        /// <summary>Everything owed and never paid. Feature 9 turns this into a ledger.</summary>
        public int Owed
        {
            get
            {
                Prune();

                var n = 0;
                for (var i = 0; i < _charges.Count; i++) n += _charges[i].Fine;

                return n;
            }
        }

        public bool IsSuspended => Points >= Suspended;

        /// <summary>How close to losing it, 0 to 1. For the HUD and the panel.</summary>
        public float Toward => Math.Min(1f, Points / (float)Suspended);

        // ---- writing on it -----------------------------------------------------

        public void Add(Violation what, int points, int fine)
        {
            _charges.Add(new Charge
            {
                What = what,
                Points = points,
                Fine = fine,
                At = Game.GameTime,
            });

            _dirty = true;

            Log.Info("Licence: " + points + " point(s) for " + Violations.Called(what) +
                     " (" + Points + " total).");
        }

        /// <summary>
        /// Wiped.
        ///
        /// An arrest or a death clears it, which is Pull Me Over's rule and the right one --
        /// being booked IS the consequence, and carrying the points through as well is being
        /// punished twice for one evening.
        /// </summary>
        public void Wipe(string why)
        {
            if (_charges.Count == 0) return;

            Log.Info("Licence wiped (" + why + "): " + _charges.Count + " charge(s) gone.");

            _charges.Clear();
            _dirty = true;
        }

        /// <summary>Drops anything old enough to have expired.</summary>
        private void Prune()
        {
            try
            {
                var now = Game.GameTime;
                var life = (int)(ExpireMinutes * 60000f);

                if (life <= 0) return;

                for (var i = _charges.Count - 1; i >= 0; i--)
                {
                    // A charge from BEFORE the clock reset reads as being in the future.
                    // Treated as expired rather than as immortal, which is the failure that
                    // would otherwise outlive every session.
                    var age = now - _charges[i].At;

                    if (age < 0 || age > life)
                    {
                        _charges.RemoveAt(i);
                        _dirty = true;
                    }
                }
            }
            catch
            {
                // A prune that throws leaves the list alone, which is safe.
            }
        }

        // ---- the record --------------------------------------------------------

        public void ToJson(Json doc)
        {
            var mine = Json.Object();
            mine.Set("points", Points);

            var list = Json.Array();

            foreach (var c in _charges)
            {
                var one = Json.Object();
                one.Set("what", c.What.ToString());
                one.Set("points", c.Points);
                one.Set("fine", c.Fine);
                one.Set("at", c.At);

                list.Add(one);
            }

            mine.Set("charges", list);

            doc.Set("licence", mine);
        }

        public void FromJson(Json doc)
        {
            try
            {
                _charges.Clear();

                if (doc == null || !doc.Has("licence")) return;

                var mine = doc["licence"];
                if (!mine.Has("charges")) return;

                foreach (var one in mine["charges"].Items)
                {
                    Violation what;

                    if (!Enum.TryParse(one["what"].AsString(), true, out what)) continue;

                    _charges.Add(new Charge
                    {
                        What = what,
                        Points = one["points"].AsInt(1),
                        Fine = one["fine"].AsInt(0),
                        At = one["at"].AsInt(0),
                    });
                }

                Prune();
                _dirty = false;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read the licence: " + ex.Message);
            }
        }

        // ---- what things cost --------------------------------------------------

        /// <summary>Points for one violation.</summary>
        public static int PointsFor(Violation what)
        {
            switch (what)
            {
                case Violation.HitPed: return 3;
                case Violation.RedLight: return 3;
                case Violation.WrongWay: return 3;
                case Violation.Speeding: return 2;
                case Violation.Pavement: return 2;
                case Violation.Collision: return 2;
                case Violation.Drifting: return 2;
                case Violation.Phone: return 2;
                default: return 1;
            }
        }

        /// <summary>
        /// The fine for one violation, before anybody's temperament.
        ///
        /// Sized against what the game gives you rather than against real money. A few hundred
        /// is a real cost early and a rounding error once you are rich, which is the correct
        /// shape -- traffic fines are not meant to be a difficulty curve.
        /// </summary>
        public static int FineFor(Violation what)
        {
            switch (what)
            {
                case Violation.HitPed: return 900;
                case Violation.RedLight: return 500;
                case Violation.WrongWay: return 500;
                case Violation.Pavement: return 400;
                case Violation.Speeding: return 350;
                case Violation.Collision: return 350;
                case Violation.Drifting: return 300;
                case Violation.Phone: return 300;
                case Violation.Burnout: return 250;
                case Violation.Wheelie: return 200;
                case Violation.Tailgating: return 200;
                case Violation.NoHelmet: return 150;
                default: return 150;
            }
        }
    }
}
