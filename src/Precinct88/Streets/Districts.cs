using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// A part of the map with one station responsible for it, and an opinion about how hard it
    /// gets policed.
    ///
    /// The two numbers are separate on purpose and the difference between them is most of what
    /// this mod has to say. DENSITY is how many cars are out; ATTENTION is how ready they are
    /// to start something with you. Davis is high density and low attention -- there are cars
    /// everywhere and none of them care that you are stood on a corner. Rockford Hills is the
    /// exact opposite: you will go a long time without seeing one, and the one you do see will
    /// pull over to ask what you are doing here.
    ///
    /// Collapsing those into a single "police level" is what makes every other mod of this kind
    /// feel the same everywhere. It is one number describing two unrelated things.
    /// </summary>
    internal sealed class District
    {
        public readonly string Name;

        /// <summary>Rockstar zone codes, as GET_NAME_OF_ZONE returns them.</summary>
        public readonly string[] Zones;

        /// <summary>Share of the world unit budget wanted here. Relative, not absolute.</summary>
        public readonly float Density;

        /// <summary>How likely an officer here is to make something of what he sees. 0 to 1.</summary>
        public readonly float Attention;

        /// <summary>Which station answers for it. Matched by name against Stations.</summary>
        public readonly string Station;

        /// <summary>
        /// How much of the patrolling here is down the back of things, 0 to 1.
        ///
        /// A THIRD NUMBER, and it earns its place for the same reason Density and Attention are
        /// separate: it describes something neither of the others can. Rockford Hills is low
        /// density and high attention and has almost no alleys worth driving; Davis is high
        /// density, low attention, and is mostly back lanes. Folding this into either of the
        /// others would make one of those two districts wrong.
        ///
        /// Multiplied by how dark it is at the point of use, so this is the NIGHT figure and
        /// daytime alley patrolling is a fraction of it.
        /// </summary>
        public readonly float Alleys;

        /// <summary>
        /// Which force this district belongs to. "City", "Sheriff", "Highway" or "Ranger".
        ///
        /// A STRING RATHER THAN A REFERENCE, so this table stays a plain list of facts about
        /// the map with no dependency on the agency definitions -- the two files describe
        /// different things and neither should need the other to be read.
        ///
        /// The ROAD can override it. See Agencies.For: a freeway is highway patrol wherever it
        /// runs, and Mount Chiliad is rangers whichever district contains it.
        /// </summary>
        public readonly string Force;

        public District(string name, string station, float density, float attention,
                        float alleys, string force, params string[] zones)
        {
            Name = name;
            Station = station;
            Density = density;
            Attention = attention;
            Alleys = alleys;
            Force = force;
            Zones = zones;
        }
    }

    /// <summary>
    /// The map, carved up the way a force would carve it up.
    ///
    /// Keyed on the game's own zone names rather than on coordinates and radii, for the same
    /// reason Hoodrich keys turf that way: GET_NAME_OF_ZONE is exact, it is free, and it never
    /// disagrees with itself at a boundary the way two overlapping circles do.
    ///
    /// Anywhere not named below falls to Unpoliced -- the desert, the sea, Zancudo, the top of
    /// Chiliad. Not an oversight: a beat patrol on Mount Gordo is a car driving up a mountain
    /// for eleven minutes to look at nothing.
    /// </summary>
    internal static class Districts
    {
        /// <summary>Nowhere in particular. Nothing patrols here and nothing responds quickly.</summary>
        public static readonly District Unpoliced =
            new District("Unpoliced", "Mission Row", 0f, 0.15f, 0f, "City");

        private static readonly District[] All =
        {
            // THE THIRD NUMBER IS THE ALLEY FIGURE, and every district has a considered one.
            //
            // A caveat that matters for the last two: out of the city, "not GPS-routable" stops
            // meaning alley and starts meaning dirt track, fire road and farm access. That is
            // still the right behaviour -- a sheriff on a back road is what those districts
            // have instead of a cruiser down a service lane -- but it is a different thing to
            // watch, and their figures are set for a back road rather than for an alley.

            // Downtown. The busiest ground in the game and the station everybody knows, and
            // Skid Row and the Textile City service lanes are some of the densest alley
            // network in the map.
            new District("Mission Row", "Mission Row", 1.00f, 0.55f, 0.55f, "City",
                         "DOWNT", "LEGSQU", "TEXTI", "SKID", "PBOX", "STRAW", "MISSION"),

            // South. Cars everywhere, and none of them care -- which is the whole point of
            // running a corner down here rather than in Rockford. Almost all of Davis and
            // Rancho is back lanes behind low housing, which is exactly what this is for.
            new District("Davis", "Davis", 0.95f, 0.30f, 0.70f, "City",
                         "DAVIS", "RANCHO", "CHAMH", "CYPRE", "SLAB"),

            // East industrial. Quiet, and quiet in a way that means nobody is coming -- and
            // nothing in the game has more service road per square metre than Cypress Flats
            // and El Burro.
            new District("La Mesa", "La Mesa", 0.55f, 0.35f, 0.75f, "City",
                         "LMESA", "EBURO", "MURRI", "ELYSIAN", "TERMINA", "PROCOB"),

            // The beach. Heavy foot traffic, light policing, and a lot of it on foot. Little
            // Seoul and the Vespucci strip have real rear access behind the shopfronts; the
            // canals and the sand have none, which is what pulls the figure down.
            new District("Vespucci", "Vespucci", 0.70f, 0.40f, 0.40f, "City",
                         "VESP", "VCANA", "DELPE", "KOREAT", "LOSPUER", "PBLUFF"),

            // Money. You will not see many, and the one you see has already noticed you.
            // Deliberately the lowest: these are driveways and private roads, not alleys, and
            // a squad car creeping down one is a different mod.
            new District("Rockford Hills", "Rockford Hills", 0.35f, 0.85f, 0.15f, "City",
                         "ROCKF", "BURTON", "MORN", "RICHM", "GOLF", "BHAMCA", "BANHAMC"),

            // Vinewood. Tourists, cameras, and a force that behaves accordingly. The Blvd
            // back lots and the studio service roads are the alleys here.
            new District("Vinewood", "Vinewood", 0.65f, 0.60f, 0.40f, "City",
                         "VINE", "WVINE", "DTVINE", "HAWICK", "ALTA", "MOVIE", "RGLEN", "TONGVAH"),

            // North of the city. Sparse and slow, and everybody local knows it. Off the
            // tarmac this is dirt track rather than alley -- see the note at the top.
            new District("Sandy Shores", "Sandy Shores", 0.30f, 0.35f, 0.30f, "Sheriff",
                         "SANDY", "GRAPES", "ALAMO", "HARMO", "CCREAK", "LAGO", "MTGORDO"),

            new District("Paleto Bay", "Paleto Bay", 0.25f, 0.40f, 0.30f, "Sheriff",
                         "PALETO", "PALFOR", "PALCOV", "MTCHIL", "CHIL", "BRADP", "BRADT"),
        };

        /// <summary>
        /// Zone code to district, built once.
        ///
        /// A dictionary rather than a scan, because this is asked on the wanted system's tick
        /// and a linear walk of eight districts times seven zones is forty-odd string compares
        /// several times a second for an answer that never changes.
        /// </summary>
        private static readonly Dictionary<string, District> ByZone = Build();

        private static Dictionary<string, District> Build()
        {
            var map = new Dictionary<string, District>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in All)
            {
                foreach (var z in d.Zones)
                {
                    // First wins. A zone listed twice is a mistake in the table above, and
                    // silently reassigning it would hide which one.
                    if (!map.ContainsKey(z)) map[z] = d;
                    else Log.Warn("Zone " + z + " is in two districts; keeping " + map[z].Name + ".");
                }
            }

            return map;
        }

        /// <summary>The zone code the game itself uses for this point.</summary>
        public static string ZoneAt(Vector3 where)
        {
            try
            {
                return Function.Call<string>(Hash.GET_NAME_OF_ZONE, where.X, where.Y, where.Z)
                       ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Who answers for this point. Never null.</summary>
        public static District At(Vector3 where)
        {
            var zone = ZoneAt(where);
            if (string.IsNullOrEmpty(zone)) return Unpoliced;

            District found;
            return ByZone.TryGetValue(zone, out found) ? found : Unpoliced;
        }

        /// <summary>Where the player is standing. The question almost everything actually asks.</summary>
        public static District Here()
        {
            try
            {
                var me = Game.Player.Character;
                return me == null || !me.Exists() ? Unpoliced : At(me.Position);
            }
            catch
            {
                return Unpoliced;
            }
        }

        /// <summary>Everything named above, for anything that needs to walk the table.</summary>
        public static IEnumerable<District> Everywhere() => All;
    }
}
