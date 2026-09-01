using System;

namespace Precinct88.Streets
{
    /// <summary>
    /// How an officer is disposed today.
    ///
    /// THIS DESCRIBES A PERSON, NOT A TICKET, and that is why it lives here rather than with
    /// the ticketing code that reads it. It is rolled once when a unit is put out and kept for
    /// the whole of that officer's round -- two officers who look at everybody, or two who
    /// look at nobody -- and it goes on to colour a caution, a search, and how long somebody
    /// is left standing at the side of the road. Ticketing is simply the first thing that
    /// happened to ask.
    ///
    /// Keeping it in Contact/Ticketing.cs meant the patrol code could not exist without the
    /// traffic-stop code, for the sake of a twelve-line enum and a dice roll. That was the
    /// only real coupling between the two, and this file is the whole of the fix.
    /// </summary>
    internal enum Temper
    {
        /// <summary>Goes by the book, more or less.</summary>
        Normal,

        /// <summary>Writes it up. Higher fine, and unlikely to let you off.</summary>
        Strict,

        /// <summary>Would rather not do the paperwork.</summary>
        Lenient,
    }

    /// <summary>Rolls one, once, when an officer comes on duty.</summary>
    internal static class Temperament
    {
        /// <summary>
        /// A quarter strict, a quarter lenient, half somewhere in the middle.
        ///
        /// Deliberately not a bell curve over a wider range. The player only ever sees one
        /// officer at a time and cannot average anything, so a spread finer than "hard, soft,
        /// ordinary" is detail nobody can perceive.
        /// </summary>
        public static Temper Roll(Random rng)
        {
            var roll = rng.Next(100);

            if (roll < 25) return Temper.Strict;
            if (roll < 50) return Temper.Lenient;

            return Temper.Normal;
        }
    }
}
