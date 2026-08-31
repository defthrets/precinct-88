using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Streets;

namespace Precinct88.Response
{
    /// <summary>Where the police think you are, as opposed to where you are.</summary>
    internal enum Hunt
    {
        /// <summary>Nothing is out about you.</summary>
        Clear,

        /// <summary>Somebody has eyes on you right now.</summary>
        Seen,

        /// <summary>They have lost you and are working outwards from where they last did.</summary>
        Searching,
    }

    /// <summary>
    /// The wanted system, rewritten around one idea: THE POLICE KNOW WHAT THEY HAVE BEEN TOLD.
    ///
    /// Vanilla tracks the player. The search radius on the minimap is a presentation layer over
    /// a system that never actually loses you -- officers path straight to your position, and
    /// the circle is a courtesy. That is why a chase in this game has exactly one tactic, which
    /// is to drive until the meter runs out, and why hiding has never once worked.
    ///
    /// Here there are two positions and they come apart. There is where you are, and there is
    /// SET_PLAYER_WANTED_CENTRE_POSITION -- the game's own idea of where to send police, which
    /// vanilla simply keeps pinned to you. This class stops pinning it. While an officer can
    /// see you it tracks you, exactly as before. The moment nobody can, it freezes at the last
    /// place somebody actually did, and everything after that is the force working outwards
    /// from a spot you are no longer standing on.
    ///
    /// The consequences all fall out of that one change and none of them needed writing:
    /// breaking line of sight is worth something, an alley is worth something, going indoors is
    /// worth something, and the moment where you watch a squad car race past the end of the
    /// street towards where you were is a thing that happens on its own.
    ///
    /// ESCALATION IS BY WHAT YOU DID, NOT BY HOW LONG IT HAS GONE ON. See Crime.Weight -- the
    /// ceiling on a traffic stop is two stars however far you run, and there is never a
    /// helicopter for it. That is the other half of vanilla this replaces: a system where
    /// duration is the only input turns every incident into the same incident.
    /// </summary>
    internal sealed class Manhunt
    {
        private const int TickMs = 320;

        /// <summary>How far an officer can be and still be said to have you.</summary>
        private const float EyesOn = 90f;

        /// <summary>Heat lost a second with nothing happening. Roughly a star a minute.</summary>
        private const float CoolPerSecond = 0.055f;

        /// <summary>Heat needed for each star. Flat, so the ceilings in Crime do the shaping.</summary>
        private const float PerStar = 1.4f;

        /// <summary>
        /// Grace after the last sighting before the search starts.
        ///
        /// Without it the state flaps every time somebody walks between you and an officer, and
        /// a search that starts and stops four times a second is a wanted centre that jitters
        /// around your feet -- which is worse than not having one.
        /// </summary>
        private const int LostAfterMs = 2200;

        /// <summary>How often a unit is sent somewhere new inside the search area.</summary>
        private const int RedirectMs = 9000;

        private readonly Settings _cfg;
        private readonly Fleet _fleet;
        private readonly Radio _radio = new Radio();
        private readonly Random _rng = new Random();

        private int _lastTick;
        private int _lastRedirect;

        private float _heat;
        private Weight _worst;
        private Offence _worstWhat;

        private int _lastSeenAt;
        private Vector3 _searchFrom;
        private float _searchRadius;
        private int _searchStarted;

        private int _pushedStars = -1;
        private bool _dispatchSet;

        public Manhunt(Settings cfg, Fleet fleet)
        {
            _cfg = cfg;
            _fleet = fleet;
        }

        /// <summary>Where things stand.</summary>
        public Hunt State { get; private set; } = Hunt.Clear;

        /// <summary>The description that is out, for anything that wants to show it.</summary>
        public Radio Description => _radio;

        /// <summary>The worst thing on the current incident, or null when there is no incident.</summary>
        public Weight Worst => _worst;

        /// <summary>How wide they are looking. Zero unless searching.</summary>
        public float SearchRadius => State == Hunt.Searching ? _searchRadius : 0f;

        /// <summary>Where they are looking. Only meaningful while searching.</summary>
        public Vector3 SearchCentre => _searchFrom;

        /// <summary>Whether anything at all is going on. The question most callers ask.</summary>
        public bool Running => State != Hunt.Clear;

        /// <summary>Said when the ticker should carry a line. Set by Main.</summary>
        public Action<string> Say;

        // ---- reporting ---------------------------------------------------------

