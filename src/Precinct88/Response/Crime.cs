using System;
using System.Collections.Generic;

namespace Precinct88.Response
{
    /// <summary>
    /// The things the police in this game can be told about.
    ///
    /// Deliberately a short list. Every entry here has to be something a system can actually
    /// detect and something a player can recognise as the reason they are now being chased --
    /// a taxonomy with forty entries in it is a taxonomy where thirty-five never fire and the
    /// other five are indistinguishable in play.
    /// </summary>
    internal enum Offence
    {
        /// <summary>Standing somewhere you obviously should not be. The mildest thing there is.</summary>
        Loitering,

        /// <summary>A gun in your hand in the street. Not fired.</summary>
        Brandishing,

        /// <summary>How you are driving. Speed, pavements, the wrong side of the road.</summary>
        Reckless,

        /// <summary>The car is not yours and it has been called in.</summary>
        StolenVehicle,

        /// <summary>Selling, holding, or the aftermath of either. Reported over the bridge.</summary>
        Dealing,

        /// <summary>Hands on somebody. Ordinary violence, nobody dead.</summary>
        Assault,

        /// <summary>Shots fired. This is the line the mod treats as the serious one.</summary>
        ShotsFired,

        /// <summary>A body. Anybody.</summary>
        Homicide,

        /// <summary>An officer. Not the same thing as a body, and the response is not either.</summary>
        OfficerDown,
    }

    /// <summary>
    /// What an offence is worth, and what it justifies sending.
    ///
    /// SEVERITY IS NOT STARS AND THIS IS THE WHOLE ARGUMENT OF THE MOD. In vanilla the star
    /// count is the only dial: it decides how many come, how hard they try, whether there is a
    /// helicopter, whether they shoot on sight, and how long it lasts. So every crime feels
    /// like the same crime at a different volume, and a man who ran three red lights ends up in
    /// the same firefight as a man who shot somebody.
    ///
    /// Here the two are separated. Severity decides WHAT IS SENT -- a road unit, a second road
    /// unit, a helicopter, SWAT -- and it is capped per offence no matter how long the chase
    /// runs. Running from a traffic stop for twenty minutes is a long chase with two cars in
    /// it. It never becomes a manhunt, because nothing you have done is a manhunt. Getting a
    /// helicopter for reckless driving is the exact vanilla behaviour this replaces.
    /// </summary>
    internal sealed class Weight
    {
        /// <summary>The most stars this on its own will ever produce.</summary>
        public readonly int Ceiling;

        /// <summary>How much heat it adds. Heat decays; stars follow heat.</summary>
        public readonly float Heat;

        /// <summary>Whether officers draw. Below this they arrest rather than shoot.</summary>
        public readonly bool GunsOut;

        /// <summary>Whether this is ever worth putting a helicopter up for.</summary>
        public readonly bool Air;

        /// <summary>Whether this is ever worth SWAT.</summary>
        public readonly bool Swat;

        /// <summary>What the radio calls it, in the ticker and in the log.</summary>
        public readonly string Called;

        public Weight(string called, int ceiling, float heat, bool gunsOut, bool air, bool swat)
        {
            Called = called;
            Ceiling = ceiling;
            Heat = heat;
            GunsOut = gunsOut;
            Air = air;
            Swat = swat;
        }
    }

    internal static class Crime
    {
        private static readonly Dictionary<Offence, Weight> Table = new Dictionary<Offence, Weight>
        {
            // A word, and that is all it is ever worth. One star, no guns, and it goes away.
            { Offence.Loitering,
              new Weight("loitering", 1, 0.6f, false, false, false) },

            // The one thing that always stops a car, and still not a shooting.
            { Offence.Brandishing,
              new Weight("a man with a gun", 2, 1.6f, false, false, false) },

            // Two stars, forever. You can outrun this one all day and it stays two stars,
            // which is what a traffic offence is.
            { Offence.Reckless,
              new Weight("reckless driving", 2, 1.2f, false, false, false) },

            { Offence.StolenVehicle,
              new Weight("a stolen vehicle", 2, 1.4f, false, false, false) },

            // Reported by the other mod, not detected here. Worth a car and a search, and
            // worth more if you run -- but never worth a helicopter, because nobody puts one
            // up over a street corner.
            { Offence.Dealing,
              new Weight("narcotics", 2, 1.5f, false, false, false) },

            { Offence.Assault,
              new Weight("an assault", 3, 2.2f, false, false, false) },

            // The line. Everything above here is answered with weapons drawn.
            { Offence.ShotsFired,
              new Weight("shots fired", 4, 3.4f, true, true, false) },

            { Offence.Homicide,
              new Weight("a homicide", 4, 4.2f, true, true, true) },

            // Its own category, and the only thing in the mod that goes straight to the top.
            // Nothing about the response to this is proportionate, and that is correct.
            { Offence.OfficerDown,
              new Weight("an officer down", 5, 6.0f, true, true, true) },
        };

        public static Weight Of(Offence what)
        {
            Weight w;
            return Table.TryGetValue(what, out w) ? w : Table[Offence.Loitering];
        }

        /// <summary>
        /// Maps an offence name coming over the bridge onto one of ours.
        ///
        /// Case-insensitive and forgiving, because the other end of the bridge is a different
        /// mod written by somebody who does not have this enum in front of them. An unknown
        /// name is Loitering rather than an exception -- a mod that crashes because another mod
        /// sent it a word it did not recognise is a mod nobody integrates with twice.
        /// </summary>
        public static bool Parse(string name, out Offence what)
        {
            what = Offence.Loitering;

            if (string.IsNullOrEmpty(name)) return false;

            foreach (Offence value in Enum.GetValues(typeof(Offence)))
            {
                if (string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    what = value;
                    return true;
                }
            }

            // A few words the other mod is likelier to reach for than ours.
            switch (name.Trim().ToLowerInvariant())
            {
                case "drugs":
                case "drug":
                case "selling":
                case "possession":
                    what = Offence.Dealing;
                    return true;

                case "gun":
                case "weapon":
                case "armed":
                    what = Offence.Brandishing;
                    return true;

                case "murder":
                case "kill":
                case "killed":
                    what = Offence.Homicide;
                    return true;

                case "copkilled":
                case "cop_killed":
                    what = Offence.OfficerDown;
                    return true;

                case "fight":
                case "punch":
                    what = Offence.Assault;
                    return true;

                case "shooting":
                case "shots":
                    what = Offence.ShotsFired;
                    return true;
            }

            return false;
        }
    }
}
