using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Response
{
    /// <summary>
    /// How much force the police are willing to use, and it is not always lethal.
    ///
    /// THE SINGLE WORST THING ABOUT VANILLA'S POLICE. One star in GTA V means armed officers
    /// firing pistols at a man who has done nothing worse than shove somebody, and it is why
    /// every low-level encounter in the game turns into a gunfight within about four seconds.
    /// There is no rung on the ladder between "ignored" and "shot at".
    ///
    /// So there is one now. Below the lethal threshold every officer near you is carrying a
    /// stun gun and nothing else -- they will still come, still chase, still take you down, and
    /// you will still lose, but you will lose consciousness rather than your life. At three
    /// stars they get their sidearms back, because by then you have done something that
    /// warrants it.
    ///
    /// IT REACHES EVERY OFFICER, NOT JUST OURS, and that is deliberate. The engine's dispatch
    /// is running alongside this build, so a rule applied only to the finite pool would be a
    /// rule the player watches get broken by the next car that turns up. Anybody wearing the
    /// uniform within range is re-armed, whoever spawned them.
    ///
    /// WHAT IT TAKES AWAY IT GIVES BACK. Every ped disarmed here is remembered, and the moment
    /// the level crosses the threshold they get a sidearm again -- otherwise a five-star
    /// firefight would be fought by whichever officers happened to arrive after the escalation
    /// while the ones already there stood about with tasers.
    /// </summary>
    internal sealed class Restraint
    {
        private const int TickMs = 1100;

        /// <summary>
        /// How far out officers are re-armed.
        ///
        /// Generous, because the swap has to have happened by the time anybody is close enough
        /// to shoot. Doing it at conversational range means the first shot is fired with the
        /// old weapon and the rule is broken exactly when it matters.
        /// </summary>
        private const float Range = 140f;

        /// <summary>
        /// The star at which they may kill you.
        ///
        /// Three. One and two are a shove, a stolen car, a man who would not stop -- things the
        /// police in any city deal with by putting hands on somebody. Three is where this mod
        /// already puts armed robbery and worse.
        /// </summary>
        private const int LethalAt = 3;

        /// <summary>How long somebody stays down once stunned. Long enough to be searched.</summary>
        private const int GroundTimeMs = 6500;

        private readonly Settings _cfg;

        /// <summary>
        /// Handles of everybody we have taken a sidearm off.
        ///
        /// Handles rather than Peds on purpose: this is only ever asked "did we do this one",
        /// the ped may be long gone by the time it is asked, and holding references to dead
        /// peds is how a script keeps entities alive that the game wants to stream out.
        /// </summary>
        private readonly HashSet<int> _stunned = new HashSet<int>();

        private int _lastTick;
        private bool _lethalLast;

        public Restraint(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>Whether the police are currently allowed to shoot. Read by the search.</summary>
        public bool Lethal { get; private set; }

        public void Update()
        {
            if (!_cfg.LethalEscalation) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                var stars = Game.Player.Wanted.WantedLevel;

                // A DRAWN GUN OUTRANKS THE STAR COUNT, and nothing about that is a special
                // case -- it is the rule the stars were always a proxy for. The ladder exists
                // because a man who shoved somebody should not be shot at; it was never an
                // argument that a man pointing a pistol should be tased. No police force in
                // the world answers a firearm with a stun gun because the paperwork is light.
                //
                // It also closes the obvious exploit in the whole idea: at one star, with
                // stun guns the only thing anybody is carrying, an armed player was
                // effectively invulnerable to the entire police force.
                var gun = Cops.HasGun(Game.Player.Character);

                Lethal = stars >= LethalAt || gun;

                if (Lethal != _lethalLast)
                {
                    Log.Info(Lethal
                        ? "Force: lethal (" + (gun ? "he has a gun out" : stars + " stars") + ")."
                        : "Force: stun guns only at " + stars + " stars.");

                    _lethalLast = Lethal;
                }

                // NOT GATED ON HAVING A WANTED LEVEL. At zero stars there is nothing to do
                // about lethality, but there may still be officers we disarmed a moment ago
                // who need their sidearms back before the next thing happens.
                foreach (var ped in World.GetNearbyPeds(me, Range))
                {
                    if (!Cops.Alive(ped)) continue;
                    if (!Cops.IsCop(ped)) continue;

                    if (Lethal) Arm(ped);
                    else Disarm(ped);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set what the police are carrying: " + ex.Message);
            }
        }

        /// <summary>Stun gun and nothing else.</summary>
        private void Disarm(Ped who)
        {
            if (_stunned.Contains(who.Handle)) return;

            try
            {
                who.Weapons.RemoveAll();
                who.Weapons.Give(WeaponHash.StunGun, 200, true, true);

                // NOT DROPPED ON DEATH. Otherwise every low-level encounter becomes a way to
                // farm stun guns off the pavement, which is a thing this rule accidentally
                // invents and nobody wants.
                Function.Call(Hash.SET_PED_DROPS_WEAPONS_WHEN_DEAD, who.Handle, false);

                // Long enough on the ground to actually be searched. The default is about a
                // second and a half, which is not a takedown, it is a stumble.
                Function.Call(Hash.SET_PED_MIN_GROUND_TIME_FOR_STUNGUN, who.Handle, GroundTimeMs);

                Close(who, true);

                _stunned.Add(who.Handle);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not hand out a stun gun: " + ex.Message);
            }
        }

        /// <summary>His sidearm back, because it has stopped being that kind of evening.</summary>
        private void Arm(Ped who)
        {
            if (!_stunned.Remove(who.Handle)) return;

            try
            {
                who.Weapons.RemoveAll();
                who.Weapons.Give(WeaponHash.Pistol, 250, true, true);

                Function.Call(Hash.SET_PED_DROPS_WEAPONS_WHEN_DEAD, who.Handle, true);

                Close(who, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not give a sidearm back: " + ex.Message);
            }
        }

        /// <summary>
        /// Whether he closes the distance or holds off and shoots.
        ///
        /// THE HALF THE STUN GUNS WERE MISSING, and without it the whole idea failed in the
        /// most literal way possible: officers were given a weapon that works at three metres
        /// and left on the combat behaviour for one that works at forty. So they did exactly
        /// what they were told -- took position behind their cars, held the range, and fired
        /// tasers across a road at somebody they were never going to hit.
        ///
        /// Four settings and they are all the same instruction said four ways.
        ///
        /// COMBAT MOVEMENT 2 is "will advance". The default has them hold ground and shoot,
        /// which is correct for a pistol and useless for this.
        ///
        /// COMBAT RANGE 0 is "near" -- how far off he is willing to settle and start
        /// shooting. On the default he stops at the range the pistol wanted.
        ///
        /// NO COVER. Attribute 0 is CA_USE_COVER, and a man taking cover from somebody he
        /// intends to lay hands on is a man who has misunderstood his job. It is also what
        /// pinned them to their cars in the first place.
        ///
        /// AND OUT OF THE CAR. Attribute 3 is CA_LEAVE_VEHICLES: you cannot tase anybody
        /// through a windscreen.
        ///
        /// All four are put back when they get their sidearms, because holding the range is
        /// the RIGHT behaviour with a pistol and walking calmly towards an armed man to grab
        /// him is not.
        /// </summary>
        private static void Close(Ped who, bool near)
        {
            try
            {
                Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, who.Handle, near ? 2 : 1);
                Function.Call(Hash.SET_PED_COMBAT_RANGE, who.Handle, near ? 0 : 1);

                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, who.Handle, 0, !near);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, who.Handle, 3, true);

                // He has to be able to see far enough to decide to start walking. The default
                // is generous already; this only matters at the top of the range.
                Function.Call(Hash.SET_PED_SEEING_RANGE, who.Handle, near ? 70f : 100f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set how an officer closes: " + ex.Message);
            }
        }

        /// <summary>
        /// Everybody armed again, whatever was going on.
        ///
        /// Called when the mod unloads. A city full of officers who can only taser people,
        /// left behind by a script that is no longer running, is a change to the game nobody
        /// can find the source of -- and unlike most of what this mod does it would persist
        /// for the whole session.
        /// </summary>
        public void Release()
        {
            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) { _stunned.Clear(); return; }

                foreach (var ped in World.GetNearbyPeds(me, Range * 2f))
                {
                    if (!Cops.Alive(ped)) continue;
                    if (!_stunned.Contains(ped.Handle)) continue;

                    Arm(ped);
                }
            }
            catch
            {
                // Teardown. Nothing left to tell.
            }

            _stunned.Clear();
        }
    }
}
