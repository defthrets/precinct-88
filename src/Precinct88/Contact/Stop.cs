using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Response;
using Precinct88.Streets;
using Precinct88.UI;

namespace Precinct88.Contact
{
    /// <summary>Why they stopped you, which decides how the scene opens.</summary>
    internal enum Why
    {
        /// <summary>A gun in your hand on a street they were driving down.</summary>
        Weapon,

        /// <summary>How you were driving.</summary>
        Driving,

        /// <summary>The car. Plate came back, or it is obviously not yours.</summary>
        Plate,

        /// <summary>You, stood where you are stood, in a district that notices.</summary>
        Suspicion,
    }

    internal enum Beat
    {
        None,

        /// <summary>Coming over. On foot they walk; in traffic they get behind you first.</summary>
        Pulling,

        /// <summary>Waiting for you to stop the car. Traffic stops only.</summary>
        WaitingForYou,

        /// <summary>Out of the car and walking to you.</summary>
        Approaching,

        /// <summary>Stood in front of you, saying the thing.</summary>
        Talking,

        /// <summary>Going through your pockets.</summary>
        Searching,

        /// <summary>Over. Either they found nothing or somebody else is dealing with it now.</summary>
        Done,
    }

    /// <summary>
    /// Being stopped, and the four minutes of a police mod that most people will actually see.
    ///
    /// A STOP IS NOT A COMBAT ENCOUNTER AND MUST NOT BE ALLOWED TO BECOME ONE BY ACCIDENT. That
    /// is the entire difficulty. The game's default answer to an officer near a player with a
    /// gun is a firefight, so every part of this scene is arranged to keep it from tipping over
    /// -- the wanted level is capped, the officer's own combat is held off, and the player is
    /// left in control of his character the whole way through.
    ///
    /// LEAVING IS ALLOWED, and it is the reason the scene works. You can walk off mid-search.
    /// You can drive off when they are stood at your window. Nothing here traps you. What
    /// happens instead is that the thing you were being stopped for stops being the thing you
    /// are wanted for, and Manhunt is told so -- which is exactly what running from a stop
    /// costs in life and exactly what makes staying a real decision rather than a cutscene you
    /// are waiting out.
    ///
    /// The search itself asks whoever is on the bridge what you are carrying. With nothing
    /// listening it is weapons and cash, which is a thinner scene but not a broken one.
    /// </summary>
    internal sealed class Stop
    {
        private const int TickMs = 120;

        /// <summary>Close enough to be talked to.</summary>
        private const float TalkRange = 2.6f;

        /// <summary>How long the search takes once he is stood over you.</summary>
        private const int SearchMs = 5200;

        /// <summary>He gives up walking after this. You outran him, or he cannot reach you.</summary>
        private const int PatienceMs = 26000;

        /// <summary>How long a traffic stop waits for you to actually stop.</summary>
        private const int PullOverMs = 18000;

        /// <summary>Slow enough to count as stopped.</summary>
        private const float StoppedSpeed = 1.6f;

        /// <summary>Walk off further than this and the stop is over, badly.</summary>
        private const float WalkedOff = 14f;

        private readonly Settings _cfg;
        private readonly Manhunt _hunt;
        private readonly Random _rng = new Random();

        private Unit _unit;
        private Ped _officer;
        private Beat _at = Beat.None;
        private Why _why;

        private int _startedAt;
        private int _phaseAt;
        private int _lastTick;
        private int _searchDone;
        private bool _handsUp;

        /// <summary>When any stop may next begin. One clock for the whole force.</summary>
        private int _nextAllowed;

        /// <summary>
        /// What the player is carrying, asked of whoever is listening on the bridge.
        ///
        /// Returns a line to show, or empty for nothing. The handler is expected to have
        /// ALREADY TAKEN IT by the time it returns -- this is a "seize and tell me what you
        /// seized", not a query. Made that way round because the alternative is two calls with
        /// a window between them in which the player can walk off having been told he was
        /// robbed and not actually having been.
        /// </summary>
        public Func<string, string> Seize;

        /// <summary>Hand the player to Custody. Set by Main.</summary>
        public Action<string> Book;

        public Stop(Settings cfg, Manhunt hunt)
        {
            _cfg = cfg;
            _hunt = hunt;
        }

        public bool Running => _at != Beat.None && _at != Beat.Done;

        /// <summary>The unit tied up in this, so nothing else re-tasks it.</summary>
        public Unit Busy => Running ? _unit : null;

