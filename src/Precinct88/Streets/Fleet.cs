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

        /// <summary>
        /// Past this and they are somebody else's problem.
        ///
        /// Raised with the budget. A bigger pool spread over the same distance is a bigger
        /// pool being deleted at the same rate, and letting go at three hundred and forty
        /// metres meant a car was released about as fast as one arrived the moment you were
        /// driving rather than walking.
        /// </summary>
        private const float LetGoRange = 430f;

        /// <summary>
        /// How long between one unit going out and the next, before the dice.
        ///
        /// THE POOL WAS TAKING LONGER TO FILL THAN A UNIT STAYS OUT, which is the quiet reason
        /// the streets looked empty however high the budget went. At twenty-four to seventy
        /// seconds a district wanting six cars needs four minutes to reach six -- and a round
        /// only lasts eleven, so a third of every unit's life was spent waiting for the rest of
        /// the shift to turn up. The target was real and almost never met.
        ///
        /// Faster now. It still puts them out ONE AT A TIME, out of sight, and they still have
        /// to drive from wherever they appeared -- none of the argument changes. The pool just
        /// reaches its own number before half of it has gone home.
        /// </summary>
        private const int GapMinMs = 9000;
        private const int GapMaxMs = 26000;

        /// <summary>And how long during a response, when the patch rhythm is the wrong one.</summary>
        private const int SurgeGapMs = 3500;

        /// <summary>How long a unit sits somewhere watching a street.</summary>
        private const int SitMinMs = 16000;
        private const int SitMaxMs = 46000;

        /// <summary>Chance the next place it heads for is somewhere it stops.</summary>
        private const int StopChancePercent = 38;

        /// <summary>How far a patch leg is. Short enough to stay in the district.</summary>
        private const float LegMin = 120f;
        private const float LegMax = 320f;

        private const int PedTypeCop = 6;

        private readonly Settings _cfg;
        private readonly Random _rng = new Random();
        private readonly List<Unit> _out = new List<Unit>();

        private int _lastTick;
        private int _nextSpawn;

        /// <summary>
        /// Consecutive failures to place a unit.
        ///
        /// Drives how hard the search tries. On open ground the first few attempts fail on the
        /// on-screen test alone, and something has to give or the district silently produces no
        /// police at all.
        /// </summary>
        private int _failures;

        /// <summary>
        /// Whether something louder is happening and the patch should stop producing cars.
        ///
        /// Wired by Main from whatever is going on: a chase, a scene, a mod on the bridge that
        /// has said it is running something. A patch car easing round the corner into a gang war
        /// is not atmosphere, it is two officers walking into a firefight they were not sent to.
        /// </summary>
        public Func<bool> Busy;

        /// <summary>
        /// Extra units wanted right now, on top of the district's ordinary patch.
        ///
        /// WITH THE ENGINE'S DISPATCH SWITCHED OFF, THIS IS THE ONLY WAY ANYBODY COMES. That is
        /// the trade the mod makes: the game will no longer conjure a car behind you, so when a
        /// serious crime happens this mod has to put the cars out itself -- and they still have
        /// to drive to you from somewhere, which is the whole point.
        ///
        /// Set by Manhunt from the star count. Zero when nothing is happening.
        /// </summary>
        public int Surge;

        /// <summary>Where a surged unit heads the moment it exists.</summary>
        public Vector3 SurgeTo;

        public bool Surging => Surge > 0;

        public Fleet(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>Units currently on the road. Live objects, not a copy.</summary>
        public IReadOnlyList<Unit> Units => _out;

        public int Count => _out.Count;

        /// <summary>Hands a borrowed car straight back. Called when a scene ends with one.</summary>
        public void Hand(Unit unit)
        {
            if (unit == null || !unit.Borrowed) return;

            _out.Remove(unit);
        }

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
            // gang war, a bike ride, a raid -- and a patch car easing round the corner into a
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

            // BORROWED CARS DO NOT COUNT. They are somebody else's, here for one scene, and
            // counting them would have the district decide it was fully policed by cars this
            // mod did not put on the road.
            foreach (var unit in _out)
            {
                if (unit.Borrowed) n++;
            }

            // Always at least one where anybody polices at all. Rounding a 0.25 density down
            // to zero means Paleto Bay has no police, which is a different claim than "not
            // many".
            if (n < 1) n = 1;

            // The surge sits ON TOP of the patch rather than replacing it, so a chase does not
            // empty the surrounding streets of the ordinary patrol -- which would be the most
            // obvious possible tell that the police in this game are a budget rather than a
            // force.
            return n + Surge;
        }

        /// <summary>Lets go of anything dead, finished, or far enough away to stop mattering.</summary>
        private void Sweep(Vector3 at, int now)
        {
            for (var i = _out.Count - 1; i >= 0; i--)
            {
                var unit = _out[i];

                // A BORROWED CAR IS DROPPED THE MOMENT ITS SCENE ENDS. It was never ours to
                // keep, and holding one would have the pool counting cars it did not put out.
                if (unit.Borrowed && unit.Doing != Duty.Contact)
                {
                    _out.RemoveAt(i);
                    continue;
                }

                var keep = unit.Update(at, _rng);

                // NOT ONE THAT IS ON ITS WAY. A unit answering a call is by definition
                // driving towards the player, so measuring it against a let-go range and
                // deleting it for being far away destroys exactly the car that was doing the
                // thing the mod exists to do -- and it happens silently, so the symptom is
                // "sometimes nobody ever arrives" with nothing in the log.
                //
                // They are not kept forever: a call goes cold, the unit is put back on patrol,
                // and the next pass lets it go normally.
                var onIts = unit.Doing == Duty.Responding || unit.Doing == Duty.Searching;

                if (keep && unit.Doing != Duty.Contact && !onIts &&
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
        /// Somewhere else on the patch.
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

            // ALLEY OR ROAD, decided here and nowhere else.
            //
            // The district says how much of its patrolling is down the back of things; the
            // clock scales it, because a service road behind a row of shops is somewhere a
            // patrol car goes at two in the morning and not at two in the afternoon. A floor
            // under it keeps a bit of it happening in daylight, so the behaviour is not a
            // thing players only ever hear about.
            var patch = unit.Patch ?? Districts.At(from);

            var bias = patch.Alleys * (0.25f + 0.75f * Alleys.Night()) * _cfg.AlleyPatrol;
            var wantAlley = _rng.NextDouble() < bias;

            if (Alleys.Find(from, LegMin, LegMax, wantAlley, unit.Patch, _rng, out next))
            {
                unit.OnABackStreet = wantAlley;
                return true;
            }

            // Nothing on the node network. Fall back to the old road-node search rather than
            // leaving the unit parked -- a car still driving is never the failure.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var angle = _rng.NextDouble() * Math.PI * 2d;
                var dist = LegMin + (float)_rng.NextDouble() * (LegMax - LegMin);

                var guess = from + new Vector3((float)Math.Cos(angle) * dist,
                                               (float)Math.Sin(angle) * dist, 0f);

                Vector3 road;
                float heading;
                if (!Stations.RoadBy(guess, out road, out heading)) continue;

                next = road;
                unit.OnABackStreet = false;
                return true;
            }

            return false;
        }

        // ---- putting one out ---------------------------------------------------

        private void TryPutOneOut(Vector3 playerAt, int now)
        {
            // A SURGE CANNOT WAIT SEVENTY SECONDS FOR THE NEXT CAR. The ordinary gap is the
            // rhythm of a patch, which is exactly wrong for a response -- with the engine's
            // dispatch off, a slow gap here is a five-star manhunt where nobody turns up.
            _nextSpawn = Surging
                ? now + SurgeGapMs + _rng.Next(SurgeGapMs)
                : now + GapMinMs + _rng.Next(GapMaxMs - GapMinMs);

            var patch = Districts.At(playerAt);
            if (patch.Density <= 0f) return;

            Vector3 spot;
            float heading;

            if (!SpawnPoint(playerAt, patch, out spot, out heading))
            {
                // FAILING COST A FULL COOLDOWN, and that was most of the bug. On open ground --
                // the Chamberlain Hills tram straight, a freeway, anywhere with a long sightline
                // -- every candidate point is visible, every attempt is rejected, and the next
                // one is not tried for up to seventy seconds. The result is a district that
                // never produces a car and a player at four stars in an empty city.
                //
                // A failure is now worth retrying almost immediately. It costs one more search
                // in a second rather than a minute of nothing.
                _failures++;

                _nextSpawn = now + (Surging ? 900 : 2500);

                if (_failures == 6)
                {
                    Log.Warn("Cannot place a unit near " + Districts.ZoneAt(playerAt) +
                             " -- every candidate was in view or off the road network. " +
                             "Widening the search.");
                }

                return;
            }

            _failures = 0;

            var unit = Make(spot, heading, patch, now);
            if (unit == null) return;

            _out.Add(unit);
            unit.Patch = patch;

            if (Surging && SurgeTo != Vector3.Zero)
            {
                // Straight to the call. It was not on a patch and never pretended to be -- this
                // is a unit answering something, which is what a response is.
                unit.RespondTo(SurgeTo, "a call");

                Log.Debug("Unit out to a call (" + _out.Count + " on the road, surge " +
                          Surge + ").");
                return;
            }

            Vector3 first;
            unit.Roll(NextLeg(unit, playerAt, out first) ? first : playerAt,
                      _rng.Next(100) < StopChancePercent);

            Log.Debug("Unit out on the " + patch.Name + " patch (" + _out.Count + " on the road).");
        }

        /// <summary>
        /// Where a new unit comes from.
        ///
        /// Out of the station when the station is close enough that driving from it is a real
        /// journey and not a ten-minute commute the player will never see. Otherwise a road
        /// node out of sight behind him, which is the ordinary compromise -- but it is at least
        /// a road, at a sensible distance, and facing the way the road goes.
        /// </summary>
        private bool SpawnPoint(Vector3 playerAt, District patch, out Vector3 spot, out float heading)
        {
            spot = Vector3.Zero;
            heading = 0f;

            if (_cfg.FromStations)
            {
                var station = Stations.For(patch, playerAt);

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

            // Closer during a surge. Still out of sight, still on a real road, still has to
            // drive -- but a responding unit starting two hundred metres out is a responding
            // unit that arrives after the player has gone.
            var near = Surging ? SpawnNear * 0.7f : SpawnNear;
            var far = Surging ? SpawnFar * 0.7f : SpawnFar;

            // THE SEARCH WIDENS AS IT KEEPS FAILING. Somewhere with a long sightline has no
            // out-of-view point at a hundred metres, and holding the radius fixed means such a
            // place simply never gets policed. Pushing further out finds ground behind a
            // building, over a rise, or round the curve of the road.
            var reach = 1f + Math.Min(_failures, 8) * 0.35f;

            near *= reach;
            far *= reach;

            for (var attempt = 0; attempt < 12; attempt++)
            {
                var angle = _rng.NextDouble() * Math.PI * 2d;
                var dist = near + (float)_rng.NextDouble() * (far - near);

                var guess = playerAt + new Vector3((float)Math.Cos(angle) * dist,
                                                   (float)Math.Sin(angle) * dist, 0f);

                if (!Stations.RoadBy(guess, out spot, out heading)) continue;

                // Not in front of him. A squad car fading into existence in the middle
                // distance is the exact thing this mod exists to stop doing.
                //
                // BUT NOT AT ANY PRICE. After enough failures the alternative is not a tidier
                // spawn, it is NO POLICE AT ALL -- which is what four stars on an empty straight
                // road turned out to be. Past that point a car appearing a long way off down
                // the road is accepted, because it is much less objectionable than a city with
                // nobody in it. Far enough away that it is a dot rather than an event.
                if (OnScreen(spot) && (_failures < 4 || spot.DistanceTo(playerAt) < 140f))
                {
                    continue;
                }

                // AND NOT ON TOP OF SOMEBODY ELSE'S SCENE. Another mod's callout, a scripted
                // roadblock, an ambulance at a crash -- all of them put emergency vehicles
                // somewhere deliberate, and a patch car materialising into the middle of one is
                // this mod damaging a scene it cannot see.
                if (EmergencyNear(spot)) continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether somebody else's emergency vehicle is already here.
        ///
        /// Cheap and deliberately broad: any police, ambulance or fire vehicle within thirty
        /// metres means something is going on at this spot that this mod did not start, and the
        /// right answer is to spawn somewhere else. Costs one nearby-vehicle scan on a spawn
        /// attempt, which happens at most every few seconds.
        /// </summary>
        private static bool EmergencyNear(Vector3 where)
        {
            try
            {
                foreach (var car in World.GetNearbyVehicles(where, 30f))
                {
                    if (car == null || !car.Exists()) continue;

                    // The Emergency class is police, ambulance and fire in one bucket, which is
                    // exactly the set worth standing clear of -- anything in it is somewhere on
                    // purpose. Helicopters are their own class, so an air unit overhead is
                    // asked about separately.
                    if (car.ClassType == VehicleClass.Emergency) return true;

                    if (car.ClassType == VehicleClass.Helicopters && car.IsSirenActive) return true;
                }
            }
            catch
            {
                // Cannot tell, so allow it. Refusing every spawn on an exception would empty
                // the streets.
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

        private Unit Make(Vector3 spot, float heading, District patch, int now)
        {
            try
            {
                // WHO POLICES HERE, rather than one cruiser everywhere. A sheriff in Blaine
                // County, highway patrol on the freeway, a ranger on the mountain.
                var force = Agencies.For(patch, spot, _rng);

                var wantCar = force.Car(_rng);
                if (wantCar == null) return null;

                var carModel = Cops.Load(force.Name + " car", wantCar);
                if (carModel == null) return null;

                var car = World.CreateVehicle(carModel.Value, spot, heading);
                carModel.Value.MarkAsNoLongerNeeded();

                if (!Cops.Alive(car)) return null;

                car.IsPersistent = true;
                car.IsEngineRunning = true;

                var unit = new Unit
                {
                    Car = car,
                    Patch = patch,
                    Force = force.Name,
                    OffDutyAt = now + (int)(_cfg.PatrolMinutes * 60000f),

                    // Rolled once and kept. Two officers who look at everybody, or two who
                    // look at nobody, for the whole of their round.
                    Interest = (float)_rng.NextDouble(),
                    Temper = Temperament.Roll(_rng),
                };

                // Two of them. One officer in a squad car is a mod that could not be bothered,
                // and it matters the moment somebody gets out: a stop with nobody left in the
                // car reads completely differently to a stop with a partner still in it.
                // A BIKE HAS ONE SEAT. Highway patrol ride them, and asking for a passenger on
                // a motorcycle puts an officer through the frame or silently fails -- either
                // way it is a second man this unit does not have.
                var seats = car.PassengerCapacity >= 1 ? 0 : -1;

                for (var seat = -1; seat <= seats; seat++)
                {
                    var wantPed = force.Ped(_rng);
                    if (wantPed == null) continue;

                    var pedModel = Cops.Load(force.Name + " uniform", wantPed);
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
                // NO STUN GUN AT SPAWN, AND THAT IS A FIX RATHER THAN A TRIM.
                //
                // They used to be issued one here as set dressing. Restraint hands out the
                // stun guns now, and crucially it also sets SET_PED_DROPS_WEAPONS_WHEN_DEAD
                // to false when it does -- so a taser it issued cannot end up on the pavement.
                // One issued HERE could, because nothing turned that flag off for an officer
                // Restraint had never processed.
                //
                // Which mattered far more than it sounds, because the search now takes the
                // player's weapons: get searched, walk over a dead officer, and the game
                // auto-equips the stun gun it finds because your hands are empty. Everything
                // you killed after that died of electrocution.
                //
                // The pistol still drops, as it always has. Only the taser was ever the
                // problem, and the flag is per-ped rather than per-weapon, so the fix is to
                // not put one there.
                who.Weapons.Give(WeaponHash.Pistol, 60, false, true);

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

        /// <summary>
        /// Wraps a police car that was already on the street so a scene can use it.
        ///
        /// THE TRAFFIC STOP COULD ONLY EVER BE RUN BY ONE OF OUR OWN CARS, of which there are
        /// three in a district -- and with the engine's dispatch running alongside this build,
        /// most of the police a player actually drives past are the engine's. So the system was
        /// waiting for a coincidence: our car, near you, looking at you, at the moment you did
        /// something, and then a dice roll. It fired ONCE in an entire evening's log.
        ///
        /// This is the smallest fix that changes that. Any marked car with an officer at the
        /// wheel can be borrowed for the length of a scene, wrapped in a Unit so that
        /// everything downstream -- lights, temper, handing back -- works unchanged.
        ///
        /// BORROWED, NOT ADOPTED. It is flagged, it does not count towards what the district
        /// wants out, and Sweep drops it the moment the scene ends. Letting these into the pool
        /// properly would mean the pool filling with cars we did not put there and never
        /// spawning any of our own, which would hollow out the one idea this mod is built on.
        /// </summary>
        public Unit Adopt(Vehicle car)
        {
            try
            {
                if (!Cops.Alive(car)) return null;

                var driver = car.Driver;
                if (!Cops.Alive(driver) || !Cops.IsCop(driver)) return null;

                // Already ours, in which case use the real one.
                foreach (var had in _out)
                {
                    if (Cops.Alive(had.Car) && had.Car.Handle == car.Handle) return had;
                }

                var unit = new Unit
                {
                    Car = car,
                    Driver = driver,
                    Patch = Districts.At(car.Position),
                    Borrowed = true,
                    OffDutyAt = Game.GameTime + 600000,
                    Interest = (float)_rng.NextDouble(),
                    Temper = Temperament.Roll(_rng),
                };

                foreach (var ped in car.Occupants)
                {
                    if (ped == null || ped.Handle == driver.Handle) continue;
                    if (!Cops.IsCop(ped)) continue;

                    unit.Crew.Add(ped);
                }

                _out.Add(unit);

                Log.Debug("Borrowed a police car that was already out.");
                return unit;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not borrow a police car: " + ex.Message);
                return null;
            }
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
