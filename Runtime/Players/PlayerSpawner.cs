using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Server-side player spawner — drop it into a networked scene (hub, mission) with the player prefab.
    /// <list type="bullet">
    /// <item>Spawns once a connection is BOTH identified (<see cref="NetworkPlayers"/> handshake) and has loaded the start scenes.</item>
    /// <item>Idempotent per <see cref="PlayerId"/>: a player gets exactly one object; a reconnecting player gets
    /// ownership of their existing object back instead of a duplicate.</item>
    /// <item>Owner comes from the server's connection, never from a client-supplied argument.</item>
    /// </list>
    /// Spawned objects belong to this spawner's scene.
    /// </summary>
    public sealed class PlayerSpawner : MonoBehaviour
    {
        [SerializeField, Tooltip("Prefab with NetworkObject + NetworkPlayer.")]
        private NetworkObject _playerPrefab;
        [SerializeField, Tooltip("Round-robin spawn points. Empty = this transform.")]
        private Transform[] _spawnPoints = new Transform[0];
        [SerializeField, Tooltip("Despawn a player's object when they disconnect. Off = keep it for their reconnect — " +
                                 "then also tick 'Prevent Despawn On Disconnect' on the prefab's NetworkObject, otherwise FishNet despawns it anyway.")]
        private bool _despawnOnDisconnect = true;

        private readonly Dictionary<PlayerId, NetworkObject> _spawned = new Dictionary<PlayerId, NetworkObject>();
        private NetworkSession _session;
        private NetworkManager _networkManager;
        private int _nextSpawnPoint;

        /// <summary> Server: the object spawned for a player, if any. </summary>
        public bool TryGetSpawned(PlayerId playerId, out NetworkObject networkObject) =>
            _spawned.TryGetValue(playerId, out networkObject) && networkObject != null;

        private void OnEnable()
        {
            _session = NetworkSession.Instance;
            _networkManager = _session.NetworkManager;

            _session.Players.ServerPlayerIdentified += OnServerPlayerIdentified;
            _session.Players.ServerPlayerDisconnected += OnServerPlayerDisconnected;
            _networkManager.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;

            // Scene loaded after players were already in (e.g. hub → mission): spawn everyone present.
            if (_networkManager.IsServerStarted)
            {
                foreach (var identity in _session.Players.ServerIdentities)
                    TrySpawn(identity);
            }
        }

        private void OnDisable()
        {
            _session.Players.ServerPlayerIdentified -= OnServerPlayerIdentified;
            _session.Players.ServerPlayerDisconnected -= OnServerPlayerDisconnected;
            _networkManager.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
        }

        private void OnServerPlayerIdentified(NetworkPlayers.Identity identity) => TrySpawn(identity);

        private void OnClientLoadedStartScenes(NetworkConnection connection, bool asServer)
        {
            if (!asServer) return;
            if (_session.Players.TryGetIdentity(connection, out var identity))
                TrySpawn(identity);
        }

        private void TrySpawn(NetworkPlayers.Identity identity)
        {
            var connection = identity.Connection;
            if (!connection.IsActive || !connection.LoadedStartScenes(true)) return;

            if (TryGetSpawned(identity.PlayerId, out var existing))
            {
                if (existing.Owner != connection)
                    existing.GiveOwnership(connection);
                return;
            }

            GetSpawnPose(out var position, out var rotation);
            var instance = Instantiate(_playerPrefab, position, rotation);
            instance.GetComponent<NetworkPlayer>().SetIdentity(identity.PlayerId, identity.DisplayName);

            _networkManager.ServerManager.Spawn(instance, connection, gameObject.scene);
            _spawned[identity.PlayerId] = instance;
        }

        private void OnServerPlayerDisconnected(NetworkPlayers.Identity identity)
        {
            if (!_despawnOnDisconnect) return;

            if (TryGetSpawned(identity.PlayerId, out var networkObject))
                _networkManager.ServerManager.Despawn(networkObject);

            _spawned.Remove(identity.PlayerId);
        }

        private void GetSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            var point = _spawnPoints.Length == 0 ? transform : _spawnPoints[_nextSpawnPoint++ % _spawnPoints.Length];
            position = point.position;
            rotation = point.rotation;
        }
    }
}
