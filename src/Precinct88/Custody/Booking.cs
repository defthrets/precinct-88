using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Response;
using Precinct88.Streets;
using Precinct88.UI;

namespace Precinct88.Custody
{
    internal enum Held
    {
        No,

        /// <summary>Cuffed on the pavement, being walked to the car.</summary>
        Cuffed,

        /// <summary>In the back of it.</summary>
        Riding,

        /// <summary>Black screen, clock running.</summary>
        Inside,

        /// <summary>Out the front, with whatever is left.</summary>
        Released,
    }

    /// <summary>
    /// What happens after they have you.
    ///
    /// The vanilla arrest is a fade to black, a fee, and a hospital respawn -- which is the same
    /// thing that happens when you die, so the game's own police have exactly one outcome
    /// wearing two costumes. Nothing is taken that you would miss, nothing is shown, and there
    /// is no reason to have preferred being arrested to being shot.
    ///
    /// THE POINT OF THIS FILE IS THAT BEING CAUGHT COSTS SOMETHING SPECIFIC. Weapons go.
    /// Contraband goes, whatever the other mod says contraband is. There is a fine, scaled to
    /// what they booked you for. And there is a wait, which is the only one of the four that
    /// anybody argues about -- it is real seconds of not playing, and it is the reason the
    /// surrender prompt is a decision rather than a free out. HoldSeconds can be set to zero by
    /// anybody who disagrees.
    ///
    /// You are released at the station you were taken to, on the pavement, with your car
    /// wherever you left it. That last part is not an oversight either.
    /// </summary>
    internal sealed class Booking
    {
        private const int TickMs = 100;

        /// <summary>How long the walk to the car is allowed to take before it is skipped.</summary>
        private const int WalkPatienceMs = 22000;

        /// <summary>How long the drive is shown for before the screen goes out.</summary>
        private const int RideMs = 2600;

        private readonly Settings _cfg;
        private readonly Manhunt _hunt;
        private readonly Witness _witness;

        private Held _at = Held.No;
        private Ped _officer;
        private Vehicle _car;
        private Station _to;

        private int _lastTick;
        private int _phaseAt;
        private int _outAt;
        private string _reason = string.Empty;
        private bool _blacked;

        /// <summary>
        /// Takes everything the player should not walk out with.
        ///
        /// Same shape as the one on Stop and for the same reason: whoever is on the bridge
        /// knows what contraband is and has already taken it by the time this returns. The
        /// string is what to show on the custody screen.
        /// </summary>
        public Func<string, string> Seize;

        public Booking(Settings cfg, Manhunt hunt, Witness witness)
        {
            _cfg = cfg;
            _hunt = hunt;
            _witness = witness;
        }

        public bool Running => _at != Held.No;

        /// <summary>Whether the player is under the mod's control and nothing else should act.</summary>
        public bool InCustody => _at == Held.Cuffed || _at == Held.Riding || _at == Held.Inside;

        // ---- starting ----------------------------------------------------------

        /// <summary>
        /// They have you. Everything from here is this class.
        ///
        /// The officer may be null -- the game arrests players on its own, and a booking that
        /// refused to start without a named officer would simply not fire on the most common
        /// way of being caught. With nobody named, the walk and the ride are skipped and it
        /// goes straight to the hold.
        /// </summary>
        public void Begin(Ped officer, string reason)
        {
            if (Running || !_cfg.CustodyEnabled) return;

            var me = Game.Player.Character;
            if (me == null || !me.Exists() || me.IsDead) return;

            _officer = officer;
            _reason = string.IsNullOrEmpty(reason) ? "an outstanding matter" : reason;
            _phaseAt = Game.GameTime;
            _blacked = false;

            _to = Stations.For(Districts.At(me.Position), me.Position);

            // The manhunt is over the moment they have hold of you. Left running, the wanted
            // level survives into the custody screen and the player is released still wanted
            // for the thing he has just been booked for -- which reads as the mod having
            // achieved nothing.
            _hunt.Clear("arrested");
            if (_witness != null) _witness.Forget();

            // HELD, not capped, and released in Finish. For the length of a booking the player
            // genuinely is not a police matter -- he is already in their hands.
            LawHold.Hold("booking");

            try
            {
                me.Weapons.Select(WeaponHash.Unarmed, true);
                Anim.Play(me, Anim.CuffedDict, Anim.CuffedClip, 49);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not cuff: " + ex.Message);
            }

            _at = _cfg.WalkToTheCar && Cops.Alive(officer) ? Held.Cuffed : Held.Inside;

            if (_at == Held.Inside) _outAt = Game.GameTime + Wait();

            Screen.Ticker("Under arrest: " + _reason + ".");
            Log.Info("Booked for " + _reason + ", taken to " + (_to == null ? "?" : _to.Name) + ".");
        }

