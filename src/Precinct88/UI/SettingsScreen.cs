using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using GTA;
using Precinct88.Contact;
using Precinct88.Core;
using Precinct88.Response;
using Precinct88.Streets;

namespace Precinct88.UI
{
    /// <summary>
    /// One line in the panel. A header when there is nothing to change.
    ///
    /// Deliberately one class with nullable behaviour rather than a hierarchy of Toggle,
    /// Slider and Picker. The three differ only in how they print themselves and what a nudge
    /// does to them, which is two delegates -- and a three-class hierarchy to hold two
    /// delegates is more file than the problem is.
    /// </summary>
    internal sealed class Row
    {
        public string Label;
        public string Note;

        /// <summary>Where it lives in the ini. Null for a header.</summary>
        public string Section;
        public string Key;

        /// <summary>What it currently reads as, on the right of the row.</summary>
        public Func<string> Show;

        /// <summary>Left or right. -1 or +1.</summary>
        public Action<int> Nudge;

        /// <summary>What to write to the ini. Usually the same as Show, but not always.</summary>
        public Func<string> Save;

        /// <summary>
        /// Whether changing it does anything before a reload.
        ///
        /// Almost everything here is read fresh off the Settings object every tick, so almost
        /// everything is live. The exceptions are marked in the panel rather than silently
        /// doing nothing, because a setting that appears to change and does not is worse than
        /// one that says it needs a reload.
        /// </summary>
        public bool Live = true;

        public bool IsHeader => Show == null;
    }

    /// <summary>
    /// The F11 panel.
    ///
    /// TWO JOBS, AND THE SECOND ONE IS THE REASON IT EXISTS. Changing settings is the obvious
    /// one. The other is the status block along the bottom, which prints what the police
    /// currently think -- the district you are in and its two numbers, how many units are
    /// actually on the road, whether anybody has eyes on you or is searching, how wide the
    /// search has got, and whether the description on the air still matches you.
    ///
    /// That block matters more than the settings do. This mod's entire premise is a difference
    /// between where you are and where the police believe you are, and NONE OF THAT IS VISIBLE
    /// FROM INSIDE THE GAME. Without a readout, "the search mechanic works" and "the search
    /// mechanic silently does nothing" look identical from the pavement. It is also the fastest
    /// way to find out whether the beat is producing cars at all, which is the first thing that
    /// will be wrong on the first run.
    ///
    /// Every change is written straight to Precinct88.ini through IniFile.SetValue, which keeps
    /// the comments and the indentation -- so the file a player has hand-edited stays a file
    /// they can hand-edit.
    /// </summary>
    internal sealed class SettingsScreen
    {
        // ---- layout ------------------------------------------------------------

        private const float PanelX = 0.5f;
        private const float PanelW = 0.42f;

        private const float TopY = 0.12f;
        private const float RowH = 0.031f;

        /// <summary>Rows visible at once. The list scrolls under a fixed window.</summary>
        private const int Window = 13;

        private const float TitleScale = 0.62f;
        private const float RowScale = 0.34f;
        private const float NoteScale = 0.28f;

        /// <summary>How long a held direction waits before it starts repeating.</summary>
        private const int RepeatAfterMs = 380;
        private const int RepeatEveryMs = 90;

        private static readonly Color Ink = Color.FromArgb(238, 238, 238);
        private static readonly Color Dim = Color.FromArgb(150, 150, 150);
        private static readonly Color Faint = Color.FromArgb(105, 105, 105);
        private static readonly Color Good = Color.FromArgb(120, 200, 130);
        private static readonly Color Warn = Color.FromArgb(225, 175, 90);
        private static readonly Color Panel = Color.FromArgb(225, 12, 12, 14);
        private static readonly Color Bar = Color.FromArgb(255, 22, 22, 26);
        private static readonly Color Picked = Color.FromArgb(255, 62, 92, 140);

