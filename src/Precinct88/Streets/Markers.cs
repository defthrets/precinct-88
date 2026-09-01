using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;
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
    /// ONE COLOUR FOR ALL OF THEM, and that was a deliberate walk-back. These were once
    /// tinted per force -- blue city, yellow sheriff, white highway, green ranger -- on the
    /// argument that it made a resolve failure visible at a glance: no yellow anywhere in
    /// Blaine County meant the sheriff model had not loaded.
    ///
    /// It was a diagnostic that cost the thing it was drawn on. A green dot on the minimap is
    /// not a police blip, it is a green dot, and four colours of police read as four different
    /// factions rather than as one force with different hats on. The map should say "police
    /// are here" before it says anything else, and it cannot say that in four colours.
    ///
    /// The resolve check did not need the colour anyway -- the log names the agency every unit
    /// goes out under, which answers the same question and answers it properly.
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

                        // RED IS NOT A FORCE, IT IS A STATE, which is why it survives the
                        // move to one colour. It says this particular car has been given
                        // something to do -- the one thing about a unit that changes minute
                        // to minute and the one thing worth being able to see from the map.
                        // The name still says which agency, because a word in the legend is
                        // not what made four colours wrong.
                        Mark(unit.Car, Sprite(unit),
                             onACall ? BlipColor.Red : Police,
                             onACall ? "Responding" : "Police",
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
                        // different shape -- and the SAME shape on purpose, because splitting
                        // them by sprite puts a second police entry in the pause map legend
                        // for what is the same thing standing up.
                        Mark(walker.Who, BlipSprite.Standard, Police, "Police", 0.45f);
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
        /// A plain dot. Sprite 1.
        ///
        /// FOURTH TIME, AND THE ROUND TRIP IS THE USEFUL PART. PoliceCar (56) was fixed white
        /// car art that ignored the tint. Standard (1) in blue came next and the pause map
        /// legend filed every patrol car in the city under "Friend". PoliceCarDot (42) fixed
        /// the legend -- its built-in name really is Police -- and it is far too big: its art
        /// is a chunky filled disc that swamps a minimap at any scale small enough to be
        /// unobtrusive, and shrinking it further just makes an illegible smudge.
        ///
        /// So back to the plain dot, which was always the right SIZE, with the actual cause of
        /// the "Friend" label fixed instead of designed around: the name is now written by the
        /// text command directly and re-applied every pass rather than set once at creation on
        /// a blip that may not have been ready to take it. See Name below.
        ///
        /// If the legend ever says Friend again, that is the answer -- the naming did not take,
        /// and the next thing to try is a different blue, since sprite 1 in BlipColor.Blue is
        /// exactly the game's own friend marker.
        /// </summary>
        private static BlipSprite Sprite(Unit unit)
        {
            return BlipSprite.Standard;
        }

        /// <summary>
        /// Police blue, whoever they are.
        ///
        /// The same blue the game uses for its own police, so a unit of ours sitting next to
        /// something vanilla does not announce which is which.
        /// </summary>
        private const BlipColor Police = BlipColor.Blue;

        private void Mark(Entity what, BlipSprite sprite, BlipColor colour, string name,
                          float scale)
        {
            try
            {
                Blip blip;

                if (_blips.TryGetValue(what.Handle, out blip) && blip != null && blip.Exists())
                {
                    // Kept up to date rather than recreated -- the COLOUR changes when a unit
                    // goes from patrol to a call, and deleting and remaking a blip every second
                    // makes the whole map flicker.
                    blip.Color = colour;
                    blip.Scale = scale;

                    // THE NAME TOO, EVERY PASS, and that is not belt and braces. It was set
                    // once at creation and the pause map showed the game's default anyway --
                    // a blip that has just been attached to an entity is not always ready to
                    // be named, and the failure is silent and invisible until somebody opens
                    // the map. Two native calls a second is nothing; being unable to trust
                    // what the legend says is not.
                    Name(blip, name);
                    return;
                }

                blip = what.AddBlip();

                if (blip == null || !blip.Exists()) return;

                blip.Sprite = sprite;
                blip.Color = colour;
                blip.Scale = scale;
                blip.IsShortRange = false;

                // Not grouped with anything, and no distance readout -- these are not
                // destinations and a metre count next to every patrol car is noise.
                blip.CategoryType = BlipCategoryType.NoDistanceShown;

                Name(blip, name);

                _blips[what.Handle] = blip;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not mark a unit: " + ex.Message);
            }
        }

        /// <summary>
        /// Puts a name on a blip.
        ///
        /// By native rather than through the property, because this is the one thing here that
        /// has already failed once in the game and been believed to work. It is a text command
        /// -- begin, hand over the string, end against the handle -- and doing it in the open
        /// means a future failure is visible in this file rather than somewhere in a wrapper.
        /// </summary>
        private static void Name(Blip blip, string text)
        {
            try
            {
                Function.Call(Hash.BEGIN_TEXT_COMMAND_SET_BLIP_NAME, "STRING");
                // ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME is the literal-string component
                // despite the name -- it has nothing to do with players and is what every
                // "STRING" text command is fed. There is no Hash.STRING.
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
                Function.Call(Hash.END_TEXT_COMMAND_SET_BLIP_NAME, blip.Handle);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not name a blip: " + ex.Message);
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
