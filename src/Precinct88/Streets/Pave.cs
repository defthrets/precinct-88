using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// Whether a place is somewhere a police officer should be standing.
    ///
    /// ONE AUTHORITY, because this got answered in three files and was wrong in all of them the
    /// same way. Route asked it about waypoints, Rounds about whether to start a clipboard, and
    /// Foot about where to spawn -- three copies of a test, so improving it meant improving it
    /// three times, and the first version of it was improved in one place and not the others.
    ///
    /// IS_POINT_ON_ROAD IS NOT ENOUGH ON ITS OWN and that is the whole reason this file has any
    /// content. It tests against the game's road polygons, which cover a carriageway and do NOT
    /// reliably cover the things a road is made of: tram reservations, painted medians, the
    /// middle of a wide junction, turning bays. An officer stood on a tram line in the middle
    /// of a six-lane road is, as far as that native is concerned, not on a road at all.
    ///
    /// So it is asked twice. The second question is how far he is from the nearest VEHICLE
    /// NODE, which is the centreline of the road link -- and a centreline is exactly what a
    /// tram reservation runs down. A pavement on even a narrow street sits four or five metres
    /// off it; the middle of the road is under two.
    ///
    /// The threshold is the only judgement in here and it is deliberately mean. A false
    /// positive costs one rejected waypoint out of five tries, which nobody sees. A false
    /// negative is a police officer stood on a tram line, which is the screenshot that caused
    /// this file to exist.
    /// </summary>
    internal static class Pave
    {
        /// <summary>
        /// Closer than this to a road's centreline and he is in the road.
        ///
        /// A two-lane street is about seven metres across, so its centreline to its kerb is
        /// three and a half and the middle of its pavement is nearer five. Anything under this
        /// is carriageway on all but the narrowest lane in the game.
        /// </summary>
        private const float TooNear = 4.2f;

        /// <summary>How far out to look for a kerb, in rings.</summary>
        private static readonly float[] Rings = { 6f, 10f, 15f, 21f };

        /// <summary>
        /// Whether this is road rather than pavement.
        ///
        /// Errs towards saying NO on an exception. Every caller uses this to reject somewhere,
        /// and a version that throws its way to "everything is a road" would leave the whole
        /// foot patrol unable to find anywhere to stand.
        /// </summary>
        public static bool OnRoad(Vector3 where)
        {
            try
            {
                if (Function.Call<bool>(Hash.IS_POINT_ON_ROAD, where.X, where.Y, where.Z, 0))
                {
                    return true;
                }

                var got = new OutputArgument();

                // Node type 1 is the ordinary road network. If there is no node anywhere near,
                // there is no road to be in the middle of.
                if (!Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE,
                                         where.X, where.Y, where.Z, got, 1, 3f, 0f))
                {
                    return false;
                }

                var node = got.GetResult<Vector3>();
                if (node == Vector3.Zero) return false;

                // FLAT DISTANCE, NOT 3D. A road on a bridge directly over a pavement is a
                // different place, and measuring through the height would call the pavement a
                // road every time it passed under a flyover.
                var dx = node.X - where.X;
                var dy = node.Y - where.Y;

                return dx * dx + dy * dy < TooNear * TooNear;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Somewhere near here a person can stand, that is not a road.
        ///
        /// GET_SAFE_COORD_FOR_PED is the game's own answer to "does a pedestrian belong here",
        /// which rules out walls, water and roofs -- and emphatically does not rule out tarmac.
        /// It is the first half of the question and this is the second.
        /// </summary>
        public static bool Spot(Vector3 near, out Vector3 found)
        {
            found = Vector3.Zero;

            try
            {
                var got = new OutputArgument();

                // 16 is the usual flag set for a pavement rather than a carriageway. It is a
                // preference, not a guarantee, which is why OnRoad is asked afterwards.
                if (!Function.Call<bool>(Hash.GET_SAFE_COORD_FOR_PED,
                                         near.X, near.Y, near.Z, true, got, 16))
                {
                    return false;
                }

                var safe = got.GetResult<Vector3>();
                if (safe == Vector3.Zero) return false;
                if (OnRoad(safe)) return false;

                found = safe;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The nearest pavement to somebody who is stood in the road.
        ///
        /// RINGS OUTWARDS RATHER THAN ONE GUESS, because the first safe coord the game offers a
        /// man stood in the middle of a road is very often another part of the same road -- it
        /// answers "somewhere you can stand", and he is already stood somewhere.
        ///
        /// The starting angle is rolled so that four officers in one junction do not all set
        /// off in the same direction, which reads worse than any of them being there did.
        /// </summary>
        public static bool Kerb(Vector3 from, Random rng, out Vector3 found)
        {
            found = Vector3.Zero;

            for (var ring = 0; ring < Rings.Length; ring++)
            {
                var start = rng.NextDouble() * Math.PI * 2d;

                for (var i = 0; i < 8; i++)
                {
                    var a = start + Math.PI * 2d * i / 8d;

                    var guess = from + new Vector3((float)Math.Cos(a) * Rings[ring],
                                                   (float)Math.Sin(a) * Rings[ring], 0f);

                    if (Spot(guess, out found)) return true;
                }
            }

            Log.Debug("No pavement found near " + from + ".");
            return false;
        }
    }
}
