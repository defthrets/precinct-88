using System;
using System.Text;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Response
{
    /// <summary>
    /// What has gone out over the air about you.
    ///
    /// THE POLICE DO NOT KNOW WHO YOU ARE. They know what somebody told them: a man in a white
    /// shirt, on foot, last seen heading north on Alta. That is a description, it goes stale,
    /// and it is wrong the moment you are not that any more.
    ///
    /// This is the single mechanic that makes the wanted rework worth having. Breaking line of
    /// sight is only half an escape in vanilla because the game re-acquires you on sight
    /// regardless -- the search radius is theatre over a system that never actually lost you.
    /// Here, walking out of an alley in a different jacket means the officer stood at the end
    /// of it is looking for somebody else, and he is looking at you while he does it.
    ///
    /// Clothing is read as the game's own drawable variations rather than as anything clever.
    /// That means it changes when you change at a wardrobe, at a shop, or through any other mod
    /// that dresses the player -- all three are legitimate ways to beat a description, and none
    /// of them needed a line of code here.
    /// </summary>
    internal sealed class Radio
    {
        /// <summary>
        /// How close an officer has to be to see through a changed description.
        ///
        /// Not immunity. A different jacket does not work at four metres with him looking
        /// straight at you, and it should not -- the description is what got him looking in
        /// your direction, not what identifies you once he is stood in front of you.
        /// </summary>
        public const float RecognisesAnywayRange = 7f;

        /// <summary>Component slots that add up to what somebody would describe you as.</summary>
        private static readonly int[] Clothing = { 3, 4, 8, 11 };

        private int _look;
        private int _plate;
        private int _carModel;
        private bool _onFoot;

        private Vector3 _lastSeen;
        private int _lastSeenAt;
        private string _lastZone = string.Empty;

        /// <summary>Whether a description has ever gone out.</summary>
        public bool OnAir { get; private set; }

        /// <summary>Where they last actually had eyes on you.</summary>
        public Vector3 LastSeen => _lastSeen;

        /// <summary>When, in game time.</summary>
        public int LastSeenAt => _lastSeenAt;

        /// <summary>The zone name that went out with it, for the ticker.</summary>
        public string LastZone => _lastZone;

        /// <summary>
        /// Puts a description out, or refreshes the one that is out.
        ///
        /// Called whenever an officer has actual eyes on the player -- so as long as they can
        /// see you, the description is by definition current and changing your shirt achieves
        /// nothing. It only becomes beatable once they have lost you, which is the whole shape
        /// of the thing.
        /// </summary>
        public void Describe(Ped who)
        {
            try
            {
                if (!Cops.Alive(who)) return;

                _look = LookOf(who);
                _onFoot = !who.IsInVehicle();

                if (!_onFoot)
                {
                    var car = who.CurrentVehicle;

                    if (Cops.Alive(car))
                    {
                        _carModel = car.Model.Hash;
                        _plate = PlateOf(car);
                    }
                }
                else
                {
                    _carModel = 0;
                    _plate = 0;
                }

                _lastSeen = who.Position;
                _lastSeenAt = Game.GameTime;
                _lastZone = Streets.Districts.ZoneAt(_lastSeen);

                OnAir = true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not take a description: " + ex.Message);
            }
        }

        /// <summary>Nothing is out about you any more.</summary>
        public void Clear()
        {
            OnAir = false;
            _look = 0;
            _carModel = 0;
            _plate = 0;
            _lastZone = string.Empty;
        }

        /// <summary>
        /// Whether the player still answers the description that went out.
        ///
        /// Two ways to stop matching and both of them are things a player does anyway: change
        /// what you are wearing, or change what you are in. Getting OUT of the described car
        /// counts -- the call said a red Sultan, and you are a man walking.
        /// </summary>
        public bool Matches(Ped who)
        {
            try
            {
                if (!OnAir) return true;
                if (!Cops.Alive(who)) return true;

                if (LookOf(who) != _look) return false;

                var inCar = who.IsInVehicle();

                // Described in a car, now on foot, or the other way round.
                if (inCar == _onFoot) return false;

                if (inCar && _carModel != 0)
                {
                    var car = who.CurrentVehicle;
                    if (!Cops.Alive(car)) return false;

                    // A DIFFERENT CAR OF THE SAME MODEL IS STILL A DIFFERENT CAR, which is
                    // what the plate is doing here. Without it, stealing the identical taxi
                    // parked behind the one you abandoned counts as not having changed cars.
                    if (car.Model.Hash != _carModel) return false;
                    if (_plate != 0 && PlateOf(car) != _plate) return false;
                }

                return true;
            }
            catch
            {
                // Cannot tell, so assume they know you. Failing towards being caught is the
                // less annoying of the two failures.
                return true;
            }
        }

        /// <summary>The line for the ticker. Short, and in the register of a radio call.</summary>
        public string Call(Weight what)
        {
            var sb = new StringBuilder();

            sb.Append("Dispatch: ").Append(what.Called);

            if (!string.IsNullOrEmpty(_lastZone))
            {
                sb.Append(", ").Append(Pretty(_lastZone));
            }

            sb.Append(_onFoot ? ". Suspect on foot." : ". Suspect in a vehicle.");

            return sb.ToString();
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
                // assembly rather than assumed, which is the only way to find this out that
                // does not involve a build error. Game.GetLocalizedString is the wrapper, and
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
