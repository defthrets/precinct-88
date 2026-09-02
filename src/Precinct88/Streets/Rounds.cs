using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// One thing an officer stops to do, and how he does it.
    ///
    /// SCENARIOS AND ANIMATIONS ARE NOT THE SAME KIND OF THING and this holds both because the
    /// good ones are split across the two. A scenario is a whole behaviour the game owns -- it
    /// picks the props, the idle variation, and the exits -- and it survives being left alone
    /// for half a minute. An animation is one clip that has to be held. A clipboard is a
    /// scenario; talking into a shoulder radio is not, and the shoulder radio is the single
    /// most police-looking thing available.
    /// </summary>
    internal struct Chore
    {
        /// <summary>A game scenario name, or null if this is an animation.</summary>
        public string Scenario;

        /// <summary>Dictionary and clip, when there is no scenario for it.</summary>
        public string Dict;
        public string Clip;

        public static Chore Of(string scenario)
        {
            return new Chore { Scenario = scenario };
        }

        public static Chore Anim(string dict, string clip)
        {
            return new Chore { Dict = dict, Clip = clip };
        }
    }

    /// <summary>
    /// What an officer does on his round when nobody needs him.
    ///
    /// A MAN WHO ONLY EVER WALKS IS A PEDESTRIAN IN A UNIFORM. The foot patrol worked -- they
    /// went out, they covered ground, they saw things -- and watching one for a minute was
    /// still watching somebody walk in a slow circle, because walking was the entire repertoire.
    /// Every real officer on a street spends most of it standing: writing something down,
    /// on the radio, on the phone, drinking something, looking at a thing.
    ///
    /// THE PAUSE IS WHERE THE TURN HAPPENS, and that is the point rather than a side effect.
    /// TASK_WANDER_IN_AREA turns a ped round when it reaches the edge of its area, and the turn
    /// is the moment the illusion breaks: he arrives at a corner, pivots for no reason, and
    /// walks back. Stopping him there to write something on a clipboard gives the turn a
    /// REASON -- he stopped, he did something, and then he went the other way, which is what a
    /// man walking a round actually looks like.
    ///
    /// Its own file rather than more of Foot, for the same reason Chats is: Foot is about
    /// putting officers on pavements and taking them off again, and it should stay that size.
    /// </summary>
    internal sealed class Rounds
    {
        /// <summary>How long between one of these and the next, for one officer.</summary>
        private const int GapMinMs = 30000;
        private const int GapMaxMs = 95000;

        /// <summary>And how long he is at it. Long enough to be walked past.</summary>
        private const int BusyMinMs = 13000;
        private const int BusyMaxMs = 28000;

        /// <summary>
        /// Everything worth stopping for.
        ///
        /// ALL GUARDED, LIKE EVERY OTHER GUESSED STRING IN THIS MOD. A scenario name this build
        /// does not have simply fails to start and he stands there instead, which is a
        /// perfectly good thing for a police officer to be doing; an animation dictionary that
        /// does not exist is time-boxed by Anim and costs a log line. Neither can hang.
        /// </summary>
        private static readonly Chore[] Business =
        {
            // The notepad. The one everybody pictures when they picture this.
            Chore.Of("WORLD_HUMAN_CLIPBOARD"),
            Chore.Of("WORLD_HUMAN_CLIPBOARD"),

            Chore.Of("WORLD_HUMAN_STAND_MOBILE"),
            Chore.Of("WORLD_HUMAN_COP_IDLES"),
            Chore.Of("WORLD_HUMAN_AA_COFFEE"),
            Chore.Of("WORLD_HUMAN_BINOCULARS"),
            Chore.Of("WORLD_HUMAN_GUARD_STAND"),

            // The shoulder radio, which has no scenario and is worth the special case.
            Chore.Anim("random@arrests", "generic_radio_chatter"),
        };

        private readonly Random _rng;

        /// <summary>Puts him back on his round when he is done. Set by Foot.</summary>
        public Action<Walker> Repost;

        public Rounds(Random rng)
        {
            _rng = rng;
        }

        /// <summary>
        /// One pass over everybody on foot.
        ///
        /// <paramref name="mayStart"/> is false while something louder is happening. Anything
        /// already running is still TICKED either way, so a hold that begins mid-clipboard can
        /// still end it rather than leaving him writing forever.
        /// </summary>
        public void Update(IReadOnlyList<Walker> walkers, int now, bool mayStart)
        {
            if (walkers == null) return;

            for (var i = 0; i < walkers.Count; i++)
            {
                var walker = walkers[i];

                if (walker == null || !walker.Alive) continue;

                try
                {
                    if (walker.Doing == Errand.Busy)
                    {
                        if (now < walker.BusyUntil) Hold(walker);
                        else Done(walker, now);

                        continue;
                    }

                    if (!mayStart) continue;
                    if (walker.Doing != Errand.Posted) continue;

                    // ONLY THE ONES WHO WALK. An officer posted on a corner is already stood
                    // doing something; stopping him to stand doing something else is a change
                    // nobody can see.
                    if (!walker.Wanders) continue;

                    if (now < walker.NextChoreAt) continue;

                    Begin(walker, now);
                }
                catch (Exception ex)
                {
                    Log.Debug("An officer's round went wrong: " + ex.Message);
                    Done(walker, now);
                }
            }
        }

        private void Begin(Walker walker, int now)
        {
            walker.Chore = Business[_rng.Next(Business.Length)];
            walker.Doing = Errand.Busy;
            walker.BusyUntil = now + BusyMinMs + _rng.Next(BusyMaxMs - BusyMinMs);

            try
            {
                if (walker.Chore.Scenario != null)
                {
                    // Cleared first. A scenario started on top of a wander sometimes takes and
                    // sometimes does not, and which one you get depends on where in his walk
                    // cycle he happened to be -- so it looks intermittent rather than broken.
                    walker.Who.Task.ClearAll();

                    Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, walker.Who.Handle,
                                  walker.Chore.Scenario, 0, true);
                }
                else
                {
                    Hold(walker);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not start an officer on something: " + ex.Message);
            }
        }

        /// <summary>
        /// Keeps an animation going. Scenarios hold themselves.
        ///
        /// Anim.Play returns early when the clip is already running, so calling this every pass
        /// is the correct way to hold a pose rather than a waste -- and it means the clip comes
        /// back if something else interrupted it.
        /// </summary>
        private static void Hold(Walker walker)
        {
            if (walker.Chore.Dict == null) return;

            Anim.Play(walker.Who, walker.Chore.Dict, walker.Chore.Clip, 1);
        }

        private void Done(Walker walker, int now)
        {
            walker.Doing = Errand.Posted;
            walker.BusyUntil = 0;
            walker.NextChoreAt = now + GapMinMs + _rng.Next(GapMaxMs - GapMinMs);

            try
            {
                if (walker.Alive) walker.Who.Task.ClearAll();
            }
            catch
            {
                // He is gone, which ends it just as well.
            }

            walker.Chore = default(Chore);

            if (Repost != null) Repost(walker);
        }
    }
}
