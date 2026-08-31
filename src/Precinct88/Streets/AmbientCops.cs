using System;
using GTA;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// Turning off the game's own police generator, which is the reason vanilla policing feels
    /// the way it does.
    ///
    /// The generator is not a spawner in the ordinary sense -- it is a density target. The
    /// engine looks at how much police presence the current area is supposed to have and
    /// creates cars until it is met, anywhere out of your immediate view. That is why a squad
    /// car appears behind you on an empty road at three in the morning, and it is why no amount
    /// of careful spawning on top of it produces a coherent force: our cars and its cars are
    /// both counted, so the harder this mod works the more crowded the streets get.
    ///
    /// So it is switched off and the Fleet becomes the only source of police in the world.
    ///
    /// RE-ASSERTED EVERY FEW SECONDS, NOT SET ONCE. The game resets these on a mission
    /// starting, a cutscene, a load, an area transition and a few other things nothing gets
    /// told about -- and a suppression that quietly lapses looks exactly like a mod that never
    /// worked. Hoodrich learned this the expensive way with its wanted-level hold: the natives
    /// were pushed at the start of a gang war and whatever happened to them after that stood.
    ///
    /// Nothing existing is deleted. Officers already on the street were put there by something
    /// -- possibly a mission, possibly another mod -- and a system that goes round removing
    /// police it did not create will eventually delete one that a story mission needed.
    /// </summary>
    internal static class AmbientCops
    {
        private const int ReassertMs = 4000;

        private static int _lastPush;
        private static bool _suppressing;

        /// <summary>Whether the vanilla generator is currently held off.</summary>
        public static bool Suppressed => _suppressing;

        /// <summary>
        /// Holds the generator off, and keeps holding it.
        ///
        /// Call every tick. It rate-limits itself, so the cost is a comparison in the frames
        /// between pushes.
        /// </summary>
        public static void Hold()
        {
            _suppressing = true;

            var now = Game.GameTime;
            if (now - _lastPush < ReassertMs) return;
            _lastPush = now;

            Push(false);
        }

        /// <summary>Gives it back. For the mod being switched off, and for teardown.</summary>
        public static void Release()
        {
            if (!_suppressing) return;

            _suppressing = false;
            Push(true);

            Log.Info("Ambient police generation handed back to the game.");
        }

        private static void Push(bool allow)
        {
            try
            {
                Function.Call(Hash.SET_CREATE_RANDOM_COPS, allow);
                Function.Call(Hash.SET_CREATE_RANDOM_COPS_NOT_ON_SCENARIOS, allow);

                // Scenario cops are the ones stood beside a car outside a station, or leaning
                // on a wall in Vespucci. Left ON deliberately: they are set dressing rather
                // than density, they do not drive, and removing them empties the stations this
                // mod spends its time pretending are staffed.
                Function.Call(Hash.SET_CREATE_RANDOM_COPS_ON_SCENARIOS, true);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not " + (allow ? "restore" : "suppress") +
                          " ambient police: " + ex.Message);
            }
        }
    }
}
