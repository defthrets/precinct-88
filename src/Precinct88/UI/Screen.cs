using System;
using System.Drawing;
using GTA;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.UI
{
    /// <summary>
    /// The only place this mod draws anything.
    ///
    /// Deliberately almost nothing: a ticker line, the game's own help box, and a subtitle.
    /// A police overhaul that puts its own HUD on the screen is a police overhaul you are
    /// looking at instead of playing, and everything here already has a native the game draws
    /// in its own style -- which is the style the player has agreed to.
    ///
    /// Every method swallows its own exceptions for the same reason the logger does: this is
    /// called from inside a tick, and a mod that dies drawing a line of text is worse than a
    /// mod that draws no text.
    /// </summary>
    internal static class Screen
    {
        private static string _lastTicker = string.Empty;
        private static int _lastTickerAt;

        /// <summary>
        /// A line in the feed, top left, in the game's own style.
        ///
        /// Notification.Show is obsolete in SHVDN 3.9; PostTicker is the replacement and takes
        /// the two flags that decide whether it is important and whether it is kept in the
        /// phone's notification list.
        ///
        /// Repeats inside four seconds are dropped. Several systems can conclude the same
        /// thing in the same tick -- a stop starting is also a description going out -- and
        /// three identical lines stacked up reads as a bug.
        /// </summary>
        public static void Ticker(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                var now = Game.GameTime;

                if (text == _lastTicker && now - _lastTickerAt < 4000) return;

                _lastTicker = text;
                _lastTickerAt = now;

                GTA.UI.Notification.PostTicker(text, false, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not post a ticker line: " + ex.Message);
            }
        }

        /// <summary>
        /// The instruction box in the top left. Must be called every frame it is wanted.
        ///
        /// Through BEGIN/END rather than the one-shot helper, because the one-shot helper does
        /// not expand button glyphs -- and a prompt reading "press INPUT_CONTEXT" is a prompt
        /// that tells the player nothing.
        /// </summary>
        public static void Help(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_HELP, 0, false, false, -1);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw help text: " + ex.Message);
            }
        }

        /// <summary>A line along the bottom, for something being said to you.</summary>
        public static void Said(string text, int ms = 3500)
        {
            if (string.IsNullOrEmpty(text)) return;

            try { GTA.UI.Screen.ShowSubtitle(text, ms); }
            catch (Exception ex) { Log.Debug("Could not show a subtitle: " + ex.Message); }
        }

        /// <summary>Fades out and waits for it, or gives up rather than hanging the game.</summary>
        public static void FadeOut(int ms = 800)
        {
            try
            {
                Function.Call(Hash.DO_SCREEN_FADE_OUT, ms);

                var until = Game.GameTime + ms + 1500;

                while (!Function.Call<bool>(Hash.IS_SCREEN_FADED_OUT) && Game.GameTime < until)
                {
                    Script.Yield();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not fade out: " + ex.Message);
            }
        }

        public static void FadeIn(int ms = 800)
        {
            try { Function.Call(Hash.DO_SCREEN_FADE_IN, ms); }
            catch (Exception ex) { Log.Debug("Could not fade in: " + ex.Message); }
        }

        /// <summary>
        /// Paints the whole screen out.
        ///
        /// Drawn rather than faded, because a fade is a transition and this is a STATE -- the
        /// hours you are held for. A fade cannot be held indefinitely without the game deciding
        /// to bring it back, and anything drawn over the top of a faded screen is invisible,
        /// which is a problem when the thing to draw is how long is left.
        /// </summary>
        public static void Black()
        {
            try
            {
                // Slightly over the edges. A rect at exactly 1.0 leaves a seam on some aspect
                // ratios, and a one-pixel line of Los Santos down the side of a black screen is
                // the sort of detail that makes the whole thing look broken.
                Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1.2f, 1.2f, 0, 0, 0, 255, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw the blackout: " + ex.Message);
            }
        }

        /// <summary>A centred line of text, for the custody screen and nothing else.</summary>
        public static void Line(string text, float y, float scale, int alpha = 255)
        {
            Text(text, 0.5f, y, scale, Color.FromArgb(alpha, 235, 235, 235), true);
        }

        /// <summary>
        /// A line of text, wherever you want it.
        ///
        /// Font 4 throughout -- Chalet London, the one the game's own menus use. Mixing fonts
        /// is the fastest way to make a panel look like it belongs to a different game than
        /// the one it is drawn over.
        /// </summary>
        public static void Text(string text, float x, float y, float scale, Color colour,
                                bool centred = false, bool rightAligned = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                Function.Call(Hash.SET_TEXT_FONT, 4);
                Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
                Function.Call(Hash.SET_TEXT_COLOUR, colour.R, colour.G, colour.B, colour.A);
                Function.Call(Hash.SET_TEXT_CENTRE, centred);

                if (rightAligned)
                {
                    // RIGHT_JUSTIFY needs a wrap window or it does nothing at all, and the
                    // window's RIGHT edge is what the text is pushed against -- so x is the
                    // right-hand edge here, not the left. This is the single most confusing
                    // piece of the text API and it silently no-ops when you get it wrong.
                    Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);
                    Function.Call(Hash.SET_TEXT_WRAP, 0f, x);
                }

                Function.Call(Hash.SET_TEXT_DROP_SHADOW);

                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y, 0);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw a line of text: " + ex.Message);
            }
        }

        /// <summary>
        /// A filled rectangle. x and y are its CENTRE, which is how the native wants it.
        ///
        /// Coordinates are 0..1 across the screen either way, so a width is already relative
        /// to the screen's width and a height to its height -- which means a square is not
        /// square. Anything that needs to be is corrected with Square below.
        /// </summary>
        public static void Rect(float x, float y, float w, float h, Color colour)
        {
            try
            {
                Function.Call(Hash.DRAW_RECT, x, y, w, h,
                              colour.R, colour.G, colour.B, colour.A, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw a rect: " + ex.Message);
            }
        }

        /// <summary>The width that matches a given height on this screen's aspect ratio.</summary>
        public static float Square(float height)
        {
            try
            {
                var ratio = Function.Call<float>(Hash.GET_ASPECT_RATIO, false);
                if (ratio < 0.1f) ratio = 16f / 9f;

                return height / ratio;
            }
            catch
            {
                return height / (16f / 9f);
            }
        }

        /// <summary>Takes the HUD and radar off for a frame. Wanted while in custody.</summary>
        public static void NoHud()
        {
            try
            {
                Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);
            }
            catch
            {
                // Cosmetic.
            }
        }
    }
}
