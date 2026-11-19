using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Scened;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace BIG.Unity.Networking
{
    public enum LobbyPhase : byte
    {
        /// <summary> Players gather, set ready, pick things. No avatars spawned. </summary>
        Lobby,
        /// <summary> Match (or lobby) scene is loading. </summary>
        Loading,
        /// <summary> Match scene loaded — PlayerSpawner there spawns avatars. </summary>
        InGame
    }

    /// <summary> One player in the room, replicated to everyone. </summary>
    [Serializable]
    public struct LobbyMember
    {
        public string PlayerId;
        public string DisplayName;
        public bool IsHost;
        public bool Ready;
        /// <summary> Free-form per-player data the game defines (chosen character, loadout... serialized as you like). </summary>
        public string Data;
        /// <summary> Where the others reconnect if this player takes over hosting (host migration). </summary>
        public string HostAddress;

        public PlayerId Id => new PlayerId(PlayerId);
    }

    /// <summary> BIG event (every machine): members, readiness, phase, seed or settings changed. </summary>
    public readonly struct LobbyChanged
    {
        public LobbyChanged(NetworkLobby lobby) => Lobby = lobby;
        public readonly NetworkLobby Lobby;
    }

    /// <summary>
    /// The room before (and between) matches — lives as long as the session, across scene changes.
    /// Spawned by the host automatically when <see cref="NetworkSession"/> has a lobby prefab (NetworkObject + this).
    /// <list type="bullet">
    /// <item>Members (from the session's player identities), Ready, per-player Data — everyone sees them.</item>
    /// <item>Host: match <see cref="Seed"/> + string <see cref="Settings"/> (map, difficulty...) for scenes that are
    /// generated rather than baked — every machine builds the same layout from them.</item>
    /// <item>Host: <see cref="StartMatch"/> loads the match scene for everyone (players joining later load it too);
    /// <see cref="ReturnToLobby"/> goes back; <see cref="Kick"/> removes a player (they see DisconnectReason.Kicked).</item>
    /// </list>
    /// Match scenes load ADDITIVELY over the scene the session started in (menu / lobby scene stays loaded below,
    /// returning just unloads the match) and become the active scene. Put a PlayerSpawner in the match scene.
    /// </summary>
    public sealed class NetworkLobby : NetworkBehaviour
    {
        public static NetworkLobby Instance { get; private set; }

        [SerializeField, Tooltip("Join-in-progress, bans, ready reset. Empty = defaults (returning players only, bans on).")]
        private LobbyRules _rules;

        private static readonly List<Action<NetworkLobby>> PendingAvailable = new List<Action<NetworkLobby>>();

        // Server only: who played this match (may return under ReturningPlayers) and who was kicked (banned).
        private readonly HashSet<string> _participants = new HashSet<string>();
        // Returning after a host migration: always let back in (they were in the room), whatever JoinInProgress says.
        private readonly HashSet<string> _returning = new HashSet<string>();
        // Members of the lost host's lobby — their Ready/Data come back when they reconnect.
        private readonly Dictionary<string, LobbyMember> _restoredMembers = new Dictionary<string, LobbyMember>();

        private readonly SyncList<LobbyMember> _members = new SyncList<LobbyMember>();
        private readonly SyncVar<LobbyPhase> _phase = new SyncVar<LobbyPhase>(LobbyPhase.Lobby);
        private readonly SyncVar<int> _seed = new SyncVar<int>();
        private readonly SyncVar<string> _matchScene = new SyncVar<string>("");
        private readonly SyncDictionary<string, string> _settings = new SyncDictionary<string, string>();
        // Synced (not just host-side) so a successor host still knows who was kicked.
        private readonly SyncHashSet<string> _banned = new SyncHashSet<string>();

        private NetworkSession _session;

        public IReadOnlyList<LobbyMember> Members => _members;
        public LobbyPhase Phase => _phase.Value;
        /// <summary> Match seed — set by the host in <see cref="StartMatch"/>; identical on every machine. </summary>
        public int Seed => _seed.Value;
        public string MatchScene => _matchScene.Value;
        public IReadOnlyDictionary<string, string> Settings => _settings;

        /// <summary> Every member is ready (host included). </summary>
        public bool AllReady
        {
            get
            {
                foreach (var member in _members)
                    if (!member.Ready) return false;
                return _members.Count > 0;
            }
        }

        public bool TryGetMember(PlayerId playerId, out LobbyMember member)
        {
            foreach (var m in _members)
            {
                if (m.PlayerId == playerId.Value)
                {
                    member = m;
                    return true;
                }
            }

            member = default;
            return false;
        }

        /// <summary> This machine's member entry (default when not listed yet). </summary>
        public LobbyMember Local
        {
            get
            {
                TryGetMember(_session.Platform.LocalPlayer, out var member);
                return member;
            }
        }

        public event Action<NetworkLobby> Changed;

        /// <summary> Host may swap rules at runtime (e.g. open a running match to everyone). </summary>
        public LobbyRules Rules
        {
            get => _rules;
            set
            {
                if (!IsServerInitialized) return;
                _rules = value != null ? value : ScriptableObject.CreateInstance<LobbyRules>();
                ApplyRoomOpenness();
            }
        }

        /// <summary>
        /// Calls <paramref name="callback"/> once the lobby exists on this machine with its state (seed, settings) synced —
        /// immediately if it already does. Match-scene scripts should use this instead of reading
        /// <see cref="Instance"/> in Start: a player joining mid-match loads the match scene BEFORE the lobby arrives.
        /// </summary>
        public static void WhenAvailable(Action<NetworkLobby> callback)
        {
            if (Instance != null && Instance.IsClientInitialized)
                callback(Instance);
            else
                PendingAvailable.Add(callback);
        }

        public override void OnStartClient()
        {
            // Joined mid-match: the match scene loaded before this object arrived — make it active now.
            if (!string.IsNullOrEmpty(_matchScene.Value))
            {
                var scene = UnitySceneManager.GetSceneByName(_matchScene.Value);
                if (scene.isLoaded) UnitySceneManager.SetActiveScene(scene);
            }

            if (!IsServerInitialized) _session.RememberLobby(Capture()); // Host migration needs it even if nothing changes.

            var callbacks = PendingAvailable.ToArray();
            PendingAvailable.Clear();
            foreach (var callback in callbacks)
                callback(this);
        }

        private void Awake()
        {
            _session = NetworkSession.Instance;
            if (_rules == null) _rules = ScriptableObject.CreateInstance<LobbyRules>();
            _members.OnChange += (op, index, previous, next, asServer) => NotifyChanged(asServer);
            _settings.OnChange += (op, key, value, asServer) => NotifyChanged(asServer);
            _phase.OnChange += (previous, next, asServer) => NotifyChanged(asServer);
            _seed.OnChange += (previous, next, asServer) => NotifyChanged(asServer);
            _matchScene.OnChange += (previous, next, asServer) => NotifyChanged(asServer);
            _banned.OnChange += (op, item, asServer) => NotifyChanged(asServer);
        }

        public override void OnStartNetwork()
        {
            Instance = this;
            NetworkManager.SceneManager.OnLoadEnd += OnSceneLoadEnd;
        }

        public override void OnStopNetwork()
        {
            NetworkManager.SceneManager.OnLoadEnd -= OnSceneLoadEnd;
            if (Instance == this) Instance = null;
            PendingAvailable.Clear(); // Waiters belonged to this session's scenes.
            // Network-loaded scenes are removed by NetworkSession when the session ends.
        }

        public override void OnStartServer()
        {
            _session.Players.ServerAdmission = Admit;
            _session.Players.ServerPlayerIdentified += OnServerPlayerIdentified;
            _session.Players.ServerPlayerDisconnected += OnServerPlayerDisconnected;

            var restore = _session.TakeLobbyToRestore();
            if (restore != null) Restore(restore);

            foreach (var identity in _session.Players.ServerIdentities)
                OnServerPlayerIdentified(identity);
            ApplyRoomOpenness();

            // The lost host was mid-match: same scene, same seed — the same world comes back for everyone.
            if (restore != null && restore.Phase != LobbyPhase.Lobby && !string.IsNullOrEmpty(restore.MatchScene))
            {
                this.Log($"[Lobby] Host migration: reloading match '{restore.MatchScene}' with seed {restore.Seed}.");
                NetworkManager.SceneManager.LoadGlobalScenes(new SceneLoadData(restore.MatchScene) { ReplaceScenes = ReplaceOption.None });
            }
        }

        /// <summary> New host after a migration: take over the lost host's room. Members come back as they reconnect. </summary>
        private void Restore(LobbySnapshot snapshot)
        {
            foreach (var pair in snapshot.Settings)
                _settings[pair.Key] = pair.Value;
            foreach (var id in snapshot.Banned)
                _banned.Add(id);

            _seed.Value = snapshot.Seed;
            bool inMatch = snapshot.Phase != LobbyPhase.Lobby && !string.IsNullOrEmpty(snapshot.MatchScene);
            _matchScene.Value = inMatch ? snapshot.MatchScene : "";
            _phase.Value = inMatch ? LobbyPhase.Loading : LobbyPhase.Lobby;

            foreach (var member in snapshot.Members)
            {
                _returning.Add(member.PlayerId);
                _restoredMembers[member.PlayerId] = member;
                if (inMatch) _participants.Add(member.PlayerId);
            }

            this.Log($"[Lobby] Restored room after host migration: {snapshot.Members.Count} member(s), phase {snapshot.Phase}.");
        }

        /// <summary> Client side: what a successor would need if the host vanished right now. </summary>
        private LobbySnapshot Capture()
        {
            var snapshot = new LobbySnapshot
            {
                Phase = _phase.Value,
                Seed = _seed.Value,
                MatchScene = _matchScene.Value
            };
            snapshot.Members.AddRange(_members);
            foreach (var pair in _settings)
                snapshot.Settings[pair.Key] = pair.Value;
            snapshot.Banned.AddRange(_banned);
            return snapshot;
        }

        public override void OnStopServer()
        {
            if (_session.Players.ServerAdmission == Admit) _session.Players.ServerAdmission = null;
            _session.Players.ServerPlayerIdentified -= OnServerPlayerIdentified;
            _session.Players.ServerPlayerDisconnected -= OnServerPlayerDisconnected;
        }

        /// <summary> Host's gate for arriving players — see <see cref="LobbyRules"/>. </summary>
        private DisconnectReason Admit(PlayerId playerId)
        {
            if (_rules.BanKickedPlayers && _banned.Contains(playerId.Value)) return DisconnectReason.Banned;
            if (_phase.Value == LobbyPhase.Lobby || _returning.Contains(playerId.Value)) return DisconnectReason.None;

            return _rules.JoinInProgress switch
            {
                JoinInProgress.Anyone => DisconnectReason.None,
                JoinInProgress.ReturningPlayers when _participants.Contains(playerId.Value) => DisconnectReason.None,
                _ => DisconnectReason.MatchInProgress
            };
        }

        /// <summary>
        /// Publishes whether NEW players can get in right now (quick play / room lists skip closed rooms). The room itself
        /// stays joinable at platform level so returning players can still come back — the host's gate decides.
        /// </summary>
        private void ApplyRoomOpenness()
        {
            bool open = _phase.Value == LobbyPhase.Lobby || _rules.JoinInProgress == JoinInProgress.Anyone;
            _session.Sessions.SetData(SessionInfo.OPEN_KEY, open ? "1" : "0");
            _session.Sessions.SetJoinable(_phase.Value == LobbyPhase.Lobby || _rules.JoinInProgress != JoinInProgress.Nobody);
        }

        #region Members (server)

        private void OnServerPlayerIdentified(NetworkPlayers.Identity identity)
        {
            if (_phase.Value != LobbyPhase.Lobby) _participants.Add(identity.PlayerId.Value); // Joined mid-match.
            if (TryGetMember(identity.PlayerId, out _)) return;

            _restoredMembers.TryGetValue(identity.PlayerId.Value, out var before); // Back after a host migration.
            _members.Add(new LobbyMember
            {
                PlayerId = identity.PlayerId.Value,
                DisplayName = identity.DisplayName,
                IsHost = identity.Connection.IsLocalClient,
                Ready = before.Ready,
                Data = before.Data,
                HostAddress = identity.HostAddress
            });
        }

        private void OnServerPlayerDisconnected(NetworkPlayers.Identity identity)
        {
            int index = IndexOf(identity.PlayerId);
            if (index >= 0) _members.RemoveAt(index);
        }

        private int IndexOf(PlayerId playerId)
        {
            for (int i = 0; i < _members.Count; i++)
                if (_members[i].PlayerId == playerId.Value) return i;
            return -1;
        }

        private void UpdateMember(NetworkConnection connection, Func<LobbyMember, LobbyMember> change)
        {
            if (!_session.Players.TryGetIdentity(connection, out var identity)) return;
            int index = IndexOf(identity.PlayerId);
            if (index >= 0) _members[index] = change(_members[index]);
        }

        #endregion

        #region Every player

        /// <summary> Mark the local player ready / not ready. </summary>
        public void SetReady(bool ready) => ServerSetReady(ready);

        /// <summary> Set the local player's free-form data (chosen character...). </summary>
        public void SetMemberData(string data) => ServerSetData(data);

        [ServerRpc(RequireOwnership = false)]
        private void ServerSetReady(bool ready, NetworkConnection sender = null) =>
            UpdateMember(sender, m => { m.Ready = ready; return m; });

        [ServerRpc(RequireOwnership = false)]
        private void ServerSetData(string data, NetworkConnection sender = null) =>
            UpdateMember(sender, m => { m.Data = data; return m; });

        #endregion

        #region Host

        /// <summary> Host: a match setting everyone reads (map, difficulty...). </summary>
        public void SetSetting(string key, string value)
        {
            if (!IsServerInitialized) return;
            _settings[key] = value;
        }

        /// <summary>
        /// Host: load <paramref name="matchScene"/> (must be in Build Profiles) for everyone with a seed — random when not
        /// given. Readiness is the game's call: check <see cref="AllReady"/> first if you need it.
        /// </summary>
        public void StartMatch(string matchScene, int? seed = null)
        {
            if (!IsServerInitialized || _phase.Value != LobbyPhase.Lobby) return;

            _seed.Value = seed ?? new System.Random().Next();
            _matchScene.Value = matchScene;
            _phase.Value = LobbyPhase.Loading;

            _participants.Clear();
            foreach (var member in _members)
                _participants.Add(member.PlayerId);
            ApplyRoomOpenness();

            this.Log($"[Lobby] Starting match '{matchScene}' with seed {_seed.Value} for {_members.Count} player(s).");
            NetworkManager.SceneManager.LoadGlobalScenes(new SceneLoadData(matchScene) { ReplaceScenes = ReplaceOption.None });
        }

        /// <summary> Host: end the match — everyone goes back to the lobby (match scene unloaded). </summary>
        public void ReturnToLobby()
        {
            if (!IsServerInitialized || _phase.Value == LobbyPhase.Lobby || string.IsNullOrEmpty(_matchScene.Value)) return;

            NetworkManager.SceneManager.UnloadGlobalScenes(new SceneUnloadData(_matchScene.Value));
            _matchScene.Value = "";
            _phase.Value = LobbyPhase.Lobby;
            _participants.Clear();
            ApplyRoomOpenness();

            if (_rules.ResetReadyAfterMatch)
            {
                for (int i = 0; i < _members.Count; i++)
                {
                    var member = _members[i];
                    member.Ready = false;
                    _members[i] = member;
                }
            }
        }

        /// <summary> Host: remove a player. They end up Offline with <see cref="DisconnectReason.Kicked"/>. </summary>
        public void Kick(PlayerId playerId)
        {
            if (!IsServerInitialized) return;
            if (!_session.Players.TryGetIdentity(playerId, out var identity) || identity.Connection.IsLocalClient) return;

            this.Log($"[Lobby] Kicking {identity.DisplayName} ({playerId}){(_rules.BanKickedPlayers ? " — banned from this room" : "")}.");
            if (_rules.BanKickedPlayers) _banned.Add(playerId.Value);
            NetworkManager.ServerManager.Broadcast(identity.Connection, new SessionEndBroadcast { Reason = (byte)DisconnectReason.Kicked });
            identity.Connection.Disconnect(false); // Not immediately — the broadcast goes out first.
        }

        #endregion

        private void OnSceneLoadEnd(SceneLoadEndEventArgs args)
        {
            foreach (var scene in args.LoadedScenes)
            {
                if (scene.name != _matchScene.Value) continue;

                // Match scene becomes active: lighting, and objects instantiated without a parent land in it.
                UnitySceneManager.SetActiveScene(scene);

                if (args.QueueData.AsServer && _phase.Value == LobbyPhase.Loading)
                    _phase.Value = LobbyPhase.InGame;
            }
        }

        private void NotifyChanged(bool asServer)
        {
            // Host runs server + client: notify once, from the client side (or the server side on a dedicated server).
            if (asServer && IsClientInitialized) return;
            if (!asServer && !IsServerInitialized) _session.RememberLobby(Capture()); // For a host migration.
            Changed?.Invoke(this);
            Events.Raise(new LobbyChanged(this));
        }
    }
}
