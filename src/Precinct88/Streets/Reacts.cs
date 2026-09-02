using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// Officers noticing you doing something, and visibly minding.
    ///
    /// REACTING IS NOT REPORTING, and the whole file exists because those had been the same
    /// thing. Response.Notice decides what is worth SENDING A CAR TO, and it is deliberately
    /// hard to trigger: most of it is a coin flip, some offences were taken out of it entirely
    /// for being things a player does constantly, and none of it fires without an officer who
    /// could see it.
    ///
    /// All of which is right, and all of which produced a man in uniform standing three feet
    /// from a car doing a burnout, looking at the middle distance. The offence was correctly
    /// judged not worth a police response. It was not correctly judged worth NOTHING -- and
    /// nothing is what an officer who has no reaction in his repertoire does.
    ///
    /// So this is the small half. He looks over. He says something. If it is bad enough he
    /// takes a few steps towards you. Then he goes back to his round, and no car is sent,
    /// nobody is dispatched, and no star is given. It costs the player nothing and it is the
    /// difference between police who are present and police who are scenery.
    ///
    /// IT OUTRANKS BOTH THE OTHER THINGS HE COULD BE DOING. Reacts is ticked before Chats and
    /// Rounds and will take a man off a clipboard, because somebody doing a burnout beside you
    /// is more interesting than your paperwork and an officer who finishes his notes first is
    /// funnier than he is convincing.
    /// </summary>
    internal sealed class Reacts
    {
        /// <summary>How far off he will notice something. Shorter than a car's eyeline.</summary>
        private const float Range = 24f;

        /// <summary>How long he watches before going back to what he was doing.</summary>
        private const int WatchMinMs = 5000;
        private const int WatchMaxMs = 9500;

        /// <summary>And how long before the same officer bothers to look again.</summary>
        private const int GapMinMs = 12000;
        private const int GapMaxMs = 30000;

        /// <summary>How recent one of the engine's "time since" answers counts as now.</summary>
        private const int JustNowMs = 2000;

        /// <summary>Fast enough, this close to a pavement, to be worth a look.</summary>
        private const float Quick = 24f;

        /// <summary>The magic float TASK_GO_TO_ENTITY wants in its second-to-last slot.</summary>
        private const float GoToDefault = 1073741824f;

        /// <summary>
        /// What he says. Ambient speech, so a name this build has not got is silent.
        /// </summary>
        private static readonly string[] Mild =
        {
            "GENERIC_CURSE_MED", "GENERIC_INSULT_MED", "CHAT_STATE",
        };

        private static readonly string[] Serious =
        {
            "GENERIC_CURSE_HIGH", "SHOUT_TO_STOP", "ARREST_PLAYER",
        };

        private readonly Random _rng;

        /// <summary>Puts him back on his round when he is done. Set by Foot.</summary>
        public Action<Walker> Repost;

        public Reacts(Random rng)
        {
            _rng = rng;
        }

        public void Update(IReadOnlyList<Walker> walkers, int now, bool mayStart)
        {
            if (walkers == null) return;

            Ped me = null;

            try
            {
                me = Game.Player.Character;
            }
            catch
            {
                return;
            }

            if (me == null || !me.Exists() || me.IsDead) return;

            // WORKED OUT ONCE FOR THE WHOLE ROW, not per officer. What the player is doing does
            // not depend on who is looking, and four officers on one street asking the engine
            // the same six questions is the same answer bought four times.
            var doing = mayStart ? What(me) : 0;

            for (var i = 0; i < walkers.Count; i++)
            {
                var walker = walkers[i];

                if (walker == null || !walker.Alive) continue;

                try
                {
                    if (walker.Doing == Errand.Watching)
                    {
                        // STILL DOING IT MEANS STILL WATCHING. Without this he looks over for
                        // eight seconds and then goes back to his round while the man in front
                        // of him is still holding a rifle -- which reads as an officer losing
                        // interest, and it is the one thing he would not do.
                        //
                        // Only for the alarming ones. Somebody who did a burnout half a minute
                        // ago is not still doing a burnout, and a look that never ends is its
                        // own kind of broken.
                        if (walker.Alarmed && doing >= 2) walker.WatchUntil = now + 4000;

                        if (now < walker.WatchUntil) Hold(walker, me);
                        else Done(walker, now);

                        continue;
                    }

                    if (doing == 0) continue;

                    // Talking to somebody outranks this. He is already dealing with a person,
                    // and turning away mid-sentence to tut at a car is worse than not reacting.
                    if (walker.Doing == Errand.Approaching) continue;
                    if (walker.Doing == Errand.Talking) continue;

                    if (now < walker.NextWatchAt) continue;

                    if (!Cops.Sees(walker.Who, me, Range)) continue;

                    Begin(walker, me, now, doing);
                }
                catch (Exception ex)
                {
                    Log.Debug("An officer's reaction went wrong: " + ex.Message);
                    Done(walker, now);
                }
            }
        }

        /// <summary>
        /// How conspicuous the player is being: 0 nothing, 1 rude, 2 alarming.
        ///
        /// NATIVES ONLY, AND NO CONTACT TYPES. Violations already answers a very similar
        /// question and is deliberately not used here -- it lives in Contact, this lives in
        /// Streets, and reaching across for it would make the foot patrol depend on the
        /// traffic-stop system for the sake of a handful of one-line checks. The two are
        /// answering different questions anyway: that one asks what you can be ticketed for,
        /// this one asks what makes somebody look up.
        /// </summary>
        private static int What(Ped me)
        {
            try
            {
                if (Function.Call<bool>(Hash.IS_PED_IN_MELEE_COMBAT, me.Handle)) return 2;
                if (Function.Call<bool>(Hash.IS_PED_JACKING, me.Handle)) return 2;

                if (!me.IsInVehicle())
                {
                    return Armed(me) ? 2 : 0;
                }

                var car = me.CurrentVehicle;
                if (!Cops.Alive(car)) return 0;

                // Not a passenger. Somebody else's driving is not your business.
                if (car.Driver == null || car.Driver.Handle != me.Handle) return 0;

                if (Function.Call<bool>(Hash.IS_VEHICLE_IN_BURNOUT, car.Handle)) return 1;

                if (Since(Hash.GET_TIME_SINCE_PLAYER_DROVE_ON_PAVEMENT)) return 2;
                if (Since(Hash.GET_TIME_SINCE_PLAYER_HIT_PED)) return 2;
                if (Since(Hash.GET_TIME_SINCE_PLAYER_DROVE_AGAINST_TRAFFIC)) return 1;

                if (car.Speed > Quick) return 1;

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool Since(Hash what)
        {
            var ms = Function.Call<int>(what, Game.Player.Handle);

            return ms >= 0 && ms < JustNowMs;
        }

        private static bool Armed(Ped me)
        {
            if (!Cops.Armed(me)) return false;

            var group = me.Weapons.Current.Group;

            return group != WeaponGroup.Unarmed &&
                   group != WeaponGroup.Melee &&
                   group != WeaponGroup.Parachute &&
                   group != WeaponGroup.PetrolCan &&
                   group != WeaponGroup.FireExtinguisher;
        }

        // ---- the reaction ------------------------------------------------------

        private void Begin(Walker walker, Ped me, int now, int how)
        {
            walker.Doing = Errand.Watching;
            walker.WatchUntil = now + WatchMinMs + _rng.Next(WatchMaxMs - WatchMinMs);
            walker.Alarmed = how >= 2;

            try
            {
                // Cleared first. A scenario -- the clipboard, the phone -- does not reliably
                // give way to a turn issued on top of it, and half of these reactions start
                // from a man who was in the middle of something.
                walker.Who.Task.ClearAll();

                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, walker.Who.Handle,
                              me.Handle, 1400);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not turn an officer round: " + ex.Message);
            }

            var lines = walker.Alarmed ? Serious : Mild;

            Cops.Say(walker.Who, lines[_rng.Next(lines.Length)]);
        }

        /// <summary>
        /// Watching you.
        ///
        /// THE LOOK IS RE-ISSUED AND THE WALK IS NOT. TASK_LOOK_AT_ENTITY is cheap and gets
        /// overridden by ambient behaviour constantly, so asking again every pass is how it
        /// stays pointed at you. TASK_GO_TO_ENTITY re-issued at the same rate would restart
        /// his approach every pass and he would jog on the spot forever, so it is given once
        /// with a duration and left alone.
        /// </summary>
        private static void Hold(Walker walker, Ped me)
        {
            try
            {
                Function.Call(Hash.TASK_LOOK_AT_ENTITY, walker.Who.Handle, me.Handle,
                              2000, 0, 2);

                if (!walker.Alarmed || walker.Stepped) return;

                walker.Stepped = true;

                // A few steps, not a chase. He is interested, not committed -- and anything
                // that actually warrants coming for you is Response.Notice's business, with a
                // car and a call behind it.
                Function.Call(Hash.TASK_GO_TO_ENTITY, walker.Who.Handle, me.Handle,
                              6000, 6f, 1.3f, GoToDefault, 0);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not hold an officer's attention: " + ex.Message);
            }
        }

        private void Done(Walker walker, int now)
        {
            walker.Doing = Errand.Posted;
            walker.WatchUntil = 0;
            walker.Alarmed = false;
            walker.Stepped = false;
            walker.NextWatchAt = now + GapMinMs + _rng.Next(GapMaxMs - GapMinMs);

            // Pushed out too, or a man who has just spent nine seconds watching a car goes
            // straight back to the clipboard he was interrupted from and does it again.
            walker.NextChoreAt = now + GapMinMs;

            try
            {
                if (walker.Alive)
                {
                    Function.Call(Hash.TASK_CLEAR_LOOK_AT, walker.Who.Handle);
                    walker.Who.Task.ClearAll();
                }
            }
            catch
            {
                // He is gone, which ends it just as well.
            }

            if (Repost != null) Repost(walker);
        }
    }
}