        /// <summary>
        /// Somebody has told the police something.
        ///
        /// The only way heat is ever added. Every detector in the mod, and everything coming
        /// over the bridge from Hoodrich, ends up here -- which is what makes the ceilings
        /// actually hold. A crime that adds stars by calling the game's own native behind this
        /// class's back is a crime with no ceiling at all.
        /// </summary>
        public void Report(Offence what, Vector3 where)
        {
            if (!_cfg.WantedEnabled) return;
            if (LawHold.Held) return;

            var weight = Crime.Of(what);

            _heat += weight.Heat;

            // The WORST thing on the incident sticks, and nothing downgrades it. Shooting
            // somebody and then getting reported for loitering does not turn a homicide back
            // into a two-star affair.
            if (_worst == null || weight.Ceiling > _worst.Ceiling)
            {
                _worst = weight;
                _worstWhat = what;
            }

            var me = Game.Player.Character;

            if (State == Hunt.Clear)
            {
                // FIRST REPORT PUTS THE DESCRIPTION OUT FROM WHERE IT HAPPENED, not from where
                // an officer happened to be looking. Somebody called this in.
                _radio.Describe(me);

                _searchFrom = where;
                _searchRadius = 45f;
                _searchStarted = Game.GameTime;
                _lastSeenAt = Game.GameTime;

                State = Hunt.Searching;

                if (Say != null) Say(_radio.Call(weight));

                Log.Info("Reported: " + what + " (" + weight.Called + ") at " +
                         Districts.ZoneAt(where) + ".");
            }
            else
            {
                // Already running. A fresh report re-centres the search on the new thing --
                // it is newer information than whatever they had.
                _searchFrom = where;
                _searchRadius = Math.Min(_searchRadius, 60f);
            }
        }

        /// <summary>Convenience for the detectors: report wherever the player is.</summary>
        public void Report(Offence what)
        {
            try
            {
                var me = Game.Player.Character;
                Report(what, me == null || !me.Exists() ? Vector3.Zero : me.Position);
            }
            catch
            {
                // A report that cannot find the player is not a report.
            }
        }

        /// <summary>Everything is dropped. For a hold starting, a death, or an arrest.</summary>
        public void Clear(string why)
        {
            if (State == Hunt.Clear && _heat <= 0f) return;

            _heat = 0f;
            _worst = null;
            _searchRadius = 0f;
            State = Hunt.Clear;

            _radio.Clear();

            try
            {
                Game.Player.Wanted.SetWantedLevel(0, false);
                Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not clear the wanted level: " + ex.Message);
            }

            _pushedStars = 0;

            LawHold.Uncap();
            RestoreDispatch();

            Log.Info("Manhunt over: " + why + ".");
        }

        // ---- per-tick ----------------------------------------------------------

        public void Update()
        {
            if (!_cfg.WantedEnabled) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;

            var elapsed = (now - _lastTick) / 1000f;
            _lastTick = now;

            if (LawHold.Held)
            {
                // Somebody is holding the police off. Nothing here fights that -- the hold IS
                // the arbiter, and a manhunt quietly re-pushing a star behind it is the exact
                // two-systems bug the hold exists to prevent.
                return;
            }

            var me = Game.Player.Character;
            if (me == null || !me.Exists()) return;

            if (State == Hunt.Clear)
            {
                // Cool off whatever is left, so a near-miss does not carry into the next one.
                if (_heat > 0f) _heat = Math.Max(0f, _heat - CoolPerSecond * elapsed);

                Adopt(me);
                return;
            }

            var seen = Eyes(me);

            if (seen)
            {
                _lastSeenAt = now;
                _radio.Describe(me);

                _searchFrom = me.Position;
                _searchRadius = 40f;
                _searchStarted = now;

                if (State != Hunt.Seen)
                {
                    State = Hunt.Seen;
                    if (Say != null) Say("Dispatch: suspect sighted.");
                }
            }
            else if (State == Hunt.Seen && now - _lastSeenAt > LostAfterMs)
            {
                State = Hunt.Searching;
                _searchStarted = now;

                if (Say != null) Say("Dispatch: lost visual. Searching the area.");
                Log.Debug("Lost visual; search from " + Districts.ZoneAt(_searchFrom) + ".");
            }

            if (State == Hunt.Searching)
            {
                Search(me, now, elapsed);
            }

            // Heat only cools once nobody has eyes on you. Standing in front of an officer
            // waiting for the meter to run down is not an escape.
            if (!seen) _heat = Math.Max(0f, _heat - CoolPerSecond * elapsed);

            if (_heat <= 0.05f)
            {
                Clear("it went cold");
                return;
            }

            PushStars();
            PushCentre(me);
            PushDispatch();
        }

