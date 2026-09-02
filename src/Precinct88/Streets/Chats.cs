using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>What an officer on foot is in the middle of.</summary>
    internal enum Errand
    {
        /// <summary>Walking his round, or stood at his post. The default.</summary>
        Posted,

        /// <summary>Crossing to somebody he intends to speak to.</summary>
        Approaching,

        /// <summary>Stood with them, talking.</summary>
        Talking,

        /// <summary>Stopped, doing something of his own. See Rounds.</summary>
        Busy,
    }

    /// <summary>
    /// Officers stopping to talk to people.
    ///
    /// WHAT THIS IS FOR. A foot patrol that only ever walks is scenery with a uniform on -- the
    /// pedestrians in this game already walk, and an officer doing the same thing in different
    /// clothes is not policing, it is a costume. The whole reason to put a man on a pavement
    /// rather than in a car is that he can talk to somebody, and until he does the feature is
    /// making a claim it does not honour.
    ///
    /// It is also the cheapest possible way to make the police look like they have business
    /// here that is not about the player. Every other system in this mod is downstream of what
    /// YOU did; this one is the only thing the police do because they were already there.
    ///
    /// THE CIVILIAN IS NOT OURS AND IS NEVER MADE OURS. He is not marked persistent, nothing
    /// permanent is set on him, and no flag is left behind that has to be undone -- if he walks
    /// off mid-sentence the conversation simply ends, which is also what happens in the street.
    /// The one exception would be BlockPermanentEvents, and it is deliberately NOT used: a
    /// pedestrian left unable to react to danger by a script that has since unloaded is a bug
    /// that outlives the mod, and it would buy nothing except a civilian who stands still
    /// slightly more reliably.
    ///
    /// FOOT ONLY, ON PURPOSE. Officers in cars do not get out for this. Two of them abandoning
    /// a vehicle at the kerb to chat, then having to find it again, is a much larger piece of
    /// behaviour with a much larger number of ways to strand a car in a live lane -- and it
    /// belongs with the stop-and-search code that already knows how to get people out of cars
    /// and back into them, which is parked.
    /// </summary>
    internal sealed class Chats
    {
        /// <summary>How far an officer will look for somebody worth a word.</summary>
        private const float LookFor = 17f;

        /// <summary>Close enough to be talking rather than shouting.</summary>
        private const float CloseEnough = 2.6f;

        /// <summary>And how far apart before it has stopped being a conversation.</summary>
        private const float DriftedOff = 6.5f;

        /// <summary>Gives up crossing to somebody after this. Pavements have obstacles.</summary>
        private const int ApproachMs = 15000;

        /// <summary>How long they stand there. Long enough to be seen from a passing car.</summary>
        private const int TalkMinMs = 12000;
        private const int TalkMaxMs = 26000;

        /// <summary>And how long before he bothers anybody else.</summary>
        private const int GapMinMs = 22000;
        private const int GapMaxMs = 75000;

        /// <summary>Walking pace. An officer who jogs over reads as an incident.</summary>
        private const float WalkSpeed = 1.15f;

        /// <summary>How often somebody says something once they are talking.</summary>
        private const int LineGapMinMs = 3500;
        private const int LineGapMaxMs = 8000;

        /// <summary>
        /// How far into a conversation the notepad comes out.
        ///
        /// NOT AT THE START, and the delay is the whole idea. An officer who walks up already
        /// writing has decided the outcome before he has heard anything; one who listens for a
        /// while and THEN gets his notepad out has heard something worth writing down. It is
        /// the same information either way -- the timing is the entire performance.
        /// </summary>
        private const float NotepadAfter = 0.45f;

        /// <summary>Taking a statement. The one scenario that reads as police work.</summary>
        private const string Notepad = "WORLD_HUMAN_CLIPBOARD";

        /// <summary>The magic float TASK_GO_TO_ENTITY wants in its second-to-last slot.</summary>
        private const float GoToDefault = 1073741824f;

        private static readonly string[] Openers =
        {
            "GENERIC_HI", "GENERIC_HOWS_IT_GOING", "CHAT_STATE",
        };

        private static readonly string[] Replies =
        {
            "GENERIC_HI", "GENERIC_HOWS_IT_GOING", "CHAT_RESP",
        };

        private readonly Random _rng;

        /// <summary>Puts a walker back on his round when he is done. Set by Foot.</summary>
        public Action<Walker> Repost;

        public Chats(Random rng)
        {
            _rng = rng;
        }

        /// <summary>
        /// One pass over everybody on foot.
        ///
        /// <paramref name="mayStart"/> is false while something louder is happening. Note that
        /// conversations already running are still TICKED in that case rather than skipped --
        /// otherwise a hold that begins mid-sentence leaves an officer holding a look-at task
        /// at a man forever, and the whole scene is frozen until the hold clears.
        /// </summary>
        public void Update(IReadOnlyList<Walker> walkers, int now, bool mayStart)
        {
            if (walkers == null) return;

            for (var i = 0; i < walkers.Count; i++)
            {
                var walker = walkers[i];

                if (walker == null || !walker.Alive)
                {
                    continue;
                }

                try
                {
                    switch (walker.Doing)
                    {
                        case Errand.Posted:
                            if (mayStart && now >= walker.NextChatAt) Look(walker, walkers, now);
                            break;

                        case Errand.Approaching:
                            Approaching(walker, now);
                            break;

                        case Errand.Talking:
                            Talking(walker, now);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("An officer's conversation went wrong: " + ex.Message);
                    Done(walker, now);
                }
            }
        }

        // ---- finding somebody --------------------------------------------------

        /// <summary>
        /// Somebody on this pavement worth a word.
        ///
        /// The nearest one that qualifies rather than a random one, because an officer walking
        /// past two people to reach a third reads as scripted -- which is exactly what it is,
        /// and the whole job here is to not look like it.
        /// </summary>
        private void Look(Walker walker, IReadOnlyList<Walker> others, int now)
        {
            // Rescheduled whether or not anybody is found, so an officer stood on an empty
            // street is not asking the world for a ped list every tick forever.
            walker.NextChatAt = now + GapMinMs + _rng.Next(GapMaxMs - GapMinMs);

            var me = walker.Who;
            var found = Nearest(me, others);

            if (found == null) return;

            walker.Subject = found;
            walker.Doing = Errand.Approaching;
            walker.StateUntil = now + ApproachMs;

            Function.Call(Hash.TASK_GO_TO_ENTITY, me.Handle, found.Handle,
                          ApproachMs, CloseEnough * 0.8f, WalkSpeed, GoToDefault, 0);
        }

        private Ped Nearest(Ped officer, IReadOnlyList<Walker> others)
        {
            Ped best = null;
            var bestDist = float.MaxValue;

            var player = Game.Player.Character;

            foreach (var ped in World.GetNearbyPeds(officer, LookFor))
            {
                if (!Worth(ped, officer, player)) continue;
                if (Claimed(ped, others)) continue;

                var d = ped.Position.DistanceTo(officer.Position);

                if (d >= bestDist) continue;

                bestDist = d;
                best = ped;
            }

            return best;
        }

        /// <summary>
        /// Whether this is somebody an officer would walk over to.
        ///
        /// THE PLAYER IS EXCLUDED DELIBERATELY. An officer approaching YOU is a stop, it means
        /// something, and it belongs to the Contact system rather than to street dressing. If
        /// this were allowed to pick the player it would be a stop-and-search with none of the
        /// rules, no reason, and no way for it to end -- and it would fire constantly, because
        /// the player is the one ped guaranteed to be near an officer.
        /// </summary>
        private static bool Worth(Ped ped, Ped officer, Ped player)
        {
            if (ped == null || !ped.Exists() || ped.IsDead) return false;
            if (officer != null && ped.Handle == officer.Handle) return false;
            if (player != null && ped.Handle == player.Handle) return false;

            if (!ped.IsHuman) return false;
            if (Cops.IsCop(ped)) return false;

            // In a car he is a driver, not somebody on the pavement, and an officer leaning
            // into a window is a traffic stop rather than a chat.
            if (ped.IsInVehicle()) return false;

            // Anybody already having a bad day. Walking up to them turns street dressing into
            // an incident this build has nothing to do with.
            if (ped.IsInCombat || ped.IsFleeing || ped.IsRagdoll) return false;
            if (ped.IsInjured || ped.IsSwimming || ped.IsClimbing) return false;

            return true;
        }

        /// <summary>Whether another officer is already talking to them.</summary>
        private static bool Claimed(Ped ped, IReadOnlyList<Walker> others)
        {
            if (others == null) return false;

            for (var i = 0; i < others.Count; i++)
            {
                var other = others[i];

                if (other == null || other.Subject == null) continue;
                if (!other.Subject.Exists()) continue;

                if (other.Subject.Handle == ped.Handle) return true;
            }

            return false;
        }

        // ---- the scene ---------------------------------------------------------

        private void Approaching(Walker walker, int now)
        {
            var them = walker.Subject;

            if (!Still(them)) { Done(walker, now); return; }

            var apart = them.Position.DistanceTo(walker.Who.Position);

            if (apart <= CloseEnough)
            {
                Begin(walker, now);
                return;
            }

            // Took too long. Somebody got round a corner, or there was a fence.
            if (now > walker.StateUntil) Done(walker, now);
        }

        private void Begin(Walker walker, int now)
        {
            var me = walker.Who;
            var them = walker.Subject;

            var length = TalkMinMs + _rng.Next(TalkMaxMs - TalkMinMs);

            walker.Doing = Errand.Talking;
            walker.StateUntil = now + length;
            walker.NextLineAt = now + LineGapMinMs;
            walker.NotepadAt = now + (int)(length * NotepadAfter);
            walker.Writing = false;

            // Face each other. Without this they end up talking past one another, which is
            // the single thing that makes a scripted conversation obvious at a glance.
            Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, me.Handle, them.Handle, 1800);
            Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, them.Handle, me.Handle, 1800);

            // And keep looking. The turn is over in two seconds; the look holds for the whole
            // conversation and is what stops them staring into the middle distance.
            Function.Call(Hash.TASK_LOOK_AT_ENTITY, me.Handle, them.Handle, length, 0, 2);
            Function.Call(Hash.TASK_LOOK_AT_ENTITY, them.Handle, me.Handle, length, 0, 2);

            // The game's own two-person chat. Unreliable enough that everything above is done
            // by hand as well -- if this does nothing, the scene still reads correctly.
            Function.Call(Hash.TASK_CHAT_TO_PED, me.Handle, them.Handle, 16, 0f, 0f, 0f, 0f, 0f);
            Function.Call(Hash.TASK_CHAT_TO_PED, them.Handle, me.Handle, 16, 0f, 0f, 0f, 0f, 0f);

            Cops.Say(me, Openers[_rng.Next(Openers.Length)]);
        }

        private void Talking(Walker walker, int now)
        {
            var them = walker.Subject;

            if (!Still(them)) { Done(walker, now); return; }

            // They walked off. People do.
            if (them.Position.DistanceTo(walker.Who.Position) > DriftedOff)
            {
                Done(walker, now);
                return;
            }

            if (now > walker.StateUntil) { Done(walker, now); return; }

            // OUT IT COMES. Once, not every pass -- TASK_START_SCENARIO_IN_PLACE restarts
            // from the top each time it is called, so re-issuing it holds him permanently on
            // the first frame of getting the notepad out and he never writes anything.
            if (!walker.Writing && now >= walker.NotepadAt)
            {
                walker.Writing = true;

                try
                {
                    Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, walker.Who.Handle,
                                  Notepad, 0, true);

                    // The look-at survives the scenario and is what keeps him facing the person
                    // he is writing about rather than facing his own notes.
                    Function.Call(Hash.TASK_LOOK_AT_ENTITY, walker.Who.Handle, them.Handle,
                                  walker.StateUntil - now, 0, 2);
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not get the notepad out: " + ex.Message);
                }
            }

            if (now < walker.NextLineAt) return;

            walker.NextLineAt = now + LineGapMinMs + _rng.Next(LineGapMaxMs - LineGapMinMs);

            // Turns, roughly. Not tracked properly on purpose -- ambient speech has its own
            // queueing and a strict alternation just produces two people talking over each
            // other on a fixed rhythm, which is worse than the occasional double.
            if (_rng.Next(2) == 0) Cops.Say(walker.Who, Openers[_rng.Next(Openers.Length)]);
            else Cops.Say(them, Replies[_rng.Next(Replies.Length)]);
        }

        /// <summary>
        /// Ends it, however it ended, and hands the officer back to his round.
        ///
        /// THE ONE PATH OUT. Every failure above routes through here rather than resetting
        /// state in place, because the civilian's look-at has to be cleared on all of them --
        /// a pedestrian left permanently looking at a spot where an officer used to be is the
        /// kind of thing that is never traced back to the mod that did it.
        /// </summary>
        private void Done(Walker walker, int now)
        {
            var them = walker.Subject;

            walker.Subject = null;
            walker.Doing = Errand.Posted;
            walker.StateUntil = 0;
            walker.Writing = false;
            walker.NextChatAt = now + GapMinMs + _rng.Next(GapMaxMs - GapMinMs);

            try
            {
                if (them != null && them.Exists() && !them.IsDead)
                {
                    Function.Call(Hash.TASK_CLEAR_LOOK_AT, them.Handle);

                    // Handed straight back to the game. He was never ours.
                    them.MarkAsNoLongerNeeded();
                }
            }
            catch
            {
                // He is gone, which is the outcome this was arranging anyway.
            }

            try
            {
                if (walker.Alive) Function.Call(Hash.TASK_CLEAR_LOOK_AT, walker.Who.Handle);
            }
            catch
            {
                // Same.
            }

            if (Repost != null) Repost(walker);
        }

        private static bool Still(Ped who)
        {
            return who != null && who.Exists() && !who.IsDead && !who.IsInVehicle();
        }
    }
}
