using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>One officer on foot, and what he is doing with himself.</summary>
    internal sealed class Walker
    {
        public Ped Who;

        /// <summary>When he has finished his shift and can be let go.</summary>
        public int OffDutyAt;

        /// <summary>Whether he walks a round or stands on a corner.</summary>
        public bool Wanders;

        /// <summary>
        /// Where he was put, so he can be sent back to it.
        ///
        /// Kept because a conversation takes him off his round -- without this he would resume
        /// wandering from wherever the person he stopped happened to be stood, and over an
        /// eight-minute shift he would drift across the district one chat at a time.
        /// </summary>
        public Vector3 PostedAt;

        /// <summary>What he is in the middle of. See Chats.</summary>
        public Errand Doing = Errand.Posted;

        /// <summary>Who he is crossing to, or stood talking to. Never ours.</summary>
        public Ped Subject;

        /// <summary>When the current errand gives up or runs out.</summary>
        public int StateUntil;

        /// <summary>When he may next bother somebody.</summary>
        public int NextChatAt;

        /// <summary>When somebody says the next thing.</summary>
        public int NextLineAt;

        /// <summary>When the notepad comes out mid-conversation, and whether it has.</summary>
        public int NotepadAt;
        public bool Writing;

        /// <summary>What he has stopped to do, and until when. See Rounds.</summary>
        public Chore Chore;
        public int BusyUntil;

        /// <summary>When he may next stop and do something.</summary>
        public int NextChoreAt;

        public bool Alive => Cops.Alive(Who);

        /// <summary>
        /// Hands him back, and whoever he was talking to with him.
        ///
        /// THE CIVILIAN IS THE PART THAT MATTERS HERE. A shift ending, a walk out of range, or
        /// the mod unloading all come through this -- and any of them can land in the middle of
        /// a conversation, leaving a pedestrian holding a look-at task aimed at a spot where an
        /// officer used to be. It expires on its own eventually, which is exactly why it would
        /// never have been traced back to here.
        /// </summary>
        public void Release()
        {
            try
            {
                if (Subject != null && Subject.Exists())
                {
                    Function.Call(Hash.TASK_CLEAR_LOOK_AT, Subject.Handle);
                    Subject.MarkAsNoLongerNeeded();
                }
            }
            catch
            {
                // He is gone, which is the outcome this was arranging anyway.
            }

            Subject = null;

            Cops.LetGo(Who);
        }
    }

    /// <summary>
    /// Police who are not in a car.
    ///
    /// EVERY OFFICER IN THIS MOD HAS BEEN INSIDE A VEHICLE, which quietly shaped everything
    /// built on top of them. A stop could only ever come from a car. Being seen with a gun
    /// meant being seen from a road. A street with no traffic on it was a street with no police
    /// on it, however busy the pavement was -- and the district densities were secretly
    /// describing traffic rather than presence.
    ///
    /// A man on foot fixes that for nothing, because everything downstream already works on
    /// PED_TYPE rather than on membership of the fleet: Cops.IsCop finds him, Witness counts him
    /// as somebody who saw it, Manhunt's sight check includes him, and Watch will start a stop
    /// off him. Nothing needed changing to make him count. That is what the type check was for.
    ///
    /// "Cops: Back on the Beat" does this by editing popgroups.ymt to raise the on-foot police
    /// density. This does it in script for the same reason as everything else in the mod -- an
    /// RPF edit is an asset mod, and every legacy-era asset mod tested on this machine has
    /// crashed the Enhanced install where its content streams.
    ///
    /// DELIBERATELY THIN. They walk, or they stand and look at things. They do not have routes,
    /// partners, radios or opinions -- the systems that already exist supply all of that the
    /// moment one of them sees something. A foot patrol that tried to be a Unit would be a
    /// second, worse Fleet.
    /// </summary>
    internal sealed class Foot
    {
        private const int TickMs = 1400;

        /// <summary>Far enough out not to appear, near enough to matter.</summary>
        private const float SpawnNear = 55f;
        private const float SpawnFar = 110f;

        /// <summary>
        /// Past this he is somebody else's problem.
        ///
        /// RAISED WITH THE ROUND, and it has to be. An officer given a hundred and thirty
        /// metres to cover who is let go at a hundred and ninety is an officer deleted halfway
        /// through his own round the moment the player walks the other way -- which reads as
        /// foot patrols that vanish rather than as one that went round the corner.
        /// </summary>
        private const float LetGoRange = 260f;

        /// <summary>How long between one going out and the next, before the dice.</summary>
        private const int GapMinMs = 30000;
        private const int GapMaxMs = 95000;

        /// <summary>A shift. Shorter than a car's round, because they cover less ground.</summary>
        private const int ShiftMinMs = 240000;
        private const int ShiftMaxMs = 480000;

        /// <summary>
        /// Scenarios a police officer can plausibly be doing on a pavement.
        ///
        /// Guarded like everything else -- a name this build has not got simply fails to start
        /// and he stands there instead, which is a perfectly good thing for a police officer to
        /// be doing.
        /// </summary>
        private static readonly string[] Standing =
        {
            "WORLD_HUMAN_COP_IDLES",
            "WORLD_HUMAN_GUARD_STAND",
            "WORLD_HUMAN_CLIPBOARD",
            "WORLD_HUMAN_STAND_MOBILE",
        };

        private readonly Settings _cfg;
        private readonly Random _rng = new Random();
        private readonly List<Walker> _out = new List<Walker>();

        private int _lastTick;
        private int _nextSpawn;

        /// <summary>
        /// Officers stopping to talk to people.
        ///
        /// Its own file and its own object rather than more of this one, so it can be taken
        /// out again without unpicking foot patrol -- and so the thing being judged when
        /// somebody says "the conversations look wrong" is one file.
        /// </summary>
        private readonly Chats _chats;

        /// <summary>
        /// What he does when he stops.
        ///
        /// Its own object beside Chats rather than more of this file, and the two never fight:
        /// both act only on a walker in Errand.Posted and both hand him back to it, so a man
        /// crossing the road to speak to somebody cannot also be getting his notepad out.
        /// </summary>
        private readonly Rounds _rounds;

        /// <summary>Off while something louder is happening. Wired by Main.</summary>
        public Func<bool> Busy;

        public Foot(Settings cfg)
        {
            _cfg = cfg;

            // The same Random, deliberately. Two of them seeded a millisecond apart on the
            // same tick is the classic way to get two systems making identical "random"
            // choices all session.
            _chats = new Chats(_rng) { Repost = w => Post(w, w.PostedAt) };
            _rounds = new Rounds(_rng) { Repost = w => Post(w, w.PostedAt) };
        }

        public int Count => _out.Count;

        /// <summary>Live list, for anything that wants to draw them. Do not hold it.</summary>
        public IReadOnlyList<Walker> Walkers => _out;

        public void Update()
        {
            if (!_cfg.FootPatrols) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            var me = Game.Player.Character;
            if (me == null || !me.Exists()) return;

            var at = me.Position;

            Sweep(at, now);

            var quiet = !Response.LawHold.Held && (Busy == null || !Busy());

            // BEFORE THE RETURN, AND TICKED EITHER WAY. A hold that begins mid-sentence must
            // still be able to end the sentence -- otherwise the officer holds a look-at at a
            // man until the gang war is over, which is a considerably stranger sight than the
            // conversation was. Only STARTING one is gated on things being quiet.
            _chats.Update(_out, now, quiet);

            // AFTER CHATS, so somebody who has just been given a conversation is not also
            // handed a clipboard on the same tick. Chats takes him out of Posted, and Rounds
            // only ever looks at men who are still in it.
            _rounds.Update(_out, now, quiet);

            if (!quiet) return;

            if (now >= _nextSpawn && _out.Count < Wanted(at)) TryPutOneOut(at, now);
        }

        /// <summary>
        /// How many should be about.
        ///
        /// FOOT PATROLS ARE A TOWN THING. An officer walking a beat down a country road in
        /// Blaine County is not policing, it is a man who has lost his car -- so this is gated
        /// on the district being policed by a city force, which is the same fact the speed
        /// limits and the agencies both already use.
        /// </summary>
        private int Wanted(Vector3 at)
        {
            var here = Districts.At(at);

            if (here.Density <= 0f) return 0;

            if (!string.Equals(here.Force, "City", StringComparison.OrdinalIgnoreCase)) return 0;

            var n = (int)Math.Round(_cfg.FootUnits * here.Density);

            return n < 0 ? 0 : n;
        }

        private void Sweep(Vector3 at, int now)
        {
            for (var i = _out.Count - 1; i >= 0; i--)
            {
                var walker = _out[i];

                var keep = walker.Alive &&
                           now < walker.OffDutyAt &&
                           walker.Who.Position.DistanceTo(at) < LetGoRange;

                if (keep) continue;

                walker.Release();
                _out.RemoveAt(i);
            }
        }

        private void TryPutOneOut(Vector3 playerAt, int now)
        {
            _nextSpawn = now + GapMinMs + _rng.Next(GapMaxMs - GapMinMs);

            var beat = Districts.At(playerAt);

            Vector3 spot;
            if (!Pavement(playerAt, out spot)) return;

            var force = Agencies.For(beat, spot, _rng);

            var model = force.Ped(_rng);
            if (model == null) return;

            var loaded = Cops.Load(force.Name + " uniform", model);
            if (loaded == null) return;

            try
            {
                var who = World.CreatePed(loaded.Value, spot);
                loaded.Value.MarkAsNoLongerNeeded();

                if (!Cops.Alive(who)) return;

                Dress(who);

                var walker = new Walker
                {
                    Who = who,
                    OffDutyAt = now + ShiftMinMs + _rng.Next(ShiftMaxMs - ShiftMinMs),
                    // MOST OF THEM WALK NOW. It was a sixty-forty split when walking was
                    // all a walker could do and two men stood on corners were the variety. The
                    // variety is what he does when he stops, so the ones who never move are
                    // the ones with nothing to show.
                    Wanders = _rng.Next(100) < 82,
                    PostedAt = spot,

                    // Not immediately. An officer who materialises and walks straight at the
                    // nearest person is a spawn with a task on it; one who has been on the
                    // street a while first is an officer.
                    NextChatAt = now + 8000 + _rng.Next(20000),

                    // Staggered against the chat clock so the two do not come due together and
                    // give every officer the same rhythm.
                    NextChoreAt = now + 18000 + _rng.Next(40000),
                };

                Post(walker, spot);

                _out.Add(walker);

                Log.Debug("Foot patrol out in " + beat.Name + " (" + _out.Count + " walking).");
            }
            catch (Exception ex)
            {
                Log.Error("Could not put an officer on foot.", ex);
            }
        }

        /// <summary>
        /// Somewhere a person can actually stand, out of sight.
        ///
        /// GET_SAFE_COORD_FOR_PED is the game's own answer to "is this a place a pedestrian
        /// belongs", which is a much harder question than it sounds -- it rules out roads,
        /// water, roofs, and the inside of walls, all of which a naive offset finds constantly.
        /// </summary>
        private bool Pavement(Vector3 near, out Vector3 spot)
        {
            spot = Vector3.Zero;

            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    var angle = _rng.NextDouble() * Math.PI * 2d;
                    var dist = SpawnNear + (float)_rng.NextDouble() * (SpawnFar - SpawnNear);

                    var guess = near + new Vector3((float)Math.Cos(angle) * dist,
                                                   (float)Math.Sin(angle) * dist, 0f);

                    var got = new OutputArgument();

                    // 16 is the usual flag set for "on a pavement, not in a road".
                    if (!Function.Call<bool>(Hash.GET_SAFE_COORD_FOR_PED,
                                             guess.X, guess.Y, guess.Z, true, got, 16))
                    {
                        continue;
                    }

                    var safe = got.GetResult<Vector3>();
                    if (safe == Vector3.Zero) continue;

                    // Not in front of him, same rule as the cars. Somebody fading in on a
                    // pavement is worse than a car doing it, because you are looking at the
                    // pavement.
                    if (Function.Call<bool>(Hash.IS_SPHERE_VISIBLE, safe.X, safe.Y, safe.Z, 4f))
                    {
                        continue;
                    }

                    spot = safe;
                    return true;
                }
                catch
                {
                    // Next try.
                }
            }

            return false;
        }

        /// <summary>Sets him walking, or standing somewhere doing something.</summary>
        /// <summary>
        /// Puts an officer on his round.
        ///
        /// Called on spawn and again every time a conversation ends, which is why it takes the
        /// spot rather than reading his current position: sending him back to where he is
        /// standing would leave him wandering out from wherever he last stopped somebody.
        /// </summary>
        private void Post(Walker walker, Vector3 spot)
        {
            if (walker == null || !walker.Alive) return;

            try
            {
                // CLEARED FIRST, ALWAYS, and this one line is why it lives here rather than in
                // the two callers. A scenario -- the clipboard he was writing on, the phone he
                // was on -- does not reliably give way to a wander task issued on top of it,
                // and when it does not, an officer stands writing in the air for the rest of
                // his shift. On a fresh spawn there is nothing to clear and it costs nothing.
                walker.Who.Task.ClearAll();

                if (walker.Wanders)
                {
                    // A ROUND RATHER THAN A WANDER ACROSS THE MAP, and the three numbers after
                    // the position are the whole of what that means.
                    //
                    // 130 is the radius -- about a city block rather than the sixty metres it
                    // was, which had him turning round in the middle of a street for no reason
                    // a player could see. 26 is the MINIMUM LENGTH OF ONE LEG and is the number
                    // that actually changed how he reads: at 3 he took a few steps, stopped,
                    // and picked somewhere else, which is a man who has lost his keys. And 2 is
                    // the pause between legs, down from 8, because the standing about is now
                    // Rounds' job and it does it with a clipboard in his hand.
                    Function.Call(Hash.TASK_WANDER_IN_AREA, walker.Who.Handle,
                                  spot.X, spot.Y, spot.Z, 130f, 26f, 2f);
                    return;
                }

                var scenario = Standing[_rng.Next(Standing.Length)];

                Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, walker.Who.Handle,
                              scenario, 0, true);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not post an officer: " + ex.Message);
            }
        }

        /// <summary>
        /// The same treatment the crews in cars get.
        ///
        /// SET_PED_AS_COP is the load-bearing one and the reason nothing downstream needed
        /// changing: without it the game does not treat him as law, so Cops.IsCop returns false,
        /// no witness check counts him, and he is a man in a costume.
        /// </summary>
        private static void Dress(Ped who)
        {
            try
            {
                Function.Call(Hash.SET_PED_AS_COP, who.Handle, true);
                Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, who.Handle,
                              Function.Call<int>(Hash.GET_HASH_KEY, "COP"));

                who.IsPersistent = true;
                who.BlockPermanentEvents = false;

                who.Weapons.Give(WeaponHash.Pistol, 60, false, true);
                who.Weapons.Give(WeaponHash.StunGun, 1, false, false);

                who.Armor = 40;

                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, who.Handle, 46, true);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, who.Handle, 5, true);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, who.Handle, 0, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set a foot officer up: " + ex.Message);
            }
        }

        /// <summary>Hands everybody back. Teardown only.</summary>
        public void Release()
        {
            foreach (var walker in _out) walker.Release();
            _out.Clear();
        }
    }
}
