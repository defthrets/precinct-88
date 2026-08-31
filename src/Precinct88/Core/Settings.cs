using System;
using System.Windows.Forms;

namespace Precinct88.Core
{
    /// <summary>
    /// Everything tunable, with a working value for every one of them.
    ///
    /// The ini is optional, and that is the right way round: a mod that will not run without
    /// its settings file is a mod that fails for anybody who deletes a file they were told
    /// they could edit. Every knob below is the value the mod runs on when the ini is absent,
    /// unreadable, or missing that particular line.
    ///
    /// Four sections because there are four systems, and each of them can be switched off on
    /// its own. Somebody who wants the beat patrol and nothing else should not have to accept
    /// a jail sentence to get it -- and somebody running LSPDFR wants exactly the opposite of
    /// everything in here, which is what [General] Enabled is for.
    /// </summary>
    internal sealed class Settings
    {
        // ---- general -----------------------------------------------------------

        /// <summary>Off entirely, for somebody who wants it installed and not running.</summary>
        public bool Enabled = true;

        /// <summary>How much goes in the log. Debug is loud and useful exactly once.</summary>
        public LogLevel Logging = LogLevel.Info;

        /// <summary>
        /// Opens the settings panel.
        ///
        /// F11 by default, and it was checked rather than picked: it is free on both installs
        /// on this machine since PullMeOverRemade came out. On a Legacy install running LSPDFR
        /// it is PlateWatch and RealNames -- but this mod stands down for LSPDFR anyway, so the
        /// two cannot both be listening.
        /// </summary>
        public Keys MenuKey = Keys.F11;

        /// <summary>
        /// Whether to stand down when a police framework is already running here.
        ///
        /// RAGE Plugin Hook with LSPDFR loaded is a different game -- it owns dispatch, it owns
        /// the wanted system, and it puts the player on the other side of all of this. Two
        /// systems each deciding what the police do is not a degraded experience, it is
        /// officers being given contradictory orders every frame, and it is very hard to
        /// diagnose from inside the game because everything half works.
        ///
        /// Detected by file rather than by assembly, because the plugin loads under RPH and
        /// not under SHVDN, so there is nothing in our AppDomain to reflect over.
        /// </summary>
        public bool StandDownForLspdfr = true;

        // ---- patrol ------------------------------------------------------------

        /// <summary>Ambient patrol: cars on a beat because the beat exists, not because of you.</summary>
        public bool PatrolEnabled = true;

        /// <summary>
        /// Cars out at once across the whole map, before the district weighting.
        ///
        /// This is a ceiling and not a target. Vinewood Hills at four in the morning gets one
        /// of these and Davis at midnight gets all of them; see World.Districts for the
        /// weights that actually decide.
        /// </summary>
        public int PatrolUnits = 3;

        /// <summary>
        /// Whether to switch off the game's own random police.
        ///
        /// The vanilla generator is the reason police appear behind you on an empty road. With
        /// it left on, every car this mod carefully drives out of a station is competing with
        /// cars the engine conjures a block away -- so the beat reads as noise rather than as
        /// policing, and the density is wrong no matter what PatrolUnits is set to.
        ///
        /// Off is the whole point of the mod. On is for somebody who wants the extra systems
        /// layered over vanilla density rather than replacing it.
        /// </summary>
        public bool SuppressVanillaPatrols = true;

        /// <summary>
        /// Whether this mod is the ONLY thing that sends police.
        ///
        /// Switches off the engine's dispatch entirely, so no squad car is ever created because
        /// you have a wanted level -- every unit that reaches you was already on the road and
        /// had to drive. This is the setting that makes the mod actually work; with it off, the
        /// game's dispatch runs alongside and floods over everything the Fleet does.
        ///
        /// Off is for somebody who wants the beat and the wanted rework layered on top of
        /// vanilla response rather than instead of it.
        /// </summary>
        public bool OwnDispatch = true;

        /// <summary>Minutes before a unit has finished its round and goes home.</summary>
        public float BeatMinutes = 11f;

        /// <summary>
        /// How much patrolling goes down the backs of things, over the district figure.
        ///
        /// A multiplier rather than a value: each district already has its own opinion, and
        /// this scales all of them at once. 0 keeps patrols on the main roads entirely.
        /// </summary>
        public float AlleyPatrol = 1f;

        /// <summary>
        /// The beam out of the driver's window after dark.
        ///
        /// Drawn per frame rather than switched on, because a police car in this game has no
        /// searchlight the engine will turn on for you.
        /// </summary>
        public bool Spotlights = true;

        /// <summary>
        /// Whether units drive out of a station rather than fading in down the street.
        ///
        /// Costs a little realism in exchange for a lot: a car that came from Mission Row is a
        /// car with a direction and a reason. It is switched off automatically when the nearest
        /// station is too far to reach before the player has moved on -- see Stations.
        /// </summary>
        public bool FromStations = true;

        // ---- wanted ------------------------------------------------------------

