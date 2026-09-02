using System;
using GTA;
using GTA.Math;
using Precinct88.Core;

namespace Precinct88.Custody
{
    /// <summary>
    /// Actual handcuffs, on actual wrists.
    ///
    /// THE CUFFS WERE THE ONE PART OF AN ARREST THAT WAS NEVER THERE. Everything around them
    /// was: the officer walks over, takes hold, turns you round, your hands go behind your
    /// back, you are restrained and you walk restrained. And there was nothing on your wrists,
    /// which is the single object the whole scene is about.
    ///
    /// THREE SEPARATE THINGS PRETENDING TO BE ONE, and it is worth being clear about which is
    /// which, because they fail independently:
    ///
    ///   SET_ENABLE_HANDCUFFS restrains him. It is the mechanics and it is invisible.
    ///   The movement clipset makes him STAND and WALK like a cuffed man. It is the posture.
    ///   This is the PROP -- a physical pair of handcuffs attached to a bone.
    ///
    /// Any two of the three without the third reads as a bug rather than as an omission, which
    /// is why they all go on at the same moment: when the animation puts the officer's hands
    /// at your wrists, about two-thirds through the clip.
    ///
    /// PH_R_HAND, NOT SKEL_R_HAND. The skeleton bone is the hand itself and a prop hung off it
    /// sits inside the fingers; the PH bone is the attachment point the game uses for anything
    /// a ped is HOLDING, which is where a pair of cuffs closed round both wrists actually
    /// wants to be.
    /// </summary>
    internal sealed class Irons
    {
        /// <summary>The game's own handcuffs, as used in its own cutscenes.</summary>
        private const string CuffModel = "p_cs_cuffs_02";

        /// <summary>
        /// Where they sit on the bone, and which way up.
        ///
        /// THESE ARE THE NUMBERS MOST LIKELY TO NEED A NUDGE and the only ones in this file
        /// worth touching. A prop on a bone is positioned in the bone's own space, so there is
        /// no way to derive them -- every mod that has ever attached anything to a hand has
        /// arrived at its own set by looking. If the cuffs float, sit inside a wrist, or point
        /// the wrong way, it is these two lines and nothing else.
        /// </summary>
        private static readonly Vector3 Sit = new Vector3(0.02f, 0.05f, -0.02f);
        private static readonly Vector3 Turn = new Vector3(80f, 180f, 0f);

        private Prop _cuffs;

        /// <summary>Whether there is a pair on somebody right now.</summary>
        public bool On => _cuffs != null && _cuffs.Exists();

        /// <summary>
        /// Puts them on.
        ///
        /// Quietly does nothing if the model will not load, which is the same bargain every
        /// other guessed name in this mod makes: a missing prop costs a pair of invisible
        /// handcuffs and a line in the log, and the arrest still happens.
        /// </summary>
        public void Cuff(Ped who)
        {
            if (On) return;

            try
            {
                if (!Cops.Alive(who)) return;

                var model = Cops.Load("handcuffs", CuffModel);
                if (model == null) return;

                _cuffs = World.CreateProp(model.Value, who.Position, false, false);

                // The model is released immediately -- the prop already exists, and holding the
                // model loaded for the length of an arrest is memory nobody asked for.
                model.Value.MarkAsNoLongerNeeded();

                if (_cuffs == null || !_cuffs.Exists())
                {
                    _cuffs = null;
                    return;
                }

                _cuffs.IsPersistent = true;

                // No collision, or a pair of handcuffs bounces off his own legs and drags the
                // ragdoll about with it.
                _cuffs.AttachTo(who.Bones[Bone.PHRightHand], Sit, Turn);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not put the cuffs on: " + ex.Message);
                Free();
            }
        }

        /// <summary>
        /// Takes them off, and off the map.
        ///
        /// DELETED RATHER THAN DETACHED. A detached prop is a pair of handcuffs lying in the
        /// road forever, and the player can pick some of them up. Every exit path in the
        /// detention calls this, including the one where the mod is unloading, because a prop
        /// attached to the player by a script that is no longer running has nothing left that
        /// could ever remove it.
        /// </summary>
        public void Free()
        {
            var cuffs = _cuffs;
            _cuffs = null;

            if (cuffs == null) return;

            try
            {
                if (cuffs.Exists())
                {
                    cuffs.Detach();
                    cuffs.Delete();
                }
            }
            catch
            {
                // Gone already, which is the outcome this was arranging.
            }
        }
    }
}
