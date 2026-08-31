using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// Finding the backs of buildings, without a single hand-typed coordinate.
    ///
    /// THE GAME ALREADY KNOWS WHICH ROADS ARE ALLEYS AND WILL TELL YOU. Every node on the
    /// vehicle network carries a flag for whether the satnav is allowed to route down it, and
    /// the ones it will not route down are exactly the things nobody navigates by: service
    /// roads, delivery bays, the cut-throughs behind a row of shops, the lane between two
    /// blocks with the bins in it. GET_VEHICLE_NODE_IS_GPS_ALLOWED is the whole technique.
    ///
    /// WHICH IS WHY THERE IS NO MAP FILE HERE. The obvious build is a JSON list of alley
    /// coordinates for South Los Santos, and it is wrong in every direction at once: it is
    /// dozens of positions typed from memory and subtly wrong, it covers only the districts
    /// somebody got round to, it is stale the moment anybody installs a map mod, and it is
    /// enormous. Asking the node network gets the real alleys, everywhere, for free -- and it
    /// covers Paleto Bay the day it covers Davis, without anybody doing Paleto Bay.
    ///
    /// What IS tuned per district is how much a patrol there prefers them. See District.Alleys.
    ///
    /// Adapted from the same trick Hoodrich's patrol used before this mod took that job over.
    /// </summary>
    internal static class Alleys
    {
        /// <summary>How many nodes out to consider. 1 is the nearest, and always the road.</summary>
        private const int SpreadMin = 1;
        private const int SpreadMax = 9;

        /// <summary>
        /// How hard to try for the kind of node asked for before settling.
        ///
        /// The last few tries take whatever they find. A patrol that refuses to move because it
        /// could not find an alley is worse than one that drives down a street.
        /// </summary>
        private const int Tries = 12;
        private const int Fussy = 8;

        /// <summary>
        /// A node near a point, preferring an alley or preferring a road.
        ///
        /// Returns false rather than guessing when nothing is found, and the caller then does
        /// not re-route -- which leaves the unit driving whatever it was already driving, and
        /// that is always better than sending it somewhere invented.
        /// </summary>
        public static bool Find(Vector3 from, float near, float far, bool wantAlley,
                                District stayIn, Random rng, out Vector3 at)
        {
            at = Vector3.Zero;

            var fallback = Vector3.Zero;
            var haveFallback = false;

            for (var tries = 0; tries < Tries; tries++)
            {
                try
                {
                    var reach = near + (float)rng.NextDouble() * (far - near);
                    var probe = from.Around(reach);

                    var id = Function.Call<int>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_ID,
                                                probe.X, probe.Y, probe.Z,
                                                SpreadMin + rng.Next(SpreadMax - SpreadMin),
                                                1, 3f, 0f);

                    if (!Function.Call<bool>(Hash.IS_VEHICLE_NODE_ID_VALID, id)) continue;

                    var got = new OutputArgument();
                    Function.Call(Hash.GET_VEHICLE_NODE_POSITION, id, got);

                    var here = got.GetResult<Vector3>();
                    if (here == Vector3.Zero) continue;

                    // A node the game has switched off is one nothing drives down -- a closed
                    // road, a piece of network behind a gate. Taking one is a unit driving at
                    // a wall for eleven minutes.
                    if (Function.Call<bool>(Hash.GET_VEHICLE_NODE_IS_SWITCHED_OFF, id)) continue;

                    if (stayIn != null && Districts.At(here) != stayIn) continue;

                    if (!haveFallback) { fallback = here; haveFallback = true; }

                    // THE ONE LINE THIS FILE EXISTS FOR. Not GPS-routable means the satnav
                    // will not send you down it, which means it is a back way.
                    var backstreet = !Function.Call<bool>(Hash.GET_VEHICLE_NODE_IS_GPS_ALLOWED, id);

                    if (tries < Fussy && backstreet != wantAlley) continue;

                    at = here;
                    return true;
                }
                catch
                {
                    // Next try. A node lookup that throws is not worth a log line twelve times.
                }
            }

            if (!haveFallback) return false;

            at = fallback;
            return true;
        }

        /// <summary>
        /// Whether a point is on a back street.
        ///
        /// For anything that wants to know where it currently is rather than where to go --
        /// the spotlight cares, because a beam swinging down an alley is the shot and a beam
        /// on a dual carriageway is a headlight.
        /// </summary>
        public static bool IsBackStreet(Vector3 at)
        {
            try
            {
                var id = Function.Call<int>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_ID,
                                            at.X, at.Y, at.Z, 1, 1, 3f, 0f);

                if (!Function.Call<bool>(Hash.IS_VEHICLE_NODE_ID_VALID, id)) return false;

                return !Function.Call<bool>(Hash.GET_VEHICLE_NODE_IS_GPS_ALLOWED, id);
            }
            catch
            {
                return false;
            }
        }

        // ---- the clock ---------------------------------------------------------

        /// <summary>
        /// How dark it is, 0 by day and 1 in the middle of the night.
        ///
        /// Ramped rather than a switch, so dusk is dusk. Everything that gets more likely after
        /// dark multiplies by this, which means one number moves the whole night character of
        /// the mod rather than five thresholds having to agree with each other.
        /// </summary>
        public static float Night()
        {
            try
            {
                var h = Function.Call<int>(Hash.GET_CLOCK_HOURS);

                if (h >= 22 || h < 5) return 1f;        // properly dark
                if (h >= 20) return 0.6f;               // dusk
                if (h == 21) return 0.85f;
                if (h == 5) return 0.5f;                // getting light
                if (h == 6) return 0.2f;

                return 0f;
            }
            catch
            {
                return 0f;
            }
        }

        public static bool IsDark() => Night() > 0.45f;
    }
}