        // ---- starting ----------------------------------------------------------

        /// <summary>Whether a stop could begin at all right now.</summary>
        public bool Possible()
        {
            if (!_cfg.ContactEnabled) return false;
            if (Running) return false;
            if (LawHold.Held) return false;
            if (Game.GameTime < _nextAllowed) return false;

            // NOT WHILE A MANHUNT IS ON. A stop is a conversation, and there is nothing to
            // talk about with a man they are already chasing -- at that point the game's own
            // pursuit behaviour is correct and this scene would be standing in front of it.
            return !_hunt.Running;
        }

        /// <summary>Begins one. Returns false when the unit could not take it.</summary>
        public bool Begin(Unit unit, Why why)
        {
            if (!Possible() || unit == null || !unit.Alive) return false;

            _unit = unit;
            _why = why;
            _officer = null;
            _handsUp = false;
            _startedAt = Game.GameTime;
            _phaseAt = _startedAt;

            unit.HandOver();

            var me = Game.Player.Character;
            var inCar = me != null && me.Exists() && me.IsInVehicle();

            _at = inCar ? Beat.Pulling : Beat.Approaching;

            if (inCar)
            {
                unit.Lights(true);
                Cops.Megaphone(unit.Driver, "COP_ARREST_PLAYER");
                Screen.Ticker("Pull over.");
            }
            else
            {
                unit.Lights(false);
                Cops.Megaphone(unit.Driver, Line(why));
            }

            // CAPPED, NOT HELD. A hold would make the player un-arrestable for the length of
            // the scene, which is the opposite of what a stop is. One star is the arrest level
            // in this game -- officers pursue and cuff at one and only draw from two -- so
            // capping here is what keeps a search from turning into a shooting because the
            // engine decided to escalate halfway through.
            LawHold.Cap(1);

            Log.Info("Stop started: " + why + (inCar ? " (traffic)" : " (on foot)") + ".");
            return true;
        }

        private static string Line(Why why)
        {
            switch (why)
            {
                case Why.Weapon: return "COP_ARREST_PLAYER";
                case Why.Driving: return "GENERIC_CURSE_MED";
                case Why.Plate: return "CHASE_SOLO";
                default: return "GENERIC_INSULT_MED";
            }
        }

        // ---- per-tick ----------------------------------------------------------

        public void Update()
        {
            if (!Running) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            var me = Game.Player.Character;

            if (me == null || !me.Exists() || me.IsDead || _unit == null || !_unit.Alive)
            {
                End("nobody left to have it with", false);
                return;
            }

            // Anything louder has taken over -- a shooting, another mod's scene, a hold. The
            // stop is not the important thing any more and should get out of the way rather
            // than keep an officer stood in the middle of it.
            if (LawHold.Held || _hunt.Running && _at != Beat.Searching)
            {
                End("something bigger happened", false);
                return;
            }

            switch (_at)
            {
                case Beat.Pulling: Pulling(me, now); break;
                case Beat.WaitingForYou: Waiting(me, now); break;
                case Beat.Approaching: Approaching(me, now); break;
                case Beat.Talking: Talking(me, now); break;
                case Beat.Searching: Searching(me, now); break;
            }
        }

        /// <summary>Getting behind the car, so the player knows who is being pulled over.</summary>
        private void Pulling(Ped me, int now)
        {
            var car = me.CurrentVehicle;

            if (!Cops.Alive(car) || car.Driver == null || car.Driver.Handle != me.Handle)
            {
                // Got out on his own, which is compliance of a sort.
                Go(Beat.Approaching, now);
                return;
            }

            try
            {
                Function.Call(Hash.TASK_VEHICLE_CHASE, _unit.Driver.Handle, me.Handle);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not follow for the stop: " + ex.Message);
            }

            Go(Beat.WaitingForYou, now);
        }

        private void Waiting(Ped me, int now)
        {
            Screen.Help("Pull over and stop the vehicle.");

            var car = me.CurrentVehicle;
            var stopped = !me.IsInVehicle() || !Cops.Alive(car) || car.Speed < StoppedSpeed;

            if (stopped)
            {
                try
                {
                    if (Cops.Alive(_unit.Driver)) _unit.Driver.Task.ClearAll();
                }
                catch { /* it is about to be re-tasked */ }

                Go(Beat.Approaching, now);
                return;
            }

            if (now - _phaseAt > PullOverMs)
            {
                // Did not stop. This is the branch that makes a traffic stop matter -- what
                // was a two-star ticket is now failing to stop for one, and the pursuit that
                // follows is the game's own, which is exactly right for it.
                Ran(me, "failing to stop");
            }
        }

