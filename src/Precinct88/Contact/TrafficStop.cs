using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Streets;
using Precinct88.UI;

namespace Precinct88.Contact
{
    /// <summary>Where a traffic stop has got to.</summary>
    internal enum Pull
    {
        /// <summary>Nothing running.</summary>
        None,

        /// <summary>Behind you with the lights on, waiting for you to stop.</summary>
        Waiting,

        /// <summary>You stopped. He is parking behind and getting out.</summary>
        Parking,

        /// <summary>Walking to your window.</summary>
        Approaching,

        /// <summary>Stood at the window. This is where it is decided.</summary>
        Talking,

        /// <summary>Done with you. Walking back to his car.</summary>
        Leaving,
    }

    /// <summary>
    /// Being pulled over.
    ///
    /// A CAUSE, NOT A CRIME, AND THAT DISTINCTION IS THE WHOLE FILE. Shooting somebody is a
    /// crime: it is reported, somebody is sent, and there is nothing to discuss. Doing fifty
    /// through Vespucci is a CAUSE -- it gets you stopped, and what happens next depends
    /// entirely on what you do about it. Vanilla collapses the two, which is why driving badly
    /// in GTA V produces a firefight.
    ///
    /// Keeping them apart is most of why a stop here is a scene rather than the opening of a
    /// pursuit, and it is why running from one is interesting: that is the exact moment a cause
    /// turns into a crime, with an officer already stood at your window.
    ///
    /// HE FOLLOWS, HE DOES NOT RAM. TASK_VEHICLE_CHASE with the pursuit behaviours left alone
    /// is a car that keeps station behind you; the ramming and boxing-in that vanilla police do
    /// belongs to a pursuit, and a pursuit is what you get by not stopping, not by being asked.
    ///
    /// THE UNIT IS BORROWED FROM PATROL, not created. Unit.HandOver takes it out of Fleet's
    /// steering entirely for the duration -- Fleet will not touch a unit in Duty.Contact -- and
    /// End gives it back. If the scene dies for any reason the unit must still be handed back,
    /// which is why every exit routes through End and why End cannot throw.
    /// </summary>
    internal sealed class TrafficStop
    {
        private const int TickMs = 400;

        /// <summary>How far an officer can be and still take an interest.</summary>
        private const float NoticeRange = 46f;

        /// <summary>Under this you have stopped, whatever you think you are doing.</summary>
        private const float StoppedSpeed = 1.6f;

        /// <summary>How long you get to pull over before it becomes failing to stop.</summary>
        private const int PullOverMs = 22000;

        /// <summary>How long he will keep trying to reach your window before giving up.</summary>
        private const int PatienceMs = 30000;

        /// <summary>How long he stands there before saying what is happening.</summary>
        private const int TalkMs = 5200;

        /// <summary>And how long you have to stay put once he is out of his car.</summary>
        private const int BoltMs = 1400;

        /// <summary>Where the driver's window is, relative to the car.</summary>
        private static readonly Vector3 WindowOffset = new Vector3(-1.35f, 0.15f, 0f);

        /// <summary>Close enough to be talking through it.</summary>
        private const float AtTheWindow = 1.7f;

        /// <summary>And how far behind you he parks.</summary>
        private static readonly Vector3 BehindOffset = new Vector3(0f, -6.5f, 0f);

        /// <summary>How long the siren whoops before it is lights only.</summary>
        private const int WhoopMs = 2600;

        /// <summary>
        /// How often an officer who can see you makes up his mind.
        ///
        /// THE TICK RATE IS NOT THE DECISION RATE, and conflating them is a trap this very
        /// nearly walked into. The scene has to be responsive once it is running, so it ticks
        /// every 400ms -- but rolling the "does he mind?" dice at 400ms means two and a half
        /// rolls a second, and any chance worth having becomes a certainty within a second of
        /// a police car appearing. Every stop would fire instantly, everywhere, and the
        /// district weighting would be invisible underneath it.
        /// </summary>
        private const int ConsiderGapMs = 2500;

        private readonly Settings _cfg;
        private readonly Fleet _fleet;
        private readonly Violations _violations;
        private readonly Random _rng = new Random();

        private int _lastTick;
        private int _lastLook;
        private int _phaseAt;
        private int _quietAt;
        private int _overAt;

        private Unit _unit;
        private Ped _officer;
        private Violation _why;

