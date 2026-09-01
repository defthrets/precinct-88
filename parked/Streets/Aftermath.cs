using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>What is happening at the body right now.</summary>
    internal enum Stage
    {
        /// <summary>An ambulance has been called and is on its way.</summary>
        Called,

        /// <summary>A paramedic is knelt over him, establishing what everybody can see.</summary>
        Medic,

        /// <summary>Taped off and waiting. Most of the five minutes is this.</summary>
        Held,

        /// <summary>The van is here and two of them have come to collect him.</summary>
        Coroner,

        /// <summary>Over. Everything gets handed back.</summary>
        Done,
    }

    /// <summary>One body, and everything that turns up because of it.</summary>
    internal sealed class Scene
    {
        public Ped Body;
        public Vector3 Where;

        public Stage At = Stage.Called;
        public int StageAt;
        public int StartedAt;

        public Vehicle Ambulance;
        public Ped Medic;

        public Vehicle Hearse;
        public readonly List<Ped> Coroners = new List<Ped>();

        public readonly List<Prop> Cones = new List<Prop>();

        /// <summary>The officer who did it, kept at the scene rather than wandering off.</summary>
        public Ped Guard;
    }

    /// <summary>
    /// What happens after the police shoot somebody.
    ///
    /// IN VANILLA, NOTHING DOES. The body lies there, the officers go back to whatever they were
    /// doing, and in a few minutes the engine streams the whole thing out as if it never
    /// happened. Which is the same problem the rest of this mod keeps finding: the game models
    /// the moment and not the consequence.
    ///
    /// So a killing leaves a scene. An ambulance comes and a paramedic kneels over him, which
    /// establishes what everybody watching already knows. Then it stands -- coned off, with the
    /// officer who fired still stood there -- for five minutes. Then a van arrives, two people
    /// get out, and they take him away.
    ///
    /// THE FIVE MINUTES ARE THE POINT. A scene that resolved in twenty seconds would be a
    /// cutscene; one that lasts long enough to still be there when you come back round the block
    /// is a thing that happened to the street. It is also long enough that most players will
    /// never see the end of it, which is correct -- the coroner is not a reward for waiting.
    ///
    /// EVERY MODEL AND EVERY SCENARIO NAME IS GUARDED. A missing hearse means the body is simply
    /// collected without one; a missing kneel animation means the paramedic stands instead.
    /// Nothing here is allowed to be a stuck scene, because a stuck scene is a corpse and four
    /// peds standing in the road forever.
    /// </summary>
    internal sealed class Aftermath
    {
        private const int TickMs = 700;

        /// <summary>How many bodies get this at once. More than two is a massacre, not a scene.</summary>
        private const int MostScenes = 2;

        /// <summary>Past this the player is not watching and the whole thing is torn down.</summary>
        private const float ForgetRange = 260f;

        /// <summary>How long the paramedic spends over him.</summary>
        private const int MedicMs = 18000;

        /// <summary>How long the coroners take once they are stood over him.</summary>
        private const int CollectMs = 9000;

        /// <summary>How long a vehicle is given to arrive before the scene moves on without it.</summary>
        private const int ArriveMs = 45000;

        /// <summary>Close enough to have arrived.</summary>
        private const float Arrived = 14f;

        private const float ConeRing = 3.4f;

        private static readonly string[] Ambulances = { "ambulance" };
        private static readonly string[] Medics = { "s_m_m_paramedic_01" };

        /// <summary>The Romero is the game's hearse. Nothing else in it reads as one.</summary>
        private static readonly string[] Hearses = { "romero", "ambulance" };

        /// <summary>No coroner model exists, so the nearest honest thing is used.</summary>
        private static readonly string[] Undertakers =
        {
            "s_m_m_doctor_01", "s_m_m_paramedic_01", "s_m_m_ups_01",
        };

        private static readonly string[] Cones = { "prop_roadcone02a", "prop_roadcone01a" };

        /// <summary>Kneeling over somebody. Falls through to standing if it does not exist.</summary>
        private const string KneelScenario = "CODE_HUMAN_MEDIC_KNEEL";
        private const string GuardScenario = "WORLD_HUMAN_COP_IDLES";

        private readonly Settings _cfg;
        private readonly Random _rng = new Random();

        private readonly List<Scene> _scenes = new List<Scene>();

        /// <summary>Bodies already dealt with, so one death is one scene.</summary>
        private readonly HashSet<int> _seen = new HashSet<int>();

        private int _lastTick;

        public Aftermath(Settings cfg)
        {
            _cfg = cfg;
        }

        public int Count => _scenes.Count;

        public void Update()
        {
            if (!_cfg.CrimeScenes) return;

            var now = Game.GameTime;
            if (now - _lastTick < TickMs) return;
            _lastTick = now;

            var me = Game.Player.Character;
            if (me == null || !me.Exists()) return;

            try
            {
                Look(me, now);

                for (var i = _scenes.Count - 1; i >= 0; i--)
                {
                    var scene = _scenes[i];

                    if (!Keep(scene, me, now))
                    {
                        Pack(scene);
                        _scenes.RemoveAt(i);
                        continue;
                    }

                    Run(scene, now);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Aftermath failed: " + ex.Message);
            }

            if (_seen.Count > 300) _seen.Clear();
        }

        // ---- finding one -------------------------------------------------------

        /// <summary>
        /// Somebody the police have shot.
        ///
        /// Ped.Killer is the game's own record of who did it, so there is no guessing and no
        /// scanning every officer against every corpse. A body killed by anybody else is not
        /// this mod's business -- the player leaving bodies about is already covered by Witness,
        /// and a scene for every one of those would be an ambulance following him around.
        /// </summary>
        private void Look(Ped me, int now)
        {
            if (_scenes.Count >= MostScenes) return;

            foreach (var ped in World.GetNearbyPeds(me, 90f))
            {
                if (ped == null || !ped.Exists() || ped.IsAlive) continue;
                if (ped.Handle == me.Handle) continue;
                if (_seen.Contains(ped.Handle)) continue;

                _seen.Add(ped.Handle);

                if (Cops.IsCop(ped)) continue;   // an officer down is not a crime scene, it is a funeral

                Ped killer = null;

                try { killer = ped.Killer as Ped; }
                catch { continue; }

                if (!Cops.IsCop(killer)) continue;

                Open(ped, killer, now);

                if (_scenes.Count >= MostScenes) return;
            }
        }

        private void Open(Ped body, Ped killer, int now)
        {
            var scene = new Scene
            {
                Body = body,
                Where = body.Position,
                StartedAt = now,
                StageAt = now,
                Guard = killer,
            };

            // He does not get to wander off. The officer who fired standing at the scene is most
            // of what makes it read as a scene rather than as a body somebody forgot.
            try
            {
                if (Cops.Alive(killer))
                {
                    killer.BlockPermanentEvents = true;

                    Function.Call(Hash.TASK_START_SCENARIO_AT_POSITION, killer.Handle,
                                  GuardScenario,
                                  scene.Where.X + 2.2f, scene.Where.Y, scene.Where.Z,
                                  0f, -1, true, false);
                }
            }
            catch
            {
                // He stands where he is, which is fine.
            }

            // The body is ours now, or the engine tidies it away mid-scene.
            try { body.IsPersistent = true; }
            catch { /* it may still stream out; Keep handles that */ }

            Ring(scene);

            scene.Ambulance = Bring(Ambulances, Medics, scene, out scene.Medic);

            _scenes.Add(scene);

            Log.Info("Crime scene opened at " + Districts.ZoneAt(scene.Where) + ".");
        }

        // ---- running it --------------------------------------------------------

        private bool Keep(Scene scene, Ped me, int now)
        {
            if (scene.At == Stage.Done) return false;

            // Out of sight and a long way off. Holding a scene the player cannot see is four
            // peds and two vehicles kept alive for nothing.
            if (scene.Where.DistanceTo(me.Position) > ForgetRange) return false;

            // The body streamed out or somebody blew it up. Nothing left to have a scene about.
            if (!scene.Body.Exists()) return false;

            return true;
        }

        private void Run(Scene scene, int now)
        {
            switch (scene.At)
            {
                case Stage.Called: Arriving(scene, now); break;
                case Stage.Medic: Tending(scene, now); break;
                case Stage.Held: Holding(scene, now); break;
                case Stage.Coroner: Collecting(scene, now); break;
            }
        }

        private void Arriving(Scene scene, int now)
        {
            var here = Cops.Alive(scene.Ambulance) &&
                       scene.Ambulance.Position.DistanceTo(scene.Where) < Arrived;

            // Moves on WITHOUT the ambulance if it cannot get there. A scene that waits forever
            // for a vehicle stuck on a kerb is a scene that never finishes.
            if (!here && now - scene.StageAt < ArriveMs) return;

            if (Cops.Alive(scene.Medic))
            {
                try
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, scene.Medic.Handle,
                                  scene.Ambulance.Handle, 0);
                }
                catch
                {
                    // He gets out or he does not.
                }
            }

            Go(scene, Stage.Medic, now);
        }

        private void Tending(Scene scene, int now)
        {
            if (Cops.Alive(scene.Medic) && now - scene.StageAt > 2500)
            {
                Kneel(scene.Medic, scene.Where);
            }

            if (now - scene.StageAt < MedicMs) return;

            // Nothing to take. The ambulance leaves, which is the whole point of it having come.
            Send(scene.Medic, scene.Ambulance);

            scene.Medic = null;
            scene.Ambulance = null;

            Go(scene, Stage.Held, now);
        }

        private void Holding(Scene scene, int now)
        {
            var held = (int)(_cfg.CrimeSceneMinutes * 60000f);

            if (now - scene.StartedAt < held) return;

            scene.Hearse = Bring(Hearses, Undertakers, scene, out var first);

            if (Cops.Alive(first)) scene.Coroners.Add(first);

            // The second of them, in the passenger seat. Two people carry a body; one drags it.
            var mate = Seat(scene.Hearse, Undertakers, 0);
            if (Cops.Alive(mate)) scene.Coroners.Add(mate);

            Go(scene, Stage.Coroner, now);

            Log.Info("Coroner called to the scene at " + Districts.ZoneAt(scene.Where) + ".");
        }

        private void Collecting(Scene scene, int now)
        {
            var here = Cops.Alive(scene.Hearse) &&
                       scene.Hearse.Position.DistanceTo(scene.Where) < Arrived;

            if (!here && now - scene.StageAt < ArriveMs) return;

            // Out, and over him.
            if (now - scene.StageAt < ArriveMs + CollectMs)
            {
                foreach (var one in scene.Coroners)
                {
                    if (!Cops.Alive(one)) continue;

                    if (one.IsInVehicle())
                    {
                        try
                        {
                            Function.Call(Hash.TASK_LEAVE_VEHICLE, one.Handle,
                                          scene.Hearse.Handle, 0);
                        }
                        catch
                        {
                            // As above.
                        }

                        continue;
                    }

                    Kneel(one, scene.Where);
                }

                if (!here) return;
                if (now - scene.StageAt < ArriveMs) return;
            }

            // TAKEN AWAY, while two people are stood over him.
            //
            // Deleted rather than carried, and that is an honest limitation: carrying a body is
            // an attached-ped animation this mod has no verified clip for, and a corpse gliding
            // along behind somebody is far worse than one that is simply gone by the time you
            // look back. They are knelt around him when it happens, which is the moment it reads
            // as least abrupt.
            try
            {
                if (scene.Body.Exists()) scene.Body.Delete();
            }
            catch
            {
                // If it will not delete it is let go instead, below.
            }

            foreach (var one in scene.Coroners) Send(one, scene.Hearse);

            Log.Info("Body removed from the scene at " + Districts.ZoneAt(scene.Where) + ".");

            Go(scene, Stage.Done, now);
        }

        // ---- the furniture -----------------------------------------------------

        /// <summary>Cones round the body, which is what says "scene" without a tape prop.</summary>
        private void Ring(Scene scene)
        {
            var model = First(Cones);
            if (model == null) return;

            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var a = i * Math.PI * 0.5d + 0.4d;

                    var at = scene.Where + new Vector3((float)Math.Cos(a) * ConeRing,
                                                       (float)Math.Sin(a) * ConeRing, 0f);

                    var cone = World.CreateProp(model.Value, at, false, false);
                    if (cone == null || !cone.Exists()) continue;

                    Function.Call(Hash.PLACE_OBJECT_ON_GROUND_PROPERLY, cone.Handle);

                    scene.Cones.Add(cone);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not cone off a scene: " + ex.Message);
            }
        }

        /// <summary>Brings a vehicle and its driver to the scene.</summary>
        private Vehicle Bring(string[] cars, string[] crew, Scene scene, out Ped driver)
        {
            driver = null;

            try
            {
                var carModel = First(cars);
                if (carModel == null) return null;

                Vector3 from;
                float heading;

                if (!Stations.RoadBy(scene.Where + Around(120f), out from, out heading))
                {
                    if (!Stations.RoadBy(scene.Where + Around(200f), out from, out heading))
                    {
                        return null;
                    }
                }

                var car = World.CreateVehicle(carModel.Value, from, heading);
                carModel.Value.MarkAsNoLongerNeeded();

                if (!Cops.Alive(car)) return null;

                car.IsPersistent = true;
                car.IsEngineRunning = true;
                car.IsSirenActive = true;

                Function.Call(Hash.SET_VEHICLE_HAS_MUTED_SIRENS, car.Handle, true);

                driver = Seat(car, crew, -1);

                if (Cops.Alive(driver))
                {
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                                  driver.Handle, car.Handle,
                                  scene.Where.X, scene.Where.Y, scene.Where.Z,
                                  20f, 786603, 10f);
                }

                return car;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send a vehicle to a scene: " + ex.Message);
                return null;
            }
        }

        private Ped Seat(Vehicle car, string[] models, int seat)
        {
            try
            {
                if (!Cops.Alive(car)) return null;

                var model = First(models);
                if (model == null) return null;

                var who = car.CreatePedOnSeat((VehicleSeat)seat, model.Value);
                model.Value.MarkAsNoLongerNeeded();

                if (!Cops.Alive(who)) return null;

                who.IsPersistent = true;
                who.BlockPermanentEvents = true;

                return who;
            }
            catch
            {
                return null;
            }
        }

        private static void Kneel(Ped who, Vector3 over)
        {
            try
            {
                if (!Cops.Alive(who)) return;

                if (who.Position.DistanceTo(over) > 2.2f)
                {
                    Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, who.Handle,
                                  over.X, over.Y, over.Z, 1.5f, -1, 1.4f, 0, 0f);
                    return;
                }

                // A scenario the build has not got simply does not start, and he stands over
                // the body instead -- which is still somebody attending a scene.
                Function.Call(Hash.TASK_START_SCENARIO_AT_POSITION, who.Handle, KneelScenario,
                              who.Position.X, who.Position.Y, who.Position.Z,
                              0f, -1, true, false);
            }
            catch
            {
                // Cosmetic.
            }
        }

        /// <summary>Puts somebody back in their vehicle and sends it away.</summary>
        private static void Send(Ped who, Vehicle car)
        {
            try
            {
                if (Cops.Alive(who) && Cops.Alive(car))
                {
                    Function.Call(Hash.TASK_ENTER_VEHICLE, who.Handle, car.Handle,
                                  20000, -1, 1.5f, 1, 0);
                }

                Cops.LetGo(who);
                Cops.LetGo(car);
            }
            catch
            {
                // Teardown.
            }
        }

        private Vector3 Around(float radius)
        {
            var a = _rng.NextDouble() * Math.PI * 2d;

            return new Vector3((float)Math.Cos(a) * radius, (float)Math.Sin(a) * radius, 0f);
        }

        private static Model? First(string[] names)
        {
            foreach (var n in names)
            {
                try
                {
                    var m = new Model(n);
                    if (!m.IsValid) continue;

                    var loaded = Cops.Load(n, m);
                    if (loaded != null) return loaded;
                }
                catch
                {
                    // Next name.
                }
            }

            return null;
        }

        private static void Go(Scene scene, Stage next, int now)
        {
            scene.At = next;
            scene.StageAt = now;
        }

        // ---- putting it away ---------------------------------------------------

        /// <summary>Hands a scene back to the game. Nothing here is ours to keep.</summary>
        private static void Pack(Scene scene)
        {
            try
            {
                foreach (var cone in scene.Cones)
                {
                    if (cone != null && cone.Exists()) cone.Delete();
                }

                scene.Cones.Clear();

                if (Cops.Alive(scene.Guard))
                {
                    scene.Guard.BlockPermanentEvents = false;
                    scene.Guard.Task.ClearAll();
                }

                Cops.LetGo(scene.Medic);
                Cops.LetGo(scene.Ambulance);

                foreach (var one in scene.Coroners) Cops.LetGo(one);
                scene.Coroners.Clear();

                Cops.LetGo(scene.Hearse);
                Cops.LetGo(scene.Body);
            }
            catch
            {
                // Teardown.
            }
        }

        /// <summary>Everything, for the mod unloading.</summary>
        public void Release()
        {
            foreach (var scene in _scenes) Pack(scene);
            _scenes.Clear();
            _seen.Clear();
        }
    }
}
