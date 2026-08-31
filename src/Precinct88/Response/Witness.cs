using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Streets;

namespace Precinct88.Response
{
    /// <summary>
    /// Everything that notices a crime and calls it in.
    ///
    /// SOMEBODY HAS TO SEE IT. That is the rule the whole file is built on, and it is the thing
    /// vanilla does not do -- shoot a man in an empty underground car park at four in the
    /// morning in GTA V and you get three stars, because the crime itself is the trigger and
    /// nobody needed to be there.
    ///
    /// Here a crime is an event and a REPORT is what reaches the police, and the second one
    /// requires a witness: an officer who can see it, or a member of the public who can, and
    /// who then takes a moment to get their phone out. No witness, no report -- and the body is
    /// still there for the next person to walk past, which is its own problem.
    ///
    /// Gunfire is the deliberate exception. It is heard rather than seen, so it carries a long
    /// way and through walls, and there is no sneaking around that with a rooftop and an angle.
    /// A silencer is a different matter and the game already models it.
    /// </summary>
    internal sealed class Witness
    {
        private const int TickMs = 500;

        /// <summary>How far a member of the public can see something happen.</summary>
        private const float PublicEyes = 45f;

        /// <summary>How far a shot carries to somebody who might call it in.</summary>
        private const float GunfireCarries = 130f;

        /// <summary>Suppressed, it carries about as far as a shout.</summary>
        private const float QuietGunfireCarries = 28f;

        /// <summary>
        /// How long between somebody seeing it and the police being told.
        ///
        /// A member of the public has to actually make the call, and the gap is playable
        /// space -- it is long enough to leave, which is the difference between a witness and
        /// a tripwire.
        /// </summary>
        private const int CallDelayMinMs = 3500;
        private const int CallDelayMaxMs = 11000;

        private readonly Settings _cfg;
        private readonly Manhunt _hunt;
        private readonly Random _rng = new Random();

        /// <summary>
        /// Bodies already counted.
        ///
        /// A corpse lies there for a long time and the scan runs twice a second, so without
        /// this one killing is reported over and over until the ped streams out.
        /// </summary>
        private readonly HashSet<int> _counted = new HashSet<int>();

        /// <summary>Reports somebody has seen but not yet phoned in.</summary>
        private readonly List<Pending> _pending = new List<Pending>();

        private int _lastTick;
        private int _lastShotAt;

        /// <summary>
        /// How close a member of the public has to be to give a face rather than a shirt.
        ///
        /// Somebody watching from across the street can tell you what colour the jacket was
        /// and what the car was. They could not pick the man out of a line-up, and a system
        /// that lets them is one where masks and distance mean nothing.
        /// </summary>
        private const float FaceRange = 18f;

        private sealed class Pending
        {
            public Offence What;
            public Vector3 Where;
            public int At;
            public Known Got;
        }

        public Witness(Settings cfg, Manhunt hunt)
        {
            _cfg = cfg;
            _hunt = hunt;
        }

        public void Update()
        {
            if (!_cfg.WantedEnabled) return;
            if (LawHold.Held) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            var me = Game.Player.Character;
            if (me == null || !me.Exists() || me.IsDead) return;

            try
            {
                Calls(now);
                Bodies(me, now);
                Gunfire(me, now);
            }
            catch (Exception ex)
            {
                Log.Debug("A witness check failed: " + ex.Message);
            }

            if (_counted.Count > 400) _counted.Clear();
        }

        /// <summary>Reports whose delay has run out.</summary>
        private void Calls(int now)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (now < _pending[i].At) continue;

