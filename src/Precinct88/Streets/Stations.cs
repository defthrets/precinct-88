using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>Somewhere cars come from and somewhere arrests are taken to.</summary>
    internal sealed class Station
    {
        public string Name;

        /// <summary>Roughly the building. Precision does not matter -- see Stations.RoadBy.</summary>
        public Vector3 Where;

        /// <summary>Where a booked player is stood after the doors close behind him.</summary>
        public Vector3 Desk;

        public float DeskHeading;

        public override string ToString() => Name;
    }

    /// <summary>
    /// Where the force lives.
    ///
    /// THE COORDINATES ARE DELIBERATELY ALLOWED TO BE WRONG, within about fifty metres, and
    /// the design is built around that rather than around getting them perfect. Nothing spawns
    /// AT a station coordinate: every car comes out of the nearest real road node, found with
    /// the game's own pathfinding, so a coordinate that is half a block off produces a car on
    /// the correct street instead of a car in a wall. Hand-typed coordinates in a mod are
    /// wrong sooner or later -- the map gets edited, somebody runs an interiors mod, a number
    /// gets a digit transposed -- and the failure mode should be "that car came from slightly
    /// the wrong corner", not "the mod spawns vehicles inside the station roof".
    ///
    /// They are also in data\stations.json rather than only in here, so anybody can move one
    /// without a compiler. The table below is the fallback when that file is missing, which is
    /// the same rule the rest of this mod follows: the data file is an override, never a
    /// dependency.
    /// </summary>
    internal static class Stations
    {
        private static List<Station> _all;

        /// <summary>
        /// The built-in table.
        ///
        /// Desk positions are inside the lobby of the stations that have one you can walk into
        /// without an interiors mod. Where a station has no real interior the desk is put on
        /// the pavement at the front door instead, which is honest about what the game can
        /// show and avoids the player being released inside geometry.
        /// </summary>
        private static List<Station> BuiltIn()
        {
            return new List<Station>
            {
                new Station
                {
                    Name = "Mission Row",
                    Where = new Vector3(425.1f, -979.5f, 30.7f),
                    Desk = new Vector3(441.0f, -978.9f, 30.7f),
                    DeskHeading = 90f
                },
                new Station
                {
                    Name = "Davis",
                    Where = new Vector3(359.0f, -1584.7f, 29.3f),
                    Desk = new Vector3(363.0f, -1592.0f, 29.3f),
                    DeskHeading = 230f
                },
                new Station
                {
                    Name = "La Mesa",
                    Where = new Vector3(827.5f, -1290.0f, 28.2f),
                    Desk = new Vector3(831.0f, -1288.0f, 28.2f),
                    DeskHeading = 180f
                },
                new Station
                {
                    Name = "Vespucci",
                    Where = new Vector3(-1108.4f, -845.5f, 19.3f),
                    Desk = new Vector3(-1102.0f, -840.0f, 19.3f),
                    DeskHeading = 30f
                },
                new Station
                {
                    Name = "Rockford Hills",
                    Where = new Vector3(-561.5f, -131.6f, 38.0f),
                    Desk = new Vector3(-565.0f, -139.0f, 38.0f),
                    DeskHeading = 200f
                },
                new Station
                {
                    Name = "Vinewood",
                    Where = new Vector3(629.5f, 6.6f, 82.8f),
                    Desk = new Vector3(636.0f, 12.0f, 82.8f),
                    DeskHeading = 60f
                },
                new Station
                {
                    Name = "Sandy Shores",
                    Where = new Vector3(1853.2f, 3686.6f, 34.2f),
                    Desk = new Vector3(1856.0f, 3680.0f, 34.2f),
                    DeskHeading = 210f
                },
                new Station
                {
                    Name = "Paleto Bay",
                    Where = new Vector3(-448.7f, 6012.0f, 31.7f),
                    Desk = new Vector3(-443.0f, 6015.0f, 31.7f),
                    DeskHeading = 45f
                },
            };
        }

        public static IReadOnlyList<Station> All
        {
            get
            {
                if (_all == null) Load();
                return _all;
            }
        }

        /// <summary>
        /// Reads data\stations.json over the built-in table.
        ///
        /// Per station, not wholesale: a file that names three of them moves three of them and
        /// leaves the rest alone. A file that is malformed moves none of them and says so, and
        /// the mod still runs -- there is no state here worth refusing to start over.
        /// </summary>
        private static void Load()
        {
            _all = BuiltIn();

            try
            {
                var doc = JsonFile.Read(Paths.StationsFile);
                if (doc == null || doc.IsNull) return;

                var list = doc.Has("stations") ? doc["stations"] : doc;
                if (list.Kind != JsonKind.Array) return;

                var moved = 0;

                foreach (var item in list.Items)
                {
                    var name = item["name"].AsString();
                    if (string.IsNullOrEmpty(name)) continue;

                    var station = _all.Find(s =>
                        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

                    if (station == null)
                    {
                        station = new Station { Name = name };
                        _all.Add(station);
                    }

                    station.Where = Point(item["where"], station.Where);
                    station.Desk = Point(item["desk"], station.Desk);
                    station.DeskHeading = item["deskHeading"].AsFloat(station.DeskHeading);

                    moved++;
                }

                if (moved > 0) Log.Info("stations.json: " + moved + " station(s) read from data.");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read stations.json; using the built-in table. " + ex.Message);
            }
        }

        private static Vector3 Point(Json node, Vector3 fallback)
        {
            if (node == null || node.IsNull) return fallback;

            return new Vector3(node["x"].AsFloat(fallback.X),
                               node["y"].AsFloat(fallback.Y),
                               node["z"].AsFloat(fallback.Z));
        }

        /// <summary>The station answering for a district, or the nearest one if it names none.</summary>
        public static Station For(District district, Vector3 fallbackNear)
        {
            if (district != null && !string.IsNullOrEmpty(district.Station))
            {
                foreach (var s in All)
                {
                    if (string.Equals(s.Name, district.Station, StringComparison.OrdinalIgnoreCase))
                    {
                        return s;
                    }
                }
            }

            return Nearest(fallbackNear);
        }

        public static Station Nearest(Vector3 to)
        {
            Station best = null;
            var bestDist = float.MaxValue;

            foreach (var s in All)
            {
                var d = s.Where.DistanceTo(to);
                if (d >= bestDist) continue;

                bestDist = d;
                best = s;
            }

            return best;
        }

        /// <summary>
        /// The kerb beside a road node, for something that wants to stop without blocking it.
        ///
        /// A VEHICLE NODE IS THE MIDDLE OF THE CARRIAGEWAY, not the side of it. That is correct
        /// for driving to and completely wrong for stopping at, and taking a node as a place to
        /// park is what put a patrol car across a lane on Cypress with its lights on and a
        /// queue behind it. The node is where traffic goes; the kerb is where a car waits.
        ///
        /// GET_ROAD_BOUNDARY_USING_HEADING is the game's own answer for where the road stops.
        /// Pulled back in from it so the car sits ON the tarmac at the edge rather than half up
        /// the pavement, and clamped, because on a wide junction the boundary can be a long way
        /// off and a car parked twelve metres sideways is in somebody's garden.
        /// </summary>
        public static Vector3 Kerb(Vector3 node, float heading)
        {
            try
            {
                var edge = new OutputArgument();

                if (Function.Call<bool>(Hash.GET_ROAD_BOUNDARY_USING_HEADING,
                                        node.X, node.Y, node.Z, heading, edge))
                {
                    var at = edge.GetResult<Vector3>();

                    var over = at - node;
                    over.Z = 0f;

                    var wide = over.Length();

                    if (wide > 1.2f && wide < 14f)
                    {
                        over.Normalize();

                        // A metre and a half back off the edge. Enough to be out of the lane,
                        // not so much that it is on the kerb.
                        return at - over * 1.5f;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("No road boundary near " + node + ": " + ex.Message);
            }

            // No boundary, or a silly one. Offset to the right of the road instead, which is
            // the correct side to pull over on and is better than the middle either way.
            try
            {
                var rad = heading * (float)(Math.PI / 180d);

                // Heading is degrees clockwise from north, so forward is (-sin, cos) and the
                // right-hand side of it is (cos, sin).
                var right = new Vector3((float)Math.Cos(rad), (float)Math.Sin(rad), 0f);

                return node + right * 3.2f;
            }
            catch
            {
                return node;
            }
        }

        /// <summary>
        /// A real road node beside a station, with the heading the road runs in.
        ///
        /// This is the piece that makes the coordinates above forgiving. GET_CLOSEST_VEHICLE_
        /// _NODE_WITH_HEADING is the game answering "where is the nearest drivable road", so
        /// whatever is typed in the table gets snapped onto tarmac before anything spawns.
        ///
        /// Returns false rather than guessing when there is no road near -- and the caller then
        /// does not spawn, which is the correct outcome. A car that could not be placed
        /// properly is better than a car in a swimming pool.
        /// </summary>
        public static bool RoadBy(Vector3 near, out Vector3 spot, out float heading)
        {
            spot = near;
            heading = 0f;

            try
            {
                var outPos = new OutputArgument();
                var outHeading = new OutputArgument();

                var ok = Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                                             near.X, near.Y, near.Z,
                                             outPos, outHeading, 1, 3.0f, 0);

                if (!ok) return false;

                spot = outPos.GetResult<Vector3>();
                heading = outHeading.GetResult<float>();

                // A node a long way from where we asked is a node on a different road, and
                // usually means the point is off the network entirely -- out at sea, or up a
                // mountain. Treated as no road rather than as a road two hundred metres away.
                return spot.DistanceTo(near) < 120f;
            }
            catch (Exception ex)
            {
                Log.Debug("No road node near " + near + ": " + ex.Message);
                return false;
            }
        }
    }
}