        private void Approaching(Ped me, int now)
        {
            if (!Cops.Alive(_officer))
            {
                _officer = _unit.Driver;

                if (!Cops.Alive(_officer)) { End("nobody got out", false); return; }

                Safe(_officer);

                try
                {
                    if (_officer.IsInVehicle())
                    {
                        Function.Call(Hash.TASK_LEAVE_VEHICLE, _officer.Handle,
                                      _unit.Car.Handle, 0);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not get an officer out: " + ex.Message);
                }
            }

            var gap = _officer.Position.DistanceTo(me.Position);

            if (gap < TalkRange)
            {
                Go(Beat.Talking, now);
                return;
            }

            if (now - _phaseAt > PatienceMs)
            {
                End("he could not get to you", true);
                return;
            }

            try
            {
                if (!_officer.IsInVehicle())
                {
                    Function.Call(Hash.TASK_GO_TO_ENTITY, _officer.Handle, me.Handle,
                                  -1, TalkRange * 0.8f, 1.8f, 0f, 0);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not walk an officer over: " + ex.Message);
            }
        }

        private void Talking(Ped me, int now)
        {
            Face(me);

            if (WalkedAway(me)) { Ran(me, "walking away from a stop"); return; }

            // The offer, and it is a real one. Holding the key is the only thing in this scene
            // the player has to do, and not doing it is a choice with a consequence rather
            // than a fail state.
            Screen.Help("Hold ~INPUT_CONTEXT~ to comply.   Walk away to refuse.");

            if (now - _phaseAt < 1200) return;

            if (!_handsUp)
            {
                Cops.Say(_officer, "COP_ARREST_PLAYER");
                Screen.Said(Said(_why));
                _handsUp = true;
            }

            if (Game.IsControlPressed(GTA.Control.Context))
            {
                Anim.Play(me, Anim.HandsUpDict, Anim.HandsUpClip, 49);

                _searchDone = now + SearchMs;
                Go(Beat.Searching, now);
            }

            // Standing there doing nothing is not refusal. Eventually he gets bored, which is
            // the correct outcome for a man who genuinely has nothing on him and knows it.
            if (now - _phaseAt > PatienceMs) End("he had nothing to hold you on", true);
        }

        private static string Said(Why why)
        {
            switch (why)
            {
                case Why.Weapon:
                    return "Put it down. Hands where I can see them.";
                case Why.Driving:
                    return "You know how fast you were going?";
                case Why.Plate:
                    return "This your vehicle? Step out.";
                default:
                    return "Hands where I can see them. What are you doing round here?";
            }
        }

        private void Searching(Ped me, int now)
        {
            Face(me);

            if (WalkedAway(me)) { Ran(me, "walking out of a search"); return; }

            if (!Game.IsControlPressed(GTA.Control.Context))
            {
                // Let go of the key mid-search. Not an escape, just back to the conversation.
                Anim.Stop(me, Anim.HandsUpDict, Anim.HandsUpClip);
                Go(Beat.Talking, now);
                return;
            }

            Screen.Help("Hold ~INPUT_CONTEXT~.   Let go to stop.");

            Anim.Play(_officer, Anim.InspectDict, Anim.InspectClip, 1);

            if (now < _searchDone) return;

            Found(me);
        }

        // ---- what they find ----------------------------------------------------

        private void Found(Ped me)
        {
            Anim.Stop(me, Anim.HandsUpDict, Anim.HandsUpClip);

            var took = string.Empty;

            // Asked, not assumed. Precinct 88 has no idea what a gram is -- whoever is on the
            // bridge does, and it has already taken it by the time this returns.
            if (_cfg.ConfiscateContraband && Seize != null)
            {
                try { took = Seize(_why.ToString()) ?? string.Empty; }
                catch (Exception ex) { Log.Debug("The seizure handler threw: " + ex.Message); }
            }

            var armed = Cops.Armed(me);

            if (!string.IsNullOrEmpty(took) || armed)
            {
                var reason = !string.IsNullOrEmpty(took) ? took : "carrying a weapon";

                Screen.Ticker("Found: " + reason + ".");
                Cops.Say(_officer, "ARREST_PLAYER");

                End("they found something", false);

                if (Book != null) Book(reason);
                return;
            }

            // Nothing. He gets a word and that is the whole of it -- which is the outcome that
            // makes the other one mean anything.
            Screen.Ticker("Nothing on you. Move along.");
            Screen.Said("Alright. On your way.");
            Cops.Say(_officer, "GENERIC_BYE");

            End("they found nothing", true);
        }

        /// <summary>The player left. The stop becomes the thing they left.</summary>
        private void Ran(Ped me, string what)
        {
            Screen.Ticker("Dispatch: suspect is not complying.");

            var worse = _why == Why.Weapon ? Offence.Brandishing
                      : _why == Why.Driving ? Offence.Reckless
                      : _why == Why.Plate ? Offence.StolenVehicle
                      : Offence.Loitering;

            End(what, false);

            // Reported AFTER the scene is torn down, so the officer is free to respond to it
            // rather than still stood in a walk-to-entity task.
            _hunt.Report(worse, me.Position);
        }

        private bool WalkedAway(Ped me)
        {
            try
            {
                return !Cops.Alive(_officer) ||
                       _officer.Position.DistanceTo(me.Position) > WalkedOff;
            }
            catch
            {
                return true;
            }
        }

        private void Face(Ped me)
        {
            try
            {
                if (Cops.Alive(_officer))
                {
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                  _officer.Handle, me.Handle, 1500);
                }
            }
            catch
            {
                // Cosmetic.
            }
        }

        /// <summary>
        /// Stops an officer deciding this is a gunfight.
        ///
        /// An armed player at two metres is, to the game's combat system, a threat -- and it
        /// will act on that in the middle of a conversation the mod is running. These three
        /// flags are what hold it off for the length of the scene, and they are put back in
        /// Release, including when the scene ends badly.
        /// </summary>
        private static void Safe(Ped officer)
        {
            try
            {
                // BlockPermanentEvents is the one that does the work: it stops ambient and
                // combat events firing at all, which is what would otherwise override the
                // walk-over task the moment he registers an armed man in front of him.
                //
                // AlwaysKeepTask is NOT the second half of this and was a mistake here. SHVDN
                // 3.9 deprecates it with a note explaining why -- it only governs what happens
                // once a ped is marked as no longer needed, which is the end of the scene, not
                // the middle of it. It looked like "hold this task", and it never was.
                officer.BlockPermanentEvents = true;

                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, officer.Handle, true);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, officer.Handle, 46, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not settle an officer for a stop: " + ex.Message);
            }
        }

