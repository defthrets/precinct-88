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

        private readonly Random _rng;

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

                // Only men who walk, and only while nothing else has them. Chats, Rounds and
                // Reacts all take him out of Posted, and all put him back.
                if (!walker.Wanders || walker.Doing != Errand.Posted) continue;

                if (walker.Adrift) continue;

                try
                {
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

                var guess = post + new Vector3((float)Math.Cos(angle) * dist,
                                               (float)Math.Sin(angle) * dist, 0f);

                Vector3 safe;
                if (!Pavement(guess, out safe)) continue;

                found.Add(safe);
            }

            return found.Count < 2 ? null : found;
        }

        /// <summary>
        /// The nearest place a person can actually stand.
        ///
        /// GET_SAFE_COORD_FOR_PED is the game's own answer to "does a pedestrian belong here",
        /// which is a much harder question than it sounds -- it rules out roads, water, roofs
        /// and the inside of walls, all of which a naive offset finds constantly. Flag 16 is
        /// the usual set for a pavement rather than a carriageway.
        /// </summary>
        private static bool Pavement(Vector3 near, out Vector3 spot)
        {
            spot = Vector3.Zero;

            try
            {
                var got = new OutputArgument();

                if (!Function.Call<bool>(Hash.GET_SAFE_COORD_FOR_PED,
                                         near.X, near.Y, near.Z, true, got, 16))
                {
                    return false;
                }

                var safe = got.GetResult<Vector3>();
                if (safe == Vector3.Zero) return false;

                spot = safe;
                return true;
            }
            catch
            {
                return false;
            }
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
