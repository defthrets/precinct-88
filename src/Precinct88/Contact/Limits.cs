using System;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Contact
{
    /// <summary>
    /// How fast you are allowed to go here.
    ///
    /// DELIBERATELY THIN FOR NOW. Speeding needs a limit to be measured against, so this exists
    /// to give it one, and it answers the question the only way that needs no data at all: the
    /// game's own road network already distinguishes arterial roads from streets, and that is
    /// most of the difference between a freeway limit and a town one.
    ///
    /// What it does NOT do yet is per-zone limits -- a school street and a dual carriageway
    /// through Vespucci are both "not a freeway" here and should not be. That is the next piece
    /// of work and this file is where it lands.
    ///
    /// Everything is in METRES PER SECOND, because that is what Vehicle.Speed is, and converting
    /// at every comparison is how a mod ends up ticketing people at 22 kph.
    /// </summary>
    internal static class Limits
    {
        /// <summary>121 kph, which is what a Los Santos freeway is signed at.</summary>
        public const float Freeway = 33.6f;

        /// <summary>80 kph. The general limit, and the one most of the map runs at.</summary>
        public const float Road = 22.2f;

        /// <summary>
        /// Back streets and service roads. Nobody signs an alley, but nobody does fifty down
        /// one either, and a patrol that sees you do it has a point.
        /// </summary>
        public const float BackStreet = 13.9f;

        /// <summary>How close to a major node counts as being on the arterial network.</summary>
        private const float OnAMainRoad = 14f;

        public static float For(Vector3 at)
        {
            try
            {
                if (Streets.Alleys.IsBackStreet(at)) return BackStreet;

                return Major(at) ? Freeway : Road;
            }
            catch
            {
                return Road;
            }
        }

        /// <summary>
        /// Whether this is the arterial network rather than a street.
        ///
        /// The same question Agencies asks to decide whether highway patrol belong here, and
        /// for the same reason -- a freeway is a different kind of road, and both the limit on
        /// it and who polices it follow from that.
        /// </summary>
        private static bool Major(Vector3 at)
        {
            try
            {
                var got = new OutputArgument();

                if (!Function.Call<bool>(Hash.GET_CLOSEST_MAJOR_VEHICLE_NODE,
                                         at.X, at.Y, at.Z, got, 3.0f, 0f))
                {
                    return false;
                }

                var major = got.GetResult<Vector3>();
                if (major == Vector3.Zero) return false;

                return major.DistanceTo(at) < OnAMainRoad;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The limit as a number a person would say, for a prompt or the HUD.</summary>
        public static int Signed(float metresPerSecond, bool kph = true)
        {
            var v = kph ? metresPerSecond * 3.6f : metresPerSecond * 2.23694f;

            // Rounded to the nearest five, because road signs are.
            return (int)(Math.Round(v / 5d) * 5d);
        }
    }
}
