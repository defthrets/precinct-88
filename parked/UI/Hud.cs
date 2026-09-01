using System;
using System.Drawing;
using GTA;
using Precinct88.Contact;
using Precinct88.Core;
using Precinct88.Custody;
using Precinct88.Response;

namespace Precinct88.UI
{
    /// <summary>
    /// Everything the police currently think, said in pictures.
    ///
    /// THE MOD USED TO SAY ALL OF THIS IN TICKER LINES and it was the wrong medium for almost
    /// all of it. "Dispatch: suspect sighted", "Dispatch: lost visual", "Pull over", "Hands up"
    /// -- every one of those is a STATE, and a state is a thing that is true for a while. A
    /// notification is a thing that happened once and then scrolls away, so the moment you
    /// looked back at the screen the answer to "what do they know" was gone, and it was gone
    /// exactly when you were busy being chased.
    ///
    /// So state is icons, and icons persist. A notification is now reserved for the things an
    /// icon genuinely cannot carry: WHAT was seized, WHAT you were booked for, what the call
    /// actually said. If it needs explaining, it gets words; if it is a state, it gets a
    /// picture.
    ///
    /// The row reads left to right as: what they are doing, then what they have on you.
    ///
    /// AND THE MOST IMPORTANT THING ON IT IS THE GREY. A red icon is something they hold that
    /// still describes you. A GREY ONE IS SOMETHING THEY HOLD THAT IS NOW WRONG -- the shirt
    /// you changed out of, the car you left two streets back. That is the single most useful
    /// fact the game can tell you, and it is invisible in every other police mod because none
    /// of them model identification as separable pieces.
    /// </summary>
    internal sealed class Hud
    {
        /// <summary>
        /// Where the row sits, as the RIGHT-HAND edge of it.
        ///
        /// The wanted stars are top right, inside the safe zone rather than against the screen
        /// edge -- so a strip pinned at 0.9855 sat well outside them and read as a separate
        /// thing floating in the corner. This lines its right edge up with theirs and puts it
        /// directly underneath, so it reads as a second row of the same HUD element.
        ///
        /// Both numbers are settings, because safe zone is a slider in the game's own display
        /// options and no single default can be right for everybody.
        /// </summary>
        private float Right => _cfg.KnownStripX;

        /// <summary>Sized to the stars rather than to nothing. They are about this tall.</summary>
        private const float IconH = 0.0265f;
        private const float Gap = 0.0055f;

        /// <summary>The state icon leads and is a size up, because it is the headline.</summary>
        private const float StateH = 0.0335f;

        /// <summary>How long a newly-gained icon stays lit up.</summary>
        private const int FlashMs = 2600;

        /// <summary>Live: they hold it and it still describes you.</summary>
        private static readonly Color Live = Color.FromArgb(255, 226, 74, 62);

        /// <summary>Beaten: they hold it and it is now wrong. The good news.</summary>
        private static readonly Color Beaten = Color.FromArgb(130, 128, 128, 134);

        /// <summary>Just gained. Brief, and bright enough to catch the eye mid-chase.</summary>
        private static readonly Color Fresh = Color.FromArgb(255, 255, 255, 255);

        /// <summary>Nobody described you at all.</summary>
        private static readonly Color Blind = Color.FromArgb(235, 120, 205, 150);

        /// <summary>They can see you right now.</summary>
        private static readonly Color Hot = Color.FromArgb(255, 240, 170, 60);

        private static readonly Known[] Order =
        {
            Known.Face, Known.Clothes, Known.Vehicle, Known.Weapon, Known.Camera,
        };

        private readonly Settings _cfg;
        private readonly Manhunt _hunt;
        private readonly Stop _stop;
        private readonly Booking _booking;
        private readonly Surrender _surrender;

        public Hud(Settings cfg, Manhunt hunt, Stop stop, Booking booking, Surrender surrender)
        {
            _cfg = cfg;
            _hunt = hunt;
            _stop = stop;
            _booking = booking;
            _surrender = surrender;
        }

        public void Draw()
        {
            if (!_cfg.ShowKnownStrip) return;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                var y = _cfg.KnownStripY;

                // A scene beats a manhunt for the headline slot. Being cuffed is a more
                // pressing fact about your situation than what they have on file.
                if (Scene(y)) return;

                if (_hunt == null || !_hunt.Running) return;

                var radio = _hunt.Description;
                if (radio == null || !radio.OnAir) return;

                var x = Right;

                // Laid out right to left so the row grows leftwards from a fixed edge. A strip
                // anchored on its left slides about as icons are gained, and a HUD element that
                // moves while you are being chased is one you cannot read.
                var known = radio.Has;
                var matching = radio.StillMatching(me);
                var now = Game.GameTime;

                for (var i = Order.Length - 1; i >= 0; i--)
                {
                    var flag = Order[i];
                    if ((known & flag) == 0) continue;

                    x -= Screen.Square(IconH);

                    var gained = radio.GainedAt(flag);
                    var fresh = gained > 0 && now - gained < FlashMs;

                    var live = (matching & flag) != 0;

                    // Weapon and Camera are facts about the CALL rather than descriptions of
                    // the man, so there is nothing to match against and nothing to beat.
                    if (flag == Known.Weapon || flag == Known.Camera) live = true;

                    Art.Icon(File(flag), x + Screen.Square(IconH) * 0.5f, y + IconH * 0.5f,
                             IconH, fresh ? Fresh : live ? Live : Beaten);

                    x -= Gap;
                }

                // The state, leading the row on the left.
                State(x, y, known == Known.Nothing);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw the HUD: " + ex.Message);
            }
        }

        /// <summary>What the police are doing, as one icon.</summary>
        private void State(float rightEdge, float y, bool blind)
        {
            var w = Screen.Square(StateH);
            var cx = rightEdge - w * 0.5f;
            var cy = y + IconH * 0.5f;

            if (blind)
            {
                // The state vanilla cannot represent: a crime is out and nobody could say a
                // thing about who did it. Green, because from where the player is standing this
                // is the best news available.
                Art.Icon("noid.png", cx, cy, StateH, Blind);
                return;
            }

            if (_hunt.State == Hunt.Seen)
            {
                Art.Icon("seen.png", cx, cy, StateH, Hot);
                return;
            }

            Art.Icon("search.png", cx, cy, StateH, Live);
        }

        /// <summary>
        /// A scene in progress, which outranks everything else on the row.
        ///
        /// Returns whether it drew, because these are exclusive with the description strip --
        /// a man in handcuffs does not need to be told what shirt they have on file.
        /// </summary>
        private bool Scene(float y)
        {
            var w = Screen.Square(StateH);
            var cx = Right - w * 0.5f;
            var cy = y + IconH * 0.5f;

            if (_booking != null && _booking.InCustody)
            {
                Art.Icon("cuffs.png", cx, cy, StateH, Live);
                return true;
            }

            if (_surrender != null && _surrender.Handing)
            {
                Art.Icon("hands.png", cx, cy, StateH, Blind);
                return true;
            }

            if (_stop != null && _stop.Running)
            {
                Art.Icon("stop.png", cx, cy, StateH, Hot);
                return true;
            }

            return false;
        }

        private static string File(Known flag)
        {
            switch (flag)
            {
                case Known.Face: return "face.png";
                case Known.Clothes: return "fit.png";
                case Known.Vehicle: return "car.png";
                case Known.Weapon: return "gun.png";
                case Known.Camera: return "cam.png";
                default: return "noid.png";
            }
        }
    }
}
