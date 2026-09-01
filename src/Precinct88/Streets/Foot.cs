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

        /// <summary>Whether he walks a beat or stands on a corner.</summary>
        public bool Wanders;

        public bool Alive => Cops.Alive(Who);

        public void Release() => Cops.LetGo(Who);
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

        /// <summary>Past this he is somebody else's problem.</summary>
        private const float LetGoRange = 190f;

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

        /// <summary>Off while something louder is happening. Wired by Main.</summary>
        public Func<bool> Busy;

        public Foot(Settings cfg)
        {
            _cfg = cfg;
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

            if (Response.LawHold.Held) return;
            if (Busy != null && Busy()) return;

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
                    Wanders = _rng.Next(100) < 60,
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
        private void Post(Walker walker, Vector3 spot)
        {
            try
            {
                if (walker.Wanders)
                {
                    // A beat rather than a wander across the map: he stays roughly where he was
                    // put, which is what makes him feel assigned to a street.
                    Function.Call(Hash.TASK_WANDER_IN_AREA, walker.Who.Handle,
                                  spot.X, spot.Y, spot.Z, 60f, 3f, 8f);
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
