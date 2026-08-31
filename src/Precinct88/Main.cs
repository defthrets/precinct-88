using System;
using System.IO;
using GTA;
using Precinct88.Api;
using Precinct88.Contact;
using Precinct88.Core;
using Precinct88.Custody;
using Precinct88.Response;
using Precinct88.Streets;
using Precinct88.UI;

namespace Precinct88
{
    /// <summary>
    /// The one Script subclass, owning tick order for everything.
    ///
    /// ONE SCRIPT, NOT SEVEN. SHVDN will happily run a Script subclass per system and it is
    /// tempting, because each of these is independent -- but they are not independent about
    /// ORDER, and that is the whole difficulty. Custody has to be asked before Contact, because
    /// a man being walked to a car must not also be getting pulled over. Contact has to be asked
    /// before the Fleet, because a unit in the middle of a stop must not be re-routed onto its
    /// beat. With separate scripts that order is whatever SHVDN happened to construct them in,
    /// which is stable until somebody renames a file.
    ///
    /// So the order below is the design, and each step says what it is standing in front of.
    /// </summary>
    public sealed class Main : Script
    {
        private readonly Settings _cfg;

        private readonly Fleet _fleet;
        private readonly Manhunt _hunt;
        private readonly Witness _witness;
        private readonly Stop _stop;
        private readonly Watch _watch;
        private readonly Surrender _surrender;
        private readonly Booking _booking;
        private readonly SettingsScreen _screen;
        private readonly KnownStrip _strip;

        private bool _parked;
        private bool _standDown;

        public Main()
        {
            try
            {
                // Fully qualified. Script exposes an inherited `Settings` property that
                // otherwise wins name resolution over our own type, and it presents as "no
                // argument given for 'filename'" -- a message about a class nobody wrote.
                _cfg = Core.Settings.Load();
                Log.Level = _cfg.Logging;

                _standDown = _cfg.StandDownForLspdfr && LspdfrHere();

                if (_standDown)
                {
                    Log.Warn("LSPDFR is installed here. It owns dispatch and the wanted system, " +
                             "and two systems giving officers contradictory orders every frame " +
                             "is very hard to diagnose from inside the game. Precinct 88 is " +
                             "standing down for this session. Set StandDownForLspdfr=false in " +
                             "Precinct88.ini to run both anyway.");
                }

                _fleet = new Fleet(_cfg);
                _hunt = new Manhunt(_cfg, _fleet);
                _witness = new Witness(_cfg, _hunt);
                _stop = new Stop(_cfg, _hunt);
                _watch = new Watch(_cfg, _fleet, _stop);
                _surrender = new Surrender(_cfg, _hunt);
                _booking = new Booking(_cfg, _hunt, _witness);

                // Given the bridge as a function rather than a value, because whether Hoodrich
                // is on the other end is not knowable yet -- its own script may not be built,
                // and it registers a handler whenever it gets round to it.
                _screen = new SettingsScreen(_cfg, _fleet, _hunt, () => Dispatch.Seizer != null);
                _strip = new KnownStrip(_cfg, _hunt);

                // The one thing in this mod that outlives a session.
                if (_cfg.CriminalProfile) _hunt.Record.Load();

                Wire();

                Interval = 0;
                Tick += OnTick;
                KeyDown += OnKey;
                Aborted += OnAborted;

                Log.Info(Build.Name + " " + Build.Version + " by " + Build.By + " loaded. " +
                         "Patrol " + OnOff(_cfg.PatrolEnabled) +
                         ", wanted " + OnOff(_cfg.WantedEnabled) +
                         ", contact " + OnOff(_cfg.ContactEnabled) +
                         ", custody " + OnOff(_cfg.CustodyEnabled) + ". " +
                         _cfg.MenuKey + " opens the settings.");
            }
            catch (Exception ex)
            {
                _parked = true;
                Log.Error("Failed to start; disabled for this session.", ex);
            }
        }

        private static string OnOff(bool b) => b ? "on" : "off";

        /// <summary>
        /// Everything that has to know about something it does not own.
        ///
        /// Kept in one method rather than scattered through the constructors, because these are
        /// the actual couplings in the mod and they are worth being able to read in one go.
        /// </summary>
        private void Wire()
        {
            // The beat stops producing cars while a stop or a booking is running. A unit
            // easing round the corner into the middle of somebody being cuffed is two scenes
            // in one street.
            _fleet.Busy = () => _stop.Running || _booking.Running || _standDown;

            // The manhunt talks; it does not draw.
            _hunt.Say = Screen.Ticker;

            // Both places the player can be searched hand off to the same seizure handler,
            // which is whatever is on the bridge -- or nothing.
            _stop.Seize = Seize;
            _booking.Seize = Seize;

            // A search that finds something goes to custody. The officer is not passed here
            // because Stop has already released him; Booking finds one.
            _stop.Book = reason => _booking.Begin(null, reason);

            _surrender.Book = (officer, reason) => _booking.Begin(officer, reason);

            // Nothing prompts, reads a key, or starts a scene behind the panel. The panel
            // disables the controls itself, so this is about the PROMPTS -- and about the
            // surrender key, which is a raw keyboard read and therefore the one input the
            // control blackout does not stop.
            Func<bool> busy = () => _screen != null && _screen.IsOpen;

            _stop.Occupied = busy;
            _watch.Occupied = busy;
            _surrender.Occupied = busy;

            // The bridge. Set last, so nothing on the other side can see a half-built mod --
            // Dispatch.Ready() is false until this line runs.
            Dispatch.Chase = _hunt;
            Dispatch.Force = _fleet;
            Dispatch.InCustody = () => _booking.InCustody;
        }

