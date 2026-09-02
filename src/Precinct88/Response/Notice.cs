using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;
using Precinct88.Streets;

namespace Precinct88.Response
{
    /// <summary>Who saw it, which decides both whether and how fast anybody hears.</summary>
    internal enum Sighted
    {
        /// <summary>Nobody at all. You got away with it.</summary>
        Nobody,

        /// <summary>One of ours. It goes out on the radio immediately.</summary>
        Officer,

        /// <summary>A passer-by. Somebody has to get their phone out first.</summary>
        Public,
    }

    /// <summary>
    /// One thing an officer could notice you doing, and what it takes to notice it.
    /// </summary>
    internal sealed class Misdeed
    {
        /// <summary>How it is said out loud. Goes on the ticker and into the log.</summary>
        public readonly string Name;

        /// <summary>
        /// How many cars it is worth, literally.
        ///
        /// It used to be an abstract severity from one to three that something else turned
        /// into a number of cars, which meant two places to look and two things to keep in
        /// step. It is the number of cars.
        /// </summary>
        public readonly int Weight;

        /// <summary>
        /// The chance an officer who saw it actually does anything about it, as a percent.
        ///
        /// NOT EVERY OFFICER CARES ABOUT EVERY THING, and without this the police read as a
        /// tripwire: do the thing anywhere near a uniform and a car is coming, every time,
        /// which is both exhausting and obviously mechanical. A man walking down the street
        /// with a pistol on show gets stopped sometimes. Gunfire gets reported always.
        ///
        /// Scaled by the district's Attention on top of this -- see Notice.Bothered. The same
        /// pistol is a much bigger deal in Rockford Hills than it is in Davis, which is what
        /// that number was always for and the first thing in this build to use it.
        /// </summary>
        public readonly int Chance;

        /// <summary>
        /// Heard rather than seen.
        ///
        /// The difference is line of sight. A gun held in the street has to be LOOKED at by
        /// somebody facing the right way; a gun fired does not, and an officer round the corner
        /// hears it exactly as well as one across the road. Getting this the wrong way round is
        /// what makes police feel either blind or psychic.
        /// </summary>
        public readonly bool Loud;

        /// <summary>How long before this same thing is worth reporting again.</summary>
        public readonly int CooldownMs;

        /// <summary>The picture for it on the strip. See UI.Icons.</summary>
        public readonly string Icon;

        /// <summary>Whether it is happening right now. Nothing in here may throw.</summary>
        public readonly Func<Ped, bool> Happening;

        /// <summary>When it may next be reported. Not a setting; bookkeeping.</summary>
        public int NextAt;

        public Misdeed(string name, int weight, bool loud, int cooldownMs, int chance,
                       string icon, Func<Ped, bool> happening)
        {
            Name = name;
            Weight = weight;
            Loud = loud;
            CooldownMs = cooldownMs;
            Chance = chance;
            Icon = icon;
            Happening = happening;
        }
    }

