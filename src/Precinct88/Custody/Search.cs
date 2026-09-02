using System;
using GTA;
using GTA.Native;
using Precinct88.Core;
using Precinct88.UI;

namespace Precinct88.Custody
{
    /// <summary>Where a one-star detention has got to.</summary>
    internal enum Detain
    {
        /// <summary>Nothing running.</summary>
        None,

        /// <summary>You are down or stood still, and he is walking over.</summary>
        Coming,

        /// <summary>He has hold of you. Cuffs going on.</summary>
        Cuffing,

        /// <summary>Turning out your pockets. One star only.</summary>
        Turning,

        /// <summary>Cuffed at two stars, and being handed to the engine for the cell.</summary>
        Booking,
    }

    /// <summary>
    /// One star: put down, cuffed, turned out, and let go.
    ///
    /// THE MISSING RUNG. In vanilla there is one outcome to being caught, at every level, and
    /// it is BUSTED: a fade to black, a fee, and a respawn -- the same thing that happens when
    /// you die, so the game's police have exactly one ending wearing two costumes. Getting
    /// stopped for a shove and getting taken down after a bank job resolve identically.
    ///
    /// So one star ends differently. An officer walks over, cuffs you, takes what you are
    /// carrying, and tells you to move along. A real cost and no lost time -- a bad afternoon
    /// rather than the end of the session. Two stars is where the cell starts.
    ///
    /// WHY THIS STARTS FROM TWENTY-SIX METRES AND NOT FROM ARM'S LENGTH, which is the whole of
    /// the tasing problem. The first version began only once an officer was already within
    /// reach -- but nothing anywhere made him walk over. So the sequence was: tased, down, up
    /// again, tased, and round forever, because the one thing that resolves it could not begin
    /// until somebody closed a gap that nobody had been told to close.
    ///
    /// Being put down is now the START of the scene rather than a precondition for it. The
    /// moment you go down the police are told to hold off and the nearest officer is sent to
    /// you. He tases you once, because that is how he stops you. He does not tase you twice,
    /// because after the first one he has somewhere to be.
    ///
    /// THE ENGINE HAS TO BE HELD OFF TWICE OVER. It will arrest you at one star given the
    /// chance and it does not ask -- PREVENT_ARREST_STATE_THIS_FRAME stops that, and has to be
    /// called EVERY FRAME because it is a this-frame flag. And it reads a wanted level as
    /// "attack this man" whatever this file believes, so LawHold.Ignore is what stops the two
    /// officers behind him deciding to have another go while he is working.
    /// </summary>
    internal sealed class Search
    {
        private const int TickMs = 250;

        /// <summary>
        /// How far off an officer will come over when you are down.
        ///
        /// A stun gun reaches a great deal further than a conversation does, so this has to
        /// cover the range he shot you from. Anything shorter and the scene cannot start from
        /// the thing that most often causes it.
        /// </summary>
        private const float Spot = 26f;

        /// <summary>Close enough to put hands on you.</summary>
        private const float Reach = 3.6f;

        /// <summary>
        /// Close enough for it to be going on.
        ///
        /// SHUFFLING IS NOT FLEEING, and the first version could not tell the difference. It
        /// measured the gap once a tick and abandoned everything the moment you crossed a line
        /// -- so a step to the side, a passing pedestrian, or the officer himself settling was
        /// enough to end it, into the worst state available: still one star, no longer being
        /// dealt with, stood next to somebody with a stun gun.
        /// </summary>
        private const float Leash = 4.8f;

        /// <summary>Past this you are not drifting, you are leaving, and it ends at once.</summary>
        private const float Gone = 12f;

        /// <summary>How long you may be out of reach before he gives up on you.</summary>
        private const int DriftGraceMs = 5000;

        /// <summary>How long he will spend trying to reach you at all.</summary>
        private const int ComeMs = 15000;

        /// <summary>How long the cuffs take to go on.</summary>
        private const int CuffMs = 2600;