        /// <summary>
        /// Hands a search to whoever registered on the bridge.
        ///
        /// Null is the ordinary case -- it means Hoodrich is not installed, or is installed and
        /// has not registered. The scene still works; it is just weapons and cash.
        /// </summary>
        private string Seize(string why)
        {
            var handler = Dispatch.Seizer;
            if (handler == null) return string.Empty;

            try { return handler(why) ?? string.Empty; }
            catch (Exception ex)
            {
                Log.Debug("The seizure handler threw: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Whether a police framework is already running here.
        ///
        /// By file rather than by assembly: LSPDFR loads under RAGE Plugin Hook, not under
        /// SHVDN, so there is nothing in our AppDomain to reflect over. Its plugin folder
        /// sitting beside the game is the only evidence available from in here.
        /// </summary>
        private static bool LspdfrHere()
        {
            try
            {
                var game = Directory.GetParent(Paths.Scripts);
                if (game == null) return false;

                return File.Exists(Path.Combine(game.FullName, "LSPDFR.dll")) ||
                       Directory.Exists(Path.Combine(game.FullName, "plugins\\LSPDFR"));
            }
            catch
            {
                return false;
            }
        }

        // ---- the tick ----------------------------------------------------------

        /// <summary>
        /// The settings key.
        ///
        /// Deliberately NOT gated on _cfg.Enabled. Switching the mod off from inside the panel
        /// would otherwise be a one-way door -- the key that turns it back on is the key the
        /// disabled mod has stopped listening for. It IS gated on standing down for LSPDFR,
        /// because in that case none of the settings do anything and a panel full of dead
        /// switches is worse than no panel.
        /// </summary>
        private void OnKey(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            if (_parked || _cfg == null || _standDown) return;

            try
            {
                if (e.KeyCode == _cfg.MenuKey) _screen.Toggle();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not open the settings: " + ex.Message);
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_parked || _cfg == null || _standDown) return;

            // THE PANEL RUNS EVEN WHEN THE MOD IS OFF, and before the early return below, for
            // the same reason the key is not gated: the switch that turns it back on lives in
            // here. Everything else stops.
            //
            // Update here and DRAW IN THE FINALLY, not together. Custody paints the whole
            // screen black and several paths below return early, so a panel drawn at the top of
            // the tick is a panel underneath the blackout -- open, taking input, and invisible.
            // Drawn last it is on top of everything, which is what a panel is.
            if (_screen != null) _screen.Update();

            try
            {
                // INSIDE THE TRY, so the finally still draws the panel. Outside it, switching
                // the mod off from within the panel takes the panel off the screen with it --
                // and the switch that turns it back on is in the panel.
                if (!_cfg.Enabled) return;

                // 1. CUSTODY FIRST, AND IT SHORT-CIRCUITS EVERYTHING. A player being walked to
                //    a car is not available to be pulled over, reported, or patrolled past, and
                //    every one of those would fight the scene for control of the same officer.
                _booking.Update();

                if (_booking.InCustody) return;

                // 2. Surrender, before anything that could shoot him. It caps the wanted level
                //    to the arrest level, and a frame in which that has not happened yet is a
                //    frame in which a man with his hands up gets shot.
                _surrender.Update();

                // 3. The manhunt, which owns the wanted level and the search.
                _hunt.Update();

                // 4. What the world noticed, feeding into it.
                _witness.Update();

                // 5. The stop currently running, before anything decides to start another one.
                _stop.Update();

                // 6. Whether to start one.
                _watch.Update();

                // 7. The beat. LAST, because by now everything that could have claimed a unit
                //    has claimed it, and Fleet will not touch a unit in Duty.Contact.
                _fleet.Update();

                // The vanilla generator, held off every tick because the game keeps switching
                // it back on. See AmbientCops -- this is a lapse that looks exactly like a mod
                // that never worked.
                if (_cfg.PatrolEnabled && _cfg.SuppressVanillaPatrols) AmbientCops.Hold();

                // Drawn after the systems have run, so it shows this frame's answer rather
                // than last frame's. Under the panel, which draws in the finally below.
                if (_strip != null) _strip.Draw();
            }
            catch (Exception ex)
            {
                Log.Error("Tick failed.", ex);
            }
            finally
            {
                if (_screen != null) _screen.Draw();
            }
        }

        // ---- teardown ----------------------------------------------------------

        private void OnAborted(object sender, EventArgs e)
        {
            try
            {
                // ORDER MATTERS HERE TOO, AND IT IS THE REVERSE OF THE TICK. The scenes are
                // ended first, because each of them holds or caps the wanted level and their
                // teardown is what gives it back. Releasing the law first and then abandoning
                // a booking would have the booking put a hold on during teardown that nothing
                // is left to release -- and a player who can never be arrested again is the
                // worst thing this mod could leave behind.
                if (_booking != null) _booking.Abandon();
                if (_surrender != null) _surrender.Drop();
                if (_stop != null) _stop.End("the mod is unloading", false);

                if (_hunt != null)
                {
                    _hunt.RestoreDispatch();

                    // Forced, because an unload is the one moment there is no later checkpoint
                    // to rely on.
                    if (_cfg.CriminalProfile) _hunt.Record.Save(true);
                }

                Cameras.Forget();

                // The generator back on before the cars go, so the streets are not empty of
                // police for the gap between this and whatever loads next.
                AmbientCops.Release();

                if (_fleet != null) _fleet.Release();

                // Last, and unconditional. Whatever went wrong above, the player gets the
                // police back.
                LawHold.ReleaseAll();

                Dispatch.Chase = null;
                Dispatch.Force = null;
                Dispatch.InCustody = null;

                Log.Info("Unloaded. Everything handed back.");
            }
            catch
            {
                // Teardown. Nothing left to tell.
            }
        }
    }
}