                _hunt.Report(_pending[i].What, _pending[i].Where, _pending[i].Got);
                _pending.RemoveAt(i);
            }
        }

        /// <summary>
        /// Somebody saw it. The police hear about it shortly.
        ///
        /// An officer who sees it himself does not go through here -- he is already there and
        /// there is nobody to phone.
        /// </summary>
        private void Later(Offence what, Vector3 where, Known got)
        {
            _pending.Add(new Pending
            {
                What = what,
                Where = where,
                Got = got,
                At = Game.GameTime + CallDelayMinMs + _rng.Next(CallDelayMaxMs - CallDelayMinMs)
            });
        }

        // ---- bodies ------------------------------------------------------------

        private void Bodies(Ped me, int now)
        {
            foreach (var ped in World.GetNearbyPeds(me, 70f))
            {
                if (ped == null || !ped.Exists() || ped.IsAlive) continue;
                if (ped.Handle == me.Handle) continue;
                if (_counted.Contains(ped.Handle)) continue;

                _counted.Add(ped.Handle);

                if (!Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY,
                                         ped.Handle, me.Handle, true))
                {
                    // Somebody else's doing. Still marked, so it is not re-checked forever.
                    continue;
                }

                var cop = Cops.IsCop(ped);
                var what = cop ? Offence.OfficerDown : Offence.Homicide;

                // AN OFFICER DOWN NEEDS NO WITNESS, and that is the one place this file breaks
                // its own rule on purpose. A unit that stops answering is itself the report --
                // the radio goes quiet, and everybody knows what that means. Making a cop
                // killing require a second officer to have watched it would mean the quietest
                // possible way to deal with the police is to make sure you get the first one.
                if (cop)
                {
                    // An officer who saw it gets everything; one who did not is reporting a
                    // colleague who stopped answering, which is a location and nothing else.
                    _hunt.Report(what, ped.Position, LawSaw(me));
                    Log.Info("Officer down.");
                    continue;
                }

                var byLaw = LawSaw(me);
                if (byLaw != Known.Nothing) { _hunt.Report(what, ped.Position, byLaw); continue; }

                var byPublic = PublicSaw(ped.Position, me);
                var onCamera = Filmed(me);

                if (byPublic.HasValue || onCamera != Known.Nothing)
                {
                    Later(what, ped.Position, (byPublic ?? Known.Nothing) | onCamera);
                }
            }
        }

        // ---- gunfire -----------------------------------------------------------

        private void Gunfire(Ped me, int now)
        {
            if (!Function.Call<bool>(Hash.IS_PED_SHOOTING, me.Handle)) return;

            // One report a burst, not one a bullet.
            if (now - _lastShotAt < 4000) return;
            _lastShotAt = now;

            var carries = Quiet(me) ? QuietGunfireCarries : GunfireCarries;

            var byLaw = LawSaw(me, carries);

            if (byLaw != Known.Nothing)
            {
                _hunt.Report(Offence.ShotsFired, me.Position, byLaw | Known.Weapon);
                return;
            }

            // SEEN AND HEARD ARE DIFFERENT REPORTS, and this is the clearest place in the mod
            // where that matters. Somebody watching gives a description. Somebody two streets
            // over who only HEARD it gives a location and the fact that there were shots --
            // which is a real report, and the police arrive knowing nothing about who they are
            // looking for. That is most of how gunfire actually gets called in, and it is the
            // state vanilla cannot represent at all.
            var watching = PublicSaw(me.Position, me);
            var onCamera = Filmed(me);

            if (watching.HasValue || onCamera != Known.Nothing)
            {
                Later(Offence.ShotsFired, me.Position,
                      (watching ?? Known.Nothing) | onCamera | Known.Weapon);
                return;
            }

            foreach (var ped in World.GetNearbyPeds(me, carries))
            {
                if (ped == null || !ped.Exists() || !ped.IsAlive) continue;
                if (ped.Handle == me.Handle || Cops.IsCop(ped)) continue;

                Later(Offence.ShotsFired, me.Position, Known.Weapon);
                return;
            }
        }

        /// <summary>Whether what is in his hands is suppressed.</summary>
        private static bool Quiet(Ped me)
        {
            try
            {
                var weapon = me.Weapons == null ? null : me.Weapons.Current;
                if (weapon == null) return false;

                foreach (var c in weapon.Components)
                {
                    if (c == null || !c.Active) continue;

                    var name = c.ComponentHash.ToString();
                    if (name.IndexOf("SUPP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("SILENC", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Cannot tell, so it is loud. Failing towards being heard is the safer of the
                // two -- a silencer that does not work is a nuisance, a rifle nobody hears is
                // a broken mod.
            }

            return false;
        }

        // ---- who is looking ----------------------------------------------------

        /// <summary>
        /// What an officer who watched it would have. Known.Nothing when none did.
        ///
        /// Everything, because he is trained, he is looking for exactly this, and he has a
        /// radio in his hand -- except a face that is covered, which Radio.Note strips for us
        /// so that no witness anywhere can identify a man in a balaclava.
        /// </summary>
        private static Known LawSaw(Ped me, float range = 70f)
        {
            foreach (var officer in Cops.Near(me.Position, range))
            {
                if (Cops.Sees(officer, me, range))
                {
                    return Known.Face | Known.Clothes | Known.Vehicle;
                }
            }

            return Known.Nothing;
        }

        /// <summary>
        /// What an ordinary person watched, or null if nobody did.
        ///
        /// Not merely nearby: line of sight from the witness to the crime, and the witness has
        /// to be ALIVE to make the call afterwards -- which is a mechanic rather than a
        /// technicality, and players work it out very quickly.
        ///
        /// What they get depends on how close they were. The clothes and the car carry a long
        /// way; a face does not. The NEAREST witness is the one whose account goes out, because
        /// the best description in the crowd is the one that ends up on the radio.
        /// </summary>
        private static Known? PublicSaw(Vector3 where, Ped me)
        {
            Known? best = null;
            var closest = float.MaxValue;

            try
            {
                foreach (var ped in World.GetNearbyPeds(where, PublicEyes))
                {
                    if (ped == null || !ped.Exists() || !ped.IsAlive) continue;
                    if (ped.Handle == me.Handle || Cops.IsCop(ped)) continue;
                    if (!Cops.Sees(ped, me, PublicEyes)) continue;

                    var gap = ped.Position.DistanceTo(me.Position);
                    if (gap >= closest) continue;

                    closest = gap;
                    best = gap < FaceRange
                        ? Known.Face | Known.Clothes | Known.Vehicle
                        : Known.Clothes | Known.Vehicle;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not check for witnesses: " + ex.Message);
            }

            return best;
        }

        /// <summary>
        /// What a camera got, or nothing.
        ///
        /// A camera is not fooled by an empty street, which is the entire reason it exists --
        /// and unlike a person it cannot be killed afterwards to stop the call. It gets the
        /// same description a close witness would, plus the fact that there is footage.
        /// </summary>
        private Known Filmed(Ped me)
        {
            if (!_cfg.CamerasWatch) return Known.Nothing;

            return Cameras.Watching(me)
                ? Known.Camera | Known.Face | Known.Clothes | Known.Vehicle
                : Known.Nothing;
        }

        /// <summary>Forgets everything pending. For an arrest, a death, or a hold starting.</summary>
        public void Forget()
        {
            _pending.Clear();
        }
    }
}
