using System;
using System.IO;
using GTA;
using Precinct88.Core;
using Precinct88.Response;
using Precinct88.Streets;
using Precinct88.UI;

namespace Precinct88
{
    /// <summary>
    /// The one Script subclass, owning tick order for everything.
    ///
    /// THIS IS THE PATROL BUILD, AND THE SMALLNESS IS THE POINT. An earlier version of this
    /// file constructed eighteen systems and wired thirty couplings between them, all added
    /// over a handful of sittings with no play session in between. It compiled the whole way
    /// and every piece of it was defensible on its own -- but when something felt wrong in the
    /// game there was no way to say which of the eighteen had caused it, and that is not a
    /// state a mod can be debugged out of. It gets rebuilt one system at a time instead, and
    /// nothing joins the build until the thing already in it has been played and believed.
    ///
    /// Everything else is under parked\ -- whole, compiling as of the commit that moved it,
    /// and out of the build only because the build script globs src\Precinct88 and nothing
    /// else. Bringing a file back is a move and a wire, not a rewrite.
    ///
    /// WHAT RUNS RIGHT NOW: cars and officers on patrol, their blips, their lights, and the
    /// suppression that stops the game spawning its own police over the top of ours. No
    /// wanted rework, no traffic stops, no arrests. The one question this build has to answer
    /// is whether police move around the map in a way that looks right.
    ///
    /// ONE SCRIPT, NOT SEVEN, and that outlives the rebuild. SHVDN will happily run a Script
    /// subclass per system, but they are not independent about ORDER -- and with separate
    /// scripts that order is whatever SHVDN happened to construct them in, which is stable
    /// until somebody renames a file. So the order below is the design, written down.
    /// </summary>
    public sealed class Main : Script
    {
        private readonly Settings _cfg;

        private readonly Fleet _fleet;
        private readonly Foot _foot;
        private readonly Markers _markers;
        private readonly Spotlight _beam;

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
                _foot = new Foot(_cfg);
                _markers = new Markers(_cfg, _fleet, _foot);
                _beam = new Spotlight(_cfg, _fleet);

                Wire();

                Interval = 0;
                Tick += OnTick;
                Aborted += OnAborted;

                Log.Info(Build.Name + " " + Build.Version + " by " + Build.By + " loaded. " +
                         "Patrol build: cars and officers only. " +
                         "Patrol " + OnOff(_cfg.PatrolEnabled) +
                         ", foot " + OnOff(_cfg.FootPatrols) +
                         ", blips " + OnOff(_cfg.PoliceBlips) +
                         ", suppression " + OnOff(_cfg.SuppressVanillaPatrols) + ".");
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
        /// Kept in one method rather than scattered through the constructors, because these
        /// are the actual couplings in the mod and they are worth being able to read in one
        /// go. In the patrol build there is exactly one, which is the healthiest this file has
        /// ever been and is worth noticing before the next system joins.
        /// </summary>
        private void Wire()
        {
            // Patrol stops producing units while something else is running. Nothing else is
            // running yet, so this is only the LSPDFR stand-down -- but the hook is the seam
            // every system that gets ported back plugs into, so it stays.
            _fleet.Busy = () => _standDown;
            _foot.Busy = () => _standDown;
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

        private void OnTick(object sender, EventArgs e)
        {
            if (_parked || _cfg == null || _standDown) return;

            Hello();

            if (!_cfg.Enabled) return;

            try
            {
                // 1. THE CARS. Everything else in this build is a consequence of where they
                //    are, so they move first and the rest of the tick reads the result.
                _fleet.Update();

                // 2. Officers who are not in a car. Independent of the fleet -- they are
                //    placed by district, not spawned out of it -- but second because a
                //    walker and a car in the same street should be the car's street.
                _foot.Update();

                // 3. After both, so a unit that has just gone out is marked on the same tick
                //    it exists rather than a second later.
                _markers.Update();

                // 4. The vanilla generator, held off every tick because the game keeps
                //    switching it back on. See AmbientCops -- this is a lapse that looks
                //    exactly like a mod that never worked.
                if (_cfg.PatrolEnabled && _cfg.SuppressVanillaPatrols)
                {
                    AmbientCops.Hold(_cfg.OwnDispatch);
                }

                // 5. EVERY FRAME, and that is the point of it being here rather than on a
                //    tick of its own. The fleet ticks at 750ms and a light drawn at that rate
                //    is a strobe.
                if (_beam != null) _beam.Draw();
            }
            catch (Exception ex)
            {
                Log.Error("Tick failed.", ex);
            }
        }

        private bool _greeted;

        /// <summary>
        /// Says it is here, once, on the first tick that can draw.
        ///
        /// THIS MOD IS SILENT BY DESIGN AND THAT MADE IT INDISTINGUISHABLE FROM BROKEN. Every
        /// other mod in a scripts\ folder announces itself somehow. Precinct 88 changes how
        /// police behave in the background, so a correct load and a failed load look exactly
        /// the same from the pavement, and the first install of it was reported as "didn't
        /// load" while the log said it had started fine and read the ini.
        ///
        /// On the first TICK rather than in the constructor. SHVDN builds scripts before the
        /// game world is up, and a ticker posted then goes into a HUD that does not exist yet
        /// -- which is the same silence, arrived at more expensively.
        /// </summary>
        private void Hello()
        {
            if (_greeted) return;
            _greeted = true;

            try
            {
                Screen.Ticker(Build.Name + " " + Build.Version + " -- patrol build. " +
                              "Police on the streets only; nothing else is running.");
            }
            catch
            {
                // The log already said it loaded. This is a courtesy, not a requirement.
            }
        }

        // ---- teardown ----------------------------------------------------------

        private void OnAborted(object sender, EventArgs e)
        {
            try
            {
                // The generator back on before the cars go, so the streets are not empty of
                // police for the gap between this and whatever loads next.
                AmbientCops.Release();

                if (_fleet != null) _fleet.Release();
                if (_foot != null) _foot.Release();

                // Before the peds and cars go, or the blips outlive what they were attached to.
                if (_markers != null) _markers.Clear();

                // Last, and unconditional. Whatever went wrong above, the player gets the
                // police back. Nothing in this build takes a hold yet, which is exactly why it
                // stays -- the day something does, this is already correct.
                LawHold.ReleaseAll();

                Log.Info("Unloaded. Everything handed back.");
            }
            catch
            {
                // Teardown. Nothing left to tell.
            }
        }
    }
}