        /// <summary>How long they hold you, scaled by what for.</summary>
        private int Wait()
        {
            var mult = _hunt.Worst == null ? 1f : 0.6f + _hunt.Worst.Ceiling * 0.35f;
            return (int)(_cfg.HoldSeconds * 1000f * mult);
        }

        // ---- per-tick ----------------------------------------------------------

        public void Update()
        {
            if (!Running) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            var me = Game.Player.Character;

            if (me == null || !me.Exists() || me.IsDead)
            {
                // Died in custody. Everything is put back and nothing is taken -- being shot
                // in the back of a police car should not also cost the fine.
                Finish(false);
                return;
            }

            switch (_at)
            {
                case Held.Cuffed: Walking(me, now); break;
                case Held.Riding: Riding(me, now); break;
                case Held.Inside: Inside(me, now); break;
            }
        }

        private void Walking(Ped me, int now)
        {
            Screen.NoHud();
            Screen.Help("You are under arrest.");

            Anim.Play(me, Anim.CuffedDict, Anim.CuffedClip, 49);

            // Cuffed means cuffed. Attack and aim off, so the player cannot swing at the
            // officer walking him to the car and turn a booking into a firefight the mod is
            // halfway through.
            Hobble();

            if (!Cops.Alive(_officer) || now - _phaseAt > WalkPatienceMs)
            {
                // Lost the escort. Rather than leave the player cuffed in the street forever,
                // the booking carries on without the journey.
                Go(Held.Inside, now);
                _outAt = now + Wait();
                return;
            }

            _car = FindCar();

            if (!Cops.Alive(_car))
            {
                Go(Held.Inside, now);
                _outAt = now + Wait();
                return;
            }

            try
            {
                Function.Call(Hash.TASK_GO_TO_ENTITY, _officer.Handle, _car.Handle,
                              -1, 3f, 1.6f, 0f, 0);
            }
            catch
            {
                // He gets there or he does not; the timeout above covers it.
            }

            if (me.Position.DistanceTo(_car.Position) > 7f)
            {
                // Walked along behind him. Set rather than tasked, because a cuffed ped given a
                // follow task fights the cuffed animation and ends up sliding.
                try
                {
                    var behind = _car.Position - _car.ForwardVector * 3f;
                    var step = (behind - me.Position);

                    if (step.Length() > 0.6f)
                    {
                        step.Normalize();
                        me.Position = me.Position + step * 0.05f;
                    }
                }
                catch
                {
                    // Cosmetic.
                }

                return;
            }

            // In the back. Seat 1 is behind the driver, which is where somebody cuffed goes.
            try
            {
                Function.Call(Hash.SET_PED_INTO_VEHICLE, me.Handle, _car.Handle, 1);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not put the player in the car: " + ex.Message);
            }

            Go(Held.Riding, now);
        }

        private void Riding(Ped me, int now)
        {
            Screen.NoHud();
            Hobble();

            Anim.Play(me, Anim.CuffedDict, Anim.CuffedClip, 49);

            if (now - _phaseAt < RideMs) return;

            Go(Held.Inside, now);
            _outAt = now + Wait();
        }

        /// <summary>
        /// The hold.
        ///
        /// Drawn black rather than faded. A fade is a transition the game owns and will end on
        /// its own; this is a state that lasts as long as it lasts, and it has to be able to
        /// show a clock over the top of itself.
        /// </summary>
        private void Inside(Ped me, int now)
        {
            Screen.NoHud();
            Screen.Black();
            Hobble();

            if (!_blacked)
            {
                _blacked = true;
                Put(me);
            }

            var left = Math.Max(0, _outAt - now);

            Screen.Line(_to == null ? "IN CUSTODY" : _to.Name.ToUpperInvariant() + " STATION",
                        0.40f, 0.9f);
            Screen.Line("Held for " + _reason, 0.47f, 0.5f, 200);

            if (left > 0)
            {
                Screen.Line("Released in " + (left / 1000 + 1) + "s", 0.545f, 0.45f, 150);
                return;
            }

            Finish(true);
        }

        /// <summary>
        /// Moves the player to the station while nothing can be seen.
        ///
        /// Done under the blackout rather than with a fade, so there is no frame in which the
        /// player is stood in a police station with the HUD up wondering what happened.
        /// </summary>
        private void Put(Ped me)
        {
            try
            {
                if (_to == null) return;

                if (me.IsInVehicle())
                {
                    Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, me.Handle);
                }

                me.Position = _to.Desk;
                me.Heading = _to.DeskHeading;

                Function.Call(Hash.FREEZE_ENTITY_POSITION, me.Handle, true);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not put the player at the desk: " + ex.Message);
            }
        }

