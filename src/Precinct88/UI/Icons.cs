using Precinct88.Contact;

namespace Precinct88.UI
{
    /// <summary>
    /// Which picture goes with which offence.
    ///
    /// A SEPARATE FILE FROM BOTH SIDES ON PURPOSE. Notice describes what the police saw and
    /// Violations describes how you were driving; neither should know that a HUD exists, and
    /// the HUD should not carry a switch statement about crime. This is the seam, and it is the
    /// only thing in the mod that has to change when a new icon is drawn.
    ///
    /// EVERYTHING FALLS BACK. An offence with no icon of its own gets a sensible general one
    /// rather than nothing -- a hole in the strip is indistinguishable from a bug, and a mod on
    /// the bridge can report any word it likes.
    /// </summary>
    internal static class Icons
    {
        /// <summary>What to draw when nothing better is known.</summary>
        public const string Unknown = "noid.png";

        /// <summary>
        /// The icon for a violation, which is the one case with a real enum behind it.
        /// </summary>
        public static string ForViolation(Violation what)
        {
            switch (what)
            {
                case Violation.Speeding: return "speed.png";
                case Violation.RedLight: return "light.png";
                case Violation.WrongWay: return "wrongway.png";
                case Violation.Burnout: return "burnout.png";
                case Violation.Drifting: return "drift.png";
                case Violation.HitPed: return "runover.png";

                // The rest are all "you were driving badly", and a steering wheel says that
                // better than five nearly-identical car pictures nobody can tell apart.
                default: return "wheel.png";
            }
        }

        /// <summary>
        /// The icon for a reported crime, by name.
        ///
        /// BY KEYWORD, AND THAT IS DELIBERATE RATHER THAN LAZY. Everything this mod reports
        /// itself passes its icon along explicitly and never reaches here. This exists for the
        /// bridge: another mod can report any string it likes, in its own vocabulary, with no
        /// reference to any type of ours -- so the only thing that can be done with the word is
        /// read it.
        /// </summary>
        public static string For(string offence)
        {
            if (string.IsNullOrEmpty(offence)) return Unknown;

            var s = offence.ToLowerInvariant();

            if (s.Contains("shot") || s.Contains("gunfire")) return "shots.png";
            if (s.Contains("point") || s.Contains("aim")) return "aim.png";
            if (s.Contains("taken") || s.Contains("jack") || s.Contains("steal")) return "key.png";
            if (s.Contains("at people") || s.Contains("ran over")) return "runover.png";
            if (s.Contains("fight") || s.Contains("punch") || s.Contains("brawl")) return "fist.png";
            if (s.Contains("gun") || s.Contains("weapon") || s.Contains("armed")) return "gun.png";

            if (s.Contains("drug") || s.Contains("deal")) return "search.png";
            if (s.Contains("car") || s.Contains("vehicle") || s.Contains("driv")) return "wheel.png";

            return Unknown;
        }
    }
}
