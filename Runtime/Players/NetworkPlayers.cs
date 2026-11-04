using System;
using System.Collections.Generic;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Transporting;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary> Client → server handshake right after FishNet authentication. </summary>
    public struct PlayerHelloBroadcast : IBroadcast
    {
        public string PlayerId;
        public string DisplayName;
        public string Version;
    }

    /// <summary>
    /// Who is who. Owned by <see cref="NetworkSession"/> (<c>NetworkSession.Instance.Players</c>).
    /// <list type="bullet">
    /// <item>Every machine: registry of spawned <see cref="NetworkPlayer"/>s by <see cref="PlayerId"/> + <see cref="Local"/>.</item>
    /// <item>Server: connection ↔ identity map built from the hello handshake. The identity comes from the
    /// transport when it can vouch for it (Steam), otherwise from the client's claim (LAN).</item>
    /// </list>
    /// </summary>
    public sealed class NetworkPlayers : IDisposable
    {
        public readonly struct Identity
        {
            public Identity(NetworkConnection connection, PlayerId playerId, string displayName)
            {
                Connection = connection;
                PlayerId = playerId;
                DisplayName = displayName;
            }

            public readonly NetworkConnection Connection;
            public readonly PlayerId PlayerId;
            public readonly string DisplayName;
        }

        private readonly NetworkSession _session;
        private readonly Dictionary<PlayerId, NetworkPlayer> _players = new Dictionary<PlayerId, NetworkPlayer>();
        private readonly Dictionary<int, Identity> _identitiesByConnection = new Dictionary<int, Identity>();
        private readonly Dictionary<PlayerId, Identity> _identitiesByPlayer = new Dictionary<PlayerId, Identity>();

        /// <summary> Spawned players on this machine (host included). </summary>
        public IReadOnlyCollection<NetworkPlayer> All => _players.Values;
        public int Count => _players.Count;
        /// <summary> This machine's player object, null until spawned. </summary>
        public NetworkPlayer Local { get; private set; }

        /// <summary> Server: identified connections (spawned or not). </summary>
        public IReadOnlyCollection<Identity> ServerIdentities => _identitiesByConnection.Values;

        /// <summary> Server: a connection completed the handshake — spawners listen to this. </summary>
        public event Action<Identity> ServerPlayerIdentified;
        /// <summary> Server: an identified connection dropped. </summary>
        public event Action<Identity> ServerPlayerDisconnected;

        internal NetworkPlayers(NetworkSession session)
        {
            _session = session;
            var nm = session.NetworkManager;
            nm.ServerManager.RegisterBroadcast<PlayerHelloBroadcast>(OnServerHello);
            nm.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            nm.ServerManager.OnServerConnectionState += OnServerConnectionState;
            nm.ClientManager.OnAuthenticated += OnClientAuthenticated;
            nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        public bool TryGet(PlayerId playerId, out NetworkPlayer player) => _players.TryGetValue(playerId, out player);

        public bool TryGetIdentity(NetworkConnection connection, out Identity identity) =>
            _identitiesByConnection.TryGetValue(connection.ClientId, out identity);

        public bool TryGetIdentity(PlayerId playerId, out Identity identity) =>
            _identitiesByPlayer.TryGetValue(playerId, out identity);

        #region NetworkPlayer registry (every machine)

        internal void Register(NetworkPlayer player)
        {
            if (!player.Id.IsValid) return; // Id not synced yet — NetworkPlayer registers again on change.

            bool isNew = !_players.TryGetValue(player.Id, out var existing) || existing != player;
            _players[player.Id] = player;
            if (player.IsOwner) Local = player;

            if (isNew)
                Events.Raise(new NetworkPlayerJoined(player));
        }

        internal void Unregister(NetworkPlayer player) => Unregister(player.Id, player);

        /// <summary> Unregister under a specific id — used when the synced id changes. </summary>
        internal void Unregister(string previousId, NetworkPlayer player) => Unregister(new PlayerId(previousId), player);

        private void Unregister(PlayerId playerId, NetworkPlayer player)
        {
            if (!playerId.IsValid || !_players.TryGetValue(playerId, out var existing) || existing != player) return;

            _players.Remove(playerId);
            if (Local == player) Local = null;
            Events.Raise(new NetworkPlayerLeft(playerId));
        }

        #endregion

        #region Handshake

        private void OnClientAuthenticated()
        {
            var platform = _session.Platform;
            _session.NetworkManager.ClientManager.Broadcast(new PlayerHelloBroadcast
            {
                PlayerId = platform.LocalPlayer.Value,
                DisplayName = platform.LocalDisplayName,
                Version = NetworkVersion.Current
            });
        }

        private void OnServerHello(NetworkConnection connection, PlayerHelloBroadcast hello, Channel channel)
        {
            if (hello.Version != NetworkVersion.Current)
            {
                connection.Log($"[Network] Kicking client {connection.ClientId}: version {hello.Version}, host runs {NetworkVersion.Current}.", LogLevel.Warning);
                connection.Disconnect(false);
                return;
            }

            if (_identitiesByConnection.ContainsKey(connection.ClientId)) return; // Repeated hello.

            var playerId = _session.Provider.TryGetVerifiedPlayerId(connection, out var verified) ? verified : new PlayerId(hello.PlayerId);
            if (!playerId.IsValid)
            {
                connection.Disconnect(false);
                return;
            }

            // Same player on a new connection (reconnect before the old one timed out) — the new one wins.
            if (_identitiesByPlayer.TryGetValue(playerId, out var stale))
            {
                RemoveIdentity(stale.Connection.ClientId);
                stale.Connection.Disconnect(true);
            }

            var identity = new Identity(connection, playerId, string.IsNullOrEmpty(hello.DisplayName) ? playerId.Value : hello.DisplayName);
            _identitiesByConnection[connection.ClientId] = identity;
            _identitiesByPlayer[playerId] = identity;
            _session.Sessions.SetMemberCount(_identitiesByConnection.Count);

            identity.Connection.Log($"[Network] {identity.DisplayName} ({playerId}) identified on connection {connection.ClientId}.");
            ServerPlayerIdentified?.Invoke(identity);
        }

        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped) return;

            if (_identitiesByConnection.TryGetValue(connection.ClientId, out var identity))
            {
                RemoveIdentity(connection.ClientId);
                _session.Sessions.SetMemberCount(_identitiesByConnection.Count);
                ServerPlayerDisconnected?.Invoke(identity);
            }
        }

        private void RemoveIdentity(int clientId)
        {
            if (_identitiesByConnection.TryGetValue(clientId, out var identity))
            {
                _identitiesByConnection.Remove(clientId);
                if (_identitiesByPlayer.TryGetValue(identity.PlayerId, out var current) && current.Connection.ClientId == clientId)
                    _identitiesByPlayer.Remove(identity.PlayerId);
            }
        }

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                _identitiesByConnection.Clear();
                _identitiesByPlayer.Clear();
            }
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                // Despawn callbacks normally unregister everything; this catches objects destroyed without them.
                _players.Clear();
                Local = null;
            }
        }

        #endregion

        public void Dispose()
        {
            var nm = _session.NetworkManager;
            if (nm == null) return; // Destroyed together with the session.

            nm.ServerManager.UnregisterBroadcast<PlayerHelloBroadcast>(OnServerHello);
            nm.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            nm.ServerManager.OnServerConnectionState -= OnServerConnectionState;
            nm.ClientManager.OnAuthenticated -= OnClientAuthenticated;
            nm.ClientManager.OnClientConnectionState -= OnClientConnectionState;
        }
    }
}