        /// <summary>And how long the search itself takes once they are on.</summary>
        private const int SearchMs = 4200;

        /// <summary>
        /// How long to wait for the engine to take him off our hands at two stars.
        ///
        /// The bust is the game's, not ours -- once the arrest block comes off and an officer
        /// is stood on top of a wanted man, it happens on its own. This is only here so that a
        /// bust which does not arrive for some reason leaves a player free rather than kneeling
        /// in handcuffs waiting for a fade that is never coming.
        /// </summary>
        private const int BookingMs = 7000;

        /// <summary>Moving faster than this and you are not waiting, you are going.</summary>
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
        /// police still want you, but no new search could begin for twenty seconds -- leaving
        /// exactly one thing able to happen in the meantime: get tased, get up, get tased
        /// again, with the one outcome that resolves it locked out.
        /// </summary>
        private const int LapsedMs = 3500;

        private readonly Settings _cfg;

        private int _lastTick;
        private int _overAt;
        private int _phaseAt;

        /// <summary>
        /// How much of the search has actually happened, in milliseconds.
        ///
        /// AN ACCUMULATOR RATHER THAN A START TIME. A start time counts wall clock, so a search
        /// you stepped out of would finish itself while you were away. This only advances on
        /// ticks where he can reach you: stepping back pauses it, stepping in resumes it.
        /// </summary>
        private int _done;
        private int _lastProgressAt;

        /// <summary>When you first went out of reach, and whether he has said anything.</summary>
        private int _driftSince;
        private bool _warned;

        private bool _cuffed;

        private Ped _officer;
        private Detain _at = Detain.None;

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

        /// <summary>Whether somebody is being dealt with right now.</summary>
        public bool Running => _at != Detain.None;

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

                // THE WHOLE REASON ONE STAR CAN END ANY OTHER WAY, and every frame, no gate --
                // it is a this-frame flag and the frames a gate misses are the frames the
                // engine uses to bust you.
                //
                // AT TWO STARS IT IS HELD OFF ONLY WHILE THE SCENE RUNS. Two stars is supposed
                // to end in a cell, so the bust is wanted -- but not before he has walked over
                // and put the cuffs on, or the whole thing is a fade to black from across the
                // road again. The moment the cuffs are on, the block comes off and the engine
                // does what it was always going to do.
                if (stars == 1 || (stars == 2 && Running && _at != Detain.Booking))
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

                if (Running) { Held(me, now, stars); return; }

                if (stars < 1 || stars > 2) return;
                if (now < _overAt) return;