        private Pull _at = Pull.None;

        /// <summary>Whether he has asked yet, and what you said. Reset by Go.</summary>
        private bool _asked;
        private bool _admitted;

        // NO `Occupied` HOOK YET, and its absence is deliberate rather than an oversight.
        // The settings panel is what would set one -- nothing else in this build takes the
        // controls -- and the panel is parked. A public hook nothing can reach is a warning at
        // build time and a lie about what this file does. It comes back with the panel, and
        // the two places that want it are marked below.

        /// <summary>Puts a line up. Wired to Screen.Ticker by Main.</summary>
        public Action<string> Say;

        public TrafficStop(Settings cfg, Fleet fleet, Violations violations)
        {
            _cfg = cfg;
            _fleet = fleet;
            _violations = violations;
        }

        /// <summary>Whether a stop is running. Fleet and Notice both ask.</summary>
        public bool Running => _at != Pull.None;

        // ---- per-tick ----------------------------------------------------------

        public void Update()
        {
            if (!_cfg.ContactEnabled || !_cfg.TrafficStops)
            {
                if (Running) End("switched off", false);
                return;
            }

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                if (me.IsDead)
                {
                    if (Running) End("he is dead", false);
                    return;
                }

                if (!Running)
                {
                    Consider(me, now);
                    return;
                }

                // The unit died, was let go, or the officer was killed. Whatever the reason,
                // there is nobody left to be stopped by.
                if (_unit == null || !_unit.Alive)
                {
                    End("the unit is gone", false);
                    return;
                }

                switch (_at)
                {
                    case Pull.Waiting: Waiting(me, now); break;
                    case Pull.Parking: Parking(me, now); break;
                    case Pull.Approaching: Approaching(me, now); break;
                    case Pull.Talking: Talking(me, now); break;
                    case Pull.Leaving: Leaving(me, now); break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("The traffic stop went wrong.", ex);
                End("something went wrong", false);
            }
        }

        // ---- deciding ----------------------------------------------------------

        /// <summary>
        /// Whether anybody minds how you are driving.
        ///
        /// THE OFFICER HAS TO BE ABLE TO SEE IT. Not a radius, not the nearest unit -- a
        /// particular car with a particular crew who had line of sight to you while you were
        /// doing it. That is the same rule Notice uses for crime and it is the mod's whole
        /// argument applied here: drive badly on an empty road and nothing happens.
        /// </summary>
        private void Consider(Ped me, int now)
        {
            if (now < _overAt) return;
            if (now - _lastLook < ConsiderGapMs) return;
            _lastLook = now;

            if (Response.LawHold.Held) return;

            // (Occupied goes here, when the panel returns: no stop may start behind it.)

            if (!me.IsInVehicle()) return;

            var car = me.CurrentVehicle;
            if (!Cops.Alive(car)) return;
            if (car.Driver == null || car.Driver.Handle != me.Handle) return;

            if (!Enforced(car)) return;

            var worst = Violations.Worst(_violations.Live);
            if (worst == null) return;

            var unit = Watching(me);
            if (unit == null) return;

            if (!Minded(worst.Value, me)) return;

            Begin(unit, worst.Value, me, now);
        }

        /// <summary>
        /// Whether this kind of vehicle is stopped at all.
        ///
        /// A cyclist run down for wobbling is not a traffic stop, it is a joke, so bicycles are
        /// off by default and each class has its own switch.
        /// </summary>
        private bool Enforced(Vehicle car)
        {
            try
            {
                if (Function.Call<bool>(Hash.IS_THIS_MODEL_A_BICYCLE, car.Model.Hash))
                {
                    return _cfg.EnforceBicycles;
                }

                if (Function.Call<bool>(Hash.IS_THIS_MODEL_A_BIKE, car.Model.Hash))
                {
                    return _cfg.EnforceBikes;
                }

                return _cfg.EnforceCars;
            }
            catch
            {
                return _cfg.EnforceCars;
            }
        }

        /// <summary>The nearest unit that is free, near, and actually looking at you.</summary>
        private Unit Watching(Ped me)
        {
            Unit best = null;
            var bestDist = NoticeRange;

            foreach (var unit in _fleet.Units)
            {
                if (!unit.Alive) continue;
                if (unit.Doing != Duty.Rolling && unit.Doing != Duty.Sitting) continue;

                var d = unit.Car.Position.DistanceTo(me.Position);
                if (d >= bestDist) continue;

                if (!Cops.Sees(unit.Driver, me, NoticeRange)) continue;

                bestDist = d;
                best = unit;
            }

            return best;
        }

