using System;
using GTA;
using GTA.Native;
using Precinct88.Core;
using Precinct88.UI;

namespace Precinct88.Custody
{
    /// <summary>
    /// One star: turned out, relieved of what you were carrying, and let go.
    ///
    /// THE MISSING RUNG. In vanilla there is one outcome to being caught, at every level, and
    /// it is BUSTED: a fade to black, a fee, and a respawn -- the same thing that happens when
    /// you die, so the game's police have exactly one ending wearing two costumes. Getting
    /// stopped for a shove and getting taken down after a bank job resolve identically.
    ///
    /// So one star ends differently now. An officer takes what you are carrying off you and
    /// tells you to move along. You lose the guns and whatever else you had on you, which is a
    /// real cost, and you lose no time at all, which is the point -- it is a bad afternoon
    /// rather than the end of the session. Two stars is where the cell starts.
    ///
    /// THE ENGINE HAS TO BE HELD OFF FOR THIS TO EXIST AT ALL. Vanilla will arrest you at one
    /// star given the chance, and it does not ask. PREVENT_ARREST_STATE_THIS_FRAME is what
    /// stops it, and it has to be called EVERY FRAME while the level is one -- it is a
    /// this-frame flag, so a tick gate of any size lets the bust through in the gaps.
    ///
    /// It deliberately does not put your hands up, cuff you, or take the camera. Those belong
    /// to Booking, which is parked, and half of an arrest is worse than none.
    /// </summary>
    internal sealed class Search
    {
        private const int TickMs = 250;

        /// <summary>Close enough to be turning out your pockets.</summary>
        private const float Reach = 3.6f;

        /// <summary>How long the search takes once he is on you.</summary>
        private const int SearchMs = 4200;

        /// <summary>
        /// Close enough for it to be going on.
        ///
        /// SHUFFLING IS NOT FLEEING, and the first version could not tell the difference. It
        /// measured the gap once a tick and abandoned the whole thing the moment you crossed a
        /// line -- so a step to the side, being nudged by a passing pedestrian, or the officer
        /// himself drifting while he settled was enough to end it. And it ended it into the
        /// worst possible state: still one star, no longer being dealt with, stood next to an
        /// officer with a stun gun.
        /// </summary>
        private const float Leash = 4.8f;

        /// <summary>Past this you are not drifting, you are leaving, and it ends at once.</summary>
        private const float Gone = 12f;

        /// <summary>How long you may be out of reach before he gives up on you.</summary>
        private const int DriftGraceMs = 5000;

        /// <summary>Moving faster than this and you are not being searched, you are leaving.</summary>
        private const float StillSpeed = 2.2f;

        /// <summary>
        /// How long before anybody bothers you again after a SUCCESSFUL search.
        ///
        /// Long, because being turned out twice in twenty seconds by two different officers is
        /// not policing, it is a bug wearing a uniform.
        /// </summary>
        private const int GraceMs = 20000;

        /// <summary>
        /// And after one that did not finish, which is a different thing entirely.
        ///
        /// THE LONG GRACE ON AN ABANDONED SEARCH WAS A TRAP. You still have the star, so the
        /// police still want you -- but no new search could begin for twenty seconds, which
        /// left exactly one thing that could happen in the meantime: get tased, get up, get
        /// tased again, with the one outcome that resolves it locked out. Short enough that
        /// being put down leads straight back into being searched, which is the point.
        /// </summary>
        private const int LapsedMs = 3500;

        private readonly Settings _cfg;

        private int _lastTick;
        private int _overAt;

        /// <summary>
        /// How much of the search has actually happened, in milliseconds.
        ///
        /// AN ACCUMULATOR RATHER THAN A START TIME, and the difference is the whole fix. A
        /// start time counts wall clock, so a search you walked out of and came back to would
        /// finish itself while you were away. This only advances on ticks where he can reach
        /// you, so stepping back genuinely pauses it and stepping in resumes it.
        /// </summary>
        private int _done;
        private int _lastProgressAt;

