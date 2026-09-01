using System;
using GTA;
using GTA.Math;
using Precinct88.Core;
using Precinct88.Streets;
using Precinct88.UI;

namespace Precinct88.Contact
{
    /// <summary>
    /// Where you go to settle up.
    ///
    /// A DEBT WITH NOWHERE TO PAY IT IS A PENALTY, NOT A MECHANIC. The ledger accrues, the panel
    /// counts, and without this the only thing the player can do about any of it is nothing --
    /// which turns a fine into a slow tax and removes the one decision that makes it
    /// interesting, which is whether to bother.
    ///
    /// THE DESK IS THE STATION DESK -- the same coordinate a booked player is stood at, which is
    /// already in stations.json and already the front counter of a police station. Pull Me Over
    /// puts its payment point at 441.8, -981.9, 30.7, which is within three metres of this mod's
    /// Mission Row desk. Two people working separately picked the same spot, which is about as
    /// much confirmation as a hand-typed coordinate ever gets.
    ///
    /// NO CLERK IS SPAWNED. Pull Me Over spawns one to stand behind the counter and it is a nice
    /// touch, but a ped placed at a guessed coordinate is a ped standing in a wall on any
    /// install where the guess is off -- and this mod's station coordinates have never been
    /// walked in-game. A prompt costs nothing and cannot be wrong in a way you have to look at.
    /// </summary>
    internal sealed class PayDesk
    {
        private const int TickMs = 400;

        /// <summary>Close enough to be at the counter.</summary>
        private const float AtTheDesk = 2.6f;

        /// <summary>How far away the blip is worth showing at all.</summary>
        private const float BlipWithin = 4000f;

        private readonly Settings _cfg;
        private readonly Tickets _tickets;

        private Blip _blip;
        private Station _marked;
        private int _lastTick;

        public PayDesk(Settings cfg, Tickets tickets)
        {
            _cfg = cfg;
            _tickets = tickets;
        }

        public void Update()
        {
            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists() || me.IsDead) { Unmark(); return; }

                if (_tickets == null || !_tickets.Owing) { Unmark(); return; }

                var station = Stations.Nearest(me.Position);
                if (station == null) { Unmark(); return; }

                Mark(station, me.Position);

                var gap = me.Position.DistanceTo(station.Desk);

                // On foot, at the counter. Not from a car through a wall.
                if (gap > AtTheDesk || me.IsInVehicle()) return;

                Offer();
            }
            catch (Exception ex)
            {
                Log.Debug("Pay desk failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The prompt, and the press.
        ///
        /// Read every tick rather than every frame is fine here -- unlike a stop, there is no
        /// hurry and nothing else competing for the button.
        /// </summary>
        private void Offer()
        {
            var owed = _tickets.Owed;
            var have = Game.Player.Money;

            if (have <= 0)
            {
                Screen.Help("You owe " + Tickets.Money(owed) + " in fines. You have nothing on you.");
                return;
            }

            var paying = Math.Min(have, owed);

            Screen.Help("Press ~INPUT_CONTEXT~ to pay " + Tickets.Money(paying) +
                        (paying < owed ? " off " + Tickets.Money(owed) + "." : " and clear it."));

            if (!Game.IsControlJustPressed(GTA.Control.Context)) return;

            var paid = _tickets.Pay();
            if (paid <= 0) return;

            Screen.Ticker(_tickets.Owing
                ? "Paid " + Tickets.Money(paid) + ". Still owing " + Tickets.Money(_tickets.Owed) + "."
                : "Paid " + Tickets.Money(paid) + ". Nothing outstanding.");
        }

        // ---- the blip ----------------------------------------------------------

        private void Mark(Station station, Vector3 from)
        {
            if (!_cfg.TicketBlips) { Unmark(); return; }

            try
            {
                if (station.Desk.DistanceTo(from) > BlipWithin) { Unmark(); return; }

                // Moved to a different station, so the old marker is on the wrong building.
                if (_blip != null && _blip.Exists() && _marked == station) return;

                Unmark();

                _blip = World.CreateBlip(station.Desk);

                if (_blip == null || !_blip.Exists()) return;

                _blip.Sprite = BlipSprite.PoliceStation;
                _blip.Color = BlipColor.Blue;
                _blip.Scale = 0.85f;
                _blip.IsShortRange = false;
                _blip.Name = "Unpaid fines";

                _marked = station;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not mark the pay desk: " + ex.Message);
            }
        }

        /// <summary>Takes the marker off. On paying up, and on teardown.</summary>
        public void Unmark()
        {
            try
            {
                if (_blip != null && _blip.Exists()) _blip.Delete();
            }
            catch
            {
                // A blip that will not delete is litter, not a failure worth reporting.
            }

            _blip = null;
            _marked = null;
        }
    }
}
