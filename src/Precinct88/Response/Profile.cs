using System;
using GTA;
using Precinct88.Core;

namespace Precinct88.Response
{
    /// <summary>
    /// How violently you go about things, remembered across incidents.
    ///
    /// EVERY OTHER NUMBER IN THIS MOD IS ABOUT ONE INCIDENT. Heat is per-chase. The description
    /// is per-chase. The star ceiling is per-offence. Nothing anywhere carries from one evening
    /// to the next, which means a player who has killed forty people this session is treated
    /// exactly like one who has never fired a shot, the moment the meter runs down.
    ///
    /// This is the number that persists. It is not a morality score and it does not judge WHAT
    /// you do -- it is entirely about HOW. Rob a shop and drive off and it barely moves. Rob a
    /// shop, shoot the clerk, and shoot the first officer through the door, and it moves a long
    /// way. What it changes is the temperature of the response: a known quantity gets a harder
    /// welcome than a stranger.
    ///
    /// Deliberately SLOW UP AND SLOW DOWN. A profile that spikes on one bad night is a
    /// difficulty setting the player did not choose, and one that never decays is a save file
    /// they have to abandon. It bleeds off in real time whether or not anything is happening,
    /// so leaving it alone is always the way back.
    /// </summary>
    internal sealed class Profile
    {
        /// <summary>Above this, they come at you expecting it. Guns out a tier earlier.</summary>
        public const float HardenedAt = 0.55f;

        /// <summary>Above this you are a standing emergency and nothing is proportionate.</summary>
        public const float NotoriousAt = 0.8f;

        /// <summary>How much a full real minute of not escalating gives back.</summary>
        private const float CoolPerMinute = 0.022f;

        private const int TickMs = 5000;

        private float _violence;
        private int _lastTick;
        private bool _dirty;

        /// <summary>0 to 1. The whole of the state.</summary>
        public float Violence
        {
            get { return _violence; }
            private set
            {
                var v = value < 0f ? 0f : value > 1f ? 1f : value;

                if (Math.Abs(v - _violence) < 0.0005f) return;

                _violence = v;
                _dirty = true;
            }
        }

        /// <summary>Whether they expect a fight from you before it starts.</summary>
        public bool Hardened => _violence >= HardenedAt;

        public bool Notorious => _violence >= NotoriousAt;

        /// <summary>
        /// What the profile does to heat.
        ///
        /// A gentle curve on purpose: 1.0 at nothing, about 1.5 at the top. Big enough that a
        /// violent player reaches the ceiling of an offence faster and holds it longer; small
        /// enough that it never turns a two-star offence into something it is not, because the
        /// per-crime ceilings still cap it and they are the honest part of the system.
        /// </summary>
        public float Multiplier => 1f + _violence * 0.5f;

        /// <summary>A word for it, for the panel and the log.</summary>
        public string Word =>
            _violence >= NotoriousAt ? "notorious"
            : _violence >= HardenedAt ? "violent"
            : _violence >= 0.25f ? "known"
            : "unremarkable";

        // ---- what moves it -----------------------------------------------------

        /// <summary>
        /// Something was reported. How much it moves the needle depends on what it was.
        ///
        /// NOT EVERY CRIME COUNTS, and the ones that do not are most of them. Driving badly,
        /// holding, dealing, being stopped -- none of that says anything about how you handle
        /// yourself, so none of it registers here. Only the things where somebody got hurt when
        /// they did not have to.
        /// </summary>
        public void Saw(Offence what)
        {
            switch (what)
            {
                case Offence.OfficerDown:
                    Violence += 0.22f;
                    break;

                case Offence.Homicide:
                    Violence += 0.10f;
                    break;

                case Offence.ShotsFired:
                    Violence += 0.035f;
                    break;

                case Offence.Assault:
                    Violence += 0.012f;
                    break;

                // Everything else says nothing about how you go about it.
            }
        }

        /// <summary>
        /// An incident ended and nobody got hurt in it.
        ///
        /// Nelson's line about GTA VI is the design here almost word for word: commit a crime,
        /// move on before the police arrive, do not start shooting, and you are generally going
        /// to be fine. This is the "generally going to be fine" -- a clean getaway is worth
        /// slightly more than the same time spent standing still.
        /// </summary>
        public void CleanGetaway()
        {
            Violence -= 0.03f;
        }

        // ---- per-tick ----------------------------------------------------------

        public void Update()
        {
            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;

            var elapsed = (now - _lastTick) / 60000f;
            _lastTick = now;

            if (elapsed <= 0f || elapsed > 1f) return;   // a load, a cutscene, a big time jump

            if (_violence > 0f) Violence -= CoolPerMinute * elapsed;
        }

        // ---- the record --------------------------------------------------------

        /// <summary>
        /// Whether this half of the record has moved since it was last written.
        ///
        /// THE FILE IS NOT WRITTEN HERE ANY MORE. This used to own record.json outright and
        /// write the whole document whenever violence changed -- which was fine while it was
        /// the only thing in there, and became a bug the moment the licence needed to persist
        /// too. Two systems each writing a complete file are two systems each deleting the
        /// other's half. Core.Record owns it now; this only says what to put in and how to read
        /// it back.
        /// </summary>
        public bool Dirty => _dirty;

        public void Clean() => _dirty = false;

        public void ToJson(Json doc)
        {
            doc.Set("violence", Math.Round(_violence, 4));
        }

        public void FromJson(Json doc)
        {
            try
            {
                if (doc == null) return;

                _violence = doc["violence"].AsFloat(0f);

                if (_violence < 0f) _violence = 0f;
                if (_violence > 1f) _violence = 1f;

                _dirty = false;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read the profile: " + ex.Message);
            }
        }
    }
}