        /// <summary>
        /// Takes over a wanted level the game handed out by itself.
        ///
        /// THIS IS THE COMPATIBILITY VALVE AND IT IS NOT OPTIONAL. The engine has its own
        /// opinions about what is a crime, and so does every other mod in the folder -- run
        /// somebody over, shoot a window out, or trip a script that calls
        /// SET_PLAYER_WANTED_LEVEL directly, and stars appear that this class never issued.
        ///
        /// The wrong answer is to suppress all of it and be the only source of police, because
        /// that quietly breaks every heist mod, mission mod and callout pack installed beside
        /// us. The right one is to notice and adopt: if the game says you are wanted and we do
        /// not know why, an incident is opened at your position with a severity read off the
        /// star count, and from that point it behaves like any other -- it searches, it has a
        /// description, and it can be lost.
        ///
        /// Which means the search mechanic works for crimes this mod has never heard of.
        /// </summary>
        private void Adopt(Ped me)
        {
            int level;

            try { level = Game.Player.Wanted.WantedLevel; }
            catch { return; }

            if (level <= 0) return;

            // Read back rather than guessed at. Three stars from the engine is violence of
            // some sort; one is somebody having seen something.
            var what = level >= 4 ? Offence.Homicide
                     : level == 3 ? Offence.ShotsFired
                     : level == 2 ? Offence.Assault
                     : Offence.Loitering;

            Log.Info("Adopting a " + level + "-star level this mod did not issue, as " + what + ".");

            // Heat set to match the level that is already showing, so PushStars does not
            // immediately drop it back to one and make the stars flicker.
            _heat = Math.Max(_heat, (level - 1) * PerStar + 0.1f);

            Report(what, me.Position);
        }

        // ---- sight -------------------------------------------------------------

        /// <summary>
        /// Whether any officer can actually see the player, allowing for the description.
        ///
        /// This is where DescriptionMatters earns its keep. A clear line of sight at sixty
        /// metres to a man who no longer answers the description is an officer looking straight
        /// at somebody he has no reason to stop -- so it does not count, and the search carries
        /// on around him. Close enough and it counts anyway; a jacket is not a disguise at four
        /// metres.
        /// </summary>
        private bool Eyes(Ped me)
        {
            var matches = !_cfg.DescriptionMatters || _radio.Matches(me);

            foreach (var officer in Cops.Near(me.Position, EyesOn))
            {
                if (!Cops.Sees(officer, me, EyesOn)) continue;

                if (matches) return true;

                if (officer.Position.DistanceTo(me.Position) < Radio.RecognisesAnywayRange)
                {
                    // Recognised in spite of the change. The description is now wrong and worth
                    // re-taking, or every officer for the rest of the chase keeps having to get
                    // within seven metres of a man they have already identified.
                    _radio.Describe(me);
                    return true;
                }
            }

            return false;
        }

        // ---- searching ---------------------------------------------------------

        private void Search(Ped me, int now, float elapsed)
        {
            _searchRadius = Math.Min(_cfg.SearchMaxRadius,
                                     _searchRadius + _cfg.SearchGrowth * elapsed);

            var outside = me.Position.DistanceTo(_searchFrom) > _searchRadius;
            var sinceSeen = (now - _lastSeenAt) / 1000f;

            if (outside && sinceSeen > _cfg.LoseThemSeconds)
            {
                Clear("you were not where they were looking");
                return;
            }

            // The radius has stopped growing and he is still not in it. They have looked
            // everywhere this call was ever going to reach.
            if (_searchRadius >= _cfg.SearchMaxRadius - 0.5f && outside &&
                sinceSeen > _cfg.LoseThemSeconds * 0.6f)
            {
                Clear("the search came up empty");
                return;
            }

            if (now - _lastRedirect < RedirectMs) return;
            _lastRedirect = now;

            SendSomebody();
        }

        /// <summary>
        /// Puts a unit into the search area.
        ///
        /// Taken from the pool rather than spawned -- that is the whole argument of Fleet, and
        /// the reason nobody may come. A quiet district staying quiet during a manhunt is not a
        /// failure of this method, it is the district.
        ///
        /// The point sent to is inside the ring rather than at its centre. Sending every unit
        /// to the same spot is four cars parked on one corner while the man they want walks
        /// past the end of the road.
        /// </summary>
        private void SendSomebody()
        {
            if (_fleet == null) return;

            // Two on a call at once, at most. More than that and the pool empties, the beat
            // stops existing, and every street in the district goes silent for the duration --
            // which is the opposite of what a manhunt should feel like.
            if (_fleet.OnCalls() >= 2) return;

            var unit = _fleet.NearestFree(_searchFrom, _searchRadius + 400f);
            if (unit == null) return;

            var angle = _rng.NextDouble() * Math.PI * 2d;
            var reach = _searchRadius * (0.35f + (float)_rng.NextDouble() * 0.6f);

            var guess = _searchFrom + new Vector3((float)Math.Cos(angle) * reach,
                                                  (float)Math.Sin(angle) * reach, 0f);

            Vector3 road;
            float heading;
            if (!Stations.RoadBy(guess, out road, out heading)) road = _searchFrom;

            unit.RespondTo(road, _worst == null ? "a call" : _worst.Called);
        }

