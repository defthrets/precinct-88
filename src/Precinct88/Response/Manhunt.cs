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

        /// <summary>How violently you work, remembered between incidents.</summary>
        private readonly Profile _profile = new Profile();

        /// <summary>Whether anybody has been hurt during THIS incident.</summary>
        private bool _bloody;

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

        /// <summary>
        /// Where the last incident happened, and for how long it is still a place with police
        /// interest in it.
        ///
        /// A CRIME SCENE DOES NOT STOP EXISTING BECAUSE YOU GOT AWAY. In vanilla the meter runs
        /// out and the street is instantly as innocent as it was that morning -- you can drive
        /// straight back to the body you left and park. Here the location stays warm: come back
        /// while it is, and somebody who is still standing there recognises the car, or the
        /// officer taking statements looks up.
        ///
        /// Deliberately cheap and deliberately forgiving. It re-reports once, at reduced heat,
        /// and then forgets -- a memory that re-armed itself would make the scene a permanent
        /// no-go zone, which is a different and much more annoying mechanic.
        /// </summary>
        private Vector3 _sceneAt;
        private Offence _sceneWhat;
        private int _sceneCold;

        /// <summary>How long a scene stays warm after you get away from it.</summary>
        private const int SceneWarmMs = 240000;

        /// <summary>How close you have to come back for it to matter.</summary>
        private const float SceneRange = 55f;

        public Manhunt(Settings cfg, Fleet fleet)
        {
            _cfg = cfg;
            _fleet = fleet;
        }

        /// <summary>Where things stand.</summary>
        public Hunt State { get; private set; } = Hunt.Clear;

        /// <summary>The description that is out, for anything that wants to show it.</summary>
        public Radio Description => _radio;

        /// <summary>How violently you work, remembered between incidents.</summary>
        public Profile Record => _profile;

        /// <summary>The worst thing on the current incident, or null when there is no incident.</summary>
        public Weight Worst => _worst;

        /// <summary>How wide they are looking. Zero unless searching.</summary>
        public float SearchRadius => State == Hunt.Searching ? _searchRadius : 0f;

        /// <summary>Where they are looking. Only meaningful while searching.</summary>
        public Vector3 SearchCentre => _searchFrom;

        /// <summary>Whether anything at all is going on. The question most callers ask.</summary>
        public bool Running => State != Hunt.Clear;

        /// <summary>
        /// A crime is out and nobody could describe the man who did it.
        ///
        /// Police converge on a street with a location and nothing else. An officer walks past
        /// you because as far as he has been told you are a member of the public -- which is
        /// the state vanilla has no way to represent at all.
        /// </summary>
        public bool Unidentified => Running && _radio.Unidentified;

        /// <summary>What they have on you. Empty when nothing is out.</summary>
        public Known Known => Running ? _radio.Has : Response.Known.Nothing;

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
        public void Report(Offence what, Vector3 where, Known got = Known.Nothing)
        {
            if (!_cfg.WantedEnabled) return;
            if (LawHold.Held) return;

            var weight = Crime.Of(what);

            // THE PROFILE SCALES HEAT, NOT THE CEILING. A violent player reaches the top of an
            // offence faster and holds it longer; he does not get a helicopter for a traffic
            // stop, because the per-crime ceilings are the honest part of the system and
            // nothing is allowed to lift them.
            _heat += weight.Heat * (_cfg.CriminalProfile ? _profile.Multiplier : 1f);

            if (_cfg.CriminalProfile) _profile.Saw(what);

            if (what == Offence.Homicide || what == Offence.OfficerDown) _bloody = true;

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
                // FIRST REPORT PUTS OUT WHATEVER THE WITNESS ACTUALLY GOT, from where it
                // happened rather than from where an officer happened to be looking.
                //
                // `got` is very often Known.Nothing, and that is the interesting case rather
                // than a degenerate one: gunfire heard through a wall is a real report with a
                // real location and no description at all. Police converge on the street and
                // walk straight past the man who did it, because as far as anybody has told
                // them he is a member of the public.
                _radio.Note(me, got);
                _radio.Seen(where);

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
                // it is newer information than whatever they had -- and ADDS whatever this
                // witness got. A man they could not describe becomes a man they can the moment
                // somebody sees him properly, without the incident restarting.
                _radio.Note(me, got);

                _searchFrom = where;
                _searchRadius = Math.Min(_searchRadius, 60f);
            }
        }

        /// <summary>Convenience for the detectors: report wherever the player is.</summary>
        public void Report(Offence what, Known got = Known.Nothing)
        {
            try
            {
                var me = Game.Player.Character;
                Report(what, me == null || !me.Exists() ? Vector3.Zero : me.Position, got);
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

            // The scene stays warm even though the hunt is over. Not on an arrest -- they have
            // you, so there is nothing left to come back to -- and not when the incident was
            // never anything, or every traffic stop leaves a landmine on the road.
            if (_cfg.SceneStaysWarm && _worst != null && _worst.Ceiling >= 3 &&
                !why.StartsWith("arrest"))
            {
                _sceneAt = _searchFrom;
                _sceneWhat = _worstWhat;
                _sceneCold = Game.GameTime + SceneWarmMs;
            }

            // Got away without hurting anybody. Nelson's line about GTA VI is the design here
            // almost word for word: commit a crime, move on before the police arrive, do not
            // start shooting, and you are generally going to be fine.
            if (_cfg.CriminalProfile && !_bloody && _worst != null) _profile.CleanGetaway();

            _bloody = false;

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

            // Attention back on before anything else. Leaving the player permanently invisible
            // to the police because an incident ended while he happened to be unidentified is
            // the worst thing this file could do.
            LawHold.Ignore(false);

            LawHold.Uncap();
            RestoreDispatch();

            // Written here rather than on a timer: the end of an incident is exactly when the
            // profile has finished moving, and Save is a no-op unless it actually changed.
            if (_cfg.CriminalProfile) _profile.Save();

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

            if (_cfg.CriminalProfile) _profile.Update();

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

                Returned(me, now);
                Adopt(me);
                return;
            }

            var seen = Eyes(me);

            if (seen)
            {
                _lastSeenAt = now;
                _radio.Seen(me.Position);

                _searchFrom = me.Position;
                _searchRadius = 40f;
                _searchStarted = now;

                // NOT ANNOUNCED. "Suspect sighted" is a STATE, and the HUD shows states -- the
                // eye icon is up for exactly as long as it is true, which a ticker line that
                // scrolls away after four seconds never was.
                State = Hunt.Seen;
            }
            else if (State == Hunt.Seen && now - _lastSeenAt > LostAfterMs)
            {
                State = Hunt.Searching;
                _searchStarted = now;

                // Also a state, also on the HUD. The eye becomes a magnifier.
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
            PushAnonymity();
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

            // Fully described. Whatever handed out these stars, the engine is already tracking
            // the player personally -- adopting it as an ANONYMOUS crime would tell the mod
            // nobody knows who he is while the game visibly does, and the two would fight.
            Report(what, me.Position, Known.Face | Known.Clothes | Known.Vehicle);
        }

        /// <summary>
        /// Came back to where you did it, while anybody still cared.
        ///
        /// Reported WITHOUT a description regardless of what they had before, because this is
        /// not a sighting -- it is the location itself being of interest again. If somebody
        /// there can actually see you, Eyes() will pick that up on the next tick and put the
        /// description back where it belongs.
        /// </summary>
        private void Returned(Ped me, int now)
        {
            if (_sceneCold == 0 || now > _sceneCold) { _sceneCold = 0; return; }

            try
            {
                if (me.Position.DistanceTo(_sceneAt) > SceneRange) return;
            }
            catch
            {
                return;
            }

            // Forgotten BEFORE the report, or the report re-enters this on the next tick with
            // the player still standing on the same spot and it fires forever.
            _sceneCold = 0;

            Log.Info("Player returned to the scene of " + _sceneWhat + ".");

            if (Say != null) Say("Dispatch: units still on scene.");

            Report(_sceneWhat, _sceneAt);
        }

        // ---- sight -------------------------------------------------------------

        /// <summary>
        /// Whether any officer can see the player AND has reason to think it is him.
        ///
        /// THIS IS WHERE THE FLAGS EARN THEIR KEEP. A clear line of sight at sixty metres to a
        /// man who answers nothing on the call is an officer looking straight at somebody he
        /// has no reason to stop -- so it does not count, and the search carries on around him.
        ///
        /// The old version had a close-range override: recognised anyway inside seven metres,
        /// on the reasoning that a jacket is not a disguise at four metres. That was papering
        /// over the single-bit model. It is gone, and the face flag replaces it properly -- if
        /// they got a look at your face and it is uncovered, range never mattered; if all they
        /// ever had was a shirt you have since changed, then standing next to an officer is
        /// standing next to an officer, because there is nothing left for him to match you
        /// against. Which is the correct and much more interesting answer.
        ///
        /// A sighting also UPGRADES the description. He is looking right at you, so he gets
        /// your face if it is showing, what you are wearing, and what you are in.
        /// </summary>
        private bool Eyes(Ped me)
        {
            var loose = !_cfg.DescriptionMatters;

            foreach (var officer in Cops.Near(me.Position, EyesOn))
            {
                if (!Cops.Sees(officer, me, EyesOn)) continue;

                if (!loose && !_radio.Recognises(me)) continue;

                // Everything he can see, and then a refresh of what they already had -- he is
                // watching, so the call is current by definition. Without the refresh, changing
                // cars in front of an officer beats a description he is looking at.
                _radio.Note(me, Known.Face | Known.Clothes | Known.Vehicle);
                _radio.Refresh(me);

                return true;
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

            // A known quantity gets one more star of room on anything already serious. Never on
            // a two-star offence -- being violent last week does not make speeding worse.
            if (_cfg.CriminalProfile && _profile.Hardened && ceiling >= 3 && ceiling < _cfg.MaxStars)
            {
                ceiling++;
            }

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
        /// Whether the engine's own officers are allowed to come after the player personally.
        ///
        /// This is what makes an unidentified crime actually feel unidentified. Without it the
        /// mod holds a coherent belief -- nobody described him -- while the game's officers walk
        /// up and open fire, because to the engine a wanted level simply means "attack". The
        /// wanted CENTRE still sits on the crime scene either way, so they keep converging on
        /// the street; they just have no reason to look twice at the man walking down it.
        ///
        /// Flips back the instant somebody identifies him, which is exactly the moment it
        /// should: shooting again in front of an officer is a description.
        /// </summary>
        private void PushAnonymity()
        {
            LawHold.Ignore(_cfg.DescriptionMatters && _radio.Unidentified);
        }

        /// <summary>
        /// What the crime justifies, told to the one place that owns the engine's police.
        ///
        /// THIS USED TO PUSH THE NATIVES ITSELF and that was the bug the whole mod was losing
        /// to. It gated services 2, 4, 12 and 14 -- the helicopters, SWAT and the army -- and
        /// left service 1, the engine's ordinary police car dispatch, fully switched on. So
        /// the moment the player had a star the game began creating squad cars behind him for
        /// the purpose, while the Fleet was carefully reassigning one that had been three
        /// streets away. Both systems responding, neither aware of the other, and the entire
        /// argument of this mod invisible underneath the result.
        ///
        /// AmbientCops now owns every police dispatch service, and all this does is say what
        /// the current incident is worth. One system, one native, one place to look.
        ///
        /// The helicopter and SWAT are still gated by severity, and that is still the thing
        /// that stops a chase over a stolen car ending with a Buzzard the way vanilla does at
        /// four stars regardless of what the four stars were for.
        /// </summary>
        private void PushDispatch()
        {
            Streets.AmbientCops.Allow(_worst != null && _worst.Air,
                                      _worst != null && _worst.Swat);

            // How much of the force this is worth, handed to the Fleet, because with the
            // engine's dispatch off the Fleet is now the ONLY thing that can answer a call.
            // Without this the beat stays at three cars during a homicide.
            if (_fleet != null)
            {
                _fleet.Surge = Surge();
                _fleet.SurgeTo = State == Hunt.Seen ? _searchFrom : _searchFrom;
            }
        }

        /// <summary>
        /// Extra cars the incident is worth, on top of the district's ordinary beat.
        ///
        /// Deliberately gentle at the bottom and steep at the top. One star is a beat car
        /// noticing you and nothing else needs to happen; five is everything there is. The
        /// numbers are small because these are REAL cars that have to drive to you -- six
        /// units converging from across a district is a great deal more presence than six
        /// spawned behind you, and it takes longer to arrive, which is the point.
        /// </summary>
        private int Surge()
        {
            switch (_pushedStars)
            {
                case 0:
                case 1: return 0;
                case 2: return 1;
                case 3: return 2;
                case 4: return 4;
                default: return 6;
            }
        }

        /// <summary>Stands the surge down. On clearing, and on teardown.</summary>
        public void RestoreDispatch()
        {
            Streets.AmbientCops.Allow(false, false);

            if (_fleet != null) _fleet.Surge = 0;
        }
    }
}