        // ---- state -------------------------------------------------------------

        private readonly Settings _cfg;
        private readonly Fleet _fleet;
        private readonly Foot _foot;
        private readonly Manhunt _hunt;
        private readonly Func<bool> _bridged;
        private readonly Licence _licence;

        private readonly List<Row> _rows = new List<Row>();

        private int _at;
        private int _scroll;

        private int _heldSince;
        private int _lastRepeat;
        private int _heldDir;

        public bool IsOpen { get; private set; }

        public SettingsScreen(Settings cfg, Fleet fleet, Foot foot, Manhunt hunt, Licence licence,
                              Func<bool> bridged)
        {
            _cfg = cfg;
            _fleet = fleet;
            _foot = foot;
            _hunt = hunt;
            _licence = licence;
            _bridged = bridged;

            Rows();
            _at = FirstReal();
        }

        public void Toggle()
        {
            IsOpen = !IsOpen;

            if (IsOpen)
            {
                _at = FirstReal();
                _scroll = 0;
                _heldDir = 0;
            }
        }

        public void Close() => IsOpen = false;

        // ---- the rows ----------------------------------------------------------

        /// <summary>
        /// Rows(), not Build().
        ///
        /// A method called Build in this class SHADOWS the Build class in Core -- the one
        /// holding the version string this panel prints in its header -- and the error it
        /// produces ("Build() is a method, which is not valid in the given context") points at
        /// the property access rather than at the name that broke it. Hoodrich has the same
        /// note about a Draw() method hiding its Draw class. It is worth never doing twice.
        /// </summary>
        private void Rows()
        {
            _rows.Clear();

            Head("General");
            Tick("Mod enabled", "General", "Enabled",
                 () => _cfg.Enabled, v => _cfg.Enabled = v,
                 "Off leaves it installed and does nothing");
            Pick("Logging", "General", "Logging",
                 () => (int)_cfg.Logging, v => _cfg.Logging = ApplyLog((LogLevel)v),
                 new[] { "Error", "Warn", "Info", "Debug" },
                 "Debug is loud and useful exactly once");
            Tick("Stand down for LSPDFR", "General", "StandDownForLspdfr",
                 () => _cfg.StandDownForLspdfr, v => _cfg.StandDownForLspdfr = v,
                 "Checked once at load", live: false);

            Head("Patrol");
            Tick("Beat patrol", "Patrol", "Enabled",
                 () => _cfg.PatrolEnabled, v => _cfg.PatrolEnabled = v,
                 "Cars out because the beat exists, not because of you");
            Slide("Units at once", "Patrol", "Units",
                  () => _cfg.PatrolUnits, v => _cfg.PatrolUnits = (int)v, 0f, 12f, 1f, "0",
                  note: "Before the district weighting. Three is a city");
            Tick("Suppress vanilla police", "Patrol", "SuppressVanillaPatrols",
                 () => _cfg.SuppressVanillaPatrols, v => _cfg.SuppressVanillaPatrols = v,
                 "Stops the game creating cars for ambient density");
            Tick("Own dispatch only", "Patrol", "OwnDispatch",
                 () => _cfg.OwnDispatch, v => _cfg.OwnDispatch = v,
                 "No car is ever created because of your stars. THE setting");
            Slide("Minutes on a beat", "Patrol", "BeatMinutes",
                  () => _cfg.BeatMinutes, v => _cfg.BeatMinutes = v, 1f, 60f, 1f, "0", "m");
            Tick("Foot patrols", "Patrol", "FootPatrols",
                 () => _cfg.FootPatrols, v => _cfg.FootPatrols = v,
                 "Town districts only. They notice what a crew in a car does");
            Slide("Officers on foot", "Patrol", "FootUnits",
                  () => _cfg.FootUnits, v => _cfg.FootUnits = (int)v, 0f, 8f, 1f, "0");
            Tick("Come from a station", "Patrol", "FromStations",
                 () => _cfg.FromStations, v => _cfg.FromStations = v,
                 "Skipped automatically when the nearest one is too far");
            Slide("Back-alley patrol", "Patrol", "AlleyPatrol",
                  () => _cfg.AlleyPatrol, v => _cfg.AlleyPatrol = v, 0f, 2f, 0.1f, "0.0", "x",
                  note: "Over each district's own figure. Heavier after dark");
            Tick("Spotlights after dark", "Patrol", "Spotlights",
                 () => _cfg.Spotlights, v => _cfg.Spotlights = v,
                 "Out of the driver's window, swung down whatever they pass");

            Head("Wanted");
            Tick("Wanted rework", "Wanted", "Enabled",
                 () => _cfg.WantedEnabled, v => _cfg.WantedEnabled = v,
                 "Police who search rather than police who know");
            Slide("Last known holds for", "Wanted", "LastKnownSeconds",
                  () => _cfg.LastKnownSeconds, v => _cfg.LastKnownSeconds = v, 3f, 120f, 1f, "0", "s",
                  note: "The number that decides whether a chase is winnable");
            Slide("Search grows at", "Wanted", "SearchGrowth",
                  () => _cfg.SearchGrowth, v => _cfg.SearchGrowth = v, 0.5f, 40f, 0.5f, "0.0", "m/s");
            Slide("Search stops at", "Wanted", "SearchMaxRadius",
                  () => _cfg.SearchMaxRadius, v => _cfg.SearchMaxRadius = v, 30f, 800f, 10f, "0", "m");
            Tick("Description matters", "Wanted", "DescriptionMatters",
                 () => _cfg.DescriptionMatters, v => _cfg.DescriptionMatters = v,
                 "Changing clothes or cars is a real way out");
            Slide("Give up after", "Wanted", "LoseThemSeconds",
                  () => _cfg.LoseThemSeconds, v => _cfg.LoseThemSeconds = v, 5f, 300f, 5f, "0", "s");
            Slide("Star ceiling", "Wanted", "MaxStars",
                  () => _cfg.MaxStars, v => _cfg.MaxStars = (int)v, 1f, 5f, 1f, "0",
                  note: "A lid ON TOP of the per-crime ceilings, not instead of them");
            Tick("Show what they know", "Wanted", "ShowKnownStrip",
                 () => _cfg.ShowKnownStrip, v => _cfg.ShowKnownStrip = v,
                 "Tags under the stars. Grey means they hold it and it is now wrong");
            Slide("Strip height", "Wanted", "KnownStripY",
                  () => _cfg.KnownStripY, v => _cfg.KnownStripY = v, 0f, 0.9f, 0.005f, "0.000",
                  note: "Nudge if another mod has moved your HUD");
            Tick("Cameras watch", "Wanted", "CamerasWatch",
                 () => _cfg.CamerasWatch, v => _cfg.CamerasWatch = v,
                 "The counter to doing it where nobody is standing");
            Tick("Criminal profile", "Wanted", "CriminalProfile",
                 () => _cfg.CriminalProfile, v => _cfg.CriminalProfile = v,
                 "How violently you work, remembered between incidents");
            Tick("Scenes stay warm", "Wanted", "SceneStaysWarm",
                 () => _cfg.SceneStaysWarm, v => _cfg.SceneStaysWarm = v,
                 "Coming back to something serious can put it back on you");

            Head("Contact");
            Tick("Stops and searches", "Contact", "Enabled",
                 () => _cfg.ContactEnabled, v => _cfg.ContactEnabled = v);
            Tick("Stop for a gun in hand", "Contact", "StopForWeapons",
                 () => _cfg.StopForWeapons, v => _cfg.StopForWeapons = v,
                 "Never a dice roll -- the one rule you can rely on");
            Tick("Traffic stops", "Contact", "TrafficStops",
                 () => _cfg.TrafficStops, v => _cfg.TrafficStops = v,
                 "Thirteen violations, four of them the game's own bookkeeping");
            Tick("Enforce on cars", "Contact", "EnforceCars",
                 () => _cfg.EnforceCars, v => _cfg.EnforceCars = v);
            Tick("Enforce on motorcycles", "Contact", "EnforceBikes",
                 () => _cfg.EnforceBikes, v => _cfg.EnforceBikes = v);
            Tick("Enforce on bicycles", "Contact", "EnforceBicycles",
                 () => _cfg.EnforceBicycles, v => _cfg.EnforceBicycles = v,
                 "Being pulled over on a BMX is funny exactly once");
            Tick("Seize on suspension", "Contact", "SeizeOnSuspension",
                 () => _cfg.SeizeOnSuspension, v => _cfg.SeizeOnSuspension = v,
                 "Locked against you, not removed. Back when the licence is");
            Slide("Charges expire after", "Contact", "ChargeMinutes",
                  () => _cfg.ChargeMinutes, v => _cfg.ChargeMinutes = v, 0f, 240f, 5f, "0", "m",
                  note: "12 points and you are off the road. 0 never expires");
            Tick("Show speed and limit", "Contact", "ShowSpeedLimit",
                 () => _cfg.ShowSpeedLimit, v => _cfg.ShowSpeedLimit = v,
                 "A limit you cannot see is a trap, not a rule");
            Tick("Use kilometres", "Contact", "SpeedInKph",
                 () => _cfg.SpeedInKph, v => _cfg.SpeedInKph = v,
                 "Off for miles per hour");
            Slide("Readout height", "Contact", "SpeedHudY",
                  () => _cfg.SpeedHudY, v => _cfg.SpeedHudY = v, 0f, 1f, 0.005f, "0.000");
            Slide("Notice range", "Contact", "NoticeRange",
                  () => _cfg.NoticeRange, v => _cfg.NoticeRange = v, 5f, 120f, 1f, "0", "m");
            Slide("Between stops", "Contact", "StopCooldownSeconds",
                  () => _cfg.StopCooldownSeconds, v => _cfg.StopCooldownSeconds = v,
                  5f, 600f, 5f, "0", "s", note: "One clock for the whole force, not one per car");

            Head("Custody");
            Tick("Arrest and custody", "Custody", "Enabled",
                 () => _cfg.CustodyEnabled, v => _cfg.CustodyEnabled = v);
            Tick("Cuffed and walked to the car", "Custody", "WalkToTheCar",
                 () => _cfg.WalkToTheCar, v => _cfg.WalkToTheCar = v);
            Slide("Held for", "Custody", "HoldSeconds",
                  () => _cfg.HoldSeconds, v => _cfg.HoldSeconds = v, 0f, 600f, 5f, "0", "s",
                  note: "Real seconds. Zero if you disagree -- the rest still happens");
            Tick("Weapons taken", "Custody", "ConfiscateWeapons",
                 () => _cfg.ConfiscateWeapons, v => _cfg.ConfiscateWeapons = v);
            Tick("Contraband taken", "Custody", "ConfiscateContraband",
                 () => _cfg.ConfiscateContraband, v => _cfg.ConfiscateContraband = v,
                 "Needs a mod on the bridge to mean anything");
            Slide("Fine", "Custody", "Fine",
                  () => _cfg.Fine, v => _cfg.Fine = (int)v, 0f, 25000f, 250f, "0", "",
                  prefix: "$", note: "Before the multiplier for what they booked you for");
            Keyed("Surrender key", "Custody", "SurrenderKey",
                  () => _cfg.SurrenderKey, v => _cfg.SurrenderKey = v);
        }

