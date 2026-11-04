using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Inspector-assignable factory of a provider (one asset per platform).
    /// Kept in its own file: Unity binds ScriptableObject types to scripts by file name, and a field of a type
    /// without a matching file silently refuses object assignments.
    /// </summary>
    public abstract class NetworkProviderAsset : ScriptableObject
    {
        public abstract INetworkProvider CreateProvider();
    }
}
