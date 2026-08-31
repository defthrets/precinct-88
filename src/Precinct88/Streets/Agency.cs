using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// Who polices a place -- the force, not the officer.
    ///
    /// EVERY UNIT IN THIS MOD WAS AN IDENTICAL LSPD CRUISER, everywhere, including a hundred
    /// miles up a dirt road in Blaine County. That is wrong in the way a map with one weather
    /// type is wrong: nothing about it is broken, and the whole world is flatter than it should
    /// be. The city has a police department, the county has a sheriff, the freeways have highway
    /// patrol, and the parks have rangers -- and the game ships models for all four.
    ///
    /// The models are NOT VERIFIED. They are the names the base game is understood to use, and
    /// every one is checked with Model.IsValid before anything is spawned, so a wrong guess
    /// costs an agency rather than costing a crash. An agency with nothing valid in it falls
    /// back to the city police, which are the one set this mod has already seen work in-game.
    /// The log says which resolved, once, so a first run answers it rather than leaving somebody
    /// wondering why they never see a sheriff.
    /// </summary>
    internal sealed class Agency
    {
        public readonly string Name;

        /// <summary>Marked cars. Checked for validity before use.</summary>
        public readonly string[] Cars;

        /// <summary>Uniforms. Same treatment.</summary>
        public readonly string[] Peds;

        public Agency(string name, string[] cars, string[] peds)
        {
            Name = name;
            Cars = cars;
            Peds = peds;
        }

        private Model[] _cars;
        private Model[] _peds;
        private bool _checked;

        /// <summary>Whether this force can actually be put on the road on this install.</summary>
        public bool Available
        {
            get
            {
                Resolve();
                return _cars.Length > 0 && _peds.Length > 0;
            }
        }

        public Model? Car(Random rng)
        {
            Resolve();
            return _cars.Length == 0 ? (Model?)null : _cars[rng.Next(_cars.Length)];
        }

        public Model? Ped(Random rng)
        {
            Resolve();
            return _peds.Length == 0 ? (Model?)null : _peds[rng.Next(_peds.Length)];
        }

        /// <summary>
        /// Filters the name lists down to what this build actually has.
        ///
        /// Once, lazily. Model.IsValid is cheap but not free, and this is asked on every spawn.
        /// </summary>
        private void Resolve()
        {
            if (_checked) return;
            _checked = true;

            _cars = Valid(Cars);
            _peds = Valid(Peds);

            if (_cars.Length == 0 || _peds.Length == 0)
            {
                Log.Warn("Agency " + Name + " has no usable models on this install (" +
                         _cars.Length + " car, " + _peds.Length + " ped). Falling back to city " +
                         "police wherever it would have been used.");
            }
            else
            {
                Log.Info("Agency " + Name + ": " + _cars.Length + " car model(s), " +
                         _peds.Length + " uniform(s).");
            }
        }

        private static Model[] Valid(string[] names)
        {
            var found = new List<Model>();

            foreach (var n in names)
            {
                try
                {
                    var m = new Model(n);
                    if (m.IsValid) found.Add(m);
                }
                catch
                {
                    // A name this build does not have. Skipping it is the point.
                }
            }

            return found.ToArray();
        }
    }

    /// <summary>
    /// Which force answers for where.
    ///
    /// Two layers, because jurisdiction genuinely is two layers. The DISTRICT says who polices
    /// the area -- city police in Los Santos, the sheriff out in the county. The ROAD then
    /// overrides it in the two cases where the road matters more than the area does: a freeway
    /// belongs to highway patrol wherever it runs, and the wilderness belongs to rangers.
    ///
    /// This is the script equivalent of what a popgroups.ymt edit does, and it is deliberately
    /// script rather than data. Editing the RPFs would mean an asset mod, and on the Enhanced
    /// install every legacy-era asset mod tested here has crashed the game where its content
    /// streams. A spawn table in C# cannot do that.
    /// </summary>
    internal static class Agencies
    {
        public static readonly Agency City = new Agency(
            "LSPD",
            new[] { "police", "police2", "police3" },
            new[] { "s_m_y_cop_01", "s_f_y_cop_01" });

        public static readonly Agency Sheriff = new Agency(
            "Sheriff",
            new[] { "sheriff", "sheriff2" },
            new[] { "s_m_y_sheriff_01", "s_f_y_sheriff_01" });

        /// <summary>
        /// Highway patrol.
        ///
        /// The Interceptor is the car the game dresses as highway patrol, and hwaycop is its own
        /// uniform -- which is the whole reason this agency is worth having rather than being a
        /// sheriff with a different car.
        /// </summary>
        public static readonly Agency Highway = new Agency(
            "Highway Patrol",
            new[] { "police3", "policeb" },
            new[] { "s_m_y_hwaycop_01" });

        public static readonly Agency Ranger = new Agency(
            "Park Ranger",
            new[] { "pranger" },
            new[] { "s_m_y_ranger_01", "s_f_y_ranger_01" });

        /// <summary>
        /// Wilderness, where a ranger makes more sense than anybody else.
        ///
        /// Zone codes rather than districts, because this cuts across them -- Tongva Hills is in
        /// the Vinewood district and is still not somewhere the LSPD patrol.
        /// </summary>
        private static readonly HashSet<string> Wild = new HashSet<string>(
            new[] { "MTCHIL", "CHIL", "MTGORDO", "TONGVAH", "TONGVAV", "LAGO", "PALFOR",
                    "CANNY", "MTJOSE", "ELYSIAN", "ZANCUDO", "DESRT", "SANCHIA" },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>How close to a main road counts as being on one.</summary>
        private const float OnAMainRoad = 14f;

        /// <summary>
        /// Who turns up here.
        ///
        /// Rolled rather than decided, because a boundary that is absolute reads as a boundary.
        /// A sheriff's car in the north of the city is not wrong -- they exist, they drive
        /// places -- and seeing one occasionally is what stops the rule being visible as a rule.
        /// </summary>
        public static Agency For(District district, Vector3 at, Random rng)
        {
            try
            {
                var zone = Districts.ZoneAt(at);

                // Wilderness first. A ranger on Mount Chiliad outranks whatever the district
                // says, because the district is a hundred square miles and the mountain is not
                // the part of it anybody polices from a station.
                if (Wild.Contains(zone) && Ranger.Available && rng.NextDouble() < 0.7d)
                {
                    return Ranger;
                }

                // The freeway. Highway patrol wherever it runs, which is the point of them.
                if (OnMainRoad(at) && Highway.Available)
                {
                    var chance = district != null && district.Density > 0.6f ? 0.25d : 0.6d;

                    if (rng.NextDouble() < chance) return Highway;
                }

                var force = district == null ? City : Named(district.Force);

                return force.Available ? force : City;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not pick an agency: " + ex.Message);
                return City;
            }
        }

        private static Agency Named(string name)
        {
            if (string.IsNullOrEmpty(name)) return City;

            if (string.Equals(name, "Sheriff", StringComparison.OrdinalIgnoreCase)) return Sheriff;
            if (string.Equals(name, "Highway", StringComparison.OrdinalIgnoreCase)) return Highway;
            if (string.Equals(name, "Ranger", StringComparison.OrdinalIgnoreCase)) return Ranger;

            return City;
        }

        /// <summary>
        /// Whether this is a main road rather than a street.
        ///
        /// GET_CLOSEST_MAJOR_VEHICLE_NODE is the game's own distinction: major nodes are the
        /// arterial network -- freeways and the big through-routes -- and a point that sits
        /// almost on top of one is on a road highway patrol would have reason to be on. A point
        /// in a housing estate has its nearest major node several hundred metres away.
        ///
        /// The alternative was reading node property flags, whose bit meanings are not
        /// documented anywhere trustworthy. This asks a question the game answers directly.
        /// </summary>
        private static bool OnMainRoad(Vector3 at)
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

        /// <summary>Resolves every agency at load, so the log answers what this install has.</summary>
        public static void Check()
        {
            var any = City.Available;

            var have = new List<string>();

            if (City.Available) have.Add("LSPD");
            if (Sheriff.Available) have.Add("Sheriff");
            if (Highway.Available) have.Add("Highway Patrol");
            if (Ranger.Available) have.Add("Park Ranger");

            Log.Info("Forces available: " + (have.Count == 0 ? "NONE" : string.Join(", ", have.ToArray())) + ".");

            if (!any)
            {
                Log.Error("Not even the city police have usable models. No unit will ever spawn.",
                          null);
            }
        }
    }
}
