using System;
using System.Text;
using FishNet.Object;
using FishNet.Serializing;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Makes a networked object survive a host migration. Put it on the ROOT of anything the host controls that should
    /// come back as it was: AI, items, doors, breakable windows...
    /// <list type="bullet">
    /// <item>Scene objects (placed in the scene): come back with their pose and state; ones despawned before the host
    /// dropped (picked up, destroyed) are despawned again. Matched by their place in the scene hierarchy.</item>
    /// <item>Spawned objects (from a prefab at runtime): spawned again from the same prefab, pose, state and owner.</item>
    /// <item>State: every <see cref="IMigratable"/> on this object and its children (see there).</item>
    /// <item>Owner: an object a player owned (held item...) is given back to them when they reconnect.</item>
    /// </list>
    /// Not for player avatars — <see cref="PlayerSpawner"/> respawns those. Nested NetworkObjects aren't supported.
    /// Without this component a scene object comes back as baked / generated, a spawned object doesn't come back.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkMigratable : NetworkBehaviour
    {
        private IMigratable[] _states = Array.Empty<IMigratable>();
        private byte[] _lastSent;   // Host: last state sent to clients.
        private byte[] _lastState;  // Client: last state received from the host.
        private float _nextWrite;
        private NetworkSession _session;

        /// <summary> Scene objects: where in the scene it sits — the same on every machine that loads the scene. </summary>
        internal string SceneKey { get; private set; } = "";
        internal byte[] LastState => _lastState;

        private void Awake()
        {
            _session = NetworkSession.Instance;
            _states = GetComponentsInChildren<IMigratable>(true);
            SceneKey = BuildSceneKey(transform); // Before anything gets re-parented at runtime.
        }

        public override void OnStartServer()
        {
            // Spread the writes of many objects over the interval.
            _nextWrite = Time.unscaledTime + UnityEngine.Random.value * _session.MigrationStateInterval;
        }

        public override void OnStopClient()
        {
            // The client side goes down: either the connection is lost (ClientManager already stopped — FishNet tears
            // objects down right after) or the host despawned this object while we're connected.
            if (_session.State != SessionState.Connected) return;

            if (!NetworkManager.ClientManager.Started)
                _session.World.Capture(this);
            else if (NetworkObject.IsSceneObject)
                _session.World.MarkRemoved(this);
        }

        private void Update()
        {
            if (!IsServerInitialized || _states.Length == 0 || _session.HostMigration == HostMigrationMode.Off) return;
            if (Time.unscaledTime < _nextWrite) return;
            _nextWrite = Time.unscaledTime + _session.MigrationStateInterval;

            var state = WriteState();
            if (_lastSent != null && state.AsSpan().SequenceEqual(_lastSent)) return;
            _lastSent = state;
            ObserversState(state);
        }

        /// <summary> Host: the latest state now (also sent when it changes). </summary>
        private byte[] WriteState()
        {
            var writer = WriterPool.Retrieve(NetworkManager);
            var part = WriterPool.Retrieve(NetworkManager);
            foreach (var component in _states)
            {
                part.Clear();
                component.WriteMigrationState(part);
                writer.WriteArraySegmentAndSize(part.GetArraySegment());
            }

            byte[] bytes = writer.GetArraySegment().ToArray();
            part.Store();
            writer.Store();
            return bytes;
        }

        // Buffered: a player joining later gets the latest state too.
        [ObserversRpc(BufferLast = true, ExcludeServer = true)]
        private void ObserversState(byte[] state) => _lastState = state;

        /// <summary> New host: put the state captured before the migration back. </summary>
        internal void ReadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;

            var reader = ReaderPool.Retrieve(state, NetworkManager);
            try
            {
                foreach (var component in _states)
                {
                    if (reader.Remaining <= 0) break; // Captured with an older layout of this object.
                    var part = ReaderPool.Retrieve(reader.ReadArraySegmentAndSize(), NetworkManager);
                    component.ReadMigrationState(part);
                    part.Store();
                }
            }
            finally
            {
                reader.Store();
            }

            _lastSent = null; // Send the restored state on the next write.
        }

        private static string BuildSceneKey(Transform transform)
        {
            var key = new StringBuilder();
            for (var t = transform; t != null; t = t.parent)
                key.Insert(0, $"/{t.name}[{t.GetSiblingIndex()}]");
            return key.ToString();
        }
    }
}
