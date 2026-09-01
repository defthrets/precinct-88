using System;
using System.Collections.Generic;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Streets;

namespace Precinct88.Contact
{
    /// <summary>
    /// How fast you are allowed to go here.
    ///
    /// DERIVED, NOT TABULATED, and that is the whole design. The obvious build is a list of
    /// speed limits per zone -- Pull Me Over ships exactly that, and it is instructive that
    /// almost every entry in its list is the same number, because filling in ninety-seven zones
    /// by hand is a job nobody finishes and the result is one value repeated.
    ///
    /// Two questions the game can answer decide it instead, and between them they carve the map
    /// up the way limits actually vary:
    ///
    ///   IS THIS THE ARTERIAL NETWORK? GET_CLOSEST_MAJOR_VEHICLE_NODE separates freeways and
    ///   through-routes from streets. It is the same test that puts highway patrol on the
    ///   freeway, and for the same reason -- a big road is a different kind of road.
    ///
    ///   IS THIS A TOWN? The district already says which force polices it, and city forces
    ///   police cities. A road out in Blaine County is a road you can drive quickly on; the
    ///   same width of tarmac through Vespucci is not.
    ///
    /// Back streets get their own answer because nobody signs an alley and nobody does fifty
    /// down one either.
    ///
    /// data\limits.json overrides any of it per zone, for the handful of places where the
    /// derivation is simply wrong -- an airport apron, a car park, a pedestrianised strip. That
    /// file ships nearly empty on purpose: it is for exceptions, and a growing list of them is
    /// a sign the rules above need fixing rather than papering over.
    ///
    /// Everything is METRES PER SECOND, because that is what Vehicle.Speed is. Converting at
    /// every comparison is how a mod ends up ticketing somebody at twenty-two kilometres an hour.
    /// </summary>
    internal static class Limits
    {
        /// <summary>Open highway. 120 kph.</summary>
        public const float Highway = 33.3f;

        /// <summary>A main road through a town. 80 kph.</summary>
        public const float Arterial = 22.2f;

        /// <summary>Open road outside a town. 90 kph.</summary>
        public const float Country = 25.0f;

        /// <summary>An ordinary street with houses on it. 50 kph.</summary>
        public const float Street = 13.9f;

        /// <summary>A service road or an alley. 30 kph.</summary>
        public const float BackStreet = 8.3f;

        /// <summary>How close to a major node counts as being on the arterial network.</summary>
        private const float OnAMainRoad = 14f;

        /// <summary>
        /// Cached, because this is asked several times a second while driving.
        ///
        /// Two node lookups and a zone name per call is not free, and the answer only changes
        /// when you have gone somewhere -- so it is recomputed when you have moved far enough
        /// to possibly be on a different road, and not otherwise.
        /// </summary>
        private static Vector3 _lastAt;
        private static float _lastLimit = Street;
        private static bool _haveLast;

        /// <summary>Far enough to be a different road.</summary>
        private const float Moved = 12f;

        public static float For(Vector3 at)
        {
            try
            {
                if (_haveLast && at.DistanceTo(_lastAt) < Moved) return _lastLimit;

                var limit = Work(at);

                _lastAt = at;
                _lastLimit = limit;
                _haveLast = true;

                return limit;
            }
            catch
            {
                return Street;
            }
        }

        private static float Work(Vector3 at)
        {
            var zone = Districts.ZoneAt(at);

            // An override wins outright. That is what it is for.
            float over;
            if (Overrides.TryGetValue(zone, out over)) return over;

            if (Alleys.IsBackStreet(at)) return BackStreet;

            var district = Districts.At(at);

            // City forces police cities. It is the same fact the agency table already carries,
            // which is why there is no second list of urban zones to keep in step with the
            // first one.
            var town = district != null &&
                       string.Equals(district.Force, "City", StringComparison.OrdinalIgnoreCase);

            if (Major(at)) return town ? Arterial : Highway;

            return town ? Street : Country;
        }

        /// <summary>
        /// Whether this is the arterial network rather than a street.
        ///
        /// The same question Agencies asks to decide whether highway patrol belong here.
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

        // ---- overrides ---------------------------------------------------------

        private static Dictionary<string, float> _overrides;

        private static Dictionary<string, float> Overrides
        {
            get
            {
                if (_overrides == null) Load();
                return _overrides;
            }
        }

        /// <summary>
        /// Reads data\limits.json, if there is one.
        ///
        /// Values in the file are in KILOMETRES PER HOUR, because that is what somebody editing
        /// a speed limit will type, and converting here is one line against a file full of
        /// surprising numbers.
        /// </summary>
        private static void Load()
        {
            _overrides = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var doc = JsonFile.Read(System.IO.Path.Combine(Paths.Data, "limits.json"));
                if (doc == null || doc.IsNull) return;

                var zones = doc.Has("zones") ? doc["zones"] : null;
                if (zones == null || zones.Kind != JsonKind.Object) return;

                foreach (var key in zones.Keys)
                {
                    var kph = zones[key].AsFloat(-1f);
                    if (kph <= 0f) continue;

                    _overrides[key] = kph / 3.6f;
                }

                if (_overrides.Count > 0)
                {
                    Log.Info("limits.json: " + _overrides.Count + " zone override(s).");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read limits.json; using derived limits only. " + ex.Message);
            }
        }

        /// <summary>Drops the cache. For a reload.</summary>
        public static void Forget()
        {
            _haveLast = false;
            _overrides = null;
        }

        // ---- saying it ---------------------------------------------------------

        /// <summary>
        /// The limit as a number a person would say.
        ///
        /// Rounded to the nearest five, because road signs are, and because 49.96 on a HUD is a
        /// mod showing you a float.
        /// </summary>
        public static int Signed(float metresPerSecond, bool kph)
        {
            var v = kph ? metresPerSecond * 3.6f : metresPerSecond * 2.23694f;

            return (int)(Math.Round(v / 5d) * 5d);
        }

        /// <summary>A speed as a whole number in the player's units.</summary>
        public static int Read(float metresPerSecond, bool kph)
        {
            return (int)Math.Round(metresPerSecond * (kph ? 3.6f : 2.23694f));
        }
    }
}
