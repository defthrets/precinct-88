using System;
using System.Drawing;
using System.Globalization;
using GTA;
using Precinct88.Contact;
using Precinct88.Core;

namespace Precinct88.UI
{
    /// <summary>
    /// The speed limit, drawn as a road sign.
    ///
    /// A FAIRNESS FEATURE BEFORE IT IS AN INFORMATION ONE. The moment speeding became something
    /// you get pulled over for, the limit stopped being trivia and became a rule -- and a rule
    /// you are enforced against but cannot see is not a rule, it is a trap. GTA V has no speed
    /// signs to read, so the only honest way to enforce a limit is to show it.
    ///
    /// THE SPEEDOMETER IS GONE. The first version showed your speed in large type with the limit
    /// underneath, and that was answering a question nobody asked -- the car already tells you
    /// how fast it is going, and every third mod in a scripts folder adds a speedometer. The
    /// only number this mod is entitled to put on screen is the one the game refuses to: what
    /// the limit here actually is.
    ///
    /// So it is a sign. Which is also the honest form for it: a limit is a thing posted at the
    /// side of a road, and a sign says "this is the rule here" in a way a readout of your own
    /// speed never did. It goes red when you are over, which is the one piece of state a plain
    /// sign cannot carry and the only reason to deviate from one.
    /// </summary>
    internal sealed class SpeedHud
    {
        /// <summary>Below this you are stopped, and a limit is not interesting.</summary>
        private const float ShowAbove = 3f;

        /// <summary>How long it lingers after you stop, so it does not blink at junctions.</summary>
        private const int LingerMs = 2500;

        /// <summary>How far over before it turns. Matches the tolerance officers allow.</summary>
        private const float Tolerance = 2.3f;

        /// <summary>The sign is 2:3, and this is its height on screen.</summary>
        private const float SignH = 0.088f;

        /// <summary>How much of the plate the artwork's own border takes up.</summary>
        private const float PlateAspect = 0.664f;

        private static readonly Color Plate = Color.FromArgb(235, 255, 255, 255);
        private static readonly Color Over = Color.FromArgb(255, 236, 108, 96);

        private static readonly Color Ink = Color.FromArgb(255, 16, 16, 18);

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

                var cx = _cfg.SpeedHudX;
                var cy = _cfg.SpeedHudY;

                // THE PLATE IS TINTED, NOT THE TEXT. The artwork is a white face inside a black
                // border, so tinting it red gives a red sign with its border intact -- black
                // multiplied by anything stays black. Colouring the number instead would leave a
                // white sign with red digits, which reads as a sign rather than as a warning.
                Art.Picture("sign.png", cx, cy,
                            Screen.Square(SignH) * PlateAspect, SignH,
                            speeding ? Over : Plate);

                // Small, because on a real sign the words are the small part.
                Screen.Text("SPEED", cx, cy - SignH * 0.36f, 0.26f, Ink, centred: true);
                Screen.Text("LIMIT", cx, cy - SignH * 0.24f, 0.26f, Ink, centred: true);

                Screen.Text(Limits.Signed(limit, kph).ToString(CultureInfo.InvariantCulture),
                            cx, cy - SignH * 0.10f, 0.85f, Ink, centred: true);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw the speed sign: " + ex.Message);
            }
        }
    }
}
