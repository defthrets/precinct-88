using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>
    /// The beam out of the driver's window, swung down whatever they are passing.
    ///
    /// DRAWN, NOT SWITCHED ON. A police car in GTA V has no searchlight the game will turn on
    /// for you -- SET_VEHICLE_SEARCHLIGHT is a helicopter's and does nothing on a cruiser.
    /// DRAW_SPOT_LIGHT puts a light wherever you ask for exactly the frame you ask, so the
    /// beam is entirely ours: an origin at the window, a direction we choose, and a redraw
    /// every single frame.
    ///
    /// EVERY FRAME IS THE LOAD-BEARING PART. This cannot live on the tick. The fleet ticks at
    /// 750ms, and a light that exists for one frame in forty-five is not a light, it is a
    /// strobe -- which is how this was first written in Hoodrich and how it was found out.
    ///
    /// The beam is EASED toward its target rather than set to it. A light that jumps between
    /// two frames reads as a light being teleported; one that swings reads as somebody aiming
    /// it, and that is the entire difference between a patrol car and a prop.
    /// </summary>
    internal sealed class Spotlight
    {
        /// <summary>Past this it is somebody else's street and not worth a draw call.</summary>
        private const float DrawRange = 95f;

        /// <summary>How fast the beam catches up. Small is slow and deliberate.</summary>
        private const float Ease = 0.055f;

        /// <summary>Close enough that they would actually look at you rather than the road.</summary>
        private const float LooksAtYou = 32f;

        /// <summary>How far ahead the beam falls when there is nothing else to point it at.</summary>
        private const float Ahead = 22f;

        private readonly Settings _cfg;
        private readonly Fleet _fleet;
        private readonly Random _rng = new Random();

        public Spotlight(Settings cfg, Fleet fleet)
        {
            _cfg = cfg;
            _fleet = fleet;
        }

        /// <summary>Called from the tick, every frame. Cheap when there is nothing to draw.</summary>
        public void Draw()
        {
            if (!_cfg.Spotlights) return;

            try
            {
                var night = Alleys.Night();

                // Daylight gets nothing. A spotlight at two in the afternoon is a mod showing
                // off a feature rather than a police car doing its job.
                if (night < 0.2f) return;

                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                foreach (var unit in _fleet.Units)
                {
                    if (!unit.Alive) continue;
                    if (unit.Car.Position.DistanceTo(me.Position) > DrawRange) continue;

                    // Not while dealing with somebody. A stop has its own light, and a beam
                    // wandering off down an alley in the middle of one is two scenes at once.
                    if (unit.Doing == Duty.Contact) continue;

                    Beam(unit, me, night);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw a spotlight: " + ex.Message);
            }
        }

        private void Beam(Unit unit, Ped me, float night)
        {
            var car = unit.Car;

            var want = Aim(unit, car, me);

            // Eased toward it. First frame snaps, or the beam starts at the origin of the map
            // and sweeps across Los Santos to get here.
            unit.Beam = unit.Beam == Vector3.Zero
                ? want
                : unit.Beam + (want - unit.Beam) * Ease;

            // Out of the driver's window: forward of the pillar, to the left, at head height.
            // Not the centre of the car, which puts the beam through the bonnet.
            var from = car.Position
                       + car.ForwardVector * 1.1f
                       + car.RightVector * -0.9f
                       + new Vector3(0f, 0f, 0.75f);

            var dir = unit.Beam - from;
            if (dir.Length() < 0.5f) return;

            dir.Normalize();

            try
            {
                Function.Call(Hash.DRAW_SPOT_LIGHT,
                              from.X, from.Y, from.Z,
                              dir.X, dir.Y, dir.Z,
                              235, 240, 255,          // slightly cold white
                              38f,                    // distance
                              14f * night,            // brightness, fading up with the dark
                              0f,                     // roundness
                              11f,                    // radius
                              1f);                    // falloff
            }
            catch
            {
                // No light this frame. Never worth more than that.
            }
        }

        /// <summary>
        /// Where the beam wants to be.
        ///
        /// Down the alley if they are in one -- that is the shot, and it is the whole reason
        /// the alley patrol and this were built in the same afternoon. Onto the player if he is
        /// close enough that a man in a car would look at him. Otherwise ahead and slightly
        /// off, which is a light being carried rather than a light being used.
        /// </summary>
        private Vector3 Aim(Unit unit, Vehicle car, Ped me)
        {
            var gap = car.Position.DistanceTo(me.Position);

            // Looked at, but not always. A beam that tracks you every second of every pass is
            // a mod with an opinion about you; one that catches you sometimes is a patrol.
            if (gap < LooksAtYou && unit.Interest > 0.5f) return me.Position;

            if (unit.Doing == Duty.Rolling && unit.Target != Vector3.Zero)
            {
                return unit.Target;
            }

            if (unit.Doing == Duty.Searching || unit.Doing == Duty.Sitting)
            {
                // Sweeping. A parked car with a light nailed to one spot is a lamp post.
                var swing = (float)Math.Sin(Game.GameTime * 0.0004d + unit.Car.Handle) * 14f;

                return car.Position
                       + car.ForwardVector * Ahead
                       + car.RightVector * swing;
            }

            return car.Position + car.ForwardVector * Ahead + car.RightVector * -6f;
        }
    }
}