    /// <summary>
    /// What the police actually notice you doing.
    ///
    /// THE MISSING HALF. Patrol put police on the streets and they drove around beautifully
    /// ignoring everything, because the only thing in this build that could start a response
    /// was parked with the rest of the wanted rework. A police force that cannot be provoked is
    /// scenery with a light bar.
    ///
    /// WHY THIS IS NOT THE ENGINE'S WANTED SYSTEM, and the reason the request needed its own
    /// file at all: the things worth noticing are mostly not crimes the game has an opinion
    /// about. Standing in a crowd with a rifle out earns you nothing until you point it at
    /// somebody. A stand-up fight outside a shop earns you nothing at all. Both are things a
    /// passing officer would obviously react to, and neither will ever produce a star on its
    /// own -- so hooking this to the wanted level would have quietly dropped most of it.
    ///
    /// IT DELIBERATELY GIVES NO STARS. This reports what was seen; Callout sends somebody. The
    /// wanted level, the search, and what officers do when they arrive belong to Manhunt, which
    /// is still parked, and inventing a second half-version of it here is how the mod got into
    /// trouble in the first place.
    ///
    /// SOMEBODY HAS TO BE THERE. There is no radius around the player inside which crime is
    /// magically known -- a report requires a real person, standing somewhere real, who could
    /// actually see or hear it. That is the whole argument of the mod applied to noticing
    /// rather than to responding, and it means a fight down an empty side street at four in
    /// the morning genuinely goes unnoticed. Check the street before, and nothing happens.
    ///
    /// THE PUBLIC COUNT, AND THAT WAS THE HOLE. For a while only our own officers could
    /// witness anything, which meant shooting a man in front of twenty people did nothing at
    /// all unless a patrol car happened to be in the same street -- and that is a far stranger
    /// claim than the one the mod was trying to make. Anybody can see you now.
    ///
    /// The difference between the two is SPEED, not eyesight. An officer has a radio and it
    /// goes out at once. A passer-by has to get their phone out, so the report lands several
    /// seconds later -- which is enough time to be round the corner, and is the difference
    /// between being caught and being described.
    ///
    /// A passer-by is also markedly less likely to bother at all. Most people do not phone the
    /// police about a man carrying a gun; they walk the other way.
    /// </summary>
    internal sealed class Notice
    {
        private const int TickMs = 500;

        /// <summary>How far an officer can make out what you are doing.</summary>
        private const float SeenRange = 55f;

        /// <summary>And how far a gunshot carries. Generous, because gunshots do.</summary>
        private const float HeardRange = 130f;

        /// <summary>
        /// How close a member of the public has to be to make anything out.
        ///
        /// Shorter than an officer's. Somebody walking to work is not scanning the street for
        /// trouble, and the ten metres of difference is the whole of that idea.
        /// </summary>
        private const float PublicSeeRange = 45f;

        /// <summary>
        /// How much less likely a passer-by is to do anything than an officer.
        ///
        /// Most people do not phone the police about a man carrying a gun. Without this,
        /// putting the public in as witnesses would have quietly undone all of the work that
        /// made these reports rare -- there is always somebody about in the city.
        /// </summary>
        private const float PublicCare = 0.5f;

        /// <summary>How long somebody takes to get their phone out and get through.</summary>
        private const int CallInMinMs = 4500;
        private const int CallInMaxMs = 10000;

        /// <summary>How recent one of the engine's "time since" answers counts as now.</summary>
        private const int JustNowMs = 1500;

        /// <summary>
        /// How long before an officer who let something go looks at it again.
        ///
        /// Much shorter than the full cooldown, and the difference matters. A one-off -- a
        /// punch, a car taken -- is simply MISSED when the roll fails, which is what makes the
        /// chance mean anything. Something you are still doing thirty seconds later gets rolled
        /// for again, so carrying a gun around long enough will eventually get you noticed
        /// without any single moment being a coin flip you cannot lose.
        /// </summary>
        private const int ShrugMs = 12000;

        private readonly Settings _cfg;
        private readonly Fleet _fleet;
        private readonly Foot _foot;
        private readonly List<Misdeed> _list;
        private readonly Random _rng = new Random();

        private int _lastTick;

        /// <summary>
        /// Something a passer-by saw and is in the middle of calling in.
        ///
        /// One at a time rather than a queue, and deliberately. Callout only ever runs one call
        /// anyway, and a backlog of pending phone calls arriving one after another would give a
        /// single moment several separate responses over the following half minute.
        /// </summary>
        private string _ringingIn;
        private string _ringingIcon;
        private Vector3 _ringingWhere;
        private int _ringingWeight;
        private int _ringingAt;

        /// <summary>Name, where, weight. Wired to Callout by Main.</summary>
        public Action<string, Vector3, int, string> Report;

        public Notice(Settings cfg, Fleet fleet, Foot foot)
        {
            _cfg = cfg;
            _fleet = fleet;
            _foot = foot;

            _list = Build();
        }

