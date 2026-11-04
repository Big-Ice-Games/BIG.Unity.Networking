using System;
using System.Collections.Generic;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// The ONE owner of the connection flow: rooms + FishNet server/client start and stop.
    /// Put it on the NetworkManager object (it lives as long as the NetworkManager).
    /// <code>
    /// Offline ─Host()──► CreatingRoom ─► StartingHost ─► Hosting
    ///    └────Join()──► JoiningRoom ──► Connecting ───► Connected
    /// any ─Leave() / failure / host lost──► Offline (+ DisconnectReason)
    /// </code>
    /// Every attempt gets a token — callbacks and transport events of an older attempt are ignored,
    /// so a late "client stopped" can never tear down the next session.
    /// Game code listens to <see cref="SessionStateChanged"/> (BIG event) or <see cref="StateChanged"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(short.MinValue + 1)] // Right after FishNet's NetworkManager, before any game script.
    public sealed class NetworkSession : MonoBehaviour
    {
        public static NetworkSession Instance { get; private set; }

        [SerializeField] private NetworkManager _networkManager;
        [SerializeField, Tooltip("Provider used in builds (and in the editor when no editor provider is set).")]
        private NetworkProviderAsset _provider;
        [SerializeField, Tooltip("Optional provider for play mode in the editor, e.g. LAN while builds use Steam.")]
        private NetworkProviderAsset _editorProvider;
        [SerializeField, Tooltip("Seconds to wait for the host to start / the client to connect.")]
        private float _connectTimeout = 15f;

        private INetworkProvider _activeProvider;
        private int _attempt;
        private float _deadline;
        private bool _stopping;

        public SessionState State { get; private set; } = SessionState.Offline;
        public DisconnectReason LastDisconnectReason { get; private set; }
        /// <summary> Room error / message of the last failure, for UI. </summary>
        public string LastError { get; private set; } = "";

        public NetworkManager NetworkManager => _networkManager;
        public INetworkProvider Provider => _activeProvider;
        public IPlatformService Platform => _activeProvider.Platform;
        public ISessionService Sessions => _activeProvider.Sessions;
        public NetworkPlayers Players { get; private set; }

        public bool IsHost => State == SessionState.Hosting;
        public bool IsInGame => State == SessionState.Hosting || State == SessionState.Connected;
        public bool IsBusy => State != SessionState.Offline && !IsInGame;

        /// <summary> C# twin of the <see cref="SessionStateChanged"/> BIG event. </summary>
        public event Action<SessionStateChanged> StateChanged;

        #region Public API

        /// <summary> Create a room and host the game in it. </summary>
        public void Host(SessionSettings settings)
        {
            if (!CanStart(nameof(Host))) return;

            int attempt = Begin(SessionState.CreatingRoom);
            Sessions.Create(settings, result =>
            {
                if (attempt != _attempt) return;

                if (!result.Success)
                {
                    Fail(DisconnectReason.RoomError, $"{result.Error} {result.Message}");
                    return;
                }

                Sessions.SetData(SessionInfo.VERSION_KEY, NetworkVersion.Current);
                _activeProvider.PrepareHost(settings);
                SetState(SessionState.StartingHost);
                _deadline = Time.unscaledTime + _connectTimeout;

                if (!_networkManager.ServerManager.StartConnection())
                    Fail(DisconnectReason.ConnectionFailed, "Server failed to start.");
                // Local client starts when the server reports Started (OnServerConnectionState).
            });
        }

        /// <summary> Join a room (from <see cref="Search"/> results, an invite, or a typed address). </summary>
        public void Join(SessionAddress address)
        {
            if (!CanStart(nameof(Join))) return;

            int attempt = Begin(SessionState.JoiningRoom);
            Sessions.Join(address, result =>
            {
                if (attempt != _attempt) return;

                if (!result.Success)
                {
                    Fail(result.Error == SessionError.VersionMismatch ? DisconnectReason.VersionMismatch : DisconnectReason.RoomError,
                        $"{result.Error} {result.Message}");
                    return;
                }

                string version = result.Info.Version;
                if (!string.IsNullOrEmpty(version) && version != NetworkVersion.Current)
                {
                    Fail(DisconnectReason.VersionMismatch, $"Host runs {version}, you run {NetworkVersion.Current}.");
                    return;
                }

                string hostAddress = result.Info.HostAddress;
                if (string.IsNullOrEmpty(hostAddress))
                {
                    Fail(DisconnectReason.RoomError, "Room has no host address.");
                    return;
                }

                _activeProvider.PrepareClient(hostAddress);
                SetState(SessionState.Connecting);
                _deadline = Time.unscaledTime + _connectTimeout;

                if (!_networkManager.ClientManager.StartConnection())
                    Fail(DisconnectReason.ConnectionFailed, "Client failed to start.");
            });
        }

        public void Join(SessionInfo session) => Join(session.Address);

        /// <summary> Leave the room and stop everything. Safe in any state. </summary>
        public void Leave() => End(DisconnectReason.LocalLeft);

        /// <summary> Find joinable rooms (LAN discovery / Steam lobby list). </summary>
        public void Search(Action<IReadOnlyList<SessionInfo>> done) => Sessions.Search(done);

        #endregion

        #region Unity

        private void Awake()
        {
            if (Instance != null)
            {
                Debug.LogError("[Network] Second NetworkSession — only one may exist (keep it on the persistent NetworkManager).", this);
                Destroy(this);
                return;
            }

            var asset = Application.isEditor && _editorProvider != null ? _editorProvider : _provider;
            if (asset == null)
                throw new InvalidOperationException($"[Network] NetworkSession on '{name}' has no Provider asset assigned (e.g. Create > BIG > Networking > LAN Provider).");
            // FishNet aborts its own Awake on invalid setup (e.g. no SpawnablePrefabs) and leaves its managers null.
            if (_networkManager.TransportManager == null)
                throw new InvalidOperationException($"[Network] NetworkManager on '{name}' failed to initialize — fix the FishNet errors above (usually SpawnablePrefabs not assigned).");

            Instance = this;
            _activeProvider = asset.CreateProvider();
            _activeProvider.Bind(_networkManager);

            Players = new NetworkPlayers(this);

            _networkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            _networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
            _networkManager.ClientManager.OnAuthenticated += OnClientAuthenticated;
            Sessions.JoinRequested += OnJoinRequested;

            this.Log($"[Network] Session ready — provider {Platform.Name}, player {Platform.LocalDisplayName} ({Platform.LocalPlayer}).");
        }

        private void Update()
        {
            _activeProvider.Tick();

            if ((State == SessionState.StartingHost || State == SessionState.Connecting) && Time.unscaledTime > _deadline)
                Fail(DisconnectReason.Timeout, "Timed out.");
        }

        private void OnDestroy()
        {
            if (Instance != this) return;

            // On application quit the NetworkManager may already be gone — FishNet stops itself then.
            if (_networkManager != null)
            {
                End(DisconnectReason.LocalLeft);
                _networkManager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
                _networkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
                _networkManager.ClientManager.OnAuthenticated -= OnClientAuthenticated;
            }
            else
            {
                Sessions.Leave();
            }

            Sessions.JoinRequested -= OnJoinRequested;

            Players.Dispose();
            _activeProvider.Dispose();
            Instance = null;
        }

        private void Reset()
        {
            _networkManager = GetComponent<NetworkManager>();
        }

        #endregion

        #region Transport events

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (_stopping) return;

            if (args.ConnectionState == LocalConnectionState.Started && State == SessionState.StartingHost)
            {
                // Server up → connect this machine's own client (host = server + client).
                if (!_networkManager.ClientManager.StartConnection())
                    Fail(DisconnectReason.ConnectionFailed, "Local client failed to start.");
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped &&
                     (State == SessionState.StartingHost || State == SessionState.Hosting))
            {
                Fail(DisconnectReason.ConnectionFailed, "Server stopped.");
            }
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (_stopping || args.ConnectionState != LocalConnectionState.Stopped) return;

            switch (State)
            {
                case SessionState.Connected: End(DisconnectReason.HostLeft); break;
                case SessionState.Connecting: Fail(DisconnectReason.ConnectionFailed, "Could not connect to the host."); break;
                case SessionState.StartingHost:
                case SessionState.Hosting: Fail(DisconnectReason.ConnectionFailed, "Local client stopped."); break;
            }
        }

        private void OnClientAuthenticated()
        {
            if (State == SessionState.StartingHost)
            {
                SetState(SessionState.Hosting);
                Sessions.SetData(SessionInfo.HOST_ADDRESS_KEY, _activeProvider.GetHostAddress());
            }
            else if (State == SessionState.Connecting)
            {
                SetState(SessionState.Connected);
            }
        }

        private void OnJoinRequested(SessionAddress address)
        {
            if (Sessions.Current != null && Sessions.Current.Address.Equals(address)) return;

            if (State != SessionState.Offline)
                End(DisconnectReason.LocalLeft);

            Join(address);
        }

        #endregion

        #region Flow

        private bool CanStart(string operation)
        {
            if (State == SessionState.Offline) return true;
            this.Log($"[Network] {operation} ignored — session is {State}. Call Leave() first.", LogLevel.Warning);
            return false;
        }

        private int Begin(SessionState state)
        {
            LastError = "";
            LastDisconnectReason = DisconnectReason.None;
            int attempt = ++_attempt;
            SetState(state);
            return attempt;
        }

        private void Fail(DisconnectReason reason, string error)
        {
            LastError = error;
            this.Log($"[Network] {State} failed: {reason} — {error}", LogLevel.Warning);
            End(reason);
        }

        /// <summary> Stops transport and room, invalidates the current attempt. Idempotent. </summary>
        private void End(DisconnectReason reason)
        {
            _attempt++;
            if (State == SessionState.Offline) return;

            _stopping = true;
            try
            {
                if (_networkManager.ClientManager.Started)
                    _networkManager.ClientManager.StopConnection();
                if (_networkManager.ServerManager.Started)
                    _networkManager.ServerManager.StopConnection(true);
                Sessions.Leave();
            }
            finally
            {
                _stopping = false;
            }

            LastDisconnectReason = reason;
            SetState(SessionState.Offline, reason);
        }

        private void SetState(SessionState state, DisconnectReason reason = DisconnectReason.None)
        {
            if (state == State) return;

            var change = new SessionStateChanged(State, state, reason);
            State = state;
            this.Log($"[Network] {change.Previous} → {change.Current}{(reason != DisconnectReason.None ? $" ({reason})" : "")}");

            StateChanged?.Invoke(change);
            Events.Raise(change);
        }

        #endregion
    }
}