        /// <summary>When you first went out of reach, and whether he has said anything.</summary>
        private int _driftSince;
        private bool _warned;

        private Ped _officer;

        /// <summary>
        /// Hands the search to whoever registered on the bridge, and says what was taken.
        ///
        /// Null is the ordinary case: it means Hoodrich is not installed, or has not
        /// registered. Guns are ours to take either way -- everything else belongs to whichever
        /// mod put it in the player's pockets.
        /// </summary>
        public Func<string, string> Seize;

        /// <summary>Puts a line up. Wired to Screen.Ticker by Main.</summary>
        public Action<string> Say;

        public Search(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>Whether somebody is being turned out right now.</summary>
        public bool Running => _officer != null;

        /// <summary>
        /// Called EVERY FRAME by Main, and the frame part is load-bearing.
        ///
        /// The arrest block below is a this-frame flag. Everything else in here is on a tick
        /// gate, but that one call cannot be, because the frames it is skipped on are exactly
        /// the frames the engine uses to bust you.
        /// </summary>
        public void Update()
        {
            if (!_cfg.CustodyEnabled || !_cfg.SearchAtOneStar) return;

            try
            {
                var stars = Game.Player.Wanted.WantedLevel;

                // THE WHOLE REASON ONE STAR CAN END ANY OTHER WAY. Every frame, no gate.
                if (stars == 1)
                {
                    Function.Call(Hash.PREVENT_ARREST_STATE_THIS_FRAME, true);
                }

                var now = Game.GameTime;
                if (now - _lastTick < TickMs) return;
                _lastTick = now;

                var me = Game.Player.Character;
                if (me == null || !me.Exists() || me.IsDead)
                {
                    if (Running) Stop("he is dead");
                    return;
                }

                if (Running) { Running_(me, now, stars); return; }

                if (stars != 1) return;
                if (now < _overAt) return;

                Consider(me, now);
            }
            catch (Exception ex)
            {
                Log.Debug("The search went wrong: " + ex.Message);
                Stop("something went wrong");
            }
        }

        /// <summary>
        /// Whether an officer has actually got hold of you.
        ///
        /// NOT A TRIGGER RADIUS. He has to be within arm's length AND you have to have stopped
        /// -- or been put down by a stun gun, which is the ordinary way this happens and the
        /// reason Restraint sets the ground time as long as it does. Sprinting past an officer
        /// at one star is not being searched, it is getting away, and it should stay that way.
        /// </summary>
        private void Consider(Ped me, int now)
        {
            if (me.IsInVehicle()) return;

            var caught = me.IsRagdoll || Function.Call<bool>(Hash.IS_PED_BEING_STUNNED, me.Handle, 0)
                         || me.Velocity.Length() < StillSpeed;

            if (!caught) return;

            var officer = Nearest(me);
            if (officer == null) return;

            _officer = officer;

            _done = 0;
            _lastProgressAt = now;
            _driftSince = 0;
            _warned = false;

            // NOBODY SHOOTS AT SOMEBODY THEY ARE SEARCHING. The engine reads a wanted level as
            // "attack this man" regardless of what this mod thinks is going on, so without
            // this the officer turning out your pockets is stood next to two others deciding
            // whether to tase you. Put back in Stop, on every path.
            Response.LawHold.Ignore(true);

            try
            {
                officer.BlockPermanentEvents = true;

                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, officer.Handle, me.Handle, 1200);
                Function.Call(Hash.TASK_LOOK_AT_ENTITY, officer.Handle, me.Handle, SearchMs, 0, 2);
            }
            catch
            {
                // He is stood there either way.
            }

            Dialogue.Say("Officer", "Hands where I can see them. Anything on you I should " +
                                    "know about?", SearchMs);

            Log.Info("Being searched at one star.");
            if (Say != null) Say("Being searched.");
        }

