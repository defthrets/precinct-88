using System;
using System.Drawing;
using GTA;
using Precinct88.Core;
using Precinct88.Response;

namespace Precinct88.UI
{
    /// <summary>
    /// A row of tags under the wanted stars saying what the police actually have on you.
    ///
    /// THE MOD ALREADY KNEW ALL OF THIS AND NEVER TOLD ANYBODY. Every flag here was being
    /// tracked and acted on before this file existed -- it was just invisible, buried in a
    /// settings panel nobody has open during a chase. A mechanic the player cannot see is a
    /// mechanic the player does not have, and "they lost my description when I changed my
    /// shirt" is not something anyone will ever deduce from an empty screen.
    ///
    /// THE STATE THAT MATTERS IS NOT KNOWN-VERSUS-UNKNOWN, IT IS KNOWN-AND-STILL-TRUE VERSUS
    /// KNOWN-AND-NOW-WRONG. A tag they hold and you still match is live and burns red. One you
    /// have beaten -- the shirt you have changed, the car you have left -- goes grey and stays
    /// on the strip, because the fact they are still looking for a man in a white vest is the
    /// most useful thing you could possibly be told, and deleting it would hide the whole
    /// point.
    ///
    /// Drawn with letters rather than icons because this mod ships no art and a mod with no art
    /// cannot lose an argument with somebody's texture pack.
    /// </summary>
    internal sealed class KnownStrip
    {
        /// <summary>Right-hand edge. Under the stars, which sit top-right in GTA V.</summary>
        private const float Right = 0.9855f;

        private const float TagW = 0.0305f;
        private const float TagH = 0.0225f;
        private const float Gap = 0.0035f;

        private const float TextScale = 0.27f;

        /// <summary>How long a newly-gained tag stays lit up.</summary>
        private const int FlashMs = 2600;

        /// <summary>Live: they hold it and it still describes you.</summary>
        private static readonly Color LiveBox = Color.FromArgb(215, 128, 32, 32);
        private static readonly Color LiveInk = Color.FromArgb(255, 255, 226, 226);

        /// <summary>Beaten: they hold it and it is now wrong. This is the good news.</summary>
        private static readonly Color DeadBox = Color.FromArgb(150, 26, 26, 30);
        private static readonly Color DeadInk = Color.FromArgb(150, 130, 130, 130);

        /// <summary>Just gained. Brief, and bright enough to catch the eye mid-chase.</summary>
        private static readonly Color FlashBox = Color.FromArgb(240, 224, 72, 56);
        private static readonly Color FlashInk = Color.FromArgb(255, 255, 255, 255);

        /// <summary>Nobody described you at all.</summary>
        private static readonly Color BlindBox = Color.FromArgb(190, 28, 44, 34);
        private static readonly Color BlindInk = Color.FromArgb(255, 150, 205, 165);

        private static readonly Known[] Order =
        {
            Known.Face, Known.Clothes, Known.Vehicle, Known.Weapon, Known.Camera,
        };

        private readonly Settings _cfg;
        private readonly Manhunt _hunt;

        public KnownStrip(Settings cfg, Manhunt hunt)
        {
            _cfg = cfg;
            _hunt = hunt;
        }

        public void Draw()
        {
            if (!_cfg.ShowKnownStrip) return;
            if (_hunt == null || !_hunt.Running) return;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                var radio = _hunt.Description;
                if (radio == null || !radio.OnAir) return;

                var y = _cfg.KnownStripY;

                // Nothing at all. Worth its own tag rather than an empty row -- a blank strip
                // reads as the feature being off, and this is the single most valuable thing
                // the strip can ever tell you.
                if (radio.Has == Known.Nothing)
                {
                    Tag("NO ID", Right - TagW * 1.6f, y, TagW * 1.6f, BlindBox, BlindInk);
                    return;
                }

                var matching = radio.StillMatching(me);
                var now = Game.GameTime;

                // Laid out right to left so the row grows leftwards from a fixed edge. A strip
                // anchored on its left would slide about as flags are gained, and a HUD element
                // that moves while you are being chased is one you cannot read.
                var x = Right;

                for (var i = Order.Length - 1; i >= 0; i--)
                {
                    var flag = Order[i];
                    if ((radio.Has & flag) == 0) continue;

                    x -= TagW;

                    var gained = radio.GainedAt(flag);
                    var fresh = gained > 0 && now - gained < FlashMs;

                    var live = (matching & flag) != 0;

                    // Weapon and Camera are facts about the CALL, not descriptions of the man,
                    // so there is nothing to match against and nothing to beat. Always live.
                    if (flag == Known.Weapon || flag == Known.Camera) live = true;

                    var box = fresh ? FlashBox : live ? LiveBox : DeadBox;
                    var ink = fresh ? FlashInk : live ? LiveInk : DeadInk;

                    Tag(Label(flag), x, y, TagW, box, ink);

                    x -= Gap;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw the description strip: " + ex.Message);
            }
        }

        private static void Tag(string text, float x, float y, float w, Color box, Color ink)
        {
            // DRAW_RECT takes a centre; the layout above works in left edges, which is the
            // easier thing to reason about when a row grows in one direction.
            Screen.Rect(x + w * 0.5f, y + TagH * 0.5f, w, TagH, box);
            Screen.Text(text, x + w * 0.5f, y + 0.0028f, TextScale, ink, centred: true);
        }

        private static string Label(Known flag)
        {
            switch (flag)
            {
                case Known.Face: return "FACE";
                case Known.Clothes: return "FIT";
                case Known.Vehicle: return "CAR";
                case Known.Weapon: return "GUN";
                case Known.Camera: return "CAM";
                default: return "?";
            }
        }
    }
}
