using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// A round with corners in it, instead of a man walking at random.
    ///
    /// TASK_WANDER_IN_AREA IS THE PROBLEM AND IT LOOKS LIKE THE SOLUTION. It puts a ped inside
    /// a circle and has him walk to arbitrary points in it forever, which from a distance is
    /// indistinguishable from patrolling and from ten metres away is obviously not: he crosses
    /// his own path, doubles back for no reason, and goes wherever the last dice roll said. It
    /// has no memory of where he has already been and no opinion about where he should go next,
    /// because it is not walking a round, it is drifting inside a boundary.
    ///
    /// A ROUTE IS FOUR OR FIVE PLACES AND AN ORDER TO VISIT THEM IN. That is the whole idea,
    /// and it is almost the whole of what reads as intelligence here -- a man who arrives
    /// somewhere, looks at it, and then leaves for somewhere specific is doing a job, and the
    /// same man picking random points is not, even though the paths look similar on a map.
    ///
    /// THE CORNERS ARE WHERE THE REST OF THE MOD HAPPENS. Arriving at a waypoint is the natural
    /// moment to get the notepad out, and Rounds already knows how -- so the arrival simply
    /// brings his next chore forward rather than doing anything itself. That is why he stops at
    /// the end of a street to write something rather than in the middle of one.
    ///
    /// IT FALLS BACK TO WANDERING AND SAYS SO. Nav mesh routing fails -- indoors, on a pier, on
    /// geometry the mesh does not cover -- and a foot patrol frozen against a wall for the rest
    /// of its shift is far worse than one drifting in a circle. Two failed legs and he goes
    /// back to the old behaviour permanently, with a line in the log.
    /// </summary>
    internal sealed class Route
    {
        /// <summary>How many corners a round has. Enough to have a shape, few enough to learn.</summary>
        private const int Corners = 4;

        /// <summary>How far out they sit from where he was posted.</summary>
        private const float LegMin = 38f;
        private const float LegMax = 78f;

        /// <summary>Close enough to have arrived.</summary>
        private const float ThereRange = 4.5f;

        /// <summary>
        /// How long one leg may take before it is written off.
        ///
        /// Generous. He walks, the mesh goes round buildings rather than through them, and a
        /// seventy-metre leg down the back of a block is a genuinely long way at walking pace.
        /// </summary>
        private const int LegMs = 65000;

        /// <summary>Walking pace. An officer who jogs his round is late for something.</summary>
        private const float Pace = 1.0f;

        /// <summary>How many legs may fail before he gives up on routes entirely.</summary>
        private const int GiveUpAfter = 2;

        /// <summary>
        /// How long he may be on a road before it stops being a crossing.
        ///
        /// THE WHOLE DIFFICULTY IS THAT BEING ON A ROAD IS SOMETIMES CORRECT. A route with
        /// corners on both sides of a street REQUIRES him to cross one, and an officer who
        /// refuses to step off the kerb is a worse bug than one who strolls up the middle. So
        /// this is not a rule about roads, it is a rule about DWELLING on them: six seconds is
        /// far longer than any crossing takes and far shorter than a walk down one.
        /// </summary>
        private const int RoadGraceMs = 6000;

        /// <summary>How often the road check runs. It is two natives, but not free.</summary>
        private const int RoadCheckMs = 1500;

        /// <summary>How many goes at placing one corner before it is left out.</summary>
        private const int Tries = 5;

        private readonly Random _rng;

        /// <summary>Puts a relocated officer back to work. Set by Foot.</summary>
        public Action<Walker> Repost;

        public Route(Random rng)
        {
            _rng = rng;
        }

        public void Update(IReadOnlyList<Walker> walkers, int now, bool quiet)
        {
            if (walkers == null || !quiet) return;

            for (var i = 0; i < walkers.Count; i++)
            {
                var walker = walkers[i];

                if (walker == null || !walker.Alive) continue;

                try
                {
                    // EVERYBODY, NOT JUST MEN ON A ROUTE, and that was the hole. The road check
                    // used to sit inside the route logic, so it only ever looked at officers
                    // who were walking one -- and the ones most likely to be found stood in a
                    // road are exactly the ones who are not: a man posted on a corner that
                    // turned out to be a tram line, or one whose route failed and who is
                    // wandering. Neither was ever asked.
                    if (walker.Doing == Errand.Posted && Astray(walker, now)) continue;

                    // The rest is the route itself, and only men who walk one have it.
                    if (!walker.Wanders || walker.Doing != Errand.Posted) continue;

                    if (walker.Adrift) continue;

                    Walk(walker, now);
                }
                catch (Exception ex)
                {
                    Log.Debug("An officer's round went wrong: " + ex.Message);
                    walker.Adrift = true;
                }
            }
        }

        private void Walk(Walker walker, int now)
        {
            if (walker.Circuit == null)
            {
                walker.Circuit = Plan(walker.PostedAt);

                if (walker.Circuit == null)
                {
                    // Nowhere round here a person can stand. Not worth retrying every tick.
                    walker.Adrift = true;
                    Log.Debug("No route could be planned; that officer will wander instead.");
                    return;
                }

                walker.Leg = 0;
                walker.LegUntil = 0;
            }

            var target = walker.Circuit[walker.Leg % walker.Circuit.Count];

            // Arrived. On to the next corner, and something to do while he is here.
            if (walker.Who.Position.DistanceTo(target) < ThereRange)
            {
                Arrived(walker, now);
                return;
            }

            // Still going, and still within its time.
            if (now < walker.LegUntil) return;

            // Either this is a new leg or the old one ran out of time.
            if (walker.LegUntil != 0)
            {
                walker.Legs++;

                if (walker.Legs >= GiveUpAfter)
                {
                    walker.Adrift = true;

                    Log.Info("An officer could not walk his round twice over; wandering instead.");

                    Wander(walker);
                    return;
                }

                // Skip the corner he could not reach rather than battering at it.
                walker.Leg++;
                target = walker.Circuit[walker.Leg % walker.Circuit.Count];
            }

            walker.LegUntil = now + LegMs;

            try
            {
                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, walker.Who.Handle,
                              target.X, target.Y, target.Z, Pace, LegMs,
                              ThereRange * 0.7f, 0, 0f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send an officer down a leg: " + ex.Message);
                walker.Adrift = true;
            }
        }



        /// <summary>
        /// Whether he is somewhere he should not be, and getting him out of it.
        ///
        /// TWO DIFFERENT FAULTS WITH ONE CURE. Either he is walking ALONG a road because the
        /// nav mesh routed him down one -- it solves for distance and has no opinion about
        /// kerbs -- or he is STOOD in one, because the spot he was posted to turned out to be
        /// a tram reservation in the middle of a junction. The second was invisible until now,
        /// because nothing looked at an officer who was not walking a route.
        ///
        /// THE GRACE IS DOING THE REAL WORK, because being on a road is sometimes correct. Any
        /// round with corners on both sides of a street requires crossing one, and an officer
        /// who will not step off a kerb looks far stupider than one who strolls up the middle.
        /// Six seconds tells a crossing from a walk with room to spare.
        ///
        /// HIS POST MOVES WITH HIM. Walking him to the pavement and leaving his post in the
        /// road is a man who returns to the tram line the moment anything reposts him -- so the
        /// kerb becomes the post, the circuit is thrown away and replanned around it, and a man
        /// who was standing still is put back to work once he arrives.
        /// </summary>
        private bool Astray(Walker walker, int now)
        {
            // Already on his way somewhere better.
            if (walker.Relocating)
            {
                if (walker.Who.Position.DistanceTo(walker.PostedAt) < ThereRange ||
                    now > walker.LegUntil)
                {
                    walker.Relocating = false;
                    walker.LegUntil = 0;

                    if (Repost != null) Repost(walker);
                }

                return true;
            }

            if (now < walker.RoadCheckAt) return false;
            walker.RoadCheckAt = now + RoadCheckMs;

            if (!Pave.OnRoad(walker.Who.Position))
            {
                walker.OnRoadSince = 0;
                return false;
            }

            if (walker.OnRoadSince == 0)
            {
                walker.OnRoadSince = now;
                return false;
            }

            if (now - walker.OnRoadSince < RoadGraceMs) return false;

            walker.OnRoadSince = 0;

            Vector3 kerb;
            if (!Pave.Kerb(walker.Who.Position, _rng, out kerb)) return false;

            walker.PostedAt = kerb;
            walker.Circuit = null;
            walker.Leg = 0;
            walker.Relocating = true;
            walker.LegUntil = now + 14000;

            try
            {
                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, walker.Who.Handle,
                              kerb.X, kerb.Y, kerb.Z, Pace, 14000, 1.5f, 0, 0f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not put an officer back on the kerb: " + ex.Message);
                walker.Relocating = false;
                return false;
            }

            Log.Debug("An officer was in the road; moved to the pavement.");
            return true;
        }

        /// <summary>
        /// He is at a corner.
        ///
        /// THE CHORE IS BROUGHT FORWARD, NOT STARTED. Rounds owns what an officer does when he
        /// stops -- the clipboard, the radio, the coffee -- and duplicating any of that here
        /// would give the same behaviour two owners. All this does is say "now would be a good
        /// moment", by moving his next chore's clock to the present, and Rounds decides on its
        /// own tick whether he actually stops.
        ///
        /// Which is why he writes something up at the end of a street rather than halfway down
        /// one, and why the pause and the turn are the same event.
        /// </summary>
        private void Arrived(Walker walker, int now)
        {
            walker.Leg++;
            walker.LegUntil = 0;
            walker.Legs = 0;

            // Not every corner. A man who stops at all four of them is a man doing a routine
            // rather than a job, and the point of the route was to stop looking mechanical.
            if (_rng.Next(100) < 55) walker.NextChoreAt = now;
        }

        /// <summary>
        /// Four places on the pavement around where he was posted.
        ///
        /// SPREAD BY ANGLE RATHER THAN AT RANDOM. Four random points near a post are, often
        /// enough, four points in a line or three in one corner -- and a round with no shape is
        /// the thing this file exists to replace. A quarter turn each, jittered, guarantees the
        /// circuit goes round something.
        ///
        /// Any corner that cannot be placed is simply left out. Three is still a round; the
        /// only failure that matters is finding none at all.
        /// </summary>
        private List<Vector3> Plan(Vector3 post)
        {
            var found = new List<Vector3>(Corners);

            var turn = Math.PI * 2d / Corners;
            var start = _rng.NextDouble() * Math.PI * 2d;

            for (var i = 0; i < Corners; i++)
            {
                // A third of a quarter-turn of slop, so the shape is not a perfect square.
                var angle = start + turn * i + (_rng.NextDouble() - 0.5d) * turn * 0.66d;
                var dist = LegMin + (float)_rng.NextDouble() * (LegMax - LegMin);

                Vector3 safe;
                if (!Corner(post, angle, dist, out safe)) continue;

                found.Add(safe);
            }

            return found.Count < 2 ? null : found;
        }

        /// <summary>
        /// One corner, tried a few times before giving up on it.
        ///
        /// THE SINGLE ATTEMPT WAS THE BUG. Each corner got exactly one roll of
        /// GET_SAFE_COORD_FOR_PED, and that native answers "somewhere a ped can BE", which
        /// includes the middle of a carriageway -- it rules out walls, water and roofs, not
        /// tarmac. So roughly one corner in three landed on a road, the officer dutifully
        /// walked to it, and the route took him up the white line.
        ///
        /// Now every candidate is checked against IS_POINT_ON_ROAD and rejected if it is on
        /// one, with the angle nudged a little each go. Five tries is plenty: a corner that
        /// cannot be placed off-road in five is somewhere there is no pavement, and a
        /// three-corner round is still a round.
        /// </summary>
        private bool Corner(Vector3 post, double angle, float dist, out Vector3 spot)
        {
            spot = Vector3.Zero;

            for (var i = 0; i < Tries; i++)
            {
                // Nudged rather than re-rolled, so a corner that has to move still ends up
                // roughly where the shape wanted it.
                var a = angle + (_rng.NextDouble() - 0.5d) * 0.9d;
                var d = dist * (0.75f + (float)_rng.NextDouble() * 0.5f);

                var guess = post + new Vector3((float)Math.Cos(a) * d,
                                               (float)Math.Sin(a) * d, 0f);

                if (!Pave.Spot(guess, out spot)) continue;

                return true;
            }

            return false;
        }



        /// <summary>The old behaviour, for anybody the mesh will not route.</summary>
        private static void Wander(Walker walker)
        {
            try
            {
                Function.Call(Hash.TASK_WANDER_IN_AREA, walker.Who.Handle,
                              walker.PostedAt.X, walker.PostedAt.Y, walker.PostedAt.Z,
                              130f, 26f, 2f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not fall back to wandering: " + ex.Message);
            }
        }
    }
}