        private void Running_(Ped me, int now, int stars)
        {
            // It stopped being a search the moment it stopped being one star. Whatever you just
            // did, the officer has bigger problems.
            if (stars != 1) { Stop("it escalated"); return; }

            if (!Cops.Alive(_officer)) { Stop("the officer is gone"); return; }

            var since = now - _lastProgressAt;
            _lastProgressAt = now;

            // Getting in a car is not drifting. It is the one unambiguous statement available.
            if (me.IsInVehicle()) { Stop("he got in a car"); return; }

            var apart = _officer.Position.DistanceTo(me.Position);

            if (apart > Gone) { Stop("he walked off"); return; }

            if (apart > Leash)
            {
                Drifting(me, now);
                return;
            }

            // Back in reach. Whatever that was, it is over.
            _driftSince = 0;
            _warned = false;

            // ONLY COUNTED WHILE HE CAN REACH YOU -- see _done. Capped per tick so a frame
            // hitch, a loading pause or a menu does not hand you a whole search for free.
            _done += since > 400 ? 400 : since;

            if (_done < SearchMs)
            {
                Screen.Help("Being searched. Stay still.");
                Pose(me);
                return;
            }

            Done(me);
        }

        /// <summary>
        /// You have stepped out of reach, and he would rather you did not.
        ///
        /// HE FOLLOWS AND HE ASKS, in that order, and only then does he give up. Ending it the
        /// instant somebody moved was not strictness, it was the scene being unable to cope
        /// with a pavement -- people get bumped, officers settle, and neither of those is a
        /// decision to walk away from a police officer.
        ///
        /// The search does not advance while this is happening. Waiting it out at five metres
        /// is not a way to be searched without being searched.
        /// </summary>
        private void Drifting(Ped me, int now)
        {
            if (_driftSince == 0) _driftSince = now;

            if (!_warned)
            {
                _warned = true;

                Dialogue.Say("Officer", "Stay where you are. I have not finished.", DriftGraceMs);
                Cops.Say(_officer, "GENERIC_CURSE_MED");
            }

            Screen.Help("Stay still.");

            try
            {
                // Walking pace. An officer who breaks into a run has decided you are running,
                // and that is the thing this whole method exists to not assume.
                Function.Call(Hash.TASK_GO_TO_ENTITY, _officer.Handle, me.Handle,
                              DriftGraceMs, Leash * 0.6f, 1.2f, 1073741824f, 0);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not follow a drifting search: " + ex.Message);
            }

            if (now - _driftSince > DriftGraceMs) Stop("he would not stay put");
        }

        /// <summary>
        /// Hands up, and an officer going through your pockets.
        ///
        /// CALLED EVERY TICK ON PURPOSE, and that is safe rather than sloppy: Anim.Play checks
        /// whether the clip is already running and returns without re-issuing it. Re-issuing is
        /// what makes a ped judder on the spot, and holding a pose by asking for it repeatedly
        /// is exactly what every call site here wants to do.
        ///
        /// FLAG 49 ON THE PLAYER, 1 ON THE OFFICER, and the difference is the point. 49 is loop
        /// plus upper-body plus allow player control -- your arms go up and you can still walk,
        /// which means walking away remains YOUR decision and ends the search as "he got away"
        /// rather than being a thing done to you. A full-body lock would be the mod taking the
        /// controller off somebody for four seconds over a one-star offence.
        ///
        /// The officer gets a plain loop because he genuinely is being held in place.
        /// </summary>
        private void Pose(Ped me)
        {
            Anim.Play(me, Anim.HandsUpDict, Anim.HandsUpClip, 49);

            if (Cops.Alive(_officer))
            {
                Anim.Play(_officer, Anim.InspectDict, Anim.InspectClip, 1);
            }
        }

        /// <summary>
        /// Hands down, and the officer stops miming.
        ///
        /// STOP_ANIM_TASK rather than clearing tasks, because clearing a PLAYER's tasks is a
        /// blunt instrument that also cancels whatever they were legitimately doing -- and
        /// leaving the clip running is worse than either: the pose survives the scene and the
        /// player walks around with his hands up until something else happens to re-task him.
        /// </summary>
        private static void Unpose(Ped me, Ped officer)
        {
            if (me != null && me.Exists())
            {
                Anim.Stop(me, Anim.HandsUpDict, Anim.HandsUpClip);
            }

            if (Cops.Alive(officer))
            {
                Anim.Stop(officer, Anim.InspectDict, Anim.InspectClip);
            }
        }

