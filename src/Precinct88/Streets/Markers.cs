using System;
using System.Collections.Generic;
using GTA;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// Blips on this mod's own police.
    ///
    /// IMMERSION-BREAKING AND WORTH IT, at least while anything is being checked. This mod's
    /// central claim is that police are a finite pool driving real routes, and the single
    /// hardest thing to verify from inside the game is whether any of that is happening at all
    /// -- a quiet street looks identical whether the beat is working perfectly or has produced
    /// nothing since load. The F11 panel answers it with a number; this answers it at a glance,
    /// on the map, including the units you are nowhere near.
    ///
    /// ONLY OUR OWN. Marking every police vehicle in the world would include whatever another
    /// script has spawned, which turns a diagnostic into a lie -- the whole question being asked
    /// is which cars are ours.
    ///
    /// Colour says which force, because that is the other thing worth checking at a glance and
    /// it costs nothing: a map with no yellow on it anywhere in Blaine County is a sheriff model
    /// that did not resolve.
    /// </summary>
    internal sealed class Markers
    {
        private readonly Settings _cfg;
        private readonly Fleet _fleet;
        private readonly Foot _foot;

        /// <summary>Entity handle to the blip drawn for it.</summary>
        private readonly Dictionary<int, Blip> _blips = new Dictionary<int, Blip>();

        private int _lastTick;

        public Markers(Settings cfg, Fleet fleet, Foot foot)
        {
            _cfg = cfg;
            _fleet = fleet;
            _foot = foot;
        }

        public void Update()
        {
            var now = Game.GameTime;
            if (now - _lastTick < 1100) return;
            _lastTick = now;

            if (!_cfg.PoliceBlips)
            {
                if (_blips.Count > 0) Clear();
                return;
            }

            try
            {
                var wanted = new HashSet<int>();

                if (_fleet != null)
                {
                    foreach (var unit in _fleet.Units)
                    {
                        if (!unit.Alive) continue;

                        var onACall = unit.Doing == Duty.Responding ||
                                      unit.Doing == Duty.Searching;

                        wanted.Add(unit.Car.Handle);

                        Mark(unit.Car, Sprite(unit),
                             onACall ? BlipColor.Red : Colour(unit.Force),
                             onACall ? "Responding" : unit.Force,
                             onACall ? 0.75f : 0.6f);
                    }
                }

                if (_foot != null && _cfg.FootPatrols)
                {
                    foreach (var walker in _foot.Walkers)
                    {
                        if (!walker.Alive) continue;

                        wanted.Add(walker.Who.Handle);

                        // Smaller, so a man reads as smaller than a car without needing a
                        // different shape.
                        Mark(walker.Who, BlipSprite.Standard, Colour("City"), "On foot", 0.45f);
                    }
                }

                // Anything marked that is no longer out. A blip left on a released car follows
                // whatever the game does with it next, which is how you end up with a police
                // marker on a taxi.
                Prune(wanted);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not update police markers: " + ex.Message);
            }
        }

        /// <summary>
        /// A DOT, NOT THE CAR SPRITE.
        ///
        /// BlipSprite.PoliceCar is FIXED ART -- a white car silhouette that ignores the colour
        /// you set on it. So every unit came out white whatever force it belonged to, the whole
        /// colour scheme silently did nothing, and the minimap filled with white cars that look
        /// like the game's own markers rather than this mod's.
        ///
        /// Sprite 1 is the plain blip and it takes a tint properly, which is what makes the
        /// force colours mean anything. A unit on a call is told apart by COLOUR rather than by
        /// shape now -- red against the force colour -- which reads better on a minimap that
        /// size anyway.
        /// </summary>
        private static BlipSprite Sprite(Unit unit)
        {
            return BlipSprite.Standard;
        }

        private static BlipColor Colour(string force)
        {
            if (string.Equals(force, "Sheriff", StringComparison.OrdinalIgnoreCase))
            {
                return BlipColor.Yellow2;
            }

            if (string.Equals(force, "Highway Patrol", StringComparison.OrdinalIgnoreCase))
            {
                return BlipColor.WhiteNotPure;
            }

            if (string.Equals(force, "Park Ranger", StringComparison.OrdinalIgnoreCase))
            {
                return BlipColor.Green;
            }

            return BlipColor.Blue;
        }

        private void Mark(Entity what, BlipSprite sprite, BlipColor colour, string name,
                          float scale)
        {
            try
            {
                Blip blip;

                if (_blips.TryGetValue(what.Handle, out blip) && blip != null && blip.Exists())
                {
                    // Kept up to date rather than recreated -- the COLOUR changes when a unit
                    // goes from a beat to a call, and deleting and remaking a blip every second
                    // makes the whole map flicker.
                    blip.Color = colour;
                    blip.Scale = scale;
                    return;
                }

                blip = what.AddBlip();

                if (blip == null || !blip.Exists()) return;

                blip.Sprite = sprite;
                blip.Color = colour;
                blip.Scale = scale;
                blip.IsShortRange = false;
                blip.Name = name;

                _blips[what.Handle] = blip;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not mark a unit: " + ex.Message);
            }
        }

        private void Prune(HashSet<int> keep)
        {
            List<int> gone = null;

            foreach (var pair in _blips)
            {
                if (keep.Contains(pair.Key)) continue;

                if (gone == null) gone = new List<int>();
                gone.Add(pair.Key);
            }

            if (gone == null) return;

            foreach (var handle in gone)
            {
                Drop(_blips[handle]);
                _blips.Remove(handle);
            }
        }

        private static void Drop(Blip blip)
        {
            try
            {
                if (blip != null && blip.Exists()) blip.Delete();
            }
            catch
            {
                // A blip that will not delete is litter, not a failure worth reporting.
            }
        }

        /// <summary>Takes every marker off. For the setting going off, and for teardown.</summary>
        public void Clear()
        {
            foreach (var pair in _blips) Drop(pair.Value);
            _blips.Clear();
        }
    }
}
