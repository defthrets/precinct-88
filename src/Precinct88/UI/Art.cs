using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Precinct88.Core;

namespace Precinct88.UI
{
    /// <summary>
    /// Our own PNGs, drawn on the HUD.
    ///
    /// WHY THE MOD SHIPS ART AT ALL, having gone out of its way not to. Because the two ways of
    /// avoiding it both fail:
    ///
    /// The game's own sprites do not have these pictures. There is no handcuffs, no eye, no
    /// camera and no police badge that can be handed to DRAW_SPRITE -- Hoodrich went looking for
    /// a skull across every texture dictionary in every dump and ended up drawing Franklin's
    /// face. Guessing at more names is guessing at art that was never shipped.
    ///
    /// And blip art, which DOES have the right pictures, cannot be put on a HUD. Blip sprites
    /// address the map; the only way to render one in a string is `~BLIP_...~`, and that works
    /// in help text and nowhere else. In a plain DRAW_TEXT it draws NOTHING -- not the tag as
    /// literal text, nothing at all, which is indistinguishable from a broken icon.
    ///
    /// So: CustomSprite, which hands a file to ScriptHookV's own texture loader. The art is
    /// white on transparent and tinted at draw time, so one file serves every state.
    ///
    /// SCALEDDRAW, NOT DRAW, and that is the difference between this working and not.
    /// CustomSprite.Draw hands the renderer a hardcoded 1280x720 -- not a pixel space, a fixed
    /// 16:9 grid -- so on any other aspect ratio it puts the art in the wrong place and
    /// stretches it. ScaledDraw uses Screen.ScaledWidth by 720, which is aspect-corrected, and
    /// in that space equal width and height really is square on screen.
    /// </summary>
    internal static class Art
    {
        /// <summary>
        /// The height of the space ScaledDraw draws into. Fixed, and NOT the screen's.
        ///
        /// GTA.UI.Screen.Height is this same 720 -- a constant in the assembly rather than a
        /// resolution. Named so the arithmetic below reads as deliberate rather than as
        /// somebody having typed a magic number.
        /// </summary>
        private const float ScaledHeight = 720f;

        private static readonly Dictionary<string, GTA.UI.CustomSprite> Loaded =
            new Dictionary<string, GTA.UI.CustomSprite>(StringComparer.OrdinalIgnoreCase);

        private static bool _missingWarned;

        /// <summary>
        /// Draws an icon, square, centred on x,y and sized by fraction of screen HEIGHT.
        ///
        /// Returns whether it actually drew, so a caller can fall back to a letter tag rather
        /// than leaving a hole -- which matters, because a missing icon is invisible and a
        /// missing icon in a row of icons silently changes what the row means.
        /// </summary>
        public static bool Icon(string file, float centreX, float centreY, float height, Color tint)
        {
            var sprite = Load(file);
            if (sprite == null) return false;

            try
            {
                var wide = Screen.Square(height) * GTA.UI.Screen.ScaledWidth;
                var tall = height * ScaledHeight;

                if (wide < 1f || tall < 1f) return false;

                sprite.Size = new SizeF(wide, tall);
                sprite.Position = new PointF(centreX * GTA.UI.Screen.ScaledWidth,
                                             centreY * ScaledHeight);
                sprite.Color = tint;
                sprite.Rotation = 0f;

                sprite.ScaledDraw();
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Icon '" + file + "' would not draw: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The same, for art that is not square.
        ///
        /// Every icon in the set IS square, so Icon forces one and that is right for all of
        /// them. A road sign is two-thirds as wide as it is tall, and squeezed into a square it
        /// stops being a sign.
        /// </summary>
        public static bool Picture(string file, float centreX, float centreY,
                                   float width, float height, Color tint)
        {
            var sprite = Load(file);
            if (sprite == null) return false;

            try
            {
                var wide = width * GTA.UI.Screen.ScaledWidth;
                var tall = height * ScaledHeight;

                if (wide < 1f || tall < 1f) return false;

                sprite.Size = new SizeF(wide, tall);
                sprite.Position = new PointF(centreX * GTA.UI.Screen.ScaledWidth,
                                             centreY * ScaledHeight);
                sprite.Color = tint;
                sprite.Rotation = 0f;

                sprite.ScaledDraw();
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Picture '" + file + "' would not draw: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The sprite for a file, made once.
        ///
        /// Cached because constructing one LOADS THE TEXTURE, and a texture loaded every frame
        /// is a handle leaked every frame. A miss is cached too, as a null, so a file that is
        /// not there costs one look at the disk rather than one per frame forever.
        /// </summary>
        private static GTA.UI.CustomSprite Load(string file)
        {
            GTA.UI.CustomSprite found;
            if (Loaded.TryGetValue(file, out found)) return found;

            Loaded[file] = null;

            try
            {
                var path = Path.Combine(Paths.Icons, file);

                if (!File.Exists(path))
                {
                    if (!_missingWarned)
                    {
                        _missingWarned = true;
                        Log.Warn("No icon art at " + Paths.Icons + ". The HUD will fall back to " +
                                 "letter tags. Run tools/make_icons.py and redeploy.");
                    }

                    return null;
                }

                // The source size here is the sprite's own, not the size it draws at -- that is
                // set per draw above. 32x32 is what Hoodrich uses and it works; the art is
                // resampled either way.
                found = new GTA.UI.CustomSprite(path, new SizeF(32f, 32f), new PointF(0f, 0f),
                                                Color.White, 0f, true);

                Loaded[file] = found;
                return found;
            }
            catch (Exception ex)
            {
                Log.Info("Icon '" + file + "' would not load: " + ex.Message);
                return null;
            }
        }

        /// <summary>Whether the art is there at all, for the preflight to say so at load.</summary>
        public static int Check(params string[] files)
        {
            var found = 0;

            foreach (var f in files)
            {
                try
                {
                    if (File.Exists(Path.Combine(Paths.Icons, f))) found++;
                }
                catch
                {
                    // Counted as missing.
                }
            }

            return found;
        }
    }
}
