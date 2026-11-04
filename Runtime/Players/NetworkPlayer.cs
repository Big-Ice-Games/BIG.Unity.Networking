using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Put on the player prefab (next to its NetworkObject). Carries the stable <see cref="PlayerId"/> and
    /// display name, assigned by the SERVER (<see cref="PlayerSpawner"/>) — never by the client itself.
    /// Registered in <see cref="NetworkPlayers"/> on every machine as soon as the id is known.
    /// </summary>
    public sealed class NetworkPlayer : NetworkBehaviour
    {
        private readonly SyncVar<string> _playerId = new SyncVar<string>();
        private readonly SyncVar<string> _displayName = new SyncVar<string>();

        // Set by the spawner before Spawn(), copied into SyncVars in OnStartServer.
        private PlayerId _pendingId;
        private string _pendingName;

        public PlayerId Id => new PlayerId(_playerId.Value);
        public string DisplayName => _displayName.Value;
        /// <summary> This machine's own player. </summary>
        public bool IsLocal => IsOwner;

        private void Awake()
        {
            _playerId.OnChange += OnIdChanged;
        }

        internal void SetIdentity(PlayerId playerId, string displayName)
        {
            _pendingId = playerId;
            _pendingName = displayName;

            if (IsServerInitialized)
                ApplyPendingIdentity();
        }

        public override void OnStartServer()
        {
            ApplyPendingIdentity();
        }

        public override void OnStartClient()
        {
            NetworkSession.Instance.Players.Register(this);
        }

        public override void OnStopClient()
        {
            // Application quit: the session may be destroyed before its players.
            if (NetworkSession.Instance != null)
                NetworkSession.Instance.Players.Unregister(this);
        }

        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            // Ownership handed back after a reconnect — refresh Local.
            if (IsClientInitialized)
                NetworkSession.Instance.Players.Register(this);
        }

        private void ApplyPendingIdentity()
        {
            if (!_pendingId.IsValid) return;
            _playerId.Value = _pendingId.Value;
            _displayName.Value = _pendingName;
        }

        private void OnIdChanged(string previous, string next, bool asServer)
        {
            if (asServer || !IsClientInitialized) return;

            if (!string.IsNullOrEmpty(previous))
                NetworkSession.Instance.Players.Unregister(previous, this);

            NetworkSession.Instance.Players.Register(this);
        }
    }
}
