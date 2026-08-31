using System;
using GTA;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Response;
using Precinct88.UI;

namespace Precinct88.Custody
{
    /// <summary>
    /// Giving up, which vanilla GTA V does not let you do.
    ///
    /// This is the single largest hole in the base game's police. There is no way to end a
    /// chase other than by escaping it or by dying, so every incident has exactly two endings
    /// and one of them is a shootout with people whose job you have made impossible. The
    /// arrest that does exist happens TO you -- an officer catches you on foot, the screen
    /// fades, and you wake up minus some money having watched none of it.
    ///
    /// So: hold a key with police close enough to see you do it, and your hands go up. The
    /// nearest officer is then told to arrest rather than left to the combat system, which is
    /// the part that actually needs writing -- at two stars and above the game's default answer
    /// to a stationary armed player is to shoot him, and a man with his hands up who gets shot
    /// anyway is worse than no surrender mechanic at all.
    ///
    /// It is cancellable right up until they have hold of you, and cancelling it is running.
    /// </summary>
    internal sealed class Surrender
    {
        private const int TickMs = 100;

        /// <summary>Close enough for somebody to see you do it.</summary>
        private const float SeenFrom = 40f;

        /// <summary>How long the key is held before it counts. Long enough not to be a slip.</summary>
        private const int HoldMs = 700;

        /// <summary>He has hold of you.</summary>
        private const float Grabbed = 2.2f;

        /// <summary>Gives up on the officer reaching you after this.</summary>
        private const int PatienceMs = 30000;

        private readonly Settings _cfg;
        private readonly Manhunt _hunt;

        private int _lastTick;
        private int _heldSince;
        private int _startedAt;

        private Ped _taking;

        /// <summary>Whether hands are up right now.</summary>
        public bool Handing { get; private set; }

        /// <summary>Hand the player over to Booking. Set by Main.</summary>
        public Action<Ped, string> Book;

        /// <summary>
        /// Whether a screen is up and owns the controls.
        ///
        /// The panel disables every control and re-enables the six it uses, so the comply key
        /// cannot physically be pressed while it is open -- but the PROMPT would still be drawn
        /// over the top of it, telling the player to hold a button that does nothing. Set by
        /// Main, which is the only thing that knows what is on screen.
        /// </summary>
        public Func<bool> Occupied;

        public Surrender(Settings cfg, Manhunt hunt)
        {
            _cfg = cfg;
            _hunt = hunt;
        }

        public void Update()
        {
            if (!_cfg.CustodyEnabled) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            var me = Game.Player.Character;
            if (me == null || !me.Exists() || me.IsDead) { Drop(); return; }

            if (!_hunt.Running) { Drop(); return; }

            if (Handing) { Handling(me, now); return; }

            // The offer. Only when somebody is actually there to surrender TO -- a prompt
            // offering to give yourself up to an empty street is a prompt that does nothing,
            // and players stop reading prompts that do nothing.
            var officer = Nearest(me);
            if (officer == null) { _heldSince = 0; return; }

            // Not while a panel owns the screen. The surrender key is a raw keyboard read
            // rather than a game control, so unlike everything else it is NOT stopped by the
            // panel disabling controls -- browsing the settings would hand you in.
            if (Occupied != null && Occupied()) { _heldSince = 0; return; }

            // The key name spelled out, NOT a ~INPUT_...~ tag. Those expand game CONTROLS, and
            // the surrender key is a keyboard key out of the ini -- so the tag would render as
            // the literal text INPUT_X for anybody who did not change it and as something worse
            // for anybody who did.
            Screen.Help("Hold [" + _cfg.SurrenderKey + "] to give yourself up.");

            if (!Game.IsKeyPressed(_cfg.SurrenderKey)) { _heldSince = 0; return; }

            if (_heldSince == 0) { _heldSince = now; return; }
            if (now - _heldSince < HoldMs) return;

            Begin(me, officer, now);
        }

        private void Begin(Ped me, Ped officer, int now)
        {
            Handing = true;
            _taking = officer;
            _startedAt = now;
            _heldSince = 0;

            try
            {
                // Empty hands first. An armed surrender is not one, and the game will treat it
                // as a standoff no matter what the animation says.
                me.Weapons.Select(WeaponHash.Unarmed, true);

                Anim.Play(me, Anim.HandsUpDict, Anim.HandsUpClip, 49);

                // ONE STAR IS THE ARREST LEVEL. Officers pursue and cuff at one and only draw
                // from two, so this single line is what turns "they shoot the man with his
                // hands up" into "they come and get him". It is a cap rather than a set, so
                // shooting from here puts it straight back.
                LawHold.Cap(1);

                Function.Call(Hash.TASK_ARREST_PED, officer.Handle, me.Handle);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not start a surrender: " + ex.Message);
            }

            Screen.Ticker("Hands up.");
            Log.Info("Player surrendered.");
        }

        private void Handling(Ped me, int now)
        {
            // Moving is refusing. Deliberately generous -- being nudged by a car should not
            // count, and neither should the small drift the hands-up clip has.
            if (me.Velocity.Length() > 2.4f || Cops.Armed(me))
            {
                Screen.Ticker("Dispatch: suspect is moving.");
                Drop();
                return;
            }

            Screen.Help("Stay still.");

            Anim.Play(me, Anim.HandsUpDict, Anim.HandsUpClip, 49);

            if (!Cops.Alive(_taking)) _taking = Nearest(me);

            if (_taking == null || now - _startedAt > PatienceMs)
            {
                // Nobody came. The hands go down and the chase is still on, which is a much
                // better outcome than standing there forever.
                Screen.Ticker("Nobody came.");
                Drop();
                return;
            }

            LawHold.Cap(1);

            if (_taking.Position.DistanceTo(me.Position) > Grabbed) return;

            var reason = _hunt.Worst == null ? "an outstanding matter" : _hunt.Worst.Called;

            var officer = _taking;
            Drop();

            if (Book != null) Book(officer, reason);
        }

        private static Ped Nearest(Ped me)
        {
            Ped best = null;
            var bestDist = SeenFrom;

            foreach (var officer in Cops.Near(me.Position, SeenFrom))
            {
                if (!Cops.Sees(officer, me, SeenFrom)) continue;

                var d = officer.Position.DistanceTo(me.Position);
                if (d >= bestDist) continue;

                bestDist = d;
                best = officer;
            }

            return best;
        }

        /// <summary>Hands down. The chase, if there is one, carries on.</summary>
        public void Drop()
        {
            if (!Handing) { _heldSince = 0; return; }

            Handing = false;
            _taking = null;
            _heldSince = 0;

            try
            {
                var me = Game.Player.Character;
                if (me != null && me.Exists()) Anim.Stop(me, Anim.HandsUpDict, Anim.HandsUpClip);
            }
            catch
            {
                // Teardown.
            }

            LawHold.Uncap();
        }
    }
}