        // ---- pushing it into the game ------------------------------------------

        /// <summary>
        /// Turns heat into stars, inside the ceiling for what was actually done.
        ///
        /// Pushed only when it changes. SET_PLAYER_WANTED_LEVEL every tick re-arms the game's
        /// own escalation and fights whatever the player is doing, and it makes the stars
        /// flicker on the HUD.
        /// </summary>
        private void PushStars()
        {
            var ceiling = _worst == null ? 1 : _worst.Ceiling;
            if (ceiling > _cfg.MaxStars) ceiling = _cfg.MaxStars;

            var stars = (int)Math.Floor(_heat / PerStar) + 1;
            if (stars > ceiling) stars = ceiling;
            if (stars < 1) stars = 1;

            // The ceiling goes into the game as well as into the arithmetic. Without it the
            // engine escalates on its own the moment you drive at an officer, and the careful
            // number above becomes a suggestion.
            LawHold.Cap(ceiling);

            if (stars == _pushedStars) return;
            _pushedStars = stars;

            try
            {
                Game.Player.Wanted.SetWantedLevel(stars, false);
                Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set the wanted level: " + ex.Message);
            }
        }

        /// <summary>
        /// THE ONE NATIVE THIS WHOLE CLASS IS BUILT AROUND.
        ///
        /// SET_PLAYER_WANTED_CENTRE_POSITION is where the game sends police to. Vanilla keeps
        /// it on the player every frame, which is what "the police always know where you are"
        /// actually is under the hood -- not omniscient AI, one coordinate that never stops
        /// updating.
        ///
        /// While somebody can see you it is set to you, so a chase behaves like a chase. While
        /// nobody can, it is left on the last place somebody did, and the engine's own dispatch
        /// does the rest.
        /// </summary>
        private void PushCentre(Ped me)
        {
            try
            {
                var to = State == Hunt.Seen ? me.Position : _searchFrom;

                Function.Call(Hash.SET_PLAYER_WANTED_CENTRE_POSITION,
                              Game.Player.Handle, to.X, to.Y, to.Z);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not move the wanted centre: " + ex.Message);
            }
        }

        /// <summary>
        /// What the game is allowed to send, by what was actually done.
        ///
        /// ENABLE_DISPATCH_SERVICE, not SET_DISPATCH_SERVICE_ACTIVE -- the latter is not in
        /// SHVDN 3.9's Hash enum, which is the sort of thing that is only ever found by
        /// reflecting the assembly.
        ///
        /// The service numbers are the game's own: 2 is the police helicopter, 4 SWAT, 12 the
        /// SWAT helicopter, 14 the army. Turning them off by severity is what stops a police
        /// chase over a stolen car from ending with a Buzzard, which happens in vanilla at four
        /// stars regardless of what the four stars were for.
        /// </summary>
        private void PushDispatch()
        {
            var air = _worst != null && _worst.Air;
            var swat = _worst != null && _worst.Swat;

            try
            {
                Function.Call(Hash.ENABLE_DISPATCH_SERVICE, 2, air);    // police helicopter
                Function.Call(Hash.ENABLE_DISPATCH_SERVICE, 12, swat);  // SWAT helicopter
                Function.Call(Hash.ENABLE_DISPATCH_SERVICE, 4, swat);   // SWAT on the road
                Function.Call(Hash.ENABLE_DISPATCH_SERVICE, 14, false); // army, never

                _dispatchSet = true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not gate dispatch: " + ex.Message);
            }
        }

        /// <summary>Hands every dispatch service back. On clearing, and on teardown.</summary>
        public void RestoreDispatch()
        {
            if (!_dispatchSet) return;
            _dispatchSet = false;

            try
            {
                foreach (var service in new[] { 2, 4, 12, 14 })
                {
                    Function.Call(Hash.ENABLE_DISPATCH_SERVICE, service, true);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not restore dispatch: " + ex.Message);
            }
        }
    }
}