        /// <summary>
        /// Everything worth noticing, in the order it is checked.
        ///
        /// WORST FIRST, because the first match on a tick wins and a man firing a gun while
        /// driving at people should be reported as the gun rather than as the driving.
        /// </summary>
        private static List<Misdeed> Build()
        {
            // WHAT WAS TAKEN OUT, AND WHY IT IS WORTH RECORDING. The first version also
            // reported burnouts, swinging a bat about, driving up on the pavement and driving
            // on the wrong side. Every one of them was defensible and together they were
            // unbearable: they are things a player does constantly and incidentally, so the
            // police became a nagging presence attached to ordinary driving rather than
            // something that happens when you do something worth noticing.
            //
            // The lesson is about the FLOOR rather than about those four in particular. The
            // bottom of this list has to be something a reasonable person would agree is
            // worth an officer's attention, because everything below that line turns the whole
            // system into noise. If they come back it will be as something an officer mentions
            // rather than something he drives across a district for.
            return new List<Misdeed>
            {
                // Heard, not seen -- see Misdeed.Loud. The only thing here that always gets
                // reported and the only thing worth more than one car.
                new Misdeed("shots fired", 2, true, 9000, 100, "shots.png",
                            me => Function.Call<bool>(Hash.IS_PED_SHOOTING, me.Handle)),

                new Misdeed("somebody pointing a gun", 1, false, 11000, 80, "aim.png",
                            me => Firearm(me) &&
                                  Function.Call<bool>(Hash.IS_PLAYER_FREE_AIMING,
                                                      Game.Player.Handle)),

                new Misdeed("a car being taken", 1, false, 18000, 75, "key.png",
                            me => Function.Call<bool>(Hash.IS_PED_JACKING, me.Handle)),

                new Misdeed("a car driven at people", 1, false, 12000, 70, "runover.png",
                            me => Driving(me) && Since(Hash.GET_TIME_SINCE_PLAYER_HIT_PED)),

                new Misdeed("a fight in the street", 1, false, 14000, 40, "fist.png",
                            me => Function.Call<bool>(Hash.IS_PED_IN_MELEE_COMBAT, me.Handle)),

                // THE RARE ONE. Carrying a gun about is not an event, it is a state, and it is
                // true for most of the time anybody plays this game -- so it is reported
                // seldom, and in Davis it is reported almost never.
                new Misdeed("a gun out in the street", 1, false, 20000, 18, "gun.png",
                            me => Firearm(me) && !me.IsInVehicle()),
            };
        }

