using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using Precinct88.Core;

namespace Precinct88.Response
{
    /// <summary>
    /// The witness that is not a person.
    ///
    /// Every other way a crime reaches the police in this mod needs somebody standing there,
    /// which gives the player one very reliable answer to everything: do it where nobody is.
    /// Cameras are the counter, and they are the reason a shop, a bank forecourt or a petrol
    /// station should feel different from an alley at three in the morning.
    ///
    /// FOUND BY PROP, NOT BY COORDINATE, and that decision is the whole file. The obvious build
    /// is a JSON list of camera positions, and it is wrong twice over: it means inventing thirty
    /// coordinates by hand and being subtly wrong about all of them, and it means the list is
    /// stale the moment anybody installs a map mod. Rockstar already placed CCTV cameras all
    /// over Los Santos as world props. Asking the game where they are gets the real ones, in
    /// the real places, for free -- and it keeps working under map mods, because a mod that adds
    /// a shop adds its camera prop too.
    ///
    /// The failure mode is the good one: if none of these model names exist, no camera ever
    /// sees anything, and the mod behaves exactly as it did before this file. Nothing breaks.
    /// </summary>
    internal static class Cameras
    {
        /// <summary>
        /// The world props that count as a camera.
        ///
        /// Not verified in-game -- these are the names the base game is understood to use, and
        /// a wrong one costs nothing because Model.IsValid filters it out before it is ever
        /// asked about. Extend the list rather than replacing it; a name that does not exist is
        /// harmless, and a missing one is a shop that quietly is not watched.
        /// </summary>
        private static readonly string[] Models =
        {
            "prop_cctv_cam_01a", "prop_cctv_cam_01b",
            "prop_cctv_cam_02a", "prop_cctv_cam_03a",
            "prop_cctv_cam_04a", "prop_cctv_cam_04c",
            "prop_cctv_cam_05a", "prop_cctv_cam_06a",
            "prop_cctv_cam_07a", "prop_cs_cctv",
        };

        /// <summary>How far a camera is any use. Generous -- it is a lens, not an eye.</summary>
        private const float Range = 26f;

        /// <summary>
        /// How wide its view is, as a dot product against its own forward vector.
        ///
        /// 0.5 is roughly a sixty-degree half-angle. Deliberately narrower than a person's --
        /// a camera points at ONE thing, which is what makes walking round the side of a shop
        /// a real answer to it rather than a superstition.
        /// </summary>
        private const float Cone = 0.5f;

        /// <summary>How often the prop scan is redone. It is the expensive part.</summary>
        private const int RescanMs = 2500;

        /// <summary>How far out to look for cameras at all.</summary>
        private const float ScanRange = 60f;

        private static Model[] _models;
        private static readonly List<Prop> _near = new List<Prop>();
        private static int _scannedAt;
        private static bool _warned;

        private static Model[] Wanted()
        {
            if (_models != null) return _models;

            var found = new List<Model>();

            foreach (var name in Models)
            {
                try
                {
                    var model = new Model(name);
                    if (model.IsValid) found.Add(model);
                }
                catch
                {
                    // A name this build does not have. Skipping it is the whole point.
                }
            }

            _models = found.ToArray();

            if (_models.Length == 0 && !_warned)
            {
                _warned = true;
                Log.Warn("No CCTV camera props are valid in this build; cameras will never see " +
                         "anything. Harmless, but the shop-camera mechanic is off.");
            }
            else
            {
                Log.Info("CCTV: " + _models.Length + " camera prop model(s) available.");
            }

            return _models;
        }

        /// <summary>
        /// Whether a camera can see this ped right now.
        ///
        /// Asked at the moment of a crime rather than continuously -- being on camera is not
        /// itself an offence, and a system that tracked it every frame would be paying for an
        /// answer nobody wants until something happens.
        /// </summary>
        public static bool Watching(Ped who)
        {
            try
            {
                if (!Cops.Alive(who)) return false;

                var models = Wanted();
                if (models.Length == 0) return false;

                Rescan(who.Position);

                foreach (var cam in _near)
                {
                    if (cam == null || !cam.Exists()) continue;

                    var to = who.Position - cam.Position;

                    var gap = to.Length();
                    if (gap > Range || gap < 0.01f) continue;

                    to.Normalize();

                    // Which way it is pointing. A camera bolted to a wall has a forward vector
                    // like anything else, so the same dot-product test that works for a man
                    // works here -- and unlike a man, it cannot turn round.
                    var facing = cam.ForwardVector;

                    if (facing.Length() < 0.01f) continue;

                    facing.Normalize();

                    if (Vector3.Dot(facing, to) < Cone) continue;

                    // Through the wall is not on camera.
                    if (!cam.IsInRange(who.Position, Range)) continue;

                    if (!Function_HasLos(cam, who)) continue;

                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Camera check failed: " + ex.Message);
            }

            return false;
        }

        private static bool Function_HasLos(Prop cam, Ped who)
        {
            try
            {
                return GTA.Native.Function.Call<bool>(
                    GTA.Native.Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, cam.Handle, who.Handle, 17);
            }
            catch
            {
                // If the game will not say, assume it can see. A camera that fails open is a
                // camera; one that fails closed is decoration.
                return true;
            }
        }

        private static void Rescan(Vector3 around)
        {
            var now = Game.GameTime;
            if (now - _scannedAt < RescanMs && _near.Count > 0) return;
            if (now - _scannedAt < RescanMs) return;

            _scannedAt = now;
            _near.Clear();

            try
            {
                foreach (var prop in GTA.World.GetNearbyProps(around, ScanRange, _models))
                {
                    if (prop != null && prop.Exists()) _near.Add(prop);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not scan for cameras: " + ex.Message);
            }
        }

        /// <summary>
        /// Forces the model check at load, so the log says how many cameras exist.
        ///
        /// Everything here is lazy on purpose -- it is a cost nobody should pay until a crime
        /// happens. But lazy also means the log stays silent about it until then, and "are
        /// cameras working" is exactly the question somebody has on a first install.
        /// </summary>
        public static void Check()
        {
            Wanted();
        }

        /// <summary>Drops the cached scan. For teardown and for a world reload.</summary>
        public static void Forget()
        {
            _near.Clear();
            _scannedAt = 0;
        }
    }
}
