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

        public District(string name, string station, float density, float attention, params string[] zones)
        {
            Name = name;
            Station = station;
            Density = density;
            Attention = attention;
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
            new District("Unpoliced", "Mission Row", 0f, 0.15f);

        private static readonly District[] All =
        {
            // Downtown. The busiest ground in the game and the station everybody knows.
            new District("Mission Row", "Mission Row", 1.00f, 0.55f,
                         "DOWNT", "LEGSQU", "TEXTI", "SKID", "PBOX", "STRAW", "MISSION"),

            // South. Cars everywhere, and none of them care -- which is the whole point of
            // running a corner down here rather than in Rockford.
            new District("Davis", "Davis", 0.95f, 0.30f,
                         "DAVIS", "RANCHO", "CHAMH", "CYPRE", "SLAB"),

            // East industrial. Quiet, and quiet in a way that means nobody is coming.
            new District("La Mesa", "La Mesa", 0.55f, 0.35f,
                         "LMESA", "EBURO", "MURRI", "ELYSIAN", "TERMINA", "PROCOB"),

            // The beach. Heavy foot traffic, light policing, and a lot of it on foot.
            new District("Vespucci", "Vespucci", 0.70f, 0.40f,
                         "VESP", "VCANA", "DELPE", "KOREAT", "LOSPUER", "PBLUFF"),

            // Money. You will not see many, and the one you see has already noticed you.
            new District("Rockford Hills", "Rockford Hills", 0.35f, 0.85f,
                         "ROCKF", "BURTON", "MORN", "RICHM", "GOLF", "BHAMCA", "BANHAMC"),

            // Vinewood. Tourists, cameras, and a force that behaves accordingly.
            new District("Vinewood", "Vinewood", 0.65f, 0.60f,
                         "VINE", "WVINE", "DTVINE", "HAWICK", "ALTA", "MOVIE", "RGLEN", "TONGVAH"),

            // North of the city. Sparse and slow, and everybody local knows it.
            new District("Sandy Shores", "Sandy Shores", 0.30f, 0.35f,
                         "SANDY", "GRAPES", "ALAMO", "HARMO", "CCREAK", "LAGO", "MTGORDO"),

            new District("Paleto Bay", "Paleto Bay", 0.25f, 0.40f,
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
