using System;
using System.Collections.Generic;
using System.Drawing;
using GTA;
using GTA.Native;
using Precinct88.Contact;
using Precinct88.Core;
using Precinct88.Response;

namespace Precinct88.UI
{
    /// <summary>
    /// A row of icons under the wanted stars saying what the police are doing.
    ///
    /// ICONS RATHER THAN NOTIFICATIONS, which was a direct instruction and is also just right:
    /// a notification is a thing that happened once and slid away, and almost everything this
    /// mod wants to tell you is a STATE -- a call is running, you are being searched, they are
    /// carrying stun guns. A state expressed as a notification has to be re-announced to stay
    /// true, and re-announcing is how a mod turns into spam. An icon is simply there while the
    /// thing is true and gone when it is not.
    ///
    /// Anything that genuinely needs a sentence gets one: the officer says it, in Dialogue.
    /// This strip is only for what a picture can carry on its own.
    ///
    /// UNDER THE STARS, using the same two settings the old known-strip used, because they are
    /// already in everybody's ini and one of them has already been tuned by hand. Right-aligned
    /// and growing LEFTWARDS, so the row does not shift under the stars as things are added --
    /// an icon that moves when a second one appears is an icon you have to re-find.
    ///
    /// It draws nothing at all when nothing is happening, which is most of the time.
    /// </summary>
    internal sealed class Status
    {
        /// <summary>
        /// Icon height as a fraction of screen height.
        ///
        /// A little under a wanted star. They sit beneath the stars and reading as a second
        /// row of them would be worse than being slightly hard to see -- the stars are the
        /// game's, this row is the mod's, and they should not be mistaken for each other.
        /// </summary>
        private const float IconH = 0.024f;

        /// <summary>How far below the stars the row sits.</summary>
        private const float BelowStars = 0.043f;

        /// <summary>Gap between them, in the same space.</summary>
        private const float Gap = 0.0075f;

        /// <summary>Fresh, and then settled. A call that has just come in is worth a look.</summary>
        private static readonly Color Fresh = Color.FromArgb(255, 236, 108, 96);
        private static readonly Color Live = Color.FromArgb(238, 238, 240, 244);
        private static readonly Color Quiet = Color.FromArgb(150, 168, 170, 178);

        /// <summary>How long a call counts as fresh.</summary>
        private const int FreshMs = 6000;

        private readonly Settings _cfg;
        private readonly Callout _callout;
        private readonly TrafficStop _traffic;
        private readonly Custody.Search _search;
        private readonly Restraint _restraint;

        /// <summary>Rebuilt each draw. A field so the draw does not allocate every frame.</summary>
        private readonly List<Tile> _row = new List<Tile>(6);

        public Status(Settings cfg, Callout callout, TrafficStop traffic,
                      Custody.Search search, Restraint restraint)
        {
            _cfg = cfg;
            _callout = callout;
            _traffic = traffic;
            _search = search;
            _restraint = restraint;
        }

        private struct Tile
        {
            public string File;
            public Color Tint;
        }