        private static LogLevel ApplyLog(LogLevel level)
        {
            Log.Level = level;
            return level;
        }

        private void Head(string label) => _rows.Add(new Row { Label = label });

        private void Tick(string label, string section, string key,
                          Func<bool> get, Action<bool> set, string note = null, bool live = true)
        {
            _rows.Add(new Row
            {
                Label = label,
                Note = note,
                Section = section,
                Key = key,
                Live = live,
                Show = () => get() ? "ON" : "OFF",
                Save = () => get() ? "true" : "false",
                Nudge = dir => set(!get()),
            });
        }

        private void Slide(string label, string section, string key,
                           Func<float> get, Action<float> set,
                           float lo, float hi, float step, string format,
                           string suffix = "", string prefix = "", string note = null)
        {
            _rows.Add(new Row
            {
                Label = label,
                Note = note,
                Section = section,
                Key = key,
                Show = () => prefix + get().ToString(format, CultureInfo.InvariantCulture) + suffix,
                Save = () => get().ToString(format == "0" ? "0" : "0.###",
                                            CultureInfo.InvariantCulture),
                Nudge = dir =>
                {
                    var v = get() + step * dir;

                    // Snapped to the step so a float does not drift to 5.500001 after enough
                    // presses and then write that into somebody's ini.
                    v = (float)Math.Round(v / step) * step;

                    set(v < lo ? lo : v > hi ? hi : v);
                },
            });
        }

