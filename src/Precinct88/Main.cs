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
    /// WHAT RUNS RIGHT NOW: cars and officers on patrol, their blips and lights, the
    /// suppression that stops the game spawning police over the top of ours, officers noticing
    /// crime and attending it, and traffic stops. No wanted rework, no arrests, no searches --
    /// running from a stop hands a wanted level to the engine and lets it do the pursuit.
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
        private readonly Notice _notice;
        private readonly Callout _callout;
        private readonly Violations _violations;
        private readonly TrafficStop _traffic;
        private readonly Restraint _restraint;
        private readonly Search _search;
        private readonly Status _status;

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
                _notice = new Notice(_cfg, _fleet, _foot);
                _callout = new Callout(_cfg, _fleet);
                _violations = new Violations(_cfg);
                _traffic = new TrafficStop(_cfg, _fleet, _violations);
                _restraint = new Restraint(_cfg);
                _search = new Search(_cfg);
                _status = new Status(_cfg, _callout, _traffic, _search, _restraint);

                Wire();

                Interval = 0;
                Tick += OnTick;
                Aborted += OnAborted;

                Log.Info(Build.Name + " " + Build.Version + " by " + Build.By + " loaded. " +
                         "Patrol build: cars and officers only. " +
                         "Patrol " + OnOff(_cfg.PatrolEnabled) +
                         ", foot " + OnOff(_cfg.FootPatrols) +
                         ", response " + OnOff(_cfg.RespondToCrime) +
                         ", stops " + OnOff(_cfg.ContactEnabled && _cfg.TrafficStops) +
                         ", stun " + OnOff(_cfg.LethalEscalation) +
                         ", search " + OnOff(_cfg.CustodyEnabled && _cfg.SearchAtOneStar) +
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
            // Patrol stops producing units while something else is running -- a car easing
            // round the corner into the middle of a stop is two scenes in one street.
            _fleet.Busy = () => _standDown || _traffic.Running;
            _foot.Busy = () => _standDown;

            // What an officer saw goes to whoever decides who attends. Notice knows nothing
            // about units and Callout knows nothing about eyesight, which is the whole reason
            // they are two files.
            _notice.Report = _callout.Report;

            // The call talks; it does not draw.
            _callout.Say = Screen.Ticker;
            _traffic.Say = Screen.Ticker;
            _search.Say = Screen.Ticker;

            // The searches hand off to whatever is on the bridge -- or to nothing, which is
            // the ordinary case and means guns only.
            _search.Seize = why => Seize(why);

            // Nobody points anything at a man who is already in handcuffs. See
            // Restraint.HoldFire for why this cannot simply be done by ignoring the player.
            _restraint.HoldFire = () => _search.Running;

            // THE BRIDGE, SET LAST, so nothing on the other side can see a half-built mod.
            // Dispatch.Ready() is false until these lines run.
            Dispatch.Calls = _callout;
            Dispatch.Force = _fleet;
            Dispatch.InCustody = () => _search.Running || _traffic.Running;
        }

        /// <summary>
        /// Hands a search to whoever registered on the bridge.
        ///
        /// Null is the ordinary case -- Hoodrich is not installed, or is installed and has not
        /// registered yet. The search still works; it is just weapons.
        /// </summary>
        private static string Seize(string why)
        {
            var handler = Dispatch.Seizer;
            if (handler == null) return string.Empty;

            try
            {
                return handler(why) ?? string.Empty;
            }
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

        private void OnTick(object sender, EventArgs e)
        {
            if (_parked || _cfg == null || _standDown) return;

            Hello();

            if (!_cfg.Enabled) return;

            try
            {
                // 1. WHAT THE POLICE MAY DO TO YOU AT ALL, before anything decides to do
                //    it. Stun guns below three stars, sidearms at and above.
                _restraint.Update();

                // 2. ONE STAR IS A SEARCH, NOT A CELL. Early, and it runs EVERY FRAME rather
                //    than on a gate -- the arrest block inside it is a this-frame flag and the
                //    frames it misses are the frames the engine uses to bust you.
                _search.Update();

                // 3. What an officer just saw, before anything acts on it. It reads the world
                //    and the officers already in it, and changes neither.
                _notice.Update();

                // 4. And who goes. BEFORE the fleet, because this sets the surge and where a
                //    surged car heads, and Fleet reads both on its own tick -- setting them
                //    afterwards means every response is one tick stale, which at 750ms is
                //    survivable and at a bad frame rate is not.
                _callout.Update();

                // 5. HOW YOU ARE DRIVING, continuously, whether or not anybody is looking.
                //    The detector runs on its own so a violation is noticed at the moment it
                //    happens rather than at the moment an officer thinks to check.
                _violations.Update();

                // 6. The stop currently running, or whether one starts. BEFORE the fleet,
                //    because it borrows a unit out of the pool and Fleet must not re-steer a
                //    car on the same tick it was handed over.
                _traffic.Update();

                // 7. THE CARS. Everything else in this build is a consequence of where they
                //    are, so they move next and the rest of the tick reads the result.
                _fleet.Update();

                // 8. Officers who are not in a car. Independent of the fleet -- they are
                //    placed by district, not spawned out of it -- but after it because a
                //    walker and a car in the same street should be the car's street.
                _foot.Update();

                // 9. After both, so a unit that has just gone out is marked on the same tick
                //    it exists rather than a second later.
                _markers.Update();

                // 10. The vanilla generator, held off every tick because the game keeps
                //    switching it back on. See AmbientCops -- this is a lapse that looks
                //    exactly like a mod that never worked.
                if (_cfg.PatrolEnabled && _cfg.SuppressVanillaPatrols)
                {
                    AmbientCops.Hold(_cfg.OwnDispatch);
                }

                // 11. WHAT IS BEING SAID TO YOU. Every frame, because it reads a key press
                //     and a gate would eat one. Updated here and DRAWN in the finally below --
                //     several paths above return early and a line drawn before them is a line
                //     that flickers off whenever a scene takes a shortcut.
                Dialogue.Update();

                // 12. EVERY FRAME, and that is the point of it being here rather than on a
                //    tick of its own. The fleet ticks at 750ms and a light drawn at that rate
                //    is a strobe.
                if (_beam != null) _beam.Draw();
            }
            catch (Exception ex)
            {
                Log.Error("Tick failed.", ex);
            }
            finally
            {
                // BOTH IN THE FINALLY, AND IN THIS ORDER. Several paths above return early, so
                // anything drawn inside the try flickers off whenever a scene takes a shortcut.
                // The strip is HUD and goes down first; somebody stood in front of you talking
                // belongs on top of it, and both should survive a frame where something threw.
                if (_status != null) _status.Draw();

                Dialogue.Draw();

                // BUSTED, and it is drawn HERE rather than in Custody for the same reason
                // everything else in this finally is: it has to happen every frame and on top
                // of whatever else went up. The engine has its own and puts it there when its
                // arrest fires -- but that is handed over at the end of the state below, so
                // without this there is a stretch of a man stood in handcuffs with the game
                // saying nothing at all.
                if (_search != null && _search.Booked) Screen.Busted();
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

            Preflight();
        }

        /// <summary>
        /// Exercises the only strings in this mod the compiler cannot check.
        ///
        /// ANIMATION DICTIONARY NAMES ARE GUESSES. They are not listed anywhere official, they
        /// differ between the two editions in places, and Anim is DESIGNED to fail quietly on a
        /// wrong one -- which is right at runtime and useless when somebody is trying to find
        /// out whether a feature works at all. An officer who stands still instead of miming a
        /// search looks exactly like a search that is not running.
        ///
        /// So they are loaded once, here, purely so the log answers the question without
        /// anybody having to get themselves arrested to find out.
        ///
        /// On the first TICK rather than in the constructor: SHVDN builds scripts before the
        /// game is up, and asking for an animation dictionary then answers no for reasons that
        /// have nothing to do with the name being wrong.
        /// </summary>
        private static void Preflight()
        {
            try
            {
                var hands = Anim.Ready(Anim.HandsUpDict);
                var inspect = Anim.Ready(Anim.InspectDict);

                var radio = Anim.Ready("random@arrests");
                var cuffs = Anim.Ready(Anim.ArrestDict);

                Log.Info("Animations: hands-up " + (hands ? "ok" : "MISSING") +
                         ", search " + (inspect ? "ok" : "MISSING") +
                         ", radio " + (radio ? "ok" : "MISSING") +
                         ", arrest " + (cuffs ? "ok" : "MISSING") + ".");

                // The one PROP this mod spawns, and a guessed name like the clips above. If it
                // is missing the arrest still happens with nothing on his wrists, which looks
                // exactly like the bug it was written to fix -- so the log says which it is.
                var irons = new Model("p_cs_cuffs_02");

                Log.Info("Handcuff prop: " + (irons.IsValid ? "ok" : "MISSING") + ".");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not check the animations: " + ex.Message);
            }
        }

        // ---- teardown ----------------------------------------------------------

        private void OnAborted(object sender, EventArgs e)
        {
            try
            {
                // Scenes first, and the stop before the call. A stop holds a unit out of the
                // pool entirely and an officer stood in the road with his events blocked; both
                // have to be given back before anything else is torn down.
                if (_traffic != null) _traffic.End("the mod is unloading", false);
                if (_search != null) _search.Stop("the mod is unloading");

                // Any question still up is ANSWERED rather than dropped -- a scene waiting on a
                // callback that never arrives is a scene that never ends.
                Dialogue.Clear();

                // AND EVERYBODY ARMED AGAIN. A city of officers who can only taser people, left
                // behind by a script that is no longer running, is a change to the game with no
                // findable source -- and unlike most of what this mod does it would last the
                // whole session.
                if (_restraint != null) _restraint.Release();

                // Units off the call and back on patrol, so nothing is left driving to
                // somewhere with its lights on while the rest of this runs.
                if (_callout != null) _callout.Clear("the mod is unloading");

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

                // The bridge last. Anything still holding a reference gets nulls rather than
                // a half-torn-down mod, and Ready() goes false the moment these do.
                Dispatch.Calls = null;
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