        public void Update()
        {
            if (!_cfg.RespondToCrime) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            if (Report == null) return;

            try
            {
                // WHOEVER IS ON THE PHONE, first and regardless of what is happening now.
                // The call was placed about something that has already finished, and it has to
                // land even if the player has since stopped, driven off, or died.
                if (_ringingIn != null && now >= _ringingAt)
                {
                    Log.Info("Called in by a passer-by: " + _ringingIn + ".");
                    Report(_ringingIn, _ringingWhere, _ringingWeight, _ringingIcon);
                    _ringingIn = null;
                }

                var me = Game.Player.Character;
                if (me == null || !me.Exists() || me.IsDead) return;

                // WORKED OUT AT MOST TWICE A TICK, NOT ONCE PER THING NOTICED. Whether an
                // officer is there does not depend on WHICH offence is being checked -- only on
                // whether it has to be seen or merely heard. Asking per offence meant a player
                // driving badly (pavement, wrong side, and a burnout all at once) paid for
                // three full line-of-sight sweeps of every officer on the map every half
                // second, for one answer repeated three times.
                Sighted? seen = null;
                Sighted? heard = null;

                foreach (var what in _list)
                {
                    if (now < what.NextAt) continue;

                    if (!Ask(what, me)) continue;

                    Sighted by;

                    if (what.Loud)
                    {
                        if (heard == null) heard = Who(me, true);
                        by = heard.Value;
                    }
                    else
                    {
                        if (seen == null) seen = Who(me, false);
                        by = seen.Value;
                    }

                    // NOBODY SAW IT. Not even a cooldown -- there is nothing to cool down from,
                    // and setting one would mean an empty street quietly used up the allowance
                    // for the busy one you walk into ten seconds later.
                    if (by == Sighted.Nobody) continue;

                    if (!Bothered(what, me, by))
                    {
                        // Seen and let go. Not the full cooldown -- see ShrugMs.
                        what.NextAt = now + ShrugMs;
                        continue;
                    }

                    what.NextAt = now + what.CooldownMs;

                    if (by == Sighted.Public)
                    {
                        // Somebody is getting their phone out. Dropped rather than queued if
                        // one is already ringing in -- see the fields.
                        if (_ringingIn == null)
                        {
                            _ringingIn = what.Name;
                            _ringingIcon = what.Icon;
                            _ringingWhere = me.Position;
                            _ringingWeight = what.Weight;
                            _ringingAt = now + CallInMinMs +
                                         _rng.Next(CallInMaxMs - CallInMinMs);

                            Log.Info("Seen by a passer-by: " + what.Name + ".");
                        }

                        return;
                    }

                    Log.Info("Noticed by an officer: " + what.Name + ".");
                    Report(what.Name, me.Position, what.Weight, what.Icon);

                    // One a tick. Reporting four things at once from one moment produces four
                    // ticker lines and one enormous response to what was really a single event.
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not check what the police can see: " + ex.Message);
            }
        }

        /// <summary>
        /// Whether the officer who saw it does anything about it.
        ///
        /// THE DISTRICT IS HALF OF THIS, and it is the first thing in the patrol build to use
        /// the Attention number the districts have carried since the beginning. Rockford Hills
        /// is 0.85 and Davis is 0.30 -- there are far fewer cars in Rockford and the one that
        /// is there has already noticed you, while in Davis there are cars everywhere and none
        /// of them care. Density and Attention being separate numbers is the mod's oldest
        /// claim and this is where it finally shows.
        ///
        /// The floor keeps the least attentive district from being a free pass: a quarter of
        /// the stated chance still gets through in Davis.
        /// </summary>
        private bool Bothered(Misdeed what, Ped me, Sighted by)
        {
            // Gunfire, and only gunfire. Everyone reports it, including the public.
            if (what.Chance >= 100) return true;

            try
            {
                var here = Districts.At(me.Position);
                var care = 0.25f + 0.75f * here.Attention;

                if (by == Sighted.Public) care *= PublicCare;

                return _rng.Next(100) < what.Chance * care;
            }
            catch
            {
                // Cannot tell where he is. Report it -- the alternative is silently policing
                // nothing at all because a zone lookup failed.
                return true;
            }
        }

        /// <summary>
        /// Runs one test, and swallows whatever it does.
        ///
        /// Every check in the list touches the game and any of them can throw on a frame where
        /// the player is mid-transition -- getting into a car, dying, being teleported by
        /// another mod. One bad frame must not stop the other nine being asked.
        /// </summary>
        private static bool Ask(Misdeed what, Ped me)
        {
            try
            {
                return what.Happening(me);
            }
            catch
            {
                return false;
            }
        }

        // ---- who was there -----------------------------------------------------

        /// <summary>
        /// Who, if anybody, could have seen or heard it.
        ///
        /// OURS FIRST AND IT SHORT-CIRCUITS. An officer outranks a passer-by -- his report is
        /// immediate rather than a phone call, and he is more likely to make one -- so finding
        /// one means there is no reason to sweep the crowd as well.
        ///
        /// Foot officers count and matter more than they look: they are the only police in this
        /// mod ever on a pavement, in a crowd, or up an alley, which is exactly where the
        /// things in the list above tend to happen.
        /// </summary>
        private Sighted Who(Ped me, bool loud)
        {
            if (Ours(me, loud)) return Sighted.Officer;

            return Public(me, loud) ? Sighted.Public : Sighted.Nobody;
        }

        /// <summary>
        /// Anybody at all who is not one of our units.
        ///
        /// VANILLA POLICE ARE NOT EXCLUDED, deliberately. Scenario officers stood outside a
        /// station are left in the world on purpose (see AmbientCops) and doing something in
        /// front of one and having nothing whatever happen looks far worse than the small
        /// inconsistency of a uniformed man phoning it in like anybody else. He is not one of
        /// the finite pool, so he does not get the pool's immediate radio -- which is the
        /// honest way to hold both ideas at once.
        ///
        /// Returns on the FIRST person found rather than counting them. Whether one person or
        /// forty saw it changes nothing here; the district's Attention already carries how much
        /// somewhere is the sort of place that reports things.
        /// </summary>
        private static bool Public(Ped me, bool loud)
        {
            var range = loud ? HeardRange : PublicSeeRange;

            try
            {
                foreach (var ped in World.GetNearbyPeds(me, range))
                {
                    if (ped == null || !ped.Exists() || ped.IsDead) continue;
                    if (ped.Handle == me.Handle) continue;
                    if (!ped.IsHuman) continue;

                    // Nobody in a lift, under the map, or otherwise not really present.
                    if (!ped.IsAlive) continue;

                    if (loud)
                    {
                        // No line of sight needed. A gunshot goes through a wall and so does
                        // the person on the other side of it reaching for their phone.
                        return true;
                    }

                    if (Cops.Sees(ped, me, range)) return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not look for witnesses: " + ex.Message);
            }

            return false;
        }

        /// <summary>Whether one of our own officers could have seen or heard it.</summary>
        private bool Ours(Ped me, bool loud)
        {
            if (_fleet != null)
            {
                foreach (var unit in _fleet.Units)
                {
                    if (!unit.Alive) continue;

                    foreach (var officer in unit.Everyone())
                    {
                        if (Noticed(officer, me, loud)) return true;
                    }
                }
            }

            if (_foot != null)
            {
                foreach (var walker in _foot.Walkers)
                {
                    if (!walker.Alive) continue;

                    if (Noticed(walker.Who, me, loud)) return true;
                }
            }

            return false;
        }

        private static bool Noticed(Ped officer, Ped me, bool loud)
        {
            try
            {
                if (!Cops.Alive(officer)) return false;

                if (loud)
                {
                    return officer.Position.DistanceTo(me.Position) <= HeardRange;
                }

                return Cops.Sees(officer, me, SeenRange);
            }
            catch
            {
                return false;
            }
        }

        // ---- the tests ---------------------------------------------------------

        /// <summary>Driving, rather than a passenger in something.</summary>
        private static bool Driving(Ped me)
        {
            if (!me.IsInVehicle()) return false;

            var car = me.CurrentVehicle;
            if (!Cops.Alive(car)) return false;

            return car.Driver != null && car.Driver.Handle == me.Handle;
        }

        /// <summary>
        /// Whether one of the engine's own "time since" counters just tripped.
        ///
        /// These return milliseconds since the thing last happened, and a very large number if
        /// it never has -- so "recently" is simply a small answer. The engine keeps this
        /// bookkeeping for its own wanted system whether or not anything reads it, which makes
        /// it far better than trying to work out from a heading and a road node whether
        /// somebody is on the wrong side of the carriageway.
        /// </summary>
        private static bool Since(Hash what)
        {
            var ms = Function.Call<int>(what, Game.Player.Handle);

            return ms >= 0 && ms < JustNowMs;
        }

        /// <summary>A gun, as opposed to a bat, a phone, or empty hands.</summary>
        private static bool Firearm(Ped me)
        {
            if (!Cops.Armed(me)) return false;

            var group = me.Weapons.Current.Group;

            return group != WeaponGroup.Unarmed &&
                   group != WeaponGroup.Melee &&
                   group != WeaponGroup.Parachute &&
                   group != WeaponGroup.PetrolCan &&
                   group != WeaponGroup.FireExtinguisher &&
                   group != WeaponGroup.DigiScanner &&
                   group != WeaponGroup.NightVision;
        }

    }
}