        /// <summary>The wanted rework: police who search rather than police who know.</summary>
        public bool WantedEnabled = true;

        /// <summary>
        /// How long an officer holds your last known position before it goes stale.
        ///
        /// This is the number that decides whether a chase is winnable. Too short and the
        /// police are useless; too long and there was no point breaking line of sight.
        /// </summary>
        public float LastKnownSeconds = 22f;

        /// <summary>How fast the search area grows around where they last had you, in metres a second.</summary>
        public float SearchGrowth = 5.5f;

        /// <summary>Widest the search gets before they give up on that area entirely.</summary>
        public float SearchMaxRadius = 260f;

        /// <summary>
        /// Whether what you look like matters.
        ///
        /// With this on, the radio carries a description -- what you are wearing and what you
        /// are driving -- and changing either one is a real way out. Off, they recognise you
        /// on sight regardless, which is vanilla behaviour and much less interesting.
        /// </summary>
        public bool DescriptionMatters = true;

        /// <summary>Seconds out of sight and out of the search area before it is dropped.</summary>
        public float LoseThemSeconds = 40f;

        /// <summary>The ceiling. Five is vanilla; three makes the city feel a great deal smaller.</summary>
        public int MaxStars = 5;

        /// <summary>
        /// The strip under the wanted stars saying what they have on you.
        ///
        /// Off is a legitimate choice for somebody who wants to work it out from the street,
        /// and the mechanic is unchanged either way -- the strip only reports.
        /// </summary>
        public bool ShowKnownStrip = true;

        /// <summary>How far down the screen the strip sits. Under the stars, wherever those are.</summary>
        public float KnownStripY = 0.083f;

        /// <summary>
        /// Cameras as a witness that is not a person.
        ///
        /// The counter to "do it where nobody is". Found as world props rather than from a
        /// coordinate list, so they are wherever the game actually put them.
        /// </summary>
        public bool CamerasWatch = true;

        /// <summary>
        /// Whether how violently you work is remembered between incidents.
        ///
        /// Off makes every night the first night, which is vanilla and is a defensible thing
        /// to want.
        /// </summary>
        public bool CriminalProfile = true;

        /// <summary>Whether coming back to where you did it can put it back on you.</summary>
        public bool SceneStaysWarm = true;

        // ---- contact -----------------------------------------------------------

        /// <summary>Officers who start something because they have a reason to.</summary>
        public bool ContactEnabled = true;

        /// <summary>A gun in your hand on a street they are on. The one thing that always stops a car.</summary>
        public bool StopForWeapons = true;

        /// <summary>Being pulled over for how you are driving, or for what you are driving.</summary>
        public bool TrafficStops = true;

        /// <summary>Which kinds of vehicle the traffic laws are enforced on at all.</summary>
        public bool EnforceCars = true;
        public bool EnforceBikes = true;

        /// <summary>
        /// Bicycles. Off by default, and Pull Me Over defaults it off too.
        ///
        /// Every rule here applies to a pushbike -- you can speed on one, ride it on the
        /// pavement, and go the wrong way up a street -- and being pulled over on a BMX is
        /// funny exactly once.
        /// </summary>
        public bool EnforceBicycles = false;

        /// <summary>How far an officer notices something worth stopping you for.</summary>
        public float NoticeRange = 30f;

        /// <summary>
        /// Seconds between one stop ending and another being possible.
        ///
        /// One clock for the whole force, not one per car. A man walking down a line of squad
        /// cars getting stopped by each of them in turn is a button being spammed.
        /// </summary>
        public float StopCooldownSeconds = 90f;

        // ---- custody -----------------------------------------------------------

        /// <summary>Getting caught means custody rather than a fade to a hospital bill.</summary>
        public bool CustodyEnabled = true;

        /// <summary>Whether they cuff and walk you rather than the screen going black on contact.</summary>
        public bool WalkToTheCar = true;

        /// <summary>Real seconds held before release, before the multiplier for what you did.</summary>
        public float HoldSeconds = 45f;

        /// <summary>Whether weapons are taken. They are given back, minus what was illegal.</summary>
        public bool ConfiscateWeapons = true;

        /// <summary>
        /// Whether contraband is taken, and what that means.
        ///
        /// This is the one setting the other mod cares about. Precinct 88 does not know what a
        /// gram is -- it asks whoever is listening on the bridge what you were carrying and
        /// tells them it is gone. With nothing listening, this only affects weapons and cash.
        /// </summary>
        public bool ConfiscateContraband = true;

        /// <summary>The fine, before the multiplier for what they booked you for.</summary>
        public int Fine = 750;

        /// <summary>Key that gives up when they have you. Hold it.</summary>
        public Keys SurrenderKey = Keys.X;

        // ---- loading -----------------------------------------------------------

