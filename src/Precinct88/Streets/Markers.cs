using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
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
    /// EVERY POLICE UNIT, NOT ONLY OURS, and that is a reversal worth explaining. This began
    /// as a diagnostic: the question was "is the pool producing cars at all", and marking the
    /// engine's as well would have answered it with a lie.
    ///
    /// It has stopped being a diagnostic. The engine's dispatch runs alongside this build, so
    /// most of the police a player actually sees are not ours -- and a map that shows three
    /// cars while eight are on the street is not a truthful map, it is a map of an
    /// implementation detail. Which mod spawned a police car is the least interesting thing
    /// about it to everybody except the person who wrote the mod.
    ///
    /// The log still answers the original question, and answers it better.
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
    /// <summary>What a marked unit is doing about you, specifically.</summary>
    internal enum Mood
    {
        /// <summary>Working. Nothing to do with you at all.</summary>
        Patrol,

        /// <summary>Has clocked you and is watching, or is stood talking to you.</summary>
        Watching,

        /// <summary>On a call. Somebody reported something and he is going.</summary>
        Responding,

        /// <summary>After YOU, and knows it.</summary>
        Chasing,
    }

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

                var me = Game.Player.Character;
                var stars = 0;

                try { stars = Game.Player.Wanted.WantedLevel; }
                catch { /* No stars is the safe answer. */ }

                if (_fleet != null)
                {
                    foreach (var unit in _fleet.Units)
                    {
                        if (!unit.Alive) continue;

                        wanted.Add(unit.Car.Handle);

                        // COLOUR IS STATE, NOT IDENTITY, and that is why four forces share one
                        // colour and four moods do not. Which agency he belongs to never
                        // changes and can wait for the legend; what he is doing about you
                        // changes minute to minute and is the only reason to look at the map.
                        var mood = Of(unit, me, stars);

                        Mark(unit.Car, Sprite(mood, false), Paint(mood), Called(mood),
                             Size(mood, 0.6f), mood == Mood.Chasing);
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
                        var mood = Of(walker, me, stars);

                        Mark(walker.Who, Sprite(mood, true), Paint(mood), Called(mood),
                             Size(mood, 0.45f), mood == Mood.Chasing);
                    }
                }

                // AND EVERYBODY ELSE'S. Scanned rather than tracked, because we do not own
                // them and have no list -- so this is the one place in the mod that asks the
                // world what is out there instead of remembering what it put there.
                Others(wanted, me, stars);

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
        /// Police this mod did not put on the street.
        ///
        /// NO DUTY AND NO ERRAND TO READ, so the mood is inferred from the only two things
        /// visible from outside: whether you are wanted, and whether he is near enough for that
        /// to be about him. It is cruder than the state our own units carry and it has to be --
        /// there is nothing else to go on.
        ///
        /// Anything already marked is skipped, which is what the set is for: a borrowed car in
        /// the middle of a traffic stop is in the pool AND in the world, and marking it twice
        /// would give it two blips fighting over one vehicle.
        /// </summary>
        private void Others(HashSet<int> wanted, Ped me, int stars)
        {
            if (me == null || !me.Exists()) return;

            try
            {
                foreach (var car in World.GetNearbyVehicles(me, Reach))
                {
                    if (!Cops.Alive(car)) continue;
                    if (wanted.Contains(car.Handle)) continue;

                    var driver = car.Driver;
                    if (!Cops.Alive(driver) || !Cops.IsCop(driver)) continue;

                    wanted.Add(car.Handle);

                    var mood = Guessed(car.Position, me, stars);

                    Mark(car, Sprite(mood, false), Paint(mood), Called(mood),
                         Size(mood, 0.6f), mood == Mood.Chasing);
                }

                foreach (var ped in World.GetNearbyPeds(me, Reach))
                {
                    if (!Cops.Alive(ped)) continue;
                    if (wanted.Contains(ped.Handle)) continue;
                    if (!Cops.IsCop(ped)) continue;

                    // In a car he is already marked as the car, or he is a passenger and does
                    // not want a second blip on top of the one his vehicle has.
                    if (ped.IsInVehicle()) continue;

                    wanted.Add(ped.Handle);

                    var mood = Guessed(ped.Position, me, stars);

                    Mark(ped, Sprite(mood, true), Paint(mood), Called(mood),
                         Size(mood, 0.45f), mood == Mood.Chasing);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not mark the rest of the police: " + ex.Message);
            }
        }

        /// <summary>The best that can be said about somebody whose orders we cannot read.</summary>
        private static Mood Guessed(Vector3 where, Ped me, int stars)
        {
            if (stars <= 0) return Mood.Patrol;

            return where.DistanceTo(me.Position) < OnYou ? Mood.Chasing : Mood.Responding;
        }

        /// <summary>
        /// What this car is doing about you.
        ///
        /// CHASING OUTRANKS RESPONDING, and the difference is worth a colour of its own. A car
        /// on a call is going somewhere; a car on a call WHILE YOU ARE WANTED is very probably
        /// coming to you, and those two want different reactions from a player glancing at a
        /// minimap.
        /// </summary>
        private static Mood Of(Unit unit, Ped me, int stars)
        {
            // A stop, or a scene somebody else is running. He is dealing with you directly and
            // is not going anywhere else.
            if (unit.Doing == Duty.Contact) return Mood.Watching;

            var onACall = unit.Doing == Duty.Responding || unit.Doing == Duty.Searching;

            if (!onACall) return Mood.Patrol;

            return stars > 0 ? Mood.Chasing : Mood.Responding;
        }

        /// <summary>
        /// And what this officer on foot is doing about you.
        ///
        /// He has no Duty, only an Errand, and the interesting one is Watching -- which Reacts
        /// sets the moment he clocks something you are doing and holds for as long as you keep
        /// doing it. That is the closest thing in this mod to "he has seen you", and it earns a
        /// colour because it is the state a player would most like to know about.
        /// </summary>
        private static Mood Of(Walker walker, Ped me, int stars)
        {
            if (walker.Doing == Errand.Watching)
            {
                return stars > 0 ? Mood.Chasing : Mood.Watching;
            }

            // Wanted, and close enough to be part of it. A man on foot forty streets away is
            // not chasing you however many stars you have.
            if (stars > 0 && me != null && me.Exists() &&
                walker.Who.Position.DistanceTo(me.Position) < OnYou)
            {
                return Mood.Chasing;
            }

            return Mood.Patrol;
        }

        /// <summary>
        /// POLICE BLUE, EXCEPT WHEN HE HAS SEEN YOU.
        ///
        /// Red was wrong and it was wrong in an interesting way: it read as an ENEMY marker,
        /// because red on a minimap in this game means something hostile. These are police,
        /// they are police the whole time, and a car answering a call is not a different
        /// faction from one that is not.
        ///
        /// So the state is carried by SHAPE, SIZE and FLASHING instead -- see Sprite and Size
        /// -- and the only colour that survives is the amber for having clocked you, which is
        /// the one state that genuinely is about you rather than about him.
        /// </summary>
        private static BlipColor Paint(Mood mood)
        {
            return mood == Mood.Watching ? Eyes : Police;
        }

        private static string Called(Mood mood)
        {
            switch (mood)
            {
                case Mood.Watching: return "Police - watching";
                case Mood.Responding: return "Police - responding";
                case Mood.Chasing: return "Police - after you";
                default: return "Police";
            }
        }

        /// <summary>
        /// Bigger the more it is about you.
        ///
        /// Scaled from the base rather than fixed, so a man on foot stays smaller than a car at
        /// every state instead of the two converging the moment anything happens.
        /// </summary>
        private static float Size(Mood mood, float baseSize)
        {
            switch (mood)
            {
                case Mood.Watching: return baseSize * 1.15f;
                case Mood.Responding: return baseSize * 1.25f;
                case Mood.Chasing: return baseSize * 1.35f;
                default: return baseSize;
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
        private static BlipSprite Sprite(Mood mood, bool onFoot)
        {
            // THE GAME'S OWN POLICE ART, and now that colour is no longer carrying the state
            // there is room for it. A dot for ordinary patrol, because that is what a patrol
            // car is on a vanilla minimap and the row of them should not shout; the marked
            // police blip once he is doing something about you, because that is exactly what
            // the game uses it for.
            if (mood == Mood.Patrol) return BlipSprite.Standard;

            return onFoot ? BlipSprite.PoliceOfficer : BlipSprite.PoliceCarDot;
        }

        /// <summary>
        /// Police blue, whoever they are.
        ///
        /// The same blue the game uses for its own police, so a unit of ours sitting next to
        /// something vanilla does not announce which is which.
        /// </summary>
        private const BlipColor Police = BlipColor.Blue;

        /// <summary>Clocked you. Not coming yet, but you are the thing he is looking at.</summary>
        private const BlipColor Eyes = BlipColor.Yellow;

        /// <summary>How far off an officer counts as being on you rather than nearby.</summary>
        private const float OnYou = 55f;

        /// <summary>
        /// How far out the world is scanned for police we did not put there.
        ///
        /// Our own units are tracked and can be marked at any distance; these have to be found,
        /// and finding them is a sweep of every vehicle and ped around the player. Far enough
        /// to cover a minimap at its usual zoom and no further.
        /// </summary>
        private const float Reach = 200f;

        private void Mark(Entity what, BlipSprite sprite, BlipColor colour, string name,
                          float scale, bool flashing)
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

                    // FLASHING IS RESERVED FOR ONE STATE and set every pass because it has to
                    // be turned back OFF as well. A blip left flashing after a pursuit ends is
                    // a police car that appears to still be after you forever.
                    blip.IsFlashing = flashing;

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
                blip.IsFlashing = flashing;
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
