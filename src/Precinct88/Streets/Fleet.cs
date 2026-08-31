using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// Every police unit this mod has put on the road, and the only place new ones come from.
    ///
    /// THIS IS THE POOL, AND THE POOL IS THE POINT. In vanilla -- and in every mod that layers
    /// on top of vanilla -- a response is a spawn: you commit a crime, the game creates a car
    /// somewhere behind you, and it arrives having existed for four seconds. That is the single
    /// thing that makes the police in this game feel like weather rather than like people.
    ///
    /// Here a response is a REASSIGNMENT. The car that comes when you are reported was already
    /// out driving Davis, it took nineteen seconds to get to you because that is how far away
    /// it was, and when the call clears it goes back to Davis. Nothing is created for you.
    ///
    /// The consequence, and it is intended: sometimes nobody comes. If the district is empty
    /// and the nearest unit is across the map, then the nearest unit is across the map. That is
    /// a feature of a city that has a finite number of police in it, and it is the whole reason
    /// the districts have different densities.
    /// </summary>
    internal sealed class Fleet
    {
        private const int TickMs = 750;

        /// <summary>Far enough out to arrive rather than to appear.</summary>
        private const float SpawnNear = 110f;
        private const float SpawnFar = 220f;

        /// <summary>Past this and they are somebody else's problem.</summary>
        private const float LetGoRange = 340f;

        /// <summary>How long between one unit going out and the next, before the dice.</summary>
        private const int GapMinMs = 24000;
        private const int GapMaxMs = 70000;

        /// <summary>How long a unit sits somewhere watching a street.</summary>
        private const int SitMinMs = 16000;
        private const int SitMaxMs = 46000;

        /// <summary>Chance the next place it heads for is somewhere it stops.</summary>
        private const int StopChancePercent = 38;

        /// <summary>How far a beat leg is. Short enough to stay in the district.</summary>
        private const float LegMin = 120f;
        private const float LegMax = 320f;

        private const int PedTypeCop = 6;

        private readonly Settings _cfg;
        private readonly Random _rng = new Random();
        private readonly List<Unit> _out = new List<Unit>();

        private int _lastTick;
        private int _nextSpawn;

        /// <summary>
        /// Whether something louder is happening and the beat should stop producing cars.
        ///
        /// Wired by Main from whatever is going on: a chase, a scene, a mod on the bridge that
        /// has said it is running something. A beat car easing round the corner into a gang war
        /// is not atmosphere, it is two officers walking into a firefight they were not sent to.
        /// </summary>
        public Func<bool> Busy;

        public Fleet(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>Units currently on the road. Live objects, not a copy.</summary>
        public IReadOnlyList<Unit> Units => _out;

        public int Count => _out.Count;

        // ---- per-tick ----------------------------------------------------------

        public void Update()
        {
            if (!_cfg.PatrolEnabled) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            var me = Game.Player.Character;
            if (me == null || !me.Exists()) return;

            var at = me.Position;

            Sweep(at, now);

            // NOTHING NEW GOES OUT WHILE THE LAW IS HELD, and this one line is most of how the
            // other mod gets what it needs without a single call. Hoodrich holds the law for a
            // gang war, a bike ride, a raid -- and a beat car easing round the corner into a
            // firefight it was not sent to is two officers walking into somebody else's scene.
            //
            // Cars already out are left where they are. They are not deleted, because a squad
            // car vanishing off the street the moment a war starts is more conspicuous than one
            // driving through it.
            if (Response.LawHold.Held) return;

            if (Busy != null && Busy()) return;

            Steer(at, now);

            if (now >= _nextSpawn && _out.Count < Wanted(at)) TryPutOneOut(at, now);
        }

        /// <summary>
        /// How many units this part of the map should have out.
        ///
        /// Scaled by the district the player is actually in rather than by a global figure,
        /// because the budget only means anything where he can see it. Standing in Rockford
        /// Hills with three cars out is wrong even though three is the setting.
        /// </summary>
        private int Wanted(Vector3 at)
        {
            var here = Districts.At(at);
            if (here.Density <= 0f) return 0;

            var n = (int)Math.Round(_cfg.PatrolUnits * here.Density);

            // Always at least one where anybody polices at all. Rounding a 0.25 density down
            // to zero means Paleto Bay has no police, which is a different claim than "not
            // many".
            return n < 1 ? 1 : n;
        }

        /// <summary>Lets go of anything dead, finished, or far enough away to stop mattering.</summary>
        private void Sweep(Vector3 at, int now)
        {
            for (var i = _out.Count - 1; i >= 0; i--)
            {
                var unit = _out[i];

                var keep = unit.Update(at, _rng);

                if (keep && unit.Doing != Duty.Contact &&
                    unit.Car.Position.DistanceTo(at) > LetGoRange)
                {
                    keep = false;
                }

                if (keep) continue;

                unit.Release();
                _out.RemoveAt(i);
            }
        }

        /// <summary>Gives anything that has arrived somewhere new to be.</summary>
        private void Steer(Vector3 at, int now)
        {
            foreach (var unit in _out)
            {
                if (unit.Doing == Duty.Contact) continue;

                switch (unit.Doing)
                {
                    case Duty.Rolling:
                        if (!unit.Arrived()) continue;

                        if (unit.StopThere)
                        {
                            // Parked at the KERB rather than stopped on the node it drove to.
                            // A node is the middle of the carriageway; see Stations.Kerb.
                            var until = now + SitMinMs + _rng.Next(SitMaxMs - SitMinMs);

                            unit.PullIn(Stations.Kerb(unit.Target, unit.Car.Heading),
                                        unit.Car.Heading, until);
                        }
                        else
                        {
                            SendOnward(unit, at);
                        }
                        break;

                    case Duty.Sitting:
                        if (now > unit.MoveOnAt) SendOnward(unit, at);
                        break;

                    case Duty.Searching:
                        // Nothing found. Nobody says so out loud; it just goes back to work.
                        if (now > unit.MoveOnAt) SendOnward(unit, at);
                        break;

                    case Duty.StandingDown:
                        if (unit.Arrived()) unit.OffDutyAt = 0;
                        break;
                }
            }
        }

        private void SendOnward(Unit unit, Vector3 playerAt)
        {
            Vector3 next;
            if (!NextLeg(unit, playerAt, out next)) return;

            unit.Roll(next, _rng.Next(100) < StopChancePercent);
        }

        /// <summary>
        /// Somewhere else on the beat.
        ///
        /// Biased towards staying in its own district, but not forced to -- a car that can only
        /// ever be inside a zone boundary drives in circles at the edges, and real beats
        /// overlap. Three tries, then it takes whatever road it found, because failing to route
        /// leaves it parked forever.
        /// </summary>
        private bool NextLeg(Unit unit, Vector3 playerAt, out Vector3 next)
        {
            next = Vector3.Zero;

            var from = Cops.Alive(unit.Car) ? unit.Car.Position : playerAt;
            Vector3 fallback = Vector3.Zero;
            var haveFallback = false;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var angle = _rng.NextDouble() * Math.PI * 2d;
                var dist = LegMin + (float)_rng.NextDouble() * (LegMax - LegMin);

                var guess = from + new Vector3((float)Math.Cos(angle) * dist,
                                               (float)Math.Sin(angle) * dist, 0f);

                Vector3 road;
                float heading;
                if (!Stations.RoadBy(guess, out road, out heading)) continue;

                if (!haveFallback) { fallback = road; haveFallback = true; }

                if (unit.Beat == null || Districts.At(road) == unit.Beat)
                {
                    next = road;
                    return true;
                }
            }

            if (!haveFallback) return false;

            next = fallback;
            return true;
        }

        // ---- putting one out ---------------------------------------------------

        private void TryPutOneOut(Vector3 playerAt, int now)
        {
            _nextSpawn = now + GapMinMs + _rng.Next(GapMaxMs - GapMinMs);

            var beat = Districts.At(playerAt);
            if (beat.Density <= 0f) return;

            Vector3 spot;
            float heading;
            if (!SpawnPoint(playerAt, beat, out spot, out heading)) return;

            var unit = Make(spot, heading, beat, now);
            if (unit == null) return;

            _out.Add(unit);

            Vector3 first;
            unit.Beat = beat;
            unit.Roll(NextLeg(unit, playerAt, out first) ? first : playerAt,
                      _rng.Next(100) < StopChancePercent);

            Log.Debug("Unit out on the " + beat.Name + " beat (" + _out.Count + " on the road).");
        }

        /// <summary>
        /// Where a new unit comes from.
        ///
        /// Out of the station when the station is close enough that driving from it is a real
        /// journey and not a ten-minute commute the player will never see. Otherwise a road
        /// node out of sight behind him, which is the ordinary compromise -- but it is at least
        /// a road, at a sensible distance, and facing the way the road goes.
        /// </summary>
        private bool SpawnPoint(Vector3 playerAt, District beat, out Vector3 spot, out float heading)
        {
            spot = Vector3.Zero;
            heading = 0f;

            if (_cfg.FromStations)
            {
                var station = Stations.For(beat, playerAt);

                if (station != null)
                {
                    var howFar = station.Where.DistanceTo(playerAt);

                    if (howFar > 60f && howFar < 700f &&
                        Stations.RoadBy(station.Where, out spot, out heading))
                    {
                        return true;
                    }
                }
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var angle = _rng.NextDouble() * Math.PI * 2d;
                var dist = SpawnNear + (float)_rng.NextDouble() * (SpawnFar - SpawnNear);

                var guess = playerAt + new Vector3((float)Math.Cos(angle) * dist,
                                                   (float)Math.Sin(angle) * dist, 0f);

                if (!Stations.RoadBy(guess, out spot, out heading)) continue;

                // Not in front of him. A squad car fading into existence in the middle
                // distance is the exact thing this mod exists to stop doing.
                if (OnScreen(spot)) continue;

                return true;
            }

            return false;
        }

        private static bool OnScreen(Vector3 where)
        {
            try
            {
                return Function.Call<bool>(Hash.IS_SPHERE_VISIBLE, where.X, where.Y, where.Z, 6f);
            }
            catch
            {
                // If the game will not say, assume it can be seen. Refusing to spawn is always
                // the safe answer here.
                return true;
            }
        }

        private Unit Make(Vector3 spot, float heading, District beat, int now)
        {
            try
            {
                var carModel = Cops.Load(Cops.Cars[_rng.Next(Cops.Cars.Length)]);
                if (carModel == null) return null;

                var car = World.CreateVehicle(carModel.Value, spot, heading);
                carModel.Value.MarkAsNoLongerNeeded();

                if (!Cops.Alive(car)) return null;

                car.IsPersistent = true;
                car.IsEngineRunning = true;

                var unit = new Unit
                {
                    Car = car,
                    Beat = beat,
                    OffDutyAt = now + (int)(_cfg.BeatMinutes * 60000f)
                };

                // Two of them. One officer in a squad car is a mod that could not be bothered,
                // and it matters the moment somebody gets out: a stop with nobody left in the
                // car reads completely differently to a stop with a partner still in it.
                for (var seat = -1; seat <= 0; seat++)
                {
                    var pedModel = Cops.Load(Cops.Uniforms[_rng.Next(Cops.Uniforms.Length)]);
                    if (pedModel == null) continue;

                    var who = car.CreatePedOnSeat((VehicleSeat)seat, pedModel.Value);
                    pedModel.Value.MarkAsNoLongerNeeded();

                    if (!Cops.Alive(who)) continue;

                    Dress(who);

                    if (seat == -1) unit.Driver = who;
                    else unit.Crew.Add(who);
                }

                if (!Cops.Alive(unit.Driver))
                {
                    unit.Release();
                    return null;
                }

                return unit;
            }
            catch (Exception ex)
            {
                Log.Error("Could not put a unit on the road.", ex);
                return null;
            }
        }

        /// <summary>
        /// Makes a spawned ped behave like police rather than like a man in a costume.
        ///
        /// SET_PED_AS_COP is the one that matters and the one everybody misses: without it the
        /// game does not treat him as law at all, so he will not respond to the wanted system,
        /// other peds do not react to him, and he will happily stand and watch a shooting.
        /// </summary>
        private static void Dress(Ped who)
        {
            try
            {
                Function.Call(Hash.SET_PED_AS_COP, who.Handle, true);
                Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, who.Handle,
                              Function.Call<int>(Hash.GET_HASH_KEY, "COP"));

                who.IsPersistent = true;
                who.BlockPermanentEvents = false;

                // Armed, and not because they are going to use it. An officer who gets out to
                // talk to you with nothing on his belt is an officer you have no reason to
                // stop for.
                who.Weapons.Give(WeaponHash.Pistol, 60, false, true);
                who.Weapons.Give(WeaponHash.StunGun, 1, false, false);

                who.Armor = 40;
                who.CanSufferCriticalHits = true;

                // 46: keep fighting rather than fleeing. 5: will use cover. Both are what
                // separates police from pedestrians the first time anything goes wrong.
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, who.Handle, 46, true);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, who.Handle, 5, true);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, who.Handle, 0, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set an officer up properly: " + ex.Message);
            }
        }

        // ---- dispatch ----------------------------------------------------------

        /// <summary>
        /// The nearest unit that can be sent to something, or null when there is nobody.
        ///
        /// Null is a real answer and callers must handle it. It is what makes a quiet district
        /// quiet, and papering over it with a spawn is exactly the behaviour this class was
        /// written to replace.
        /// </summary>
        public Unit NearestFree(Vector3 to, float within = 700f)
        {
            Unit best = null;
            var bestDist = within;

            foreach (var unit in _out)
            {
                if (!unit.Alive) continue;
                if (unit.Doing == Duty.Contact || unit.Doing == Duty.StandingDown) continue;

                var d = unit.Car.Position.DistanceTo(to);
                if (d >= bestDist) continue;

                bestDist = d;
                best = unit;
            }

            return best;
        }

        /// <summary>Every unit already on a call, so nothing sends two cars to one thing.</summary>
        public int OnCalls()
        {
            var n = 0;

            foreach (var unit in _out)
            {
                if (unit.Doing == Duty.Responding || unit.Doing == Duty.Searching) n++;
            }

            return n;
        }

        /// <summary>Whichever unit owns this ped, or null. Used when an officer is shot at.</summary>
        public Unit Owning(Ped who)
        {
            if (who == null) return null;

            foreach (var unit in _out)
            {
                foreach (var p in unit.Everyone())
                {
                    if (p.Handle == who.Handle) return unit;
                }
            }

            return null;
        }

        /// <summary>Hands everything back. Teardown only.</summary>
        public void Release()
        {
            foreach (var unit in _out) unit.Release();
            _out.Clear();
        }
    }
}