        private void Pick(string label, string section, string key,
                          Func<int> get, Action<int> set, string[] options, string note = null)
        {
            _rows.Add(new Row
            {
                Label = label,
                Note = note,
                Section = section,
                Key = key,
                Show = () => Option(options, get()),
                Save = () => Option(options, get()),
                Nudge = dir =>
                {
                    var i = get() + dir;

                    if (i < 0) i = options.Length - 1;
                    if (i >= options.Length) i = 0;

                    set(i);
                },
            });
        }

        private static string Option(string[] options, int i) =>
            i >= 0 && i < options.Length ? options[i] : "?";

        /// <summary>
        /// A keyboard key, cycled through a short list rather than captured.
        ///
        /// Capturing the next keypress is the obvious design and it is a trap in a panel that
        /// is itself driven by the keyboard: there is no keystroke left that means "cancel",
        /// and a player who opens the capture by accident has to alt-tab out. A short list of
        /// keys that are actually free on a modded install is more useful anyway -- the ini is
        /// there for anybody who wants something else.
        /// </summary>
        private void Keyed(string label, string section, string key,
                           Func<System.Windows.Forms.Keys> get,
                           Action<System.Windows.Forms.Keys> set)
        {
            var choices = new[]
            {
                System.Windows.Forms.Keys.X,
                System.Windows.Forms.Keys.B,
                System.Windows.Forms.Keys.K,
                System.Windows.Forms.Keys.L,
                System.Windows.Forms.Keys.J,
                System.Windows.Forms.Keys.OemPeriod,
                System.Windows.Forms.Keys.OemQuestion,
            };

            _rows.Add(new Row
            {
                Label = label,
                Note = "Hold it when police can see you",
                Section = section,
                Key = key,
                Show = () => "[" + get() + "]",
                Save = () => get().ToString(),
                Nudge = dir =>
                {
                    var i = Array.IndexOf(choices, get());

                    // Not in the list -- somebody set it by hand in the ini. Start from the
                    // beginning rather than jumping somewhere arbitrary.
                    if (i < 0) { set(choices[0]); return; }

                    i += dir;
                    if (i < 0) i = choices.Length - 1;
                    if (i >= choices.Length) i = 0;

                    set(choices[i]);
                },
            });
        }

