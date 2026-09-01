using System;
using System.Collections.Generic;
using System.Text;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Response
{
    /// <summary>
    /// The separate things the police can know about you, each of which is beaten differently.
    ///
    /// A FLAGS SET RATHER THAN A BOOLEAN, and that is the whole point of this file. "They know
    /// you or they do not" is one bit, and one bit cannot express the situation that makes a
    /// chase interesting: they have a description of a man in a white shirt driving a red
    /// Sultan, they never got a look at his face, and he has just walked out of a clothes shop.
    ///
    /// Each flag is captured independently, matches independently, and is escaped independently.
    /// Ditching the car stops being all-or-nothing.
    /// </summary>
    [Flags]
    internal enum Known
    {
        /// <summary>A crime was called in and nobody could say a thing about who did it.</summary>
        Nothing = 0,

        /// <summary>
        /// They got a look at your face.
        ///
        /// The one axis you cannot change afterwards -- there is no plastic surgeon in this
        /// mod and there should not be. It is beaten BEFORE the crime, by covering your face,
        /// which is exactly the bandana in Red Dead and exactly the point.
        /// </summary>
        Face = 1 << 0,

        /// <summary>What you were wearing. Beaten by changing.</summary>
        Clothes = 1 << 1,

        /// <summary>What you were driving, down to the plate. Beaten by changing or walking.</summary>
        Vehicle = 1 << 2,

        /// <summary>What you did it with. Does not identify you; it decides how they come.</summary>
        Weapon = 1 << 3,

        /// <summary>A camera has it, which is why nobody had to be standing there.</summary>
        Camera = 1 << 4,
    }

    /// <summary>
    /// What has gone out over the air about you.
    ///
    /// THE POLICE DO NOT KNOW WHO YOU ARE. They know what somebody told them, and what somebody
    /// told them has holes in it. That is a description, it goes stale, and it is wrong the
    /// moment you are not that any more.
    ///
    /// Vanilla re-acquires you on sight regardless -- the search radius is theatre over a
    /// system that never lost you. Here, walking out of an alley in a different jacket means
    /// the officer at the end of it is looking for somebody else, and he is looking at YOU
    /// while he does it.
    ///
    /// Clothing is read as the game's own drawable variations rather than as anything clever,
    /// so it changes at a wardrobe, at a shop, or through any other mod that dresses the
    /// player. All three are legitimate ways to beat a description and none of them needed a
    /// line of code here.
    /// </summary>
    internal sealed class Radio
    {
        /// <summary>Component slots that add up to what somebody would describe you as.</summary>
        private static readonly int[] Clothing = { 3, 4, 8, 11 };

        /// <summary>
        /// The mask slot.
        ///
        /// Component 1 is berd -- masks, balaclavas, bandanas. Zero is a bare face on every
        /// player model, so a non-zero drawable here is something over it. Helmets are props
        /// rather than components and are asked for separately.
        /// </summary>
        private const int MaskSlot = 1;

        private int _look;
        private int _plate;
        private int _carModel;
        private bool _describedInCar;

        private Vector3 _lastSeen;
        private int _lastSeenAt;
        private string _lastZone = string.Empty;

        /// <summary>What they have. The whole state of this class in one value.</summary>
        public Known Has { get; private set; }

        /// <summary>
        /// When each flag was last gained, for the HUD to flash it.
        ///
        /// A player who is told nothing when the police learn his plate has been given a
        /// mechanic he cannot see working.
        /// </summary>
        private readonly Dictionary<Known, int> _gainedAt = new Dictionary<Known, int>();

        /// <summary>Whether anything at all has gone out.</summary>
        public bool OnAir { get; private set; }

        /// <summary>
        /// Whether a crime is out but nobody could describe the man who did it.
        ///
        /// The state vanilla has no way to represent and the reason the flags exist. Police
        /// converge on a location; an officer walks straight past you because as far as he
        /// knows you are a member of the public.
        /// </summary>
        public bool Unidentified => OnAir && Has == Known.Nothing;

        public Vector3 LastSeen => _lastSeen;
        public int LastSeenAt => _lastSeenAt;
        public string LastZone => _lastZone;

        /// <summary>When this flag was last gained, or 0.</summary>
        public int GainedAt(Known flag)
        {
            int at;
            return _gainedAt.TryGetValue(flag, out at) ? at : 0;
        }

        // ---- taking a description ----------------------------------------------

        /// <summary>
        /// Records what a witness actually got, and nothing more.
        ///
        /// THE CALLER SAYS WHAT WAS SEEN, because only the caller knows. An officer stood in
        /// front of you gets everything. Somebody watching from a third-floor window across
        /// the street gets your shirt and your car and could not pick you out of a line-up. A
        /// camera gets whatever is in frame and does not care that the street was empty.
        ///
        /// Each axis captures the CURRENT value at the moment it is gained, so learning your
        /// plate an hour into a chase records the car you are in now, not the one you started
        /// in. Axes already held are not re-captured -- that would quietly refresh a
        /// description you have already beaten.
        /// </summary>
        public void Note(Ped who, Known gained)
        {
            try
            {
                if (!Cops.Alive(who)) return;

                var now = Game.GameTime;

                // A face nobody could see is not a face they got. Asked here rather than at
                // every call site, so no witness anywhere can accidentally identify a man in
                // a balaclava.
                if ((gained & Known.Face) != 0 && Masked(who)) gained &= ~Known.Face;

                // On foot there is no vehicle to describe. Without this, a witness "gets your
                // vehicle" while you are walking, records a model of zero, and you are then
                // permanently un-matchable on an axis they think they hold.
                if ((gained & Known.Vehicle) != 0 && !who.IsInVehicle()) gained &= ~Known.Vehicle;

                foreach (Known flag in Enum.GetValues(typeof(Known)))
                {
                    if (flag == Known.Nothing) continue;
                    if ((gained & flag) == 0) continue;
                    if ((Has & flag) != 0) continue;   // already held; do not refresh it

                    Capture(who, flag);

                    Has |= flag;
                    _gainedAt[flag] = now;
                }

                _lastSeen = who.Position;
                _lastSeenAt = now;
                _lastZone = Streets.Districts.ZoneAt(_lastSeen);

                OnAir = true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not take a description: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-takes the axes they already hold, because somebody is looking right at you.
        ///
        /// Note() deliberately never refreshes a held axis -- an old description quietly
        /// updating itself is the entire mechanic failing silently. But that rule is wrong in
        /// one specific case, and it is the case that would otherwise look most like a bug: an
        /// officer with eyes on you WATCHES you get out of the red Sultan and into a black
        /// Baller. If the call still says red Sultan after that, the player has beaten a
        /// description in front of the man reading it.
        ///
        /// So: while they can see you AND have identified you, whatever they hold is current by
        /// definition. Only called from that one place, on purpose.
        /// </summary>
        public void Refresh(Ped who)
        {
            try
            {
                if (!Cops.Alive(who) || Has == Known.Nothing) return;

                if ((Has & Known.Clothes) != 0) Capture(who, Known.Clothes);

                // Only while there is a vehicle to re-take. Getting OUT of the described car
                // while they watch has to leave the old one on the call -- they know he was in
                // it, and "suspect now on foot" is Eyes() noticing, not this.
                if ((Has & Known.Vehicle) != 0 && who.IsInVehicle()) Capture(who, Known.Vehicle);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not refresh the description: " + ex.Message);
            }
        }

        /// <summary>Where they last had eyes on you, whether or not they identified you.</summary>
        public void Seen(Vector3 where)
        {
            _lastSeen = where;
            _lastSeenAt = Game.GameTime;
            _lastZone = Streets.Districts.ZoneAt(where);
            OnAir = true;
        }

        private void Capture(Ped who, Known flag)
        {
            switch (flag)
            {
                case Known.Clothes:
                    _look = LookOf(who);
                    break;

                case Known.Vehicle:
                    var car = who.CurrentVehicle;

                    if (Cops.Alive(car))
                    {
                        _carModel = car.Model.Hash;
                        _plate = PlateOf(car);
                        _describedInCar = true;
                    }
                    break;

                // Face, Weapon and Camera have nothing to record. Face is the player and cannot
                // change; the other two are facts about the call rather than about him.
            }
        }

        public void Clear()
        {
            Has = Known.Nothing;
            OnAir = false;

            _look = 0;
            _carModel = 0;
            _plate = 0;
            _describedInCar = false;
            _lastZone = string.Empty;

            _gainedAt.Clear();
        }

        /// <summary>Drops one axis. For a change of clothes they have not seen, and for tests.</summary>
        public void Forget(Known flag)
        {
            Has &= ~flag;
            _gainedAt.Remove(flag);
        }

        // ---- matching ----------------------------------------------------------

        /// <summary>
        /// Which of the things they know still describe the man standing here.
        ///
        /// Returns the intersection, not a yes or no, because the caller often wants to know
        /// WHICH -- the HUD shows it, and an officer who has your car but not your face behaves
        /// differently to one who has both.
        /// </summary>
        public Known StillMatching(Ped who)
        {
            var matching = Known.Nothing;

            try
            {
                if (!Cops.Alive(who)) return Known.Nothing;

                // A face they have is a face they have, unless it is covered NOW. Putting a
                // mask on after they identified you does not un-identify you -- they already
                // know -- but it does stop them picking you out of a crowd on sight, which is
                // the honest reading and the one that leaves masks worth wearing late.
                if ((Has & Known.Face) != 0 && !Masked(who)) matching |= Known.Face;

                if ((Has & Known.Clothes) != 0 && LookOf(who) == _look) matching |= Known.Clothes;

                if ((Has & Known.Vehicle) != 0 && _describedInCar && who.IsInVehicle())
                {
                    var car = who.CurrentVehicle;

                    if (Cops.Alive(car) && car.Model.Hash == _carModel &&
                        (_plate == 0 || PlateOf(car) == _plate))
                    {
                        // A DIFFERENT CAR OF THE SAME MODEL IS STILL A DIFFERENT CAR, which is
                        // what the plate is doing here. Without it, stealing the identical taxi
                        // parked behind the one you abandoned counts as not having changed cars.
                        matching |= Known.Vehicle;
                    }
                }
            }
            catch
            {
                // Cannot tell, so assume they know you. Failing towards being caught is the
                // less annoying of the two failures.
                return Has;
            }

            return matching;
        }

        /// <summary>
        /// Whether an officer looking straight at this man has reason to act.
        ///
        /// ANY axis is enough. He does not need to be certain -- a shirt matching the call is
        /// reason to walk over, and that is how it should feel from the pavement.
        /// </summary>
        public bool Recognises(Ped who) => StillMatching(who) != Known.Nothing;

        /// <summary>Whether the face is covered right now.</summary>
        public static bool Masked(Ped who)
        {
            try
            {
                if (!Cops.Alive(who)) return false;

                if (Function.Call<bool>(Hash.IS_PED_WEARING_HELMET, who.Handle)) return true;

                return Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, who.Handle, MaskSlot) != 0;
            }
            catch
            {
                return false;
            }
        }

        // ---- what it reads as --------------------------------------------------

        /// <summary>The line for the ticker. Short, and in the register of a radio call.</summary>
        public string Call(Weight what)
        {
            var sb = new StringBuilder();

            sb.Append("Dispatch: ").Append(what.Called);

            if (!string.IsNullOrEmpty(_lastZone)) sb.Append(", ").Append(Pretty(_lastZone));

            if (Has == Known.Nothing)
            {
                // The whole reason this state is worth having. Somebody phoned in a crime and
                // could not tell them a single thing about who did it.
                sb.Append(". No description.");
                return sb.ToString();
            }

            sb.Append(". ").Append(Describe());

            return sb.ToString();
        }

        /// <summary>What they have, said the way a dispatcher would say it.</summary>
        public string Describe()
        {
            var bits = new List<string>();

            if ((Has & Known.Face) != 0) bits.Add("suspect identified");
            if ((Has & Known.Clothes) != 0) bits.Add("clothing described");
            if ((Has & Known.Vehicle) != 0) bits.Add("vehicle described");
            if ((Has & Known.Camera) != 0) bits.Add("on camera");

            return bits.Count == 0 ? "No description." : string.Join(", ", bits.ToArray()) + ".";
        }

        /// <summary>
        /// A zone code turned back into something a person would say.
        ///
        /// The game hands out DTVINE and LEGSQU. A dispatch line reading "shots fired, LEGSQU"
        /// is a mod showing you its internals.
        /// </summary>
        private static string Pretty(string zone)
        {
            try
            {
                // GET_LABEL_TEXT is NOT in SHVDN 3.9's Hash enum -- checked by reflecting the
                // assembly rather than assumed. Game.GetLocalizedString is the wrapper, and
                // DOES_TEXT_LABEL_EXIST is asked first because the lookup hands back the key
                // itself when there is no entry, and for a zone code that looks like a real
                // answer.
                if (!Function.Call<bool>(Hash.DOES_TEXT_LABEL_EXIST, zone)) return zone;

                var full = Game.GetLocalizedString(zone);

                if (!string.IsNullOrEmpty(full) &&
                    !string.Equals(full, zone, StringComparison.OrdinalIgnoreCase) &&
                    !full.StartsWith("NULL", StringComparison.OrdinalIgnoreCase))
                {
                    return full;
                }
            }
            catch
            {
                // Fall through to the code, which is at least true.
            }

            return zone;
        }

        private static int LookOf(Ped who)
        {
            var hash = 17;

            foreach (var slot in Clothing)
            {
                try
                {
                    var drawable = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, who.Handle, slot);
                    var texture = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, who.Handle, slot);

                    hash = hash * 31 + drawable;
                    hash = hash * 31 + texture;
                }
                catch
                {
                    // A slot that will not read contributes nothing rather than throwing off
                    // the whole signature.
                }
            }

            return hash;
        }

        private static int PlateOf(Vehicle car)
        {
            try
            {
                var plate = car.Mods == null ? null : car.Mods.LicensePlate;
                return string.IsNullOrEmpty(plate) ? 0 : plate.GetHashCode();
            }
            catch
            {
                return 0;
            }
        }
    }
}