        public static Settings Load()
        {
            var s = new Settings();

            try
            {
                var ini = IniFile.Load(Paths.Ini);

                if (ini == null)
                {
                    Log.Warn("No Precinct88.ini beside the dll; running on built-in defaults.");
                    return s;
                }

                s.Enabled = ini.GetBool("General", "Enabled", s.Enabled);
                s.StandDownForLspdfr = ini.GetBool("General", "StandDownForLspdfr", s.StandDownForLspdfr);

                s.Logging = ini.GetEnum("General", "Logging", s.Logging);
                s.MenuKey = ini.GetKey("General", "MenuKey", s.MenuKey);

                s.PatrolEnabled = ini.GetBool("Patrol", "Enabled", s.PatrolEnabled);
                s.PatrolUnits = (int)Clamp(ini.GetInt("Patrol", "Units", s.PatrolUnits), 0f, 12f);
                s.SuppressVanillaPatrols = ini.GetBool("Patrol", "SuppressVanillaPatrols",
                                                       s.SuppressVanillaPatrols);
                s.BeatMinutes = Clamp(ini.GetFloat("Patrol", "BeatMinutes", s.BeatMinutes), 1f, 60f);
                s.FromStations = ini.GetBool("Patrol", "FromStations", s.FromStations);
                s.OwnDispatch = ini.GetBool("Patrol", "OwnDispatch", s.OwnDispatch);
                s.AlleyPatrol = Clamp(ini.GetFloat("Patrol", "AlleyPatrol", s.AlleyPatrol), 0f, 2f);
                s.Spotlights = ini.GetBool("Patrol", "Spotlights", s.Spotlights);

                s.WantedEnabled = ini.GetBool("Wanted", "Enabled", s.WantedEnabled);
                s.LastKnownSeconds = Clamp(ini.GetFloat("Wanted", "LastKnownSeconds",
                                                        s.LastKnownSeconds), 3f, 120f);
                s.SearchGrowth = Clamp(ini.GetFloat("Wanted", "SearchGrowth", s.SearchGrowth), 0.5f, 40f);
                s.SearchMaxRadius = Clamp(ini.GetFloat("Wanted", "SearchMaxRadius",
                                                       s.SearchMaxRadius), 30f, 800f);
                s.DescriptionMatters = ini.GetBool("Wanted", "DescriptionMatters", s.DescriptionMatters);
                s.LoseThemSeconds = Clamp(ini.GetFloat("Wanted", "LoseThemSeconds",
                                                       s.LoseThemSeconds), 5f, 300f);
                s.MaxStars = (int)Clamp(ini.GetInt("Wanted", "MaxStars", s.MaxStars), 1f, 5f);
                s.ShowKnownStrip = ini.GetBool("Wanted", "ShowKnownStrip", s.ShowKnownStrip);
                s.KnownStripY = Clamp(ini.GetFloat("Wanted", "KnownStripY", s.KnownStripY), 0f, 0.9f);
                s.CamerasWatch = ini.GetBool("Wanted", "CamerasWatch", s.CamerasWatch);
                s.CriminalProfile = ini.GetBool("Wanted", "CriminalProfile", s.CriminalProfile);
                s.SceneStaysWarm = ini.GetBool("Wanted", "SceneStaysWarm", s.SceneStaysWarm);

                s.ContactEnabled = ini.GetBool("Contact", "Enabled", s.ContactEnabled);
                s.StopForWeapons = ini.GetBool("Contact", "StopForWeapons", s.StopForWeapons);
                s.TrafficStops = ini.GetBool("Contact", "TrafficStops", s.TrafficStops);
                s.EnforceCars = ini.GetBool("Contact", "EnforceCars", s.EnforceCars);
                s.EnforceBikes = ini.GetBool("Contact", "EnforceBikes", s.EnforceBikes);
                s.EnforceBicycles = ini.GetBool("Contact", "EnforceBicycles", s.EnforceBicycles);
                s.NoticeRange = Clamp(ini.GetFloat("Contact", "NoticeRange", s.NoticeRange), 5f, 120f);
                s.StopCooldownSeconds = Clamp(ini.GetFloat("Contact", "StopCooldownSeconds",
                                                           s.StopCooldownSeconds), 5f, 600f);

                s.CustodyEnabled = ini.GetBool("Custody", "Enabled", s.CustodyEnabled);
                s.WalkToTheCar = ini.GetBool("Custody", "WalkToTheCar", s.WalkToTheCar);
                s.HoldSeconds = Clamp(ini.GetFloat("Custody", "HoldSeconds", s.HoldSeconds), 0f, 600f);
                s.ConfiscateWeapons = ini.GetBool("Custody", "ConfiscateWeapons", s.ConfiscateWeapons);
                s.ConfiscateContraband = ini.GetBool("Custody", "ConfiscateContraband",
                                                     s.ConfiscateContraband);
                s.Fine = (int)Clamp(ini.GetInt("Custody", "Fine", s.Fine), 0f, 1000000f);

                s.SurrenderKey = ini.GetKey("Custody", "SurrenderKey", s.SurrenderKey);
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the ini; using defaults.", ex);
            }

            return s;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }
}
