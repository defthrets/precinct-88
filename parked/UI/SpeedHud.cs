using System;
using System.Drawing;
using System.Globalization;
using GTA;
using Precinct88.Contact;
using Precinct88.Core;

namespace Precinct88.UI
{
    /// <summary>
    /// Your speed and the limit, while it matters.
    ///
    /// A FAIRNESS FEATURE BEFORE IT IS AN INFORMATION ONE. The moment speeding became something
    /// you get pulled over for, the limit stopped being trivia and became a rule -- and a rule
    /// you are enforced against but cannot see is not a rule, it is a trap. GTA V has no speed
    /// signs to read, so the only honest way to enforce a limit is to show it.
    ///
    /// NOT ALWAYS UP, though. A permanent speedometer is a different mod, and this one has gone
    /// to some trouble to keep the screen clear. It appears while you are actually driving and
    /// fades out again when you are not, and it goes red the moment you are over -- which is the
    /// only state anybody needs to read quickly.
    ///
    /// Numbers rather than an icon, and that is not a lapse from the icons-for-states rule. A
    /// speed IS a number; there is no picture of forty-seven.
    /// </summary>
    internal sealed class SpeedHud
    {
        /// <summary>Below this you are stopped, and a limit is not interesting.</summary>
        private const float ShowAbove = 3f;

        /// <summary>How long it lingers after you stop, so it does not blink at junctions.</summary>
        private const int LingerMs = 2500;

        /// <summary>How far over before it turns. Matches the tolerance officers allow.</summary>
        private const float Tolerance = 2.3f;

        private const float BigScale = 0.52f;
        private const float SmallScale = 0.28f;

        private static readonly Color Ink = Color.FromArgb(225, 235, 235, 235);
        private static readonly Color Faint = Color.FromArgb(180, 140, 140, 146);
        private static readonly Color Over = Color.FromArgb(255, 226, 74, 62);
        private static readonly Color Plate = Color.FromArgb(150, 12, 12, 14);

        private readonly Settings _cfg;

        private int _lastMoving;

        public SpeedHud(Settings cfg)
        {
            _cfg = cfg;
        }

        public void Draw()
        {
            if (!_cfg.ShowSpeedLimit) return;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists() || !me.IsInVehicle()) return;

                var car = me.CurrentVehicle;
                if (!Cops.Alive(car)) return;

                // A passenger is not driving and does not need a limit.
                if (car.Driver == null || car.Driver.Handle != me.Handle) return;

                var now = Game.GameTime;
                var speed = car.Speed;

                if (speed > ShowAbove) _lastMoving = now;
                else if (now - _lastMoving > LingerMs) return;

                var kph = _cfg.SpeedInKph;

                var limit = Limits.For(car.Position);
                var speeding = speed > limit + Tolerance;

                var x = _cfg.SpeedHudX;
                var y = _cfg.SpeedHudY;

                var w = Screen.Square(0.052f);

                // A small plate behind it, or white numbers vanish over a white car.
                Screen.Rect(x, y + 0.026f, w, 0.052f, Plate);

                Screen.Text(Limits.Read(speed, kph).ToString(CultureInfo.InvariantCulture),
                            x, y, BigScale, speeding ? Over : Ink, centred: true);

                Screen.Text((kph ? "KPH" : "MPH") + "   LIMIT " +
                            Limits.Signed(limit, kph).ToString(CultureInfo.InvariantCulture),
                            x, y + 0.033f, SmallScale, speeding ? Over : Faint, centred: true);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw the speed readout: " + ex.Message);
            }
        }
    }
}