        /// <summary>
        /// What it costs, and then you are free to go.
        ///
        /// THE WANTED LEVEL GOES LAST. Clearing it first would end the one-star state, which is
        /// what the arrest block above is keyed on, and the engine would have a frame in which
        /// to do something else with you before this finished.
        /// </summary>
        private void Done(Ped me)
        {
            var took = string.Empty;

            try
            {
                if (Cops.Armed(me))
                {
                    me.Weapons.RemoveAll();
                    took = "your weapons";
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not take the weapons: " + ex.Message);
            }

            // WHATEVER ELSE YOU WERE CARRYING, which this mod knows nothing about. Hoodrich
            // answers with what it took; anything else on the bridge answers for itself.
            var also = Hand("searched at one star");

            if (!string.IsNullOrEmpty(also))
            {
                took = string.IsNullOrEmpty(took) ? also : took + " and " + also;
            }

            try
            {
                var wanted = Game.Player.Wanted;

                wanted.SetWantedLevel(0, false);
                wanted.ApplyWantedLevelChangeNow(false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not let him go: " + ex.Message);
            }

            Dialogue.Say("Officer", string.IsNullOrEmpty(took)
                ? "You are clean. Go on, move along."
                : "I am taking " + took + ". Count yourself lucky.");

            if (Say != null)
            {
                Say(string.IsNullOrEmpty(took)
                    ? "Searched and sent on your way."
                    : "Searched. They took " + took + ".");
            }

            Log.Info("Search done. Took: " +
                     (string.IsNullOrEmpty(took) ? "nothing" : took) + ".");

            Stop("done", GraceMs);
        }

        /// <summary>Asks the bridge what else was on him. Nothing thrown may leave it.</summary>
        private string Hand(string why)
        {
            var handler = Seize;
            if (handler == null) return string.Empty;

            try
            {
                return handler(why) ?? string.Empty;
            }
            catch (Exception ex)
            {
                Log.Debug("The seizure handler threw: " + ex.Message);
                return string.Empty;
            }
        }

        private Ped Nearest(Ped me)
        {
            try
            {
                foreach (var ped in World.GetNearbyPeds(me, Reach))
                {
                    if (!Cops.Alive(ped)) continue;
                    if (!Cops.IsCop(ped)) continue;
                    if (ped.IsInVehicle()) continue;

                    return ped;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not find who has hold of you: " + ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Ends it, however it ended.
        ///
        /// THE ONLY WAY OUT, and it gives the officer back his events. An officer left with
        /// BlockPermanentEvents set is a man who will stand in a firefight without reacting to
        /// it, and nothing later will ever clear it for him.
        /// </summary>
        public void Stop(string why, int graceMs = LapsedMs)
        {
            var officer = _officer;

            _officer = null;
            _overAt = Game.GameTime + graceMs;
            _driftSince = 0;
            _warned = false;

            // THE POLICE CAN SEE YOU AGAIN. Unconditional and before anything that can fail:
            // a player left permanently ignored by every officer in the game, by a scene that
            // ended badly, is a far worse bug than the one this was preventing.
            Response.LawHold.Ignore(false);

            // HANDS DOWN BEFORE ANYTHING ELSE, and before the early return below. A scene that
            // ends with nobody having been found still has a player stood in the street with
            // his arms in the air, and nothing later would ever put them down.
            try
            {
                Unpose(Game.Player.Character, officer);
            }
            catch
            {
                // The pose ends on its own when they are next re-tasked.
            }

            if (officer == null) return;

            try
            {
                if (officer.Exists())
                {
                    officer.BlockPermanentEvents = false;
                    Function.Call(Hash.TASK_CLEAR_LOOK_AT, officer.Handle);
                }
            }
            catch
            {
                // He is gone, which is the outcome this was arranging anyway.
            }

            Log.Info("Search over: " + why + ".");
        }
    }
}