        /// <summary>
        /// Whether this particular crew can be bothered.
        ///
        /// The district is the dial, exactly as it is for Notice. The same man doing the same
        /// fifty gets stopped in Rockford Hills and waved through in Davis, and the crew's own
        /// Interest -- rolled once when they came on duty and kept -- is what stops every car
        /// in the city behaving identically.
        /// </summary>
        private bool Minded(Violation what, Ped me)
        {
            try
            {
                var here = Districts.At(me.Position);
                var weight = Violations.Weight(what);

                // Per look rather than per tick -- see ConsiderGapMs. Weight 1 is a nothing
                // and weight 3 is a red light, scaled by where you are: about a one in six
                // chance every two and a half seconds for speeding through Mission Row, a
                // quarter for jumping a light in Rockford Hills, and appreciably less than
                // either in Davis, where nobody cares.
                var chance = 10f * weight * (0.35f + 0.9f * here.Attention);

                return _rng.Next(100) < chance;
            }
            catch
            {
                return false;
            }
        }

        // ---- the scene ---------------------------------------------------------

        private void Begin(Unit unit, Violation what, Ped me, int now)
        {
            _unit = unit;
            _why = what;
            _officer = null;

            // OUT OF FLEET'S HANDS ENTIRELY. Fleet will not steer a unit in Duty.Contact, so
            // from here until End nothing else gives this car an order.
            unit.HandOver();

            try
            {
                unit.Light(Lamps.Urgent);
                _quietAt = now + WhoopMs;

                Function.Call(Hash.TASK_VEHICLE_CHASE, unit.Driver.Handle, me.Handle);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not start the follow: " + ex.Message);
            }

            Go(Pull.Waiting, now);

            Log.Info("Traffic stop started for " + Violations.Called(what) + ".");
            if (Say != null) Say("Being pulled over: " + Violations.Called(what) + ".");
        }

        /// <summary>Following you, asking. This is the part you can refuse.</summary>
        private void Waiting(Ped me, int now)
        {
            // Down to lights once the whoop has done its job. A siren that keeps going for the
            // whole stop is the thing every police mod gets wrong.
            if (_quietAt != 0 && now > _quietAt)
            {
                _unit.Light(Lamps.Watching);
                _quietAt = 0;
            }

            // (Occupied guards this when the panel returns -- a prompt drawn over an open
            // panel tells the player to do something the panel is swallowing.)
            Screen.Help("Pull over and stop.");

            var car = me.IsInVehicle() ? me.CurrentVehicle : null;
            var stopped = car == null || !Cops.Alive(car) || car.Speed < StoppedSpeed;

            if (stopped)
            {
                Go(Pull.Parking, now);
                return;
            }

            if (now - _phaseAt > PullOverMs) Ran(me, "failing to stop");
        }