        private static void Loose(Ped officer)
        {
            try
            {
                if (!Cops.Alive(officer)) return;

                officer.BlockPermanentEvents = false;

                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, officer.Handle, 46, true);
            }
            catch
            {
                // Teardown.
            }
        }

        private void Go(Beat next, int now)
        {
            _at = next;
            _phaseAt = now;
        }

        /// <summary>
        /// Ends it and puts everything back.
        ///
        /// `clean` says whether the unit goes back to its beat or is simply released to the
        /// game. A stop that ended in an arrest or a chase leaves the officer where the next
        /// system found him, because that system is now driving.
        /// </summary>
        public void End(string why, bool clean)
        {
            if (_at == Beat.None) return;

            Log.Info("Stop ended: " + why + ".");

            try
            {
                var me = Game.Player.Character;
                if (me != null && me.Exists()) Anim.Stop(me, Anim.HandsUpDict, Anim.HandsUpClip);
            }
            catch { /* teardown */ }

            Loose(_officer);

            if (_unit != null && _unit.Alive)
            {
                _unit.Dark();

                if (clean)
                {
                    try
                    {
                        if (Cops.Alive(_officer) && !_officer.IsInVehicle())
                        {
                            Function.Call(Hash.TASK_ENTER_VEHICLE, _officer.Handle,
                                          _unit.Car.Handle, 20000, -1, 1.5f, 1, 0);
                        }
                    }
                    catch { /* he will be let go with the unit either way */ }

                    // Handed back to the fleet, which gives it somewhere to be on the next
                    // tick. Not routed from here -- Fleet owns where units go.
                    _unit.Doing = Duty.Sitting;
                    _unit.MoveOnAt = Game.GameTime + 4000;
                }
                else
                {
                    _unit.Doing = Duty.Rolling;
                }
            }

            _at = Beat.None;
            _unit = null;
            _officer = null;
            _handsUp = false;

            _nextAllowed = Game.GameTime + (int)(_cfg.StopCooldownSeconds * 1000f);

            LawHold.Uncap();
        }
    }
}