                Consider(me, now);
            }
            catch (Exception ex)
            {
                Log.Debug("The search went wrong: " + ex.Message);
                Stop("something went wrong");
            }
        }

        // ---- starting ----------------------------------------------------------

        /// <summary>
        /// Whether somebody has stopped you, one way or the other.
        ///
        /// TWO WAYS IN, AND THE TASER IS THE COMMON ONE. Either you are stood still with an
        /// officer right there, which is compliance, or you are on the floor, which is not.
        /// Both end in the same place; only the dignity differs. Being down reaches much
        /// further, because being down is not something you can walk out of.
        /// </summary>
        private void Consider(Ped me, int now)
        {
            if (me.IsInVehicle()) return;

            var down = me.IsRagdoll ||
                       Function.Call<bool>(Hash.IS_PED_BEING_STUNNED, me.Handle, 0);

            if (!down && me.Velocity.Length() > StillSpeed) return;

            var officer = Nearest(me, down ? Spot : Reach);
            if (officer == null) return;

            _officer = officer;
            _at = Detain.Coming;
            _phaseAt = now;

            _done = 0;
            _lastProgressAt = now;
            _driftSince = 0;
            _warned = false;
            _cuffed = false;

            // NOBODY GETS A SECOND GO WHILE HE IS WALKING OVER. This is the line that ends the
            // tase-get-up-tase loop: the engine reads a wanted level as "attack this man"
            // regardless of what this file is doing, so until the police are told otherwise
            // every officer in sight is still solving the same problem the same way.
            Response.LawHold.Ignore(true);

            Safe(officer);

            Dialogue.Say("Officer", down
                ? "Stay down. Do not make this worse."
                : "Hands where I can see them. Stay right there.", ComeMs);

            Log.Info("Detained at one star (" + (down ? "put down" : "stopped") + ").");
            if (Say != null) Say("Detained.");
        }

        // ---- the scene ---------------------------------------------------------

        private void Held(Ped me, int now, int stars)
        {
            // Three stars is a different evening entirely -- they have their sidearms back
            // and nobody is putting cuffs on anybody. Zero means it resolved itself.
            if (stars < 1 || stars > 2) { Stop("it escalated"); return; }

            if (!Cops.Alive(_officer)) { Stop("the officer is gone"); return; }

            var since = now - _lastProgressAt;
            _lastProgressAt = now;

            // Getting in a car is not drifting. It is the one unambiguous statement available.
            if (me.IsInVehicle()) { Stop("he got in a car"); return; }

            var apart = _officer.Position.DistanceTo(me.Position);

            switch (_at)
            {
                case Detain.Coming: Coming(me, now, apart); return;
                case Detain.Cuffing: Cuffing(me, now, apart); return;
                case Detain.Turning: Turning(me, now, apart, since); return;
                case Detain.Booking: Booking(me, now); return;
            }
        }

        /// <summary>
        /// Walking over to you.
        ///
        /// HE WALKS. A run would say he thinks you are going somewhere, and the whole point of
        /// this phase is that nobody has decided that yet -- you are down, or you are stood
        /// still, and either way there is nothing to chase.
        /// </summary>
        private void Coming(Ped me, int now, float apart)
        {
            if (apart <= Reach)
            {
                _at = Detain.Cuffing;
                _phaseAt = now;

                Dialogue.Say("Officer", "Hands behind your back.", CuffMs + 800);
                return;
            }

            // Up and away. Being on the floor does not count as leaving.
            var bolting = !me.IsRagdoll && me.Velocity.Length() > StillSpeed * 1.6f;

            if (apart > Gone || (bolting && apart > Leash))
            {
                Stop("he took off");
                return;
            }

            if (now - _phaseAt > ComeMs) { Stop("he could not get to you"); return; }

            Screen.Help("Stay where you are.");
            Anim.Play(me, Anim.HandsUpDict, Anim.HandsUpClip, 49);

            try
            {
                Function.Call(Hash.TASK_GO_TO_ENTITY, _officer.Handle, me.Handle,
                              ComeMs, Reach * 0.7f, 1.25f, 1073741824f, 0);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send an officer over: " + ex.Message);
            }
        }

        /// <summary>
        /// The cuffs going on.
        ///
        /// BOTH THE HOLD AND THE LOOK, because neither is reliable on its own.
        /// SET_ENABLE_HANDCUFFS is what actually restrains somebody -- it is the mechanical
        /// part, and without it "cuffed" is a pose you can walk out of. The clip is what makes
        /// it visible, because the native's own presentation depends on what the ped happened
        /// to be doing when it was applied and cannot be counted on to read.
        ///
        /// EVERY EXIT PATH UNCUFFS. A player left permanently in handcuffs by a script that is
        /// no longer running has no way to work out why and no way to undo it.
        /// </summary>
        private void Cuffing(Ped me, int now, float apart)
        {
            if (apart > Gone) { Stop("he took off"); return; }

            if (!_cuffed)
            {
                _cuffed = true;

                try
                {
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, me.Handle, true);
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not put the cuffs on: " + ex.Message);
                }

                Cops.Say(_officer, "ARREST_PLAYER");
            }

            Screen.Help("Cuffed.");
            Pose(me);

            if (now - _phaseAt < CuffMs) return;

            _phaseAt = now;
            _lastProgressAt = now;

            // ONE STAR IS A SEARCH. TWO IS A CELL. Same walk over, same cuffs, same animation
            // -- and that is the point of running the scene at both levels rather than only at
            // one. What changes is what is at the end of it, which is the thing the player is
            // actually being taught: at one star you lose what you were carrying, at two you
            // lose the evening.
            if (Game.Player.Wanted.WantedLevel <= 1)
            {
                _at = Detain.Turning;

                Dialogue.Say("Officer", "Anything on you I should know about?", SearchMs);
                return;
            }

            _at = Detain.Booking;

            Dialogue.Say("Officer", "You are under arrest. Watch your head.", BookingMs);

            // AND THE POLICE CAN HAVE HIM BACK. Held off for the walk over and the cuffs so
            // that neither got interrupted; released now, because the arrest is the outcome
            // rather than something to be protected from.
            Response.LawHold.Ignore(false);

            Log.Info("Cuffed at two stars; handing over for the bust.");
        }

        /// <summary>Going through your pockets.</summary>
        private void Turning(Ped me, int now, float apart, int since)
        {
            if (apart > Gone) { Stop("he walked off"); return; }

            if (apart > Leash) { Drifting(me, now); return; }

            // Back in reach. Whatever that was, it is over.
            _driftSince = 0;
            _warned = false;

            // ONLY COUNTED WHILE HE CAN REACH YOU -- see _done. Capped per tick so a frame
            // hitch, a loading pause or a menu does not hand you a whole search for free.
            _done += since > 400 ? 400 : since;

            if (_done < SearchMs)
            {
                Screen.Help("Being searched.");
                Pose(me);
                return;
            }

            Done(me);
        }

        /// <summary>
        /// Cuffed at two stars, waiting for the game to take him.
        ///
        /// THE BUST IS THE ENGINE'S AND ALWAYS WAS. With the arrest block off and an officer
        /// stood on top of a wanted man, it fires on its own -- there is no native for "arrest
        /// me now" worth trusting, and the engine's own version already does the fade, the fee
        /// and the station. All this does is hold the pose until it happens.
        ///
        /// The timeout is the point of the state existing at all. If the bust does not arrive
        /// -- he backed into geometry, the officer died, something else took the wanted level
        /// away -- the player must end up free and uncuffed rather than kneeling in the road
        /// waiting for a fade that is never coming.
        /// </summary>
        private void Booking(Ped me, int now)
        {
            Screen.Help("Under arrest.");
            Pose(me);

            if (now - _phaseAt > BookingMs) Stop("the bust never came");
        }

        /// <summary>
        /// You have got out of reach, and he would rather you had not.
        ///
        /// HE FOLLOWS AND HE ASKS, in that order, and only then does he give up. Ending it the
        /// instant somebody moved was not strictness, it was the scene being unable to cope
        /// with a pavement -- people get bumped, officers settle, and neither of those is a
        /// decision to walk away from a police officer.
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
            Pose(me);

            try
            {
                Function.Call(Hash.TASK_GO_TO_ENTITY, _officer.Handle, me.Handle,
                              DriftGraceMs, Leash * 0.6f, 1.2f, 1073741824f, 0);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not follow a drifting search: " + ex.Message);
            }

            if (now - _driftSince > DriftGraceMs) Stop("he would not stay put");
        }

        // ---- poses -------------------------------------------------------------

        /// <summary>
        /// Cuffed, and an officer going through your pockets.
        ///
        /// CALLED EVERY TICK ON PURPOSE, and that is safe rather than sloppy: Anim.Play checks
        /// whether the clip is already running and returns without re-issuing it. Re-issuing is
        /// what makes a ped judder on the spot, and holding a pose by asking for it repeatedly
        /// is exactly what every call site here wants to do.
        ///
        /// FLAG 49 THROUGHOUT: loop, upper body, and allow player control. The cuffs already
        /// hold you mechanically, so the clip does not also need to -- and a full-body lock on
        /// top of them is two systems fighting over one ped, which reads as a stutter.
        /// </summary>
        private void Pose(Ped me)
        {
            Anim.Play(me, Anim.CuffedDict, Anim.CuffedClip, 49);

            if (Cops.Alive(_officer))
            {
                Anim.Play(_officer, Anim.InspectDict, Anim.InspectClip, 1);
            }
        }

        /// <summary>
        /// Everything off, on every exit.
        ///
        /// STOP_ANIM_TASK rather than clearing tasks, because clearing a PLAYER's tasks is a
        /// blunt instrument that also cancels whatever they were legitimately doing -- and
        /// leaving a clip running is worse than either: the pose survives the scene and the
        /// player walks around with his hands behind his back until something re-tasks him.
        /// </summary>
        private static void Unpose(Ped me, Ped officer)
        {
            if (me != null && me.Exists())
            {
                Anim.Stop(me, Anim.HandsUpDict, Anim.HandsUpClip);
                Anim.Stop(me, Anim.CuffedDict, Anim.CuffedClip);
            }

            if (Cops.Alive(officer))
            {
                Anim.Stop(officer, Anim.InspectDict, Anim.InspectClip);
            }
        }

        // ---- the end -----------------------------------------------------------

        /// <summary>
        /// What it costs, and then you are free to go.
        ///
        /// THE WANTED LEVEL GOES LAST. Clearing it first would end the one-star state, which is
        /// what the arrest block is keyed on, and the engine would have a frame in which to do
        /// something else with you before this finished.
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

        private static Ped Nearest(Ped me, float within)
        {
            Ped best = null;
            var bestDist = within;

            try
            {
                foreach (var ped in World.GetNearbyPeds(me, within))
                {
                    if (!Cops.Alive(ped)) continue;
                    if (!Cops.IsCop(ped)) continue;
                    if (ped.IsInVehicle()) continue;

                    var d = ped.Position.DistanceTo(me.Position);
                    if (d >= bestDist) continue;

                    bestDist = d;
                    best = ped;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not find who has hold of you: " + ex.Message);
            }

            return best;
        }

        /// <summary>
        /// Stops the officer reacting to anything while he deals with you.
        ///
        /// BlockPermanentEvents is the one that does the work: it stops ambient and combat
        /// events firing at all, which is what would otherwise override the walk-over the
        /// moment he registers an armed man in front of him. Cleared again in Stop.
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
                Log.Debug("Could not settle an officer: " + ex.Message);
            }
        }

        /// <summary>
        /// Ends it, however it ended.
        ///
        /// THE ONLY WAY OUT, and the order in here is the order of how bad it would be to skip
        /// each one. The cuffs come off first and unconditionally: a player left handcuffed by
        /// a scene that died has no way to work out why and no way to undo it. Then the police
        /// can see him again -- being permanently invisible to every officer in the game is the
        /// same class of problem in the other direction. Only then the cosmetics.
        /// </summary>
        public void Stop(string why, int graceMs = LapsedMs)
        {
            if (_at == Detain.None) return;

            var officer = _officer;

            _officer = null;
            _at = Detain.None;
            _overAt = Game.GameTime + graceMs;
            _driftSince = 0;
            _warned = false;

            var me = Game.Player.Character;

            if (_cuffed)
            {
                _cuffed = false;

                try
                {
                    if (me != null && me.Exists())
                    {
                        Function.Call(Hash.UNCUFF_PED, me.Handle);
                        Function.Call(Hash.SET_ENABLE_HANDCUFFS, me.Handle, false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not take the cuffs off: " + ex.Message);
                }
            }

            Response.LawHold.Ignore(false);

            try
            {
                Unpose(me, officer);
            }
            catch
            {
                // The pose ends on its own when they are next re-tasked.
            }

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

            Log.Info("Detention over: " + why + ".");
        }
    }
}