        /// <summary>
        /// Pulling in behind you.
        ///
        /// A POINT BEHIND YOUR CAR, NOT "NEAR YOU". A unit that simply stops where the chase
        /// task left it ends up alongside, across the bonnet, or in the oncoming lane, and the
        /// officer then walks round the front of his own car to reach you. Six and a half
        /// metres back on your own heading is where a police car actually stops.
        /// </summary>
        private void Parking(Ped me, int now)
        {
            var car = me.IsInVehicle() ? me.CurrentVehicle : null;

            // Out of the car already, so there is nothing to park behind.
            if (car == null || !Cops.Alive(car))
            {
                Go(Pull.Approaching, now);
                return;
            }

            if (now == _phaseAt || now - _phaseAt < TickMs)
            {
                try
                {
                    var behind = car.GetOffsetPosition(BehindOffset);

                    Function.Call(Hash.TASK_VEHICLE_PARK, _unit.Driver.Handle,
                                  _unit.Car.Handle, behind.X, behind.Y, behind.Z,
                                  car.Heading, 0, 20f, false);
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not park behind: " + ex.Message);
                }
            }

            var gap = _unit.Car.Position.DistanceTo(car.Position);

            // Close enough, or stopped trying. Either way he gets out here.
            if (gap < 11f && _unit.Car.Speed < StoppedSpeed) { Go(Pull.Approaching, now); return; }

            if (now - _phaseAt > 9000) Go(Pull.Approaching, now);
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

            // Driving off with an officer stood in the road is failing to stop, and it is the
            // most satisfying moment in the whole scene to have work properly.
            if (Bolted(me, now)) return;

            // WHERE HE WALKS TO, and it is not simply "the player". A man still in his car is
            // approached at the driver's window; a man on the pavement is approached directly.
            // Walking to the player's own position while he is in a car aims the officer at the
            // middle of the vehicle, which reads as trying to walk through the door.
            var window = Window(me);
            var target = window ?? me.Position;

            if (_officer.Position.DistanceTo(target) < (window.HasValue ? AtTheWindow : 2.2f))
            {
                Go(Pull.Talking, now);
                return;
            }

            if (now - _phaseAt > PatienceMs) { End("he could not get to you", true); return; }

            try
            {
                if (_officer.IsInVehicle()) return;

                if (window.HasValue)
                {
                    // To a POINT rather than to an entity, because the point is beside the car
                    // and the entity is inside it.
                    Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, _officer.Handle,
                                  window.Value.X, window.Value.Y, window.Value.Z,
                                  1.6f, -1, AtTheWindow * 0.6f, 0, 0f);
                }
                else
                {
                    Function.Call(Hash.TASK_GO_TO_ENTITY, _officer.Handle, me.Handle,
                                  -1, 1.8f, 1.8f, 0f, 0);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not walk an officer over: " + ex.Message);
            }
        }

        /// <summary>
        /// At the window, and the only place the outcome is decided.
        ///
        /// A WARNING IS AN OUTCOME, NOT A FAILURE. If every stop ends in a fine then a stop is
        /// a toll booth and the only interesting decision -- whether to run -- has one obvious
        /// answer. Being let off for something minor is what makes stopping worth doing.
        /// </summary>
        private void Talking(Ped me, int now)
        {
            if (Bolted(me, now)) return;

            try
            {
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, _officer.Handle, me.Handle, 1500);
                Function.Call(Hash.TASK_LOOK_AT_ENTITY, _officer.Handle, me.Handle, TalkMs, 0, 2);
            }
            catch
            {
                // He is stood there either way.
            }

            // HE SPEAKS FIRST, AND HE ASKS. The old version stood him at the window for
            // five seconds and then put the verdict in the corner of the screen, which made
            // the officer scenery and the notification the mod. The whole reason he walks over
            // is that this happens between two people.
            if (!_asked)
            {
                if (now - _phaseAt < 900) return;

                _asked = true;

                Cops.Say(_officer, "GENERIC_HI");

                Dialogue.Ask("Officer",
                             "Do you know why I stopped you? " + Opening() ,
                             "Admit it", "Deny it",
                             said => _admitted = said);

                return;
            }

            // Still waiting on you. The question times itself out, so this cannot hang.
            if (Dialogue.Waiting) return;

            var weight = Violations.Weight(_why);

            // Weight 1 is almost always a word. Weight 3 almost never is. The crew's own temper
            // moves it either way, so two officers on the same offence are not the same stop.
            var letOff = weight <= 1 ? 70 : weight == 2 ? 34 : 12;

            if (_unit.Temper == Temper.Lenient) letOff += 22;
            else if (_unit.Temper == Temper.Strict) letOff -= 18;

            // WHAT YOU SAID COUNTS FOR SOMETHING, AND NOT VERY MUCH. Admitting it helps a
            // little, because officers would rather not argue; it does not help enough to make
            // the question a lever you pull for a free pass, which would make asking it
            // pointless. Saying nothing is read as denying it -- see Dialogue.Ask.
            letOff += _admitted ? 12 : -6;

            if (_rng.Next(100) < letOff)
            {
                Dialogue.Say("Officer", Warned());
                if (Say != null) Say("Let off with a warning: " + Violations.Called(_why) + ".");
                Log.Info("Warning given for " + Violations.Called(_why) + ".");
            }
            else
            {
                Fine(weight);
            }

            Go(Pull.Leaving, now);
        }