        // ---- input -------------------------------------------------------------

        public void Update()
        {
            if (!IsOpen) return;

            // EVERYTHING OFF, then the handful back on that this panel uses. The other way
            // round -- disabling the controls we know about -- leaves whatever we did not
            // think of still reaching the player, so browsing the settings fires his gun.
            Game.DisableAllControlsThisFrame();

            foreach (var c in new[]
            {
                GTA.Control.PhoneUp, GTA.Control.PhoneDown,
                GTA.Control.PhoneLeft, GTA.Control.PhoneRight,
                GTA.Control.PhoneSelect, GTA.Control.PhoneCancel,
                GTA.Control.LookUpDown, GTA.Control.LookLeftRight,
            })
            {
                Game.EnableControlThisFrame(c);
            }

            if (Game.IsControlJustPressed(GTA.Control.PhoneCancel)) { Close(); return; }

            if (Game.IsControlJustPressed(GTA.Control.PhoneUp)) Move(-1);
            if (Game.IsControlJustPressed(GTA.Control.PhoneDown)) Move(1);

            Held();

            // Enter does the same as right, which is what a toggle wants and what a slider can
            // live with.
            if (Game.IsControlJustPressed(GTA.Control.PhoneSelect)) Change(1);
        }

        /// <summary>Left and right repeat when held, or a slider is forty presses wide.</summary>
        private void Held()
        {
            var now = Game.GameTime;

            var left = Game.IsControlPressed(GTA.Control.PhoneLeft);
            var right = Game.IsControlPressed(GTA.Control.PhoneRight);

            var dir = right ? 1 : left ? -1 : 0;

            if (dir == 0) { _heldDir = 0; return; }

            if (dir != _heldDir)
            {
                _heldDir = dir;
                _heldSince = now;
                _lastRepeat = now;

                Change(dir);
                return;
            }

            if (now - _heldSince < RepeatAfterMs) return;
            if (now - _lastRepeat < RepeatEveryMs) return;

            _lastRepeat = now;
            Change(dir);
        }

