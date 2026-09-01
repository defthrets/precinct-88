using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Contact
{
    /// <summary>
    /// The K-9 unit, and the reason you do not get to decline a search.
    ///
    /// From "Ambient AI Police", which gives its traffic stops three shapes: a ticket, a pat
    /// down and then a ticket, or a dog walked round the vehicle and then a ticket. The dog is
    /// the one worth having, because it is the only one that changes what the player can DO
    /// rather than what he watches.
    ///
    /// EVERY SEARCH IN THIS MOD SO FAR HAS BEEN OPTIONAL. Holding the key is consent, letting go
    /// walks you out of it, and that is deliberate -- the whole scene is built so that leaving
    /// is always available. A dog is the exception that makes the rule mean something: once it
    /// has walked the car, the search happens whether you hold anything or not. You can still
    /// drive off. You just cannot politely decline.
    ///
    /// GUARDED LIKE EVERY OTHER MODEL GUESS. a_c_shepherd is the German Shepherd and the game
    /// does ship one, but if it will not load the stop simply carries on as an ordinary ticket
    /// and nobody is any the wiser. A missing dog must never be a stuck scene.
    /// </summary>
    internal sealed class Dog
    {
        /// <summary>The German Shepherd. Chop is a_c_shepherd too, which is the same model.</summary>
        private const string Model = "a_c_shepherd";

        /// <summary>How long he spends going round it before anybody says anything.</summary>
        private const int SniffMs = 9000;

        /// <summary>How far round the car he walks each leg.</summary>
        private const float Orbit = 3.1f;

        private Ped _dog;
        private int _startedAt;
        private int _nextLeg;
        private int _corner;

        public bool Out => Cops.Alive(_dog);

        /// <summary>Whether he has finished walking round it.</summary>
        public bool Done => Out && Game.GameTime - _startedAt > SniffMs;

        /// <summary>
        /// Puts one out beside the officer.
        ///
        /// Returns whether it worked. False means no dog, which the caller must treat as an
        /// ordinary stop rather than as an error -- a model that will not load is the most
        /// ordinary thing in the world.
        /// </summary>
        public bool Send(Ped officer, Vehicle car)
        {
            if (Out) return true;

            try
            {
                if (!Cops.Alive(officer) || !Cops.Alive(car)) return false;

                var model = Cops.Load(Model);
                if (model == null) return false;

                var beside = officer.Position + officer.RightVector * 1.1f;

                _dog = World.CreatePed(model.Value, beside);
                model.Value.MarkAsNoLongerNeeded();

                if (!Cops.Alive(_dog)) return false;

                _dog.IsPersistent = true;

                // A police dog is police. Everything that asks whether somebody is law -- the
                // witness scan, the sight check -- then includes him, which is correct: a dog
                // that watched you do something has, for this mod's purposes, seen it.
                Function.Call(Hash.SET_PED_AS_COP, _dog.Handle, true);
                Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, _dog.Handle,
                              Function.Call<int>(Hash.GET_HASH_KEY, "COP"));

                _dog.BlockPermanentEvents = true;

                _startedAt = Game.GameTime;
                _nextLeg = 0;
                _corner = 0;

                Log.Info("K-9 out at a stop.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send a dog: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Walks him round the car, a corner at a time.
        ///
        /// Corners rather than a circle, because a follow-offset task keeps him glued to one
        /// side and a wander sends him into the road. Four legs is enough to read as searching
        /// the vehicle and short enough that nobody is waiting on it.
        /// </summary>
        public void Update(Vehicle car)
        {
            if (!Out || !Cops.Alive(car)) return;

            var now = Game.GameTime;
            if (now < _nextLeg) return;

            _nextLeg = now + 2200;

            try
            {
                // Round the car: back left, back right, front right, front left.
                var offsets = new[]
                {
                    new Vector3(-Orbit * 0.5f, -Orbit, 0f),
                    new Vector3(Orbit * 0.5f, -Orbit, 0f),
                    new Vector3(Orbit * 0.5f, Orbit, 0f),
                    new Vector3(-Orbit * 0.5f, Orbit, 0f),
                };

                var to = car.GetOffsetPosition(offsets[_corner % offsets.Length]);
                _corner++;

                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, _dog.Handle,
                              to.X, to.Y, to.Z, 1.6f, -1, 0.8f, 0, 0f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not walk the dog: " + ex.Message);
            }
        }

        /// <summary>Hands him back to the game.</summary>
        public void Away()
        {
            if (_dog == null) return;

            Cops.LetGo(_dog);
            _dog = null;
        }
    }
}
