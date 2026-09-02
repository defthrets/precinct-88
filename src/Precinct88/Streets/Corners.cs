using System;
using System.Collections.Generic;
using System.IO;
using GTA.Math;
using Precinct88.Core;

namespace Precinct88.Streets
{
    /// <summary>Somewhere a foot patrol makes a point of walking past.</summary>
    internal sealed class Corner
    {
        /// <summary>For the log, and for whoever edits the file. Never shown in game.</summary>
        public string Name;

        public Vector3 Where;
    }

    /// <summary>
    /// The handful of places the police keep an eye on.
    ///
    /// THE ONE THING CODE CANNOT WORK OUT FOR ITSELF. Everything else about the foot patrol is
    /// derived -- pavements come from the nav mesh, alleys from which nodes the satnav refuses,
    /// districts from zone codes. None of that can tell you which corner of Chamberlain Hills
    /// is worth an officer's attention, because that is a fact about who stands there rather
    /// than about the geometry, and the geometry is all the game knows.
    ///
    /// So it is typed in, and typed in a FILE rather than in here. A corner is somewhere a
    /// player has decided is interesting -- because another mod puts people there, because a
    /// mission uses it, because they simply like the spot -- and a list like that belongs
    /// somewhere it can be changed without a compiler.
    ///
    /// EVERYTHING THAT HAPPENS WHEN HE ARRIVES ALREADY EXISTED. Rounds stops him and gets his
    /// notepad out; Chats finds him whoever is standing there and has him talk to them. This
    /// file adds no behaviour at all. It only answers "where", and the rest of the mod was
    /// already waiting to do something about it.
    /// </summary>
    internal static class Corners
    {
        private static List<Corner> _all;

        /// <summary>Everything read from the file. Empty if there is not one.</summary>
        public static IReadOnlyList<Corner> All
        {
            get
            {
                if (_all == null) Load();
                return _all;
            }
        }

        /// <summary>
        /// The corners near a point, nearest first.
        ///
        /// A LIST RATHER THAN THE NEAREST ONE, because a round with two of these in it is a
        /// round that visibly goes somewhere -- an officer walking between two corners he cares
        /// about reads completely differently to one who visits a single spot and then wanders.
        /// </summary>
        public static List<Corner> Near(Vector3 to, float within, int most)
        {
            var found = new List<Corner>();

            foreach (var corner in All)
            {
                if (corner.Where.DistanceTo(to) > within) continue;

                found.Add(corner);
            }

            found.Sort((a, b) => a.Where.DistanceTo(to).CompareTo(b.Where.DistanceTo(to)));

            if (found.Count > most) found.RemoveRange(most, found.Count - most);

            return found;
        }

        /// <summary>
        /// Reads data\corners.json, if there is one.
        ///
        /// NO CORNERS IS A PERFECTLY GOOD ANSWER and the commonest one -- a fresh install has
        /// no file, and foot patrols simply walk ordinary routes. So a missing file is not a
        /// warning, and only a file that exists and cannot be read is.
        /// </summary>
        private static void Load()
        {
            _all = new List<Corner>();

            try
            {
                var path = Path.Combine(Paths.Data, "corners.json");

                if (!File.Exists(path)) return;

                var doc = JsonFile.Read(path);
                if (doc == null || doc.IsNull) return;

                var list = doc.Has("corners") ? doc["corners"] : doc;
                if (list.Kind != JsonKind.Array) return;

                foreach (var item in list.Items)
                {
                    var where = new Vector3(item["x"].AsFloat(0f),
                                            item["y"].AsFloat(0f),
                                            item["z"].AsFloat(0f));

                    if (where == Vector3.Zero) continue;

                    _all.Add(new Corner
                    {
                        Name = item["name"].AsString() ?? "a corner",
                        Where = where,
                    });
                }

                Log.Info("corners.json: " + _all.Count + " place(s) the patrol keeps an eye on.");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read corners.json; foot patrols will walk ordinary rounds. " +
                         ex.Message);
            }
        }

        /// <summary>Forgets the file, so an edit is picked up on the next reload.</summary>
        public static void Forget()
        {
            _all = null;
        }
    }
}