        // ---- what it costs -----------------------------------------------------

        private void Finish(bool charge)
        {
            var me = Game.Player.Character;

            try
            {
                if (me != null && me.Exists())
                {
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, me.Handle, false);
                    Anim.Stop(me, Anim.CuffedDict, Anim.CuffedClip);
                    Function.Call(Hash.CLEAR_PED_TASKS, me.Handle);
                }
            }
            catch
            {
                // Teardown. The releases below matter more than the animation.
            }

            if (charge && me != null && me.Exists()) Charge(me);

            // The car and the escort are handed back to the game. They were part of a scene
            // that is over, and holding them would leave a squad car parked in the street for
            // the rest of the session.
            Cops.LetGo(_officer);
            Cops.LetGo(_car);

            _officer = null;
            _car = null;
            _at = Held.No;
            _blacked = false;

            // LAST, AND ALWAYS. Every path out of a booking comes through here, including
            // dying in the back of the car -- so this is the one line standing between a
            // crashed scene and a player who can never be arrested again.
            LawHold.ReleaseAll();

            Screen.FadeIn(900);
        }

        private void Charge(Ped me)
        {
            var lines = string.Empty;

            try
            {
                if (_cfg.ConfiscateWeapons)
                {
                    Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, me.Handle, true);
                    lines = "weapons";
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not take the weapons: " + ex.Message);
            }

            if (_cfg.ConfiscateContraband && Seize != null)
            {
                try
                {
                    var took = Seize(_reason);

                    if (!string.IsNullOrEmpty(took))
                    {
                        lines = string.IsNullOrEmpty(lines) ? took : lines + ", " + took;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("The seizure handler threw: " + ex.Message);
                }
            }

            var fine = Fine();

            if (fine > 0)
            {
                try
                {
                    // Never below zero. A negative balance is a state the game's own UI does
                    // not really handle, and a fine that puts somebody there is a fine that
                    // looks like a bug.
                    var have = Game.Player.Money;
                    Game.Player.Money = Math.Max(0, have - fine);

                    if (have > 0)
                    {
                        lines = string.IsNullOrEmpty(lines)
                            ? "$" + Math.Min(have, fine)
                            : lines + ", $" + Math.Min(have, fine);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not take the fine: " + ex.Message);
                }
            }

            Screen.Ticker(string.IsNullOrEmpty(lines)
                ? "Released. They had nothing to hold you on."
                : "Released. Seized: " + lines + ".");

            Log.Info("Released. Seized: " + (string.IsNullOrEmpty(lines) ? "nothing" : lines) + ".");
        }

        private int Fine()
        {
            var mult = _hunt.Worst == null ? 1f : 0.5f + _hunt.Worst.Ceiling * 0.4f;
            return (int)(_cfg.Fine * mult);
        }

        // ---- the rest ----------------------------------------------------------

        private void Go(Held next, int now)
        {
            _at = next;
            _phaseAt = now;
        }

        private Vehicle FindCar()
        {
            if (Cops.Alive(_car)) return _car;

            try
            {
                if (Cops.Alive(_officer) && Cops.Alive(_officer.LastVehicle))
                {
                    return _officer.LastVehicle;
                }

                var me = Game.Player.Character;

                foreach (var car in World.GetNearbyVehicles(me.Position, 40f))
                {
                    if (!Cops.Alive(car)) continue;

                    foreach (var name in Cops.Cars)
                    {
                        if (car.Model == new Model(name)) return car;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not find a car to put him in: " + ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Takes the player's hands away without taking the camera.
        ///
        /// Disabled controls rather than SET_PLAYER_CONTROL(false), which also stops him
        /// looking around -- and a two-minute booking in which the camera is frozen facing a
        /// wall is a two-minute booking spent looking at a wall. Must be called every frame.
        /// </summary>
        private static void Hobble()
        {
            try
            {
                Game.DisableControlThisFrame(GTA.Control.Attack);
                Game.DisableControlThisFrame(GTA.Control.Attack2);
                Game.DisableControlThisFrame(GTA.Control.Aim);
                Game.DisableControlThisFrame(GTA.Control.Jump);
                Game.DisableControlThisFrame(GTA.Control.Enter);
                Game.DisableControlThisFrame(GTA.Control.SelectWeapon);
                Game.DisableControlThisFrame(GTA.Control.Sprint);
            }
            catch
            {
                // Cosmetic in the worst case.
            }
        }

        /// <summary>Ends it wherever it is, putting everything back. Teardown only.</summary>
        public void Abandon()
        {
            if (!Running) return;

            Log.Warn("Booking abandoned mid-scene; putting everything back.");
            Finish(false);
        }
    }
}
