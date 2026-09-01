using System;
using Precinct88.Contact;
using Precinct88.Response;

namespace Precinct88.Core
{
    /// <summary>
    /// The one thing that outlives a session, and the ONE place that writes it.
    ///
    /// Profile owned record.json outright and wrote the whole document every time it changed.
    /// That was fine while it was the only thing in there and became a bug the moment anything
    /// else needed to persist: two systems each writing a complete file is two systems each
    /// deleting the other's half, and the loser is whichever saved second. It is the same shape
    /// as the two counted law-holds, and it is worth catching before it happens rather than
    /// after.
    ///
    /// So the file has an owner. Each system says how to write itself into a document and how to
    /// read itself back out; this decides when, and never writes a partial one.
    /// </summary>
    internal static class Record
    {
        /// <summary>Bumped when the shape changes in a way an old file cannot be read as.</summary>
        private const int Version = 1;

        public static void Load(Profile profile, Licence licence, Tickets tickets)
        {
            try
            {
                var doc = JsonFile.Read(Paths.SaveFile);

                if (doc == null || doc.IsNull)
                {
                    // No file is a new player, not an error. There is nothing here worth
                    // refusing to start over.
                    return;
                }

                if (profile != null) profile.FromJson(doc);
                if (licence != null) licence.FromJson(doc);
                if (tickets != null) tickets.FromJson(doc);

                Log.Info("Record loaded: " + (profile == null ? "?" : profile.Word) +
                         ", " + (licence == null ? 0 : licence.Points) + " licence point(s), " +
                         Tickets.Money(tickets == null ? 0 : tickets.Owed) + " outstanding.");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read the record; starting clean. " + ex.Message);
            }
        }

        /// <summary>
        /// Writes it, and only when something has actually moved.
        ///
        /// Both halves are asked whether they are dirty, and the document is built from BOTH
        /// whichever one asked -- which is the entire point of this class existing.
        /// </summary>
        public static void Save(Profile profile, Licence licence, Tickets tickets,
                                bool force = false)
        {
            try
            {
                var dirty = force ||
                            (profile != null && profile.Dirty) ||
                            (licence != null && licence.Dirty) ||
                            (tickets != null && tickets.Dirty);

                if (!dirty) return;

                var doc = Json.Object();
                doc.Set("version", Version);

                if (profile != null) profile.ToJson(doc);
                if (licence != null) licence.ToJson(doc);
                if (tickets != null) tickets.ToJson(doc);

                if (!JsonFile.Write(Paths.SaveFile, doc)) return;

                if (profile != null) profile.Clean();
                if (licence != null) licence.Clean();
                if (tickets != null) tickets.Clean();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not write the record: " + ex.Message);
            }
        }
    }
}
