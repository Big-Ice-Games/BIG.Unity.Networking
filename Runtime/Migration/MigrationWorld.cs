using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace BIG.Unity.Networking
{
    /// <summary> One <see cref="NetworkMigratable"/> as a client saw it the moment the host was lost. </summary>
    internal sealed class MigratedObject
    {
        public string Scene;
        public bool IsSceneObject;
        public string SceneKey;
        public ushort PrefabId;
        public ushort CollectionId;
        public Vector3 Position;
        public Quaternion Rotation;
        public PlayerId Owner;
        public byte[] State;
    }

    /// <summary> The world a successor rebuilds: objects as last seen + scene objects that were gone already. </summary>
    internal sealed class WorldSnapshot
    {
        public readonly List<MigratedObject> Objects = new List<MigratedObject>();
        public readonly HashSet<string> RemovedSceneObjects = new HashSet<string>(); // Scene + "|" + SceneKey.

        /// <summary> Scenes loaded when the host was lost — they come back from the migration, not fresh. </summary>
        public readonly HashSet<string> Scenes = new HashSet<string>();

        public static string Key(string scene, string sceneKey) => scene + "|" + sceneKey;
    }

    /// <summary>
    /// Host migration of the WORLD (everything with <see cref="NetworkMigratable"/>), owned by <see cref="NetworkSession"/>.
    /// Client: collects objects as they're torn down when the host connection drops, and scene objects the host
    /// despawned. New host: restores each scene once the server has it loaded again.
    /// </summary>
    internal sealed class MigrationWorld
    {
        private readonly NetworkSession _session;

        // Client.
        private WorldSnapshot _collecting = new WorldSnapshot();

        // New host.
        private WorldSnapshot _restoring;
        private readonly List<string> _scenesToRestore = new List<string>();
        private readonly HashSet<string> _restoredScenes = new HashSet<string>(); // A later match in the same scene starts fresh.
        private readonly Dictionary<string, int> _waitedFrames = new Dictionary<string, int>();
        private const int MAX_WAIT_FRAMES = 120;
        private readonly Dictionary<PlayerId, List<NetworkObject>> _pendingOwners = new Dictionary<PlayerId, List<NetworkObject>>();

        public MigrationWorld(NetworkSession session) => _session = session;

        #region Client

        public void Capture(NetworkMigratable migratable)
        {
            var networkObject = migratable.NetworkObject;
            if (migratable.GetComponent<NetworkPlayer>() != null) return; // Avatars are respawned by PlayerSpawner.

            var captured = new MigratedObject
            {
                Scene = migratable.gameObject.scene.name,
                IsSceneObject = networkObject.IsSceneObject,
                SceneKey = migratable.SceneKey,
                PrefabId = networkObject.PrefabId,
                CollectionId = networkObject.SpawnableCollectionId,
                Owner = _session.Players.PlayerIdOfClient(networkObject.OwnerId),
                State = migratable.LastState
            };
            migratable.transform.GetPositionAndRotation(out captured.Position, out captured.Rotation);
            _collecting.Objects.Add(captured);
        }

        public void MarkRemoved(NetworkMigratable migratable) =>
            _collecting.RemovedSceneObjects.Add(WorldSnapshot.Key(migratable.gameObject.scene.name, migratable.SceneKey));

        /// <summary> A scene went away while connected (match ended) — its removals no longer mean anything. </summary>
        public void ForgetScene(string scene) =>
            _collecting.RemovedSceneObjects.RemoveWhere(key => key.StartsWith(scene + "|"));

        /// <summary> Migration starts: everything collected so far is the world to hand over. </summary>
        public WorldSnapshot TakeSnapshot()
        {
            var snapshot = _collecting;
            for (int i = 0; i < UnitySceneManager.sceneCount; i++)
                snapshot.Scenes.Add(UnitySceneManager.GetSceneAt(i).name);
            _collecting = new WorldSnapshot();
            return snapshot;
        }

        #endregion

        #region New host

        /// <summary> This machine hosts after a migration: rebuild scenes loaded now, the rest when the server loads them. </summary>
        public void BeginRestore(WorldSnapshot snapshot)
        {
            _restoring = snapshot;
            _restoredScenes.Clear();
            _waitedFrames.Clear();
            _scenesToRestore.Clear();
            _session.Log($"[Network] Host migration: restoring {snapshot.Objects.Count} object(s), " +
                         $"{snapshot.RemovedSceneObjects.Count} removed scene object(s).");
            for (int i = 0; i < UnitySceneManager.sceneCount; i++)
                _scenesToRestore.Add(UnitySceneManager.GetSceneAt(i).name);
        }

        public bool IsRestoring(string scene) => _restoring != null && _restoring.Scenes.Contains(scene) && !_restoredScenes.Contains(scene);

        public void OnServerSceneLoaded(string scene)
        {
            if (_restoring != null && !_restoredScenes.Contains(scene)) _scenesToRestore.Add(scene);
        }

        /// <summary> Every frame on the host. A scene is restored once all its scene objects are spawned by the server. </summary>
        public void Tick()
        {
            if (_restoring == null || _scenesToRestore.Count == 0) return;

            for (int i = _scenesToRestore.Count - 1; i >= 0; i--)
            {
                if (TryRestoreScene(_scenesToRestore[i]))
                    _scenesToRestore.RemoveAt(i);
            }
        }

        private bool TryRestoreScene(string sceneName)
        {
            var scene = UnitySceneManager.GetSceneByName(sceneName);
            if (!scene.isLoaded || _restoredScenes.Contains(sceneName)) return true; // Gone again / done already.

            var manager = _session.NetworkManager;

            // Scene objects: wait until the server spawned them all (one frame after the load, usually).
            var sceneObjects = new List<NetworkMigratable>();
            foreach (var root in scene.GetRootGameObjects())
                sceneObjects.AddRange(root.GetComponentsInChildren<NetworkMigratable>(true));
            // (Not forever: one kept inactive on purpose must not block the scene.)
            _waitedFrames.TryGetValue(sceneName, out int waited);
            _waitedFrames[sceneName] = waited + 1;
            if (waited < MAX_WAIT_FRAMES)
            {
                foreach (var migratable in sceneObjects)
                    if (migratable.NetworkObject.IsSceneObject && !migratable.NetworkObject.IsSpawned) return false;
            }

            int restored = 0;
            var byKey = new Dictionary<string, MigratedObject>();
            foreach (var captured in _restoring.Objects)
                if (captured.IsSceneObject && captured.Scene == sceneName) byKey[captured.SceneKey] = captured;

            foreach (var migratable in sceneObjects)
            {
                var networkObject = migratable.NetworkObject;
                if (!networkObject.IsSceneObject || !networkObject.IsSpawned) continue;

                if (_restoring.RemovedSceneObjects.Contains(WorldSnapshot.Key(sceneName, migratable.SceneKey)))
                {
                    manager.ServerManager.Despawn(networkObject);
                    restored++;
                }
                else if (byKey.TryGetValue(migratable.SceneKey, out var captured))
                {
                    Apply(migratable, captured);
                    restored++;
                }
            }

            // Spawned objects of this scene: from their prefab again.
            foreach (var captured in _restoring.Objects)
            {
                if (captured.IsSceneObject || captured.Scene != sceneName) continue;
                if (captured.CollectionId != 0)
                {
                    _session.Log($"[Network] Host migration: prefab {captured.PrefabId} is in spawnable collection {captured.CollectionId} — only collection 0 is restored.", LogLevel.Warning);
                    continue;
                }

                var prefab = manager.GetPrefab(captured.PrefabId, asServer: true);
                var instance = Object.Instantiate(prefab, captured.Position, captured.Rotation);
                manager.ServerManager.Spawn(instance, null, scene);
                Apply(instance.GetComponent<NetworkMigratable>(), captured);
                restored++;
            }

            Physics.SyncTransforms(); // Colliders and bodies at the restored poses before the next step.
            _restoredScenes.Add(sceneName);
            _waitedFrames.Remove(sceneName);
            if (restored > 0) _session.Log($"[Network] Host migration: scene '{sceneName}' — {restored} object(s) restored.");
            return true;
        }

        private void Apply(NetworkMigratable migratable, MigratedObject captured)
        {
            migratable.transform.SetPositionAndRotation(captured.Position, captured.Rotation);
            // A Rigidbody keeps its own pose (from the scene load) and would write it back on the next physics step.
            if (migratable.TryGetComponent<Rigidbody>(out var body))
            {
                body.position = captured.Position;
                body.rotation = captured.Rotation;
                if (!body.isKinematic)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
            migratable.ReadState(captured.State);

            if (!captured.Owner.IsValid) return;
            if (_session.Players.TryGetIdentity(captured.Owner, out var identity))
            {
                migratable.NetworkObject.GiveOwnership(identity.Connection);
                return;
            }

            if (!_pendingOwners.TryGetValue(captured.Owner, out var list))
                _pendingOwners[captured.Owner] = list = new List<NetworkObject>();
            list.Add(migratable.NetworkObject);
        }

        /// <summary> A player is back on the new host — hand them what they owned. </summary>
        public void OnServerPlayerIdentified(NetworkPlayers.Identity identity)
        {
            if (!_pendingOwners.TryGetValue(identity.PlayerId, out var owned)) return;
            _pendingOwners.Remove(identity.PlayerId);
            foreach (var networkObject in owned)
                if (networkObject != null && networkObject.IsSpawned)
                    networkObject.GiveOwnership(identity.Connection);
        }

        #endregion

        public void Clear()
        {
            _collecting = new WorldSnapshot();
            _restoring = null;
            _scenesToRestore.Clear();
            _restoredScenes.Clear();
            _waitedFrames.Clear();
            _pendingOwners.Clear();
        }
    }
}