        /// <summary>
        /// What he opens with, which is the offence in his own words.
        ///
        /// Violations.Called already phrases every offence as something a person would say out
        /// loud -- "a burnout in a public street" rather than BURNOUT -- which is exactly what
        /// this needs and the reason it is not re-written here.
        /// </summary>
        private string Opening()
        {
            switch (_unit.Temper)
            {
                case Temper.Strict:
                    return "That was " + Violations.Called(_why) + ".";

                case Temper.Lenient:
                    return "Looked a lot like " + Violations.Called(_why) + " to me.";

                default:
                    return "I clocked " + Violations.Called(_why) + ".";
            }
        }

        /// <summary>And what he says when he decides not to bother.</summary>
        private string Warned()
        {
            switch (_unit.Temper)
            {
                case Temper.Strict:
                    return "I am writing nothing this time. Do not give me a reason to.";

                case Temper.Lenient:
                    return "Go on, get out of here. Slow down.";

                default:
                    return "Consider this a warning. On your way.";
            }
        }

        /// <summary>
        /// Taken there and then.
        ///
        /// STRAIGHT OFF THE PLAYER RATHER THAN ONTO A LEDGER. Tickets, interest, and settling
        /// up at a station desk are written and parked; a debt nothing can be paid against
        /// would be a number that never mattered. Money now is the honest small version.
        /// </summary>
        private void Fine(int weight)
        {
            var amount = 90 * weight + _rng.Next(60);

            if (_unit.Temper == Temper.Strict) amount = (int)(amount * 1.35f);
            else if (_unit.Temper == Temper.Lenient) amount = (int)(amount * 0.8f);

            try
            {
                Game.Player.Money = Math.Max(0, Game.Player.Money - amount);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not take the fine: " + ex.Message);
            }

            Dialogue.Say("Officer", "That is a " + amount + " dollar ticket for " +
                                    Violations.Called(_why) + ". Sign here.");

            if (Say != null)
            {
                Say("Ticketed $" + amount + " for " + Violations.Called(_why) + ".");
            }

            Log.Info("Fined $" + amount + " for " + Violations.Called(_why) + ".");
        }

