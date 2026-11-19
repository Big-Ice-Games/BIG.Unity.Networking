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
    /// Connected ─host dropped──► Migrating ─► Hosting (successor) / Connected (others) / Offline (nobody made it)
    /// </code>
    /// Host migration (<see cref="HostMigrationMode"/>, needs the lobby): every client keeps a copy of the lobby; when the
    /// host drops, successors are tried in lobby order (lost host skipped). The one whose turn it is hosts the same room,
    /// restores the lobby and reloads the match from its seed; the others reconnect to the address it published in the
    /// lobby. Each successor gets <see cref="NetworkConfig.MigrationRoundTimeout"/> seconds before the next one in line takes over.
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
        [SerializeField, Tooltip("All settings: provider, timing, room defaults, lobby, host migration (Create > BIG > Networking > Network Config).")]
        private NetworkConfig _config;

        private const float MIGRATION_RETRY_DELAY = 0.75f;

        private HostMigrationMode? _hostMigrationOverride; // Set from code at runtime — the asset stays untouched.
        private string _hostPassword = ""; // Host: the room's password ("" = open).
        private string _joinPassword = ""; // Client: what we got in with — also the room's password if we take over hosting.

        private INetworkProvider _activeProvider;
        private int _attempt;
        private float _deadline;
        private bool _stopping;
        private DisconnectReason _remoteEndReason; // Sent by the host right before it disconnects us (kick).
        private QuickPlayRun _quickPlay;
        private bool _destroying;

        // Scenes the network loaded on this machine (match scenes...). Removed locally when the session ends — a player who
        // was refused right after connecting still got the running match's scene and must not keep it.
        private readonly HashSet<string> _networkScenes = new HashSet<string>();
        private readonly List<AsyncOperation> _unloading = new List<AsyncOperation>();

        // Host migration.
        private LobbySnapshot _lobbySnapshot;  // Client: the lobby as last seen.
        private LobbySnapshot _lobbyToRestore; // New host: picked up by the new NetworkLobby.
        private MigrationRun _migration;
        private bool _poseKnown, _restorePose;
        private string _poseScene;
        private Vector3 _posePosition;
        private Quaternion _poseRotation;

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
        public bool IsMigrating => State == SessionState.Migrating;

        public NetworkConfig Config => _config;

        /// <summary>
        /// From the config; settable from code at runtime (game options) without touching the asset.
        /// Every machine should use the same mode.
        /// </summary>
        public HostMigrationMode HostMigration
        {
            get => _hostMigrationOverride ?? _config.HostMigration;
            set => _hostMigrationOverride = value;
        }

        /// <summary> Host: seconds between checks of <see cref="IMigratable"/> state; changed state is sent to every client. </summary>
        public float MigrationStateInterval => _config.MigrationStateInterval;

        /// <summary> The room this machine is in has a password (host: it set one; client: it joined with one). </summary>
        public bool IsRoomLocked => IsHost ? _hostPassword.Length > 0 : _joinPassword.Length > 0;

        /// <summary> Host migration of the world (<see cref="NetworkMigratable"/>). </summary>
        internal MigrationWorld World { get; private set; }

        /// <summary>
        /// Host: this scene comes back from a host migration — its <see cref="NetworkMigratable"/> objects (AI, items...) are
        /// restored as they were. Host-side spawners check this before spawning their initial objects, or they'd double up.
        /// </summary>
        public bool IsRestoringAfterMigration(UnityEngine.SceneManagement.Scene scene) => World.IsRestoring(scene.name);

        /// <summary> Host migration: the successor being tried now, while <see cref="IsMigrating"/>. </summary>
        public PlayerId MigrationCandidate => _migration != null && _migration.Round < _migration.Candidates.Count
            ? _migration.Candidates[_migration.Round].Id
            : PlayerId.None;

        /// <summary> C# twin of the <see cref="SessionStateChanged"/> BIG event. </summary>
        public event Action<SessionStateChanged> StateChanged;
        /// <summary> C# twin of the <see cref="HostMigrated"/> BIG event. </summary>
        public event Action<HostMigrated> Migrated;

        #region Public API

        /// <summary> Host with the config's room defaults (name, max players, visibility, password). </summary>
        public void Host() => Host(_config.CreateRoomSettings(Platform));

        /// <summary> Create a room and host the game in it. Empty name = the config's room name template. </summary>
        public void Host(SessionSettings settings)
        {
            if (!CanStart(nameof(Host))) return;

            if (string.IsNullOrWhiteSpace(settings.Name))
                settings.Name = NetworkConfig.FormatRoomName(_config.RoomName, Platform);
            int attempt = Begin(SessionState.CreatingRoom);
            _hostPassword = settings.Password ?? "";
            Sessions.Create(settings, result =>
            {
                if (attempt != _attempt) return;

                if (!result.Success)
                {
                    Fail(DisconnectReason.RoomError, $"{result.Error} {result.Message}");
                    return;
                }

                Sessions.SetData(SessionInfo.VERSION_KEY, NetworkVersion.Current);
                Sessions.SetData(SessionInfo.LOCKED_KEY, _hostPassword.Length > 0 ? "1" : "0");
                _activeProvider.PrepareHost(settings);
                SetState(SessionState.StartingHost);
                _deadline = Time.unscaledTime + _config.ConnectTimeout;

                if (!_networkManager.ServerManager.StartConnection())
                    Fail(DisconnectReason.ConnectionFailed, "Server failed to start.");
                // Local client starts when the server reports Started (OnServerConnectionState).
            });
        }

        /// <summary>
        /// Join a room (from <see cref="Search"/> results, an invite, or a typed address). <paramref name="password"/> for
        /// locked rooms (<see cref="SessionInfo.IsLocked"/>) — a wrong one ends with <see cref="DisconnectReason.WrongPassword"/>.
        /// </summary>
        public void Join(SessionAddress address, string password = "")
        {
            if (!CanStart(nameof(Join))) return;

            int attempt = Begin(SessionState.JoiningRoom);
            _joinPassword = password ?? "";
            Sessions.Join(address, result =>
            {
                if (attempt != _attempt) return;

                if (!result.Success)
                {
                    Fail(result.Error == SessionError.VersionMismatch ? DisconnectReason.VersionMismatch : DisconnectReason.RoomError,
                        $"{result.Error} {result.Message}");
                    return;
                }

                if (result.Info.IsLocked && _joinPassword.Length == 0)
                {
                    Fail(DisconnectReason.WrongPassword, "This room needs a password.");
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
                _deadline = Time.unscaledTime + _config.ConnectTimeout;

                if (!_networkManager.ClientManager.StartConnection())
                    Fail(DisconnectReason.ConnectionFailed, "Client failed to start.");
            });
        }

        public void Join(SessionInfo session, string password = "") => Join(session.Address, password);

        /// <summary> Host: rename the room (room lists show it). </summary>
        public void SetRoomName(string roomName)
        {
            if (!IsHost || string.IsNullOrWhiteSpace(roomName)) return;
            Sessions.SetName(roomName.Trim());
        }

        /// <summary>
        /// Host: lock the room with a password, or open it ("" / null). Players already inside stay; they're told the new
        /// password so whoever takes over hosting after a migration keeps the room locked.
        /// </summary>
        public void SetRoomPassword(string password)
        {
            if (!IsHost) return;
            _hostPassword = password ?? "";
            Sessions.SetData(SessionInfo.LOCKED_KEY, _hostPassword.Length > 0 ? "1" : "0");
            _networkManager.ServerManager.Broadcast(new RoomPasswordBroadcast { Password = _hostPassword });
            this.Log($"[Network] Room {(_hostPassword.Length > 0 ? "locked with a password" : "opened (no password)")}.");
        }

        /// <summary> Server: a joining player's password is right (or the room has none). </summary>
        internal bool ServerAcceptsPassword(string password) => _hostPassword.Length == 0 || password == _hostPassword;

        /// <summary> Client: the password sent to the host in the hello. </summary>
        internal string JoinPassword => _joinPassword;

        private void OnRoomPasswordBroadcast(RoomPasswordBroadcast message, Channel channel) => _joinPassword = message.Password ?? "";

        /// <summary> Leave the room and stop everything. Safe in any state. </summary>
        public void Leave()
        {
            _quickPlay = null;
            End(DisconnectReason.LocalLeft);
        }

        /// <summary>
        /// Host: leave, but let the others carry on with a successor (host migration) — even in
        /// <see cref="HostMigrationMode.WhenHostDrops"/>. Same as <see cref="Leave"/> when migration is Off or not hosting.
        /// </summary>
        public void LeaveAndHandOver()
        {
            _quickPlay = null;
            End(DisconnectReason.LocalLeft, handOver: HostMigration != HostMigrationMode.Off);
        }

        /// <summary>
        /// "Play": join the fullest joinable public room of this version; if joining fails try the next one; with none
        /// left, host a new public room from <paramref name="hostSettings"/>.
        /// </summary>
        /// <summary> Quick play hosting the config's room defaults when there's no room to join. </summary>
        public void QuickPlay() => QuickPlay(_config.CreateRoomSettings(Platform));

        public void QuickPlay(SessionSettings hostSettings)
        {
            if (!CanStart(nameof(QuickPlay))) return;

            var run = new QuickPlayRun(hostSettings);
            _quickPlay = run;
            this.Log("[Network] Quick play: searching rooms...");

            Search(results =>
            {
                if (_quickPlay != run || State != SessionState.Offline) return; // Cancelled / something else started.

                foreach (var room in results)
                {
                    bool versionOk = string.IsNullOrEmpty(room.Version) || room.Version == NetworkVersion.Current;
                    if (room.IsOpenForNewPlayers && !room.IsLocked && versionOk)
                        run.Candidates.Add(room);
                }
                run.Candidates.Sort((a, b) => b.MemberCount.CompareTo(a.MemberCount)); // Fill rooms up first.
                QuickPlayNext(run);
            });
        }

        private void QuickPlayNext(QuickPlayRun run)
        {
            if (run.Candidates.Count > 0)
            {
                var room = run.Candidates[0];
                run.Candidates.RemoveAt(0);
                this.Log($"[Network] Quick play: joining '{room.Name}' ({room.MemberCount}/{room.MaxPlayers}).");
                Join(room);
                return;
            }

            _quickPlay = null;
            this.Log("[Network] Quick play: no room to join — hosting a public one.");
            run.HostSettings.Visibility = SessionVisibility.Public;
            run.HostSettings.Password = ""; // Quick play rooms are open to anyone.
            Host(run.HostSettings);
        }

        private sealed class QuickPlayRun
        {
            public readonly SessionSettings HostSettings;
            public readonly List<SessionInfo> Candidates = new List<SessionInfo>();
            public QuickPlayRun(SessionSettings hostSettings) => HostSettings = hostSettings;
        }

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

            if (_config == null)
                throw new InvalidOperationException($"[Network] NetworkSession on '{name}' has no Network Config (Create > BIG > Networking > Network Config).");
            var asset = _config.ActiveProvider;
            if (asset == null)
                throw new InvalidOperationException($"[Network] Network Config '{_config.name}' has no Provider (e.g. Create > BIG > Networking > LAN Provider).");
            // FishNet aborts its own Awake on invalid setup (e.g. no SpawnablePrefabs) and leaves its managers null.
            if (_networkManager.TransportManager == null)
                throw new InvalidOperationException($"[Network] NetworkManager on '{name}' failed to initialize — fix the FishNet errors above (usually SpawnablePrefabs not assigned).");

            Instance = this;
            _config.ApplyTiming(_networkManager); // Tick rate, physics mode — same on every machine.
            _config.Changed += OnConfigChanged;
            _activeProvider = asset.CreateProvider();
            _activeProvider.Bind(_networkManager);

            Players = new NetworkPlayers(this);
            World = new MigrationWorld(this);
            Players.ServerPlayerIdentified += World.OnServerPlayerIdentified;

            _networkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            _networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
            _networkManager.ClientManager.OnAuthenticated += OnClientAuthenticated;
            _networkManager.ClientManager.RegisterBroadcast<SessionEndBroadcast>(OnSessionEndBroadcast);
            _networkManager.ClientManager.RegisterBroadcast<RoomPasswordBroadcast>(OnRoomPasswordBroadcast);
            _networkManager.SceneManager.OnLoadEnd += OnNetworkSceneLoaded;
            _networkManager.SceneManager.OnUnloadEnd += OnNetworkSceneUnloaded;
            Sessions.JoinRequested += OnJoinRequested;
            DevTools.DevOverlay.CreateIfEnabled(_config, transform);

            this.Log($"[Network] Session ready — provider {Platform.Name}, player {Platform.LocalDisplayName} ({Platform.LocalPlayer}).");
        }

        /// <summary> The config asset was edited in play mode: re-apply what works live (the rest is read at the next Host / Join). </summary>
        private void OnConfigChanged(NetworkConfig config)
        {
            config.ApplyTiming(_networkManager); // (No log: dragging a slider fires this every frame.)

            if (config.DevOverlay == DevTools.DevOverlayMode.Off && DevTools.DevOverlay.Instance != null)
                Destroy(DevTools.DevOverlay.Instance.gameObject);
            else
                DevTools.DevOverlay.CreateIfEnabled(config, transform);
        }

        private void Update()
        {
            _activeProvider.Tick();
            if (IsHost) World.Tick();

            if ((State == SessionState.StartingHost || State == SessionState.Connecting) && Time.unscaledTime > _deadline)
                Fail(DisconnectReason.Timeout, "Timed out.");

            if (State == SessionState.Migrating)
                TickMigration();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            _destroying = true;

            // On application quit the NetworkManager may already be gone — FishNet stops itself then.
            if (_networkManager != null)
            {
                End(DisconnectReason.LocalLeft);
                _networkManager.SceneManager.OnLoadEnd -= OnNetworkSceneLoaded;
                _networkManager.SceneManager.OnUnloadEnd -= OnNetworkSceneUnloaded;
                _networkManager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
                _networkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
                _networkManager.ClientManager.OnAuthenticated -= OnClientAuthenticated;
                _networkManager.ClientManager.UnregisterBroadcast<SessionEndBroadcast>(OnSessionEndBroadcast);
                _networkManager.ClientManager.UnregisterBroadcast<RoomPasswordBroadcast>(OnRoomPasswordBroadcast);
            }
            else
            {
                Sessions.Leave();
            }

            Sessions.JoinRequested -= OnJoinRequested;
            _config.Changed -= OnConfigChanged;

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

            bool startingHost = State == SessionState.StartingHost || IsMigratingAsHost;

            if (args.ConnectionState == LocalConnectionState.Started && startingHost)
            {
                // Server up → connect this machine's own client (host = server + client).
                if (!_networkManager.ClientManager.StartConnection())
                    Fail(DisconnectReason.ConnectionFailed, "Local client failed to start.");
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped && (startingHost || State == SessionState.Hosting))
            {
                Fail(DisconnectReason.ConnectionFailed, "Server stopped.");
            }
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (_stopping || args.ConnectionState != LocalConnectionState.Stopped) return;

            switch (State)
            {
                case SessionState.Connected:
                    // A reason from the host = it ended the session on purpose (left, kicked us). None = it dropped.
                    if (_remoteEndReason != DisconnectReason.None) End(_remoteEndReason);
                    else if (!TryBeginMigration()) End(DisconnectReason.HostLeft);
                    break;
                case SessionState.Connecting: Fail(DisconnectReason.ConnectionFailed, "Could not connect to the host."); break;
                case SessionState.StartingHost:
                case SessionState.Hosting: Fail(DisconnectReason.ConnectionFailed, "Local client stopped."); break;
                case SessionState.Migrating:
                    if (_remoteEndReason != DisconnectReason.None) End(_remoteEndReason); // Successor refused us.
                    else if (_migration.LocalHost) Fail(DisconnectReason.ConnectionFailed, "Local client stopped (host migration).");
                    else _migration.RetryAt = Time.unscaledTime + MIGRATION_RETRY_DELAY; // Successor not up yet.
                    break;
            }
        }

        private void OnClientAuthenticated()
        {
            if (State == SessionState.StartingHost)
            {
                SetState(SessionState.Hosting);
                Sessions.SetData(SessionInfo.HOST_ADDRESS_KEY, _activeProvider.GetHostAddress());
                SpawnLobby();
            }
            else if (State == SessionState.Connecting)
            {
                _quickPlay = null;
                SetState(SessionState.Connected);
            }
            else if (State == SessionState.Migrating)
            {
                FinishMigration();
            }
        }

        private void SpawnLobby()
        {
            if (_config.LobbyPrefab == null) return;
            var lobby = Instantiate(_config.LobbyPrefab);
            lobby.SetIsGlobal(true); // Survives match scene loads.
            _networkManager.ServerManager.Spawn(lobby);
        }

        private void OnSessionEndBroadcast(SessionEndBroadcast message, Channel channel)
        {
            _remoteEndReason = (DisconnectReason)message.Reason;
        }

        private void OnNetworkSceneLoaded(FishNet.Managing.Scened.SceneLoadEndEventArgs args)
        {
            foreach (var scene in args.LoadedScenes)
            {
                _networkScenes.Add(scene.name);
                if (args.QueueData.AsServer) World.OnServerSceneLoaded(scene.name);
            }
        }

        private void OnNetworkSceneUnloaded(FishNet.Managing.Scened.SceneUnloadEndEventArgs args)
        {
            foreach (var scene in args.UnloadedScenesV2)
            {
                _networkScenes.Remove(scene.Name);
                World.ForgetScene(scene.Name);
            }
        }

        /// <summary> Session over: drop what the network loaded, back to the scene the session started in. </summary>
        private void UnloadNetworkScenes()
        {
            foreach (var sceneName in _networkScenes)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(sceneName);
                // Never the last loaded scene (Unity can't unload it — e.g. a game that loads matches with ReplaceScenes).
                if (scene.isLoaded && UnityEngine.SceneManagement.SceneManager.sceneCount > 1)
                {
                    var operation = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
                    if (operation != null) _unloading.Add(operation);
                }
            }
            _networkScenes.Clear();
        }

        private bool NetworkScenesUnloaded()
        {
            _unloading.RemoveAll(operation => operation.isDone);
            return _unloading.Count == 0;
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
            _remoteEndReason = DisconnectReason.None;
            int attempt = ++_attempt;
            SetState(state);
            return attempt;
        }

        private void Fail(DisconnectReason reason, string error)
        {
            // Quick play: a failed join (full, closed, other version, unreachable) moves on to the next room.
            var quickPlay = State == SessionState.JoiningRoom || State == SessionState.Connecting ? _quickPlay : null;

            LastError = error;
            this.Log($"[Network] {State} failed: {reason} — {error}", LogLevel.Warning);
            End(reason);

            if (quickPlay != null && quickPlay == _quickPlay)
                QuickPlayNext(quickPlay);
        }

        /// <summary>
        /// Stops transport and room, invalidates the current attempt. Idempotent.
        /// A host tells its clients it LEFT (so they don't migrate) unless it hands the room over.
        /// </summary>
        private void End(DisconnectReason reason, bool handOver = false)
        {
            _attempt++;
            _migration = null;
            _lobbySnapshot = null;
            _lobbyToRestore = null;
            _poseKnown = _restorePose = false;
            if (State == SessionState.Offline) return;

            _stopping = true;
            try
            {
                bool handsOver = handOver || HostMigration == HostMigrationMode.Always;
                if (State == SessionState.Hosting && !handsOver && _networkManager.ServerManager.Started)
                    _networkManager.ServerManager.Broadcast(new SessionEndBroadcast { Reason = (byte)DisconnectReason.HostLeft });
                // (Flushed before the disconnect: StopConnection(true) sends the disconnect messages and iterates outgoing.)

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

            World.Clear(); // After stopping: tearing down the client fills it, and this session is over.
            _hostPassword = _joinPassword = "";
            LastDisconnectReason = reason;
            SetState(SessionState.Offline, reason);
        }

        private void SetState(SessionState state, DisconnectReason reason = DisconnectReason.None)
        {
            if (state == State) return;

            var change = new SessionStateChanged(State, state, reason);
            State = state;
            this.Log($"[Network] {change.Previous} → {change.Current}{(reason != DisconnectReason.None ? $" ({reason})" : "")}");

            if (state == SessionState.Offline && !_destroying)
                UnloadNetworkScenes();

            StateChanged?.Invoke(change);
            Events.Raise(change);
        }

        #endregion

        #region Host migration

        private sealed class MigrationRun
        {
            public readonly LobbySnapshot Snapshot;
            public readonly PlayerId PreviousHost;
            /// <summary> Lobby order, lost host left out. This machine is always among them (it's the last resort). </summary>
            public readonly List<LobbyMember> Candidates = new List<LobbyMember>();
            public int Round;
            public bool RoundStarted;
            public float RoundDeadline;
            public float RetryAt;
            public bool LocalHost;
            public WorldSnapshot World;

            public MigrationRun(LobbySnapshot snapshot)
            {
                Snapshot = snapshot;
                PreviousHost = snapshot.Host;
            }
        }

        private bool IsMigratingAsHost => State == SessionState.Migrating && _migration != null && _migration.LocalHost;

        /// <summary> Client lost a host that didn't say goodbye: start migrating if the room can carry on. </summary>
        private bool TryBeginMigration()
        {
            if (HostMigration == HostMigrationMode.Off || _lobbySnapshot == null) return false;

            var run = new MigrationRun(_lobbySnapshot);
            var local = Platform.LocalPlayer;
            bool listed = false;
            foreach (var member in run.Snapshot.Members)
            {
                if (member.IsHost) continue;
                bool isLocal = member.Id == local;
                listed |= isLocal;
                if (isLocal || !string.IsNullOrEmpty(member.HostAddress))
                    run.Candidates.Add(member);
            }
            if (!listed) return false; // Joined a moment ago, the lobby doesn't know us yet.

            _migration = run;
            _lobbySnapshot = null; // The successor's lobby sends a fresh one.
            run.World = World.TakeSnapshot(); // Collected while FishNet tore the objects down.
            _remoteEndReason = DisconnectReason.None;
            _restorePose = _poseKnown && run.Snapshot.Phase != LobbyPhase.Lobby;
            this.Log(_restorePose ? $"[Network] Host migration: last pose {_posePosition} in '{_poseScene}' will be restored." : "[Network] Host migration: no pose to restore.");

            this.Log($"[Network] Host {run.PreviousHost} lost — migrating. Successors in order: " +
                     string.Join(", ", run.Candidates.ConvertAll(c => c.DisplayName)) + ".");
            SetState(SessionState.Migrating);
            UnloadNetworkScenes(); // The match comes back from the successor, fresh, from the same seed.
            return true;
        }

        private void TickMigration()
        {
            var run = _migration;
            if (!NetworkScenesUnloaded()) return; // Never reconnect while the old match scene is still there.

            float now = Time.unscaledTime;
            if (!run.RoundStarted)
            {
                StartMigrationRound(run);
                return;
            }

            if (now > run.RoundDeadline)
            {
                if (run.LocalHost)
                    Fail(DisconnectReason.Timeout, "Could not start hosting (host migration).");
                else
                    NextMigrationRound(run);
                return;
            }

            if (!run.LocalHost && run.RetryAt > 0f && now >= run.RetryAt)
            {
                run.RetryAt = 0f;
                ConnectToSuccessor(run);
            }
        }

        private void StartMigrationRound(MigrationRun run)
        {
            var candidate = run.Candidates[run.Round];
            run.RoundStarted = true;
            run.RoundDeadline = Time.unscaledTime + _config.MigrationRoundTimeout;
            run.LocalHost = candidate.Id == Platform.LocalPlayer;
            Sessions.SetHost(candidate.Id);

            if (!run.LocalHost)
            {
                this.Log($"[Network] Host migration: connecting to {candidate.DisplayName} ({candidate.HostAddress}).");
                ConnectToSuccessor(run);
                return;
            }

            this.Log("[Network] Host migration: this machine takes over hosting.");
            var room = Sessions.Current;
            var settings = new SessionSettings
            {
                Name = room.Name,
                MaxPlayers = room.MaxPlayers > 0 ? room.MaxPlayers : Mathf.Max(_config.MaxPlayers, run.Snapshot.Members.Count),
                Visibility = room.Visibility,
                Joinable = true,
                Password = _joinPassword
            };

            _hostPassword = _joinPassword; // We got in with it — the room stays locked with the same password.
            _lobbyToRestore = run.Snapshot;
            World.BeginRestore(run.World); // AI, items, broken windows... — set before the server starts so spawners see it.
            Sessions.SetData(SessionInfo.VERSION_KEY, NetworkVersion.Current);
            Sessions.SetData(SessionInfo.LOCKED_KEY, _hostPassword.Length > 0 ? "1" : "0");
            _activeProvider.PrepareHost(settings);
            if (!_networkManager.ServerManager.StartConnection())
                Fail(DisconnectReason.ConnectionFailed, "Server failed to start (host migration).");
            // Local client starts when the server reports Started; Hosting once it's authenticated.
        }

        private void ConnectToSuccessor(MigrationRun run)
        {
            if (_networkManager.ClientManager.Started) return;
            _activeProvider.PrepareClient(run.Candidates[run.Round].HostAddress);
            if (!_networkManager.ClientManager.StartConnection())
                run.RetryAt = Time.unscaledTime + MIGRATION_RETRY_DELAY;
        }

        private void NextMigrationRound(MigrationRun run)
        {
            this.Log($"[Network] Host migration: {run.Candidates[run.Round].DisplayName} didn't come up — trying the next one.", LogLevel.Warning);

            _stopping = true;
            try
            {
                if (_networkManager.ClientManager.Started)
                    _networkManager.ClientManager.StopConnection();
            }
            finally
            {
                _stopping = false;
            }

            run.Round++;
            run.RoundStarted = false;
            run.RetryAt = 0f;
            // This machine is a candidate too, so the list never runs out before it tried hosting itself.
        }

        private void FinishMigration()
        {
            var run = _migration;
            _migration = null;
            var newHost = run.Candidates[run.Round].Id;

            if (run.LocalHost)
            {
                SetState(SessionState.Hosting);
                Sessions.SetData(SessionInfo.HOST_ADDRESS_KEY, _activeProvider.GetHostAddress());
                SpawnLobby(); // Restores the room from _lobbyToRestore.
            }
            else
            {
                SetState(SessionState.Connected);
            }

            this.Log($"[Network] Host migration done — new host {(run.LocalHost ? "is this machine" : newHost.ToString())}.");
            var migrated = new HostMigrated(run.PreviousHost, newHost, run.LocalHost);
            Migrated?.Invoke(migrated);
            Events.Raise(migrated);
        }

        /// <summary> Client: the lobby as it is now — kept for a host migration (<see cref="NetworkLobby"/>). </summary>
        internal void RememberLobby(LobbySnapshot snapshot)
        {
            if (State == SessionState.Connected) _lobbySnapshot = snapshot;
        }

        /// <summary> New host: the lost host's lobby, once (<see cref="NetworkLobby"/> restores it). </summary>
        internal LobbySnapshot TakeLobbyToRestore()
        {
            var snapshot = _lobbyToRestore;
            _lobbyToRestore = null;
            return snapshot;
        }

        /// <summary> Local player's last pose — it comes back there after a host migration (<see cref="NetworkPlayer"/>). </summary>
        internal void RememberLocalPose(Transform player)
        {
            _poseKnown = true;
            _poseScene = player.gameObject.scene.name;
            player.GetPositionAndRotation(out _posePosition, out _poseRotation);
        }

        /// <summary>
        /// Once, after a migration: the pose to put the respawned local avatar back to. Matched by "the scene the player was in
        /// is loaded again", not by the avatar's own scene — FishNet places objects spawned on CLIENTS in the active scene.
        /// </summary>
        internal bool TryTakePoseToRestore(out Vector3 position, out Quaternion rotation)
        {
            position = _posePosition;
            rotation = _poseRotation;
            if (!_restorePose) return false;
            if (!UnityEngine.SceneManagement.SceneManager.GetSceneByName(_poseScene).isLoaded)
            {
                this.Log($"[Network] Host migration: scene '{_poseScene}' of the last pose isn't loaded — avatar stays at its spawn point.", LogLevel.Warning);
                return false;
            }
            _restorePose = false;
            this.Log($"[Network] Host migration: avatar back to {position} (scene '{_poseScene}').");
            return true;
        }

        #endregion
    }
}
