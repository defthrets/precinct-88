using System;
using System.Globalization;
using GTA;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Contact
{
    /// <summary>
    /// What you owe, and the fact that nobody collects it at the roadside.
    ///
    /// THE OLD BEHAVIOUR WAS WRONG TWICE. A ticket took the money out of your pocket the instant
    /// it was written, which is not what a ticket is -- an officer at the window does not run a
    /// card machine. And Licence.Owed summed every fine ever issued, including all the ones
    /// already paid, so the panel confidently reported a debt that did not exist.
    ///
    /// Both come from the same mistake: treating the FINE and the CHARGE as one thing. They are
    /// not. A charge is points on a licence and it expires. A debt is money and it does not --
    /// it sits there, and it gets worse.
    ///
    /// So the two are separate now. Points live on the licence and decay. Money lives here and
    /// accrues interest until somebody pays it at a station desk.
    ///
    /// INTEREST PER GAME DAY, NOT PER REAL MINUTE, which is the only choice that reads as
    /// interest rather than as a leak. A GTA day is forty-eight real minutes, so three percent a
    /// day is a slow, ignorable pressure over one session and a genuine problem over ten -- which
    /// is exactly the shape a fine you keep not paying should have.
    /// </summary>
    internal sealed class Tickets
    {
        /// <summary>Per game day, as a percentage. Pull Me Over uses three and it is a good number.</summary>
        public float InterestPercent = 3f;

        private int _owed;

        /// <summary>The game day the last interest was added on. -1 before anything is owed.</summary>
        private int _lastDay = -1;

        private bool _dirty;

        public bool Dirty => _dirty;

        public void Clean() => _dirty = false;

        /// <summary>What is outstanding right now.</summary>
        public int Owed => _owed;

        public bool Owing => _owed > 0;

        /// <summary>Written up. Nobody has taken anything yet.</summary>
        public void Add(int fine)
        {
            if (fine <= 0) return;

            _owed += fine;
            _dirty = true;

            // Interest starts from the day it was issued rather than from whenever the clock
            // last happened to be read -- otherwise a ticket written just before midnight
            // accrues a day of interest for the four minutes it has existed.
            if (_lastDay < 0) _lastDay = Today();

            Log.Info("Ticket issued: " + Money(fine) + ". Outstanding " + Money(_owed) + ".");
        }

        /// <summary>
        /// Pays what the player can afford, and says how much went.
        ///
        /// PARTIAL PAYMENT IS ALLOWED on purpose. A debt you cannot clear in one go is a debt
        /// that would otherwise be uninteractable -- you stand at the desk being told no, with
        /// nothing to do about it. Paying some of it is progress, and progress is what stops a
        /// growing number being merely a nuisance.
        /// </summary>
        public int Pay()
        {
            if (_owed <= 0) return 0;

            try
            {
                var have = Game.Player.Money;
                if (have <= 0) return 0;

                var paying = Math.Min(have, _owed);

                Game.Player.Money = have - paying;

                _owed -= paying;
                _dirty = true;

                if (_owed <= 0) _lastDay = -1;

                Log.Info("Paid " + Money(paying) + ". Outstanding " + Money(_owed) + ".");

                return paying;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not take a payment: " + ex.Message);
                return 0;
            }
        }

        /// <summary>Wiped. For an arrest, where the fine is levied separately.</summary>
        public void Wipe(string why)
        {
            if (_owed <= 0) return;

            Log.Info("Tickets cleared (" + why + "): " + Money(_owed) + " written off.");

            _owed = 0;
            _lastDay = -1;
            _dirty = true;
        }

        // ---- per-tick ----------------------------------------------------------

        private int _lastTick;

        public void Update()
        {
            var now = Game.GameTime;
            if (now - _lastTick < 4000) return;
            _lastTick = now;

            if (_owed <= 0) return;

            try
            {
                var today = Today();
                if (today == _lastDay) return;

                // A day rolled over. Only ever ONE day's worth, however many rolled -- a save
                // loaded a month later should not compound thirty times in one frame, and
                // nobody would ever connect that number to anything.
                _lastDay = today;

                var added = (int)Math.Round(_owed * (InterestPercent / 100f));
                if (added < 1) added = 1;

                _owed += added;
                _dirty = true;

                Log.Info("Interest: " + Money(added) + " added. Outstanding " + Money(_owed) + ".");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not add interest: " + ex.Message);
            }
        }

        /// <summary>
        /// A number that changes once per game day.
        ///
        /// Day-of-month alone rolls from 31 back to 1, so a month boundary reads as a change of
        /// thirty days or of none depending on which way you compare it. Folded with the month
        /// and year it is simply a number that goes up.
        /// </summary>
        private static int Today()
        {
            try
            {
                var d = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
                var m = Function.Call<int>(Hash.GET_CLOCK_MONTH);
                var y = Function.Call<int>(Hash.GET_CLOCK_YEAR);

                return y * 400 + m * 32 + d;
            }
            catch
            {
                return 0;
            }
        }

        // ---- the record --------------------------------------------------------

        public void ToJson(Json doc)
        {
            var mine = Json.Object();
            mine.Set("owed", _owed);
            mine.Set("day", _lastDay);

            doc.Set("tickets", mine);
        }

        public void FromJson(Json doc)
        {
            try
            {
                if (doc == null || !doc.Has("tickets")) return;

                var mine = doc["tickets"];

                _owed = Math.Max(0, mine["owed"].AsInt(0));
                _lastDay = mine["day"].AsInt(-1);

                _dirty = false;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read the ticket ledger: " + ex.Message);
            }
        }

        public static string Money(int amount)
        {
            return "$" + amount.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