        /// <summary>Back to his car, and the unit back to patrol once he is in it.</summary>
        private void Leaving(Ped me, int now)
        {
            if (!Cops.Alive(_officer)) { End("done", true); return; }

            try
            {
                if (_officer.IsInVehicle(_unit.Car) || now - _phaseAt > 16000)
                {
                    End("done", true);
                    return;
                }

                if (now - _phaseAt < TickMs)
                {
                    Function.Call(Hash.TASK_ENTER_VEHICLE, _officer.Handle, _unit.Car.Handle,
                                  -1, -1, 1.6f, 1, 0);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send an officer back: " + ex.Message);
                End("done", true);
            }
        }

        // ---- running -----------------------------------------------------------

        /// <summary>
        /// Whether you have driven off with him stood beside you.
        ///
        /// A short grace, because the game reports a speed for a car being nudged by its own
        /// suspension and a stop that ends because you rolled forward a foot is a stop nobody
        /// can complete.
        /// </summary>
        private bool Bolted(Ped me, int now)
        {
            if (now - _phaseAt < BoltMs) return false;
            if (!me.IsInVehicle()) return false;

            var car = me.CurrentVehicle;
            if (!Cops.Alive(car)) return false;

            if (car.Speed < StoppedSpeed + 2.4f) return false;

            Ran(me, "driving off from a stop");
            return true;
        }

        /// <summary>
        /// The moment a cause becomes a crime.
        ///
        /// THIS IS THE POINT OF THE WHOLE FILE. Everything above is a conversation you were
        /// invited to have; this is what happens when you decline it, and it is the one place
        /// in the mod where a wanted level is handed straight to the engine on purpose. An
        /// officer was stood close enough to talk to you -- there is nothing anonymous about
        /// it, nothing to search for, and no argument to be had about what he knows.
        ///
        /// Two stars rather than one. One star is a thing you drive out of in fifteen seconds
        /// and it would make refusing a stop the obviously correct play every time.
        /// </summary>
        private void Ran(Ped me, string what)
        {
            End(what, false);

            try
            {
                // TWO CALLS, NOT ONE. Player.WantedLevel is deprecated in SHVDN 3.9 and the
                // replacement is deliberately in two parts: SetWantedLevel stages the change
                // and ApplyWantedLevelChangeNow commits it. Staging alone looks exactly like
                // nothing happening, which is the worst possible failure here.
                //
                // The false is "do not delay the law response". A stop that is being run from
                // is not the moment to give the player a grace period.
                var wanted = Game.Player.Wanted;

                if (wanted.WantedLevel < 2)
                {
                    wanted.SetWantedLevel(2, false);
                    wanted.ApplyWantedLevelChangeNow(false);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not raise the wanted level: " + ex.Message);
            }

            if (Say != null) Say("Failing to stop.");
            Log.Info("Ran from a traffic stop: " + what + ".");
        }

        // ---- housekeeping ------------------------------------------------------

        private void Go(Pull next, int now)
        {
            _at = next;
            _phaseAt = now;

            // Per phase, not per stop. Talking is the only phase that asks anything, and it is
            // entered exactly once -- but resetting here means a phase that ever gets re-entered
            // cannot silently skip its question.
            _asked = false;
            _admitted = false;
        }

        /// <summary>
        /// Ends it, however it ended, and hands the unit back.
        ///
        /// THE ONLY WAY OUT, and it must never throw. Every failure above routes through here
        /// because a unit left in Duty.Contact is a car Fleet will never steer again -- it sits
        /// in the road with its lights on until the player drives far enough away for the sweep
        /// to delete it, and nothing in the log says why.
        /// </summary>
        public void End(string why, bool quietly)
        {
            if (_at == Pull.None) return;

            var unit = _unit;
            var officer = _officer;

            _at = Pull.None;
            _unit = null;
            _officer = null;
            _quietAt = 0;

            // A gap before anybody else takes an interest, so being let off is not immediately
            // followed by the next car deciding the same thing about the same driving.
            _overAt = Game.GameTime + (int)(Math.Max(1f, _cfg.StopCooldownSeconds) * 1000f);

            // A QUESTION ONLY, NOT WHATEVER IS ON SCREEN. Driving off mid-sentence leaves a
            // question box up with a live callback behind it, waiting on somebody who is now
            // three streets away. But End also runs on the ordinary path, moments after the
            // verdict was put on screen -- so clearing unconditionally would swallow the one
            // line the player most needs to read.
            if (Dialogue.Waiting) Dialogue.Clear();

            try
            {
                if (officer != null && officer.Exists())
                {
                    officer.BlockPermanentEvents = false;
                    Function.Call(Hash.TASK_CLEAR_LOOK_AT, officer.Handle);
                }
            }
            catch
            {
                // He is gone, which is the outcome this was arranging anyway.
            }

            try
            {
                if (unit != null && unit.Alive)
                {
                    unit.Light(Lamps.Dark);

                    // Back on patrol from where he is standing. Fleet gives him somewhere
                    // proper to be on its next pass.
                    unit.BackToWork(unit.Car.Position);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not hand the unit back: " + ex.Message);
            }

            if (!quietly) Log.Info("Traffic stop over: " + why + ".");
        }

        // ---- odds and ends -----------------------------------------------------

        /// <summary>Where the driver's window is, or null if there is not one.</summary>
        private static Vector3? Window(Ped me)
        {
            try
            {
                if (!me.IsInVehicle()) return null;

                var car = me.CurrentVehicle;
                if (!Cops.Alive(car)) return null;

                // Not a bike. There is no window on a motorcycle and the offset puts him in the
                // road beside it, which looks like he has wandered off.
                if (Function.Call<bool>(Hash.IS_THIS_MODEL_A_BIKE, car.Model.Hash)) return null;
                if (Function.Call<bool>(Hash.IS_THIS_MODEL_A_BICYCLE, car.Model.Hash)) return null;

                return car.GetOffsetPosition(WindowOffset);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Stops the officer reacting to anything on his way over.
        ///
        /// BlockPermanentEvents is the one that does the work: it stops ambient and combat
        /// events firing at all, which is what would otherwise override the walk-over the
        /// moment he registers an armed man in front of him. Cleared again in End.
        ///
        /// AlwaysKeepTask is NOT the second half of this. SHVDN 3.9 deprecates it with a note
        /// explaining why -- it only governs what happens once a ped is marked no longer
        /// needed, which is the end of a scene rather than the middle of one.
        /// </summary>
        private static void Safe(Ped officer)
        {
            try
            {
                officer.BlockPermanentEvents = true;

                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, officer.Handle, true);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, officer.Handle, 46, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not settle an officer for a stop: " + ex.Message);
            }
        }
    }
}
