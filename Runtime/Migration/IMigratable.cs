using FishNet.Serializing;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// State of a networked object that must survive a host migration (AI brain, health, broken window, item charges...).
    /// Put it on a component next to <see cref="NetworkMigratable"/> (same object or its children).
    /// <list type="bullet">
    /// <item><see cref="WriteMigrationState"/> runs on the HOST — where the full state lives, host-only fields included —
    /// every <c>NetworkSession.MigrationStateInterval</c>; when the bytes changed they're sent to every client.</item>
    /// <item><see cref="ReadMigrationState"/> runs on the NEW host after a migration, once the object is back (spawned
    /// again), before anyone else sees it. Set SyncVars there — clients get them as usual.</item>
    /// </list>
    /// Write and read the same fields in the same order. FishNet writers: WriteInt32, WriteSingle, WriteString,
    /// WriteVector3, or <c>Write&lt;T&gt;</c> for any type FishNet can serialize.
    /// </summary>
    public interface IMigratable
    {
        void WriteMigrationState(Writer writer);
        void ReadMigrationState(Reader reader);
    }
}
