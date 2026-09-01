using System;
using System.Collections.Generic;
using System.Globalization;
using GTA;
using Precinct88.Core;
using Precinct88.Streets;
using Precinct88.UI;

namespace Precinct88.Contact
{
    /// <summary>How the two in the car are about their job today.</summary>
    internal enum Temper
    {
        /// <summary>Goes by the book, more or less.</summary>
        Normal,

        /// <summary>Writes it up. Higher fine, and unlikely to let you off.</summary>
        Strict,

        /// <summary>Would rather not do the paperwork.</summary>
        Lenient,
    }

    /// <summary>
    /// What a traffic stop actually ends in.
    ///
    /// UNTIL NOW IT ENDED IN NOTHING. An officer walked over, said a line, searched you if you
    /// let him, and left -- and if you were carrying nothing the entire scene cost you a minute
    /// and had no consequence at all. Which made every violation in feature 2 a piece of
    /// detection with nothing on the end of it.
    ///
    /// A stop now ends in a WARNING or a TICKET, and the difference between them is the most
    /// interesting decision in the system, because it is not the player's. It belongs to two
    /// specific officers who have a temperament and can see your record.
    ///
    /// THE THREE INPUTS ARE DELIBERATE AND THEY PULL AGAINST EACH OTHER:
    ///
    ///   WHAT YOU DID. Something serious is not a warning however nice they are being.
    ///   WHO STOPPED YOU. Strict, normal or lenient, rolled once per unit and kept for its whole
    ///     round -- so the same two officers behave the same way twice, which is what makes it
    ///     read as people rather than as a dice roll.
    ///   WHAT IS ALREADY ON YOUR LICENCE. Points make a warning much less likely, which is the
    ///     mechanism that makes a second stop feel different from a first.
    ///
    /// Pull Me Over has all three and its ini gave the shape of the numbers: a base warning
    /// chance around a quarter, strict and lenient at a quarter each, fines moved by about a
    /// fifth either way. Those proportions are good and are roughly what is used here.
    /// </summary>
    internal static class Ticketing
    {
        /// <summary>Chance an ordinary officer lets an ordinary offence go.</summary>
        private const int BaseWarning = 30;

        /// <summary>Each point already on the licence costs this much of that chance.</summary>
        private const int PerPointPenalty = 6;

        private const int StrictPenalty = 20;
        private const int LenientBonus = 22;

        private const float StrictFine = 1.25f;
        private const float LenientFine = 0.85f;

        /// <summary>
        /// Above this total weight nobody is letting you off.
        ///
        /// Three is one serious violation -- a red light, the wrong way up a street, hitting
        /// somebody. A warning for that is not leniency, it is the mod not meaning it.
        /// </summary>
        private const int NeverWarnAbove = 2;

        /// <summary>Rolled per unit and kept, so one crew behaves consistently.</summary>
        public static Temper TemperFor(Random rng)
        {
            var roll = rng.Next(100);

            if (roll < 25) return Temper.Strict;
            if (roll < 50) return Temper.Lenient;

            return Temper.Normal;
        }

        /// <summary>
        /// Decides and applies the outcome, and says what happened.
        ///
        /// Returns the line to show, because this is one of the few things in the mod that
        /// genuinely needs words -- an icon cannot say how much, or how many points, or that
        /// you have two left before you lose it.
        /// </summary>
        public static string Settle(IReadOnlyList<Violation> what, Licence licence,
                                    Tickets tickets, Temper temper, Settings cfg)
        {
            if (what == null || what.Count == 0) return string.Empty;

            var worst = Violations.Worst(what);
            if (!worst.HasValue) return string.Empty;

            var heaviest = Violations.Weight(worst.Value);

            if (Warned(heaviest, licence, temper))
            {
                Log.Info("Traffic stop: warning for " + Violations.Called(worst.Value) + ".");

                return "Warning: " + Violations.Called(worst.Value) + ". Nothing on your licence.";
            }

            // A ticket covers EVERYTHING he has on you, not just the headline. Being stopped
            // for the wheelie and paying only for the wheelie, having also been on the pavement
            // and on the phone, is the sort of thing that makes a system feel unfinished.
            var points = 0;
            var fine = 0;

            foreach (var v in what)
            {
                var p = Licence.PointsFor(v);
                var f = (int)(Licence.FineFor(v) * Multiplier(temper));

                licence.Add(v, p, f);

                points += p;
                fine += f;
            }

            // BOOKED, NOT TAKEN. An officer at a car window does not run a card machine, and
            // the old behaviour -- money out of your pocket the instant the ticket was written
            // -- is not what a ticket is. It goes on the ledger and you settle it at a station.
            if (tickets != null) tickets.Add(fine);

            var line = "Ticket: " + Money(fine) + ", " + points +
                       (points == 1 ? " point" : " points") + ".";

            if (tickets != null && tickets.Owed > fine)
            {
                line += " " + Money(tickets.Owed) + " outstanding.";
            }

            var left = Licence.Suspended - licence.Points;

            if (licence.IsSuspended)
            {
                line += " Your licence is gone.";
            }
            else if (left <= 3)
            {
                line += " " + left + (left == 1 ? " point" : " points") + " left.";
            }

            Log.Info("Traffic stop: ticket, " + fine + " and " + points + " point(s). Licence now " +
                     licence.Points + ".");

            return line;
        }

        private static bool Warned(int heaviest, Licence licence, Temper temper)
        {
            // Serious is serious. Temperament does not reach this far.
            if (heaviest > NeverWarnAbove) return false;

            var chance = BaseWarning;

            chance -= licence.Points * PerPointPenalty;

            if (temper == Temper.Strict) chance -= StrictPenalty;
            if (temper == Temper.Lenient) chance += LenientBonus;

            if (chance <= 0) return false;
            if (chance > 95) chance = 95;

            return new Random().Next(100) < chance;
        }

        private static float Multiplier(Temper temper)
        {
            switch (temper)
            {
                case Temper.Strict: return StrictFine;
                case Temper.Lenient: return LenientFine;
                default: return 1f;
            }
        }

        private static string Money(int amount)
        {
            return "$" + amount.ToString("N0", CultureInfo.InvariantCulture);
        }

        /// <summary>How an officer would describe your record, for the panel.</summary>
        public static string Standing(Licence licence)
        {
            if (licence == null) return "clean";

            var p = licence.Points;

            if (p == 0) return "clean";
            if (p >= Licence.Suspended) return "SUSPENDED";

            return p + " point" + (p == 1 ? "" : "s");
        }
    }
}