        private void Move(int dir)
        {
            for (var step = 0; step < _rows.Count; step++)
            {
                _at += dir;

                if (_at < 0) _at = _rows.Count - 1;
                if (_at >= _rows.Count) _at = 0;

                if (!_rows[_at].IsHeader) break;
            }

            // Keep the cursor inside the window, and keep the header above it visible when
            // the cursor is on the first row of a section.
            if (_at < _scroll + 1) _scroll = Math.Max(0, _at - 1);
            if (_at > _scroll + Window - 2) _scroll = _at - Window + 2;

            if (_scroll > _rows.Count - Window) _scroll = Math.Max(0, _rows.Count - Window);
            if (_scroll < 0) _scroll = 0;
        }

        private void Change(int dir)
        {
            if (_at < 0 || _at >= _rows.Count) return;

            var row = _rows[_at];
            if (row.IsHeader || row.Nudge == null) return;

            try
            {
                row.Nudge(dir);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not change " + row.Label + ": " + ex.Message);
                return;
            }

            Save(row);
        }

        /// <summary>
        /// Straight to the ini, on every change.
        ///
        /// Not on close, and not on a timer. A settings screen that saves when you leave it is
        /// a settings screen that loses everything when the game crashes with it open -- and
        /// this one is most likely to be open on a first run, which is exactly when a crash is
        /// most likely.
        /// </summary>
        private void Save(Row row)
        {
            if (row.Section == null || row.Key == null || row.Save == null) return;

            try
            {
                if (!IniFile.SetValue(Paths.Ini, row.Section, row.Key, row.Save()))
                {
                    Log.Warn("Could not write " + row.Key + " to " + Paths.Ini +
                             " -- the change applies for this session but will not stick.");
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not save " + row.Key + ": " + ex.Message);
            }
        }

        // ---- drawing -----------------------------------------------------------

        public void Draw()
        {
            if (!IsOpen) return;

            try
            {
                var rows = Math.Min(Window, _rows.Count);

                var bodyH = rows * RowH;
                var headH = 0.055f;
                var footH = 0.134f;

                var top = TopY;

                // Header.
                Screen.Rect(PanelX, top + headH * 0.5f, PanelW, headH, Bar);
                Screen.Text("PRECINCT 88", PanelX - PanelW * 0.5f + 0.012f,
                            top + 0.010f, TitleScale, Ink);
                Screen.Text(Build.Version, PanelX + PanelW * 0.5f - 0.012f,
                            top + 0.019f, NoteScale, Faint, rightAligned: true);

                // Body.
                var bodyTop = top + headH;
                Screen.Rect(PanelX, bodyTop + bodyH * 0.5f, PanelW, bodyH, Panel);

                for (var i = 0; i < rows; i++)
                {
                    var index = _scroll + i;
                    if (index >= _rows.Count) break;

                    DrawRow(_rows[index], index, bodyTop + i * RowH);
                }

                // Status, which is the half of this panel that matters.
                DrawStatus(bodyTop + bodyH, footH);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw the settings panel: " + ex.Message);
            }
        }

        private void DrawRow(Row row, int index, float y)
        {
            var left = PanelX - PanelW * 0.5f + 0.012f;
            var right = PanelX + PanelW * 0.5f - 0.012f;

            if (row.IsHeader)
            {
                Screen.Text(row.Label.ToUpperInvariant(), left, y + 0.007f, NoteScale, Warn);
                return;
            }

            var here = index == _at;

            if (here) Screen.Rect(PanelX, y + RowH * 0.5f, PanelW, RowH, Picked);

            Screen.Text(row.Label, left, y + 0.004f, RowScale, here ? Ink : Dim);

            var value = row.Show();
            if (!row.Live) value += "  (reload)";

            Screen.Text(value, right, y + 0.004f, RowScale, here ? Ink : Dim, rightAligned: true);
        }

        /// <summary>
        /// What the police currently think.
        ///
        /// The reason the panel exists. Everything above is a number in a file; this is the
        /// only place the mod's actual premise -- that where you are and where they believe you
        /// are have come apart -- is visible at all.
        /// </summary>
        private void DrawStatus(float top, float height)
        {
            Screen.Rect(PanelX, top + height * 0.5f, PanelW, height, Bar);

            var left = PanelX - PanelW * 0.5f + 0.012f;
            var right = PanelX + PanelW * 0.5f - 0.012f;

            var y = top + 0.006f;

            var district = Districts.Here();

            Screen.Text(district.Name + "  /  " + district.Force +
                        (Alleys.IsDark() ? "   (night)" : ""), left, y, NoteScale, Ink);
            Screen.Text("den " + district.Density.ToString("0.00", CultureInfo.InvariantCulture) +
                        "  att " + district.Attention.ToString("0.00", CultureInfo.InvariantCulture) +
                        "  alley " + district.Alleys.ToString("0.00", CultureInfo.InvariantCulture),
                        right, y, NoteScale, Faint, rightAligned: true);

            y += 0.021f;

            var units = _fleet == null ? 0 : _fleet.Count;
            var onCalls = _fleet == null ? 0 : _fleet.OnCalls();

            var backs = 0;

            if (_fleet != null)
            {
                foreach (var u in _fleet.Units)
                {
                    if (u.OnABackStreet) backs++;
                }
            }

            var surge = _fleet == null ? 0 : _fleet.Surge;

            Screen.Text("limit here " +
                        Limits.Signed(Limits.For(Game.Player.Character.Position),
                                      _cfg.SpeedInKph) +
                        (_cfg.SpeedInKph ? " kph" : " mph"),
                        right, y, NoteScale, Faint, rightAligned: true);

            Screen.Text("units out " + units + "   on a call " + onCalls +
                        "   on foot " + (_foot == null ? 0 : _foot.Count) +
                        "   back streets " + backs +
                        (surge > 0 ? "   surge +" + surge : ""),
                        left, y, NoteScale, surge > 0 ? Warn : Dim);

            var mine = AmbientCops.Suppressed && _cfg.OwnDispatch;

            Screen.Text(mine ? "dispatch is ours"
                        : AmbientCops.Suppressed ? "ambient off, GAME DISPATCH ON"
                        : "game police ON",
                        right, y, NoteScale, mine ? Faint : Warn, rightAligned: true);

            y += 0.021f;

            Screen.Text(HuntLine(), left, y, NoteScale,
                        _hunt != null && _hunt.Running ? Warn : Dim);

            Screen.Text(_bridged != null && _bridged() ? "Hoodrich bridged" : "no bridge",
                        right, y, NoteScale,
                        _bridged != null && _bridged() ? Good : Faint, rightAligned: true);

            y += 0.021f;

            if (_cfg.CriminalProfile && _hunt != null && _hunt.Record != null)
            {
                var record = _hunt.Record;

                Screen.Text("profile " + record.Word +
                            " (" + record.Violence.ToString("0.00", CultureInfo.InvariantCulture) + ")",
                            left, y, NoteScale,
                            record.Notorious ? Warn : record.Hardened ? Warn : Dim);
            }
            else
            {
                Screen.Text("profile off", left, y, NoteScale, Faint);
            }

            Screen.Text(Radio.Masked(Game.Player.Character) ? "face covered" : "face uncovered",
                        right, y, NoteScale,
                        Radio.Masked(Game.Player.Character) ? Good : Faint, rightAligned: true);

            // Its own row. The licence and the profile are two different records of two
            // different things, and sharing a line with one centred over the other is how you
            // get text drawn through text.
            y += 0.021f;

            Screen.Text("licence " + Ticketing.Standing(_licence), left, y, NoteScale,
                        _licence != null && _licence.IsSuspended ? Warn : Dim);

            if (_licence != null && _licence.Owed > 0)
            {
                Screen.Text("owed $" + _licence.Owed.ToString("N0", CultureInfo.InvariantCulture),
                            right, y, NoteScale, Faint, rightAligned: true);
            }

            y += 0.021f;

            Screen.Text("Arrows move and change   Enter toggles   Backspace closes",
                        PanelX, y, NoteScale, Faint, centred: true);
        }

        private string HuntLine()
        {
            if (LawHold.Held) return "law held off";
            if (_hunt == null || !_hunt.Running) return "nothing out on you";

            var what = _hunt.Worst == null ? "something" : _hunt.Worst.Called;

            if (_hunt.State == Hunt.Seen) return "SEEN -- " + what;

            var radius = _hunt.SearchRadius.ToString("0", CultureInfo.InvariantCulture);

            // NO DESCRIPTION AT ALL is a different situation to a stale one, and the panel is
            // the one place that difference has to be legible -- from the street they look
            // identical right up until an officer walks past you.
            if (_hunt.Unidentified) return "searching " + radius + "m -- " + what + " -- NO DESCRIPTION";

            var has = _hunt.Description == null ? Known.Nothing : _hunt.Description.Has;

            var matching = _hunt.Description == null
                ? Known.Nothing
                : _hunt.Description.StillMatching(Game.Player.Character);

            return "searching " + radius + "m -- " + what +
                   " -- has " + Short(has) +
                   ", you match " + (matching == Known.Nothing ? "nothing" : Short(matching));
        }

        /// <summary>The flags as three-letter tags, so a whole description fits on one row.</summary>
        private static string Short(Known k)
        {
            if (k == Known.Nothing) return "nothing";

            var bits = new List<string>();

            if ((k & Known.Face) != 0) bits.Add("FACE");
            if ((k & Known.Clothes) != 0) bits.Add("FIT");
            if ((k & Known.Vehicle) != 0) bits.Add("CAR");
            if ((k & Known.Weapon) != 0) bits.Add("GUN");
            if ((k & Known.Camera) != 0) bits.Add("CAM");

            return string.Join("+", bits.ToArray());
        }

        private int FirstReal()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (!_rows[i].IsHeader) return i;
            }

            return 0;
        }
    }
}