        public void Draw()
        {
            if (!_cfg.ShowKnownStrip) return;

            try
            {
                _row.Clear();

                Gather();

                if (_row.Count == 0) return;

                var step = Screen.Square(IconH) + Gap;

                // ANCHORED TO THE SAFE ZONE, NOT THE SCREEN, and that was the whole bug. The
                // wanted stars are pinned inside the safe zone like every other piece of the
                // game's HUD; this row was pinned to a fraction of the raw screen width. On
                // 16:9 those are near enough the same place, which is why it looked right --
                // on an ultrawide the safe zone is a long way in from the edge and the icons
                // sailed off to the right of the stars they were meant to sit under.
                //
                // The two settings are NUDGES from that anchor now rather than absolute
                // positions, so a display that puts them slightly off can still be corrected
                // without them being wrong everywhere else.
                var x = Edge() + _cfg.KnownStripX;
                var y = Top() + BelowStars + _cfg.KnownStripY;

                // RIGHT-ALIGNED, BUILT LEFTWARDS. The last icon always sits at the same place
                // under the stars, so nothing already on screen moves when something new joins.
                for (var i = _row.Count - 1; i >= 0; i--)
                {
                    Art.Icon(_row[i].File, x, y, IconH, _row[i].Tint);
                    x -= step;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw the status strip: " + ex.Message);
            }
        }

        /// <summary>
        /// The right-hand edge of the safe zone, as a fraction of screen width.
        ///
        /// THE ARITHMETIC IS THE GAME'S, NOT MINE. GET_SAFE_ZONE_SIZE returns roughly 0.85 to
        /// 1.0 and is emphatically NOT a margin -- the conversion below (round to two places,
        /// scale to a 0-10 range, multiply by a magic 5.4 against the real pixel aspect) is the
        /// one every HUD library in this game arrived at, because it is what the engine does
        /// internally. Any tidier-looking version of it is wrong on some resolution.
        /// </summary>
        private static float Edge()
        {
            try
            {
                var safe = Function.Call<float>(Hash.GET_SAFE_ZONE_SIZE);

                var g = Math.Round((double)safe, 2);
                g = 10d - ((g * 100d) - 90d);

                const float Hmp = 5.4f;

                var ratio = Function.Call<float>(Hash.GET_ASPECT_RATIO, false);
                if (ratio < 0.1f) ratio = 16f / 9f;

                // In fractions of the screen, which is what everything else here works in.
                var margin = (float)(g * Hmp * ratio) / (720f * ratio);

                return 1f - margin;
            }
            catch
            {
                return 0.94f;
            }
        }

        /// <summary>The top of the safe zone, in fractions of screen height.</summary>
        private static float Top()
        {
            try
            {
                var safe = Function.Call<float>(Hash.GET_SAFE_ZONE_SIZE);

                var g = Math.Round((double)safe, 2);
                g = 10d - ((g * 100d) - 90d);

                return (float)(g * 5.4d) / 720f;
            }
            catch
            {
                return 0.02f;
            }
        }

        /// <summary>
        /// What is true right now, in the order it should read.
        ///
        /// WHAT is happening first, then WHO is dealing with it, then HOW. That ordering is the
        /// sentence a player would say out loud -- "shots fired, a car is coming, and they are
        /// carrying tasers" -- and keeping it fixed means the same fact is always in the same
        /// place in the row.
        /// </summary>
        private void Gather()
        {
            var now = Game.GameTime;

            // 1. WHAT. The thing that was called in, or the thing you were stopped for.
            if (_callout != null && _callout.Running)
            {
                _row.Add(new Tile
                {
                    File = _callout.Icon,
                    Tint = now - _callout.FreshAt < FreshMs ? Fresh : Live,
                });
            }
            else if (_traffic != null && _traffic.Running)
            {
                _row.Add(new Tile { File = Icons.ForViolation(_traffic.Why), Tint = Fresh });
            }

            // 2. WHO. Somebody is on their way, or already talking to you.
            if (_traffic != null && _traffic.Running)
            {
                _row.Add(new Tile { File = "stop.png", Tint = Live });
            }
            else if (_callout != null && _callout.OnIt > 0)
            {
                _row.Add(new Tile { File = "siren.png", Tint = Live });
            }

            if (_search != null && _search.Running)
            {
                _row.Add(new Tile { File = "search.png", Tint = Fresh });
                _row.Add(new Tile { File = "hands.png", Tint = Live });
            }

            // 3. HOW. Only worth saying once there is something to be shot with.
            if (_restraint == null) return;

            try
            {
                if (Game.Player.Wanted.WantedLevel <= 0) return;
            }
            catch
            {
                return;
            }

            _row.Add(_restraint.Lethal
                ? new Tile { File = "gun.png", Tint = Fresh }
                : new Tile { File = "taser.png", Tint = Quiet });
        }
    }
}
