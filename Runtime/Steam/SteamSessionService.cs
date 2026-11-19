using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace BIG.Unity.Networking.Steam
{
    /// <summary>
    /// Rooms = Steam lobbies. The lobby is the directory (name, version, joinable, host SteamID); the game itself runs over
    /// FishySteamworks P2P to the host's SteamID.
    /// <list type="bullet">
    /// <item>Join entry points: room list (<see cref="Search"/>), Steam overlay invite / "Join game" on a friend
    /// (<see cref="JoinRequested"/>), and the <c>+connect_lobby &lt;id&gt;</c> launch argument Steam passes when the game
    /// is started by an invite.</item>
    /// <item>Rich presence: friends see the status and can "Join game" while the room is joinable.</item>
    /// <item>Search returns public lobbies of this game plus lobbies friends are in (friends-only lobbies don't show up
    /// in Steam's public list).</item>
    /// </list>
    /// </summary>
    internal sealed class SteamSessionService : ISessionService, IDisposable
    {
        private const string PROVIDER = "Steam";
        private const string GAME_KEY = "big.game";
        private const string NAME_KEY = "big.name";
        private const string CONNECT_ARGUMENT = "+connect_lobby";

        private readonly SteamPlatform _platform;
        private readonly string _gameId;

        private readonly CallResult<LobbyCreated_t> _lobbyCreated;
        private readonly CallResult<LobbyEnter_t> _lobbyEntered;
        private readonly CallResult<LobbyMatchList_t> _lobbyList;
        private readonly Callback<GameLobbyJoinRequested_t> _joinRequested;
        private readonly Callback<GameRichPresenceJoinRequested_t> _richPresenceJoinRequested;

        private Action<SessionResult> _pendingCreate;
        private SessionSettings _pendingSettings;
        private Action<SessionResult> _pendingJoin;
        private readonly List<Action<IReadOnlyList<SessionInfo>>> _searchCallbacks = new List<Action<IReadOnlyList<SessionInfo>>>();
        private bool _launchArgumentChecked;

        // Host migration: this machine hosts now but Steam hasn't made it the lobby owner yet — owner calls wait here.
        private bool _takingOver;
        private readonly Dictionary<string, string> _pendingData = new Dictionary<string, string>();
        private bool? _pendingJoinable;
        private SessionVisibility? _pendingVisibility;

        public CSteamID CurrentLobby { get; private set; } = CSteamID.Nil;
        public SessionInfo Current { get; private set; }
        public event Action<SessionAddress> JoinRequested;

        public SteamSessionService(SteamNetworkProvider settings, SteamPlatform platform)
        {
            _platform = platform;
            _gameId = string.IsNullOrEmpty(settings.GameId) ? Application.productName : settings.GameId;

            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            _lobbyList = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _richPresenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
        }

        #region Create / join / leave

        public void Create(SessionSettings settings, Action<SessionResult> done)
        {
            _pendingCreate = done;
            _pendingSettings = settings;
            _lobbyCreated.Set(SteamMatchmaking.CreateLobby(ToLobbyType(settings.Visibility), Mathf.Max(1, settings.MaxPlayers)));
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            var done = _pendingCreate;
            var settings = _pendingSettings;
            _pendingCreate = null;
            if (done == null) return; // Cancelled by Leave().

            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                done(SessionResult.Fail(SessionError.PlatformError, $"CreateLobby: {(ioFailure ? "IO failure" : result.m_eResult.ToString())}"));
                return;
            }

            CurrentLobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(CurrentLobby, GAME_KEY, _gameId);
            SteamMatchmaking.SetLobbyData(CurrentLobby, NAME_KEY, string.IsNullOrEmpty(settings.Name) ? _platform.LocalDisplayName : settings.Name);
            SteamMatchmaking.SetLobbyJoinable(CurrentLobby, settings.Joinable);

            Current = ReadLobby(CurrentLobby);
            UpdateRichPresence();
            this.Log($"[Steam] Lobby {CurrentLobby.m_SteamID} created ({settings.Visibility}, max {settings.MaxPlayers}, game '{_gameId}').");
            done(SessionResult.Ok(Current));
        }

        public void Join(SessionAddress address, Action<SessionResult> done)
        {
            if (address.Provider != PROVIDER || !ulong.TryParse(address.Value, out ulong lobbyId))
            {
                done(SessionResult.Fail(SessionError.NotFound, $"Not a Steam lobby: {address}"));
                return;
            }

            _pendingJoin = done;
            _lobbyEntered.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)));
        }

        private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
        {
            var done = _pendingJoin;
            _pendingJoin = null;
            var lobby = new CSteamID(result.m_ulSteamIDLobby);

            if (done == null)
            {
                SteamMatchmaking.LeaveLobby(lobby); // Leave() was called while joining.
                return;
            }

            var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
            if (ioFailure || response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                var error = response switch
                {
                    EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist => SessionError.NotFound,
                    EChatRoomEnterResponse.k_EChatRoomEnterResponseFull => SessionError.Full,
                    EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed => SessionError.NotJoinable,
                    EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited => SessionError.NotJoinable,
                    _ => SessionError.PlatformError
                };
                done(SessionResult.Fail(error, ioFailure ? "IO failure" : response.ToString()));
                return;
            }

            if (SteamMatchmaking.GetLobbyData(lobby, GAME_KEY) != _gameId)
            {
                SteamMatchmaking.LeaveLobby(lobby);
                done(SessionResult.Fail(SessionError.NotFound, "Lobby belongs to another game."));
                return;
            }

            CurrentLobby = lobby;
            Current = ReadLobby(lobby);
            UpdateRichPresence();
            done(SessionResult.Ok(Current));
        }

        public void Leave()
        {
            _pendingCreate = null;
            _pendingJoin = null;
            ClearTakeOver();

            if (CurrentLobby.IsValid())
                SteamMatchmaking.LeaveLobby(CurrentLobby);

            CurrentLobby = CSteamID.Nil;
            Current = null;
            SteamFriends.ClearRichPresence();
        }

        #endregion

        #region Search

        public void Search(Action<IReadOnlyList<SessionInfo>> done)
        {
            _searchCallbacks.Add(done);
            if (_searchCallbacks.Count > 1) return; // Already searching — this caller gets the same results.

            SteamMatchmaking.AddRequestLobbyListStringFilter(GAME_KEY, _gameId, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
            _lobbyList.Set(SteamMatchmaking.RequestLobbyList());
        }

        private void OnLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            var found = new Dictionary<ulong, SessionInfo>();
            int publicCount = ioFailure ? 0 : (int)result.m_nLobbiesMatching;

            if (!ioFailure)
            {
                for (int i = 0; i < result.m_nLobbiesMatching; i++)
                {
                    var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                    found[lobby.m_SteamID] = ReadLobby(lobby);
                }
            }

            // Friends-only lobbies never appear in the public list — add the ones friends are in.
            var appId = SteamUtils.GetAppID();
            int friends = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < friends; i++)
            {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(friend, out var game)) continue;
                if (game.m_gameID.AppID() != appId || !game.m_steamIDLobby.IsValid()) continue;
                if (found.ContainsKey(game.m_steamIDLobby.m_SteamID)) continue;

                var info = ReadLobby(game.m_steamIDLobby);
                if (string.IsNullOrEmpty(info.Name))
                    info.Name = $"{SteamFriends.GetFriendPersonaName(friend)}'s game"; // Lobby data not cached yet.
                found[game.m_steamIDLobby.m_SteamID] = info;
            }

            this.Log($"[Steam] Lobby search for '{_gameId}': {publicCount} public{(ioFailure ? " (IO failure)" : "")}, " +
                     $"{found.Count - publicCount} via friends. Steam never lists your own lobbies — test with a second account.");

            var results = new List<SessionInfo>(found.Values);
            var callbacks = _searchCallbacks.ToArray();
            _searchCallbacks.Clear();
            foreach (var callback in callbacks)
                callback(results);
        }

        #endregion

        #region Room data (owner)

        public void SetJoinable(bool joinable)
        {
            if (_takingOver && !IsOwner) _pendingJoinable = joinable;
            if (!IsOwner) return;
            SteamMatchmaking.SetLobbyJoinable(CurrentLobby, joinable);
            Current.Joinable = joinable;
            UpdateRichPresence();
        }

        public void SetVisibility(SessionVisibility visibility)
        {
            if (_takingOver && !IsOwner) _pendingVisibility = visibility;
            if (!IsOwner) return;
            SteamMatchmaking.SetLobbyType(CurrentLobby, ToLobbyType(visibility));
            Current.Visibility = visibility;
        }

        private static ELobbyType ToLobbyType(SessionVisibility visibility) => visibility switch
        {
            SessionVisibility.Public => ELobbyType.k_ELobbyTypePublic,
            SessionVisibility.FriendsOnly => ELobbyType.k_ELobbyTypeFriendsOnly,
            _ => ELobbyType.k_ELobbyTypePrivate
        };

        public void SetData(string key, string value)
        {
            if (_takingOver && !IsOwner) _pendingData[key] = value;
            if (!IsOwner) return;
            SteamMatchmaking.SetLobbyData(CurrentLobby, key, value);
            Current.Data[key] = value;
        }

        public void SetName(string name)
        {
            SetData(NAME_KEY, name); // Queued like any owner call while taking over after a migration.
            if (IsOwner) Current.Name = name;
        }

        /// <summary> Steam tracks lobby members itself. </summary>
        public void SetMemberCount(int count)
        {
            if (Current != null) Current.MemberCount = count;
        }

        public void Invite(PlayerId friend)
        {
            if (CurrentLobby.IsValid() && ulong.TryParse(friend.Value, out ulong steamId))
                SteamMatchmaking.InviteUserToLobby(CurrentLobby, new CSteamID(steamId));
        }

        private bool IsOwner => CurrentLobby.IsValid() && SteamMatchmaking.GetLobbyOwner(CurrentLobby) == SteamUser.GetSteamID();

        /// <summary>
        /// Steam moves lobby ownership by itself when the owner leaves — maybe to someone else than our successor.
        /// Whoever owns the lobby hands it to <paramref name="newHost"/>; the new host queues owner calls until it owns it.
        /// </summary>
        public void SetHost(PlayerId newHost)
        {
            if (!CurrentLobby.IsValid()) return;
            if (Current != null) Current.Owner = newHost;

            if (newHost == _platform.LocalPlayer)
            {
                _takingOver = !IsOwner;
                if (_takingOver) this.Log("[Steam] Taking over the lobby — waiting for Steam to make this machine the owner.");
                return;
            }

            ClearTakeOver();
            if (IsOwner && ulong.TryParse(newHost.Value, out ulong steamId))
            {
                this.Log($"[Steam] Handing lobby ownership to {steamId} (host migration).");
                SteamMatchmaking.SetLobbyOwner(CurrentLobby, new CSteamID(steamId));
            }
        }

        private void FlushTakeOver()
        {
            this.Log("[Steam] Lobby ownership arrived — publishing room data.");
            foreach (var pair in _pendingData)
            {
                SteamMatchmaking.SetLobbyData(CurrentLobby, pair.Key, pair.Value);
                Current.Data[pair.Key] = pair.Value;
            }
            if (_pendingVisibility.HasValue) SetVisibility(_pendingVisibility.Value);
            if (_pendingJoinable.HasValue) SetJoinable(_pendingJoinable.Value);
            ClearTakeOver();
            UpdateRichPresence();
        }

        private void ClearTakeOver()
        {
            _takingOver = false;
            _pendingData.Clear();
            _pendingJoinable = null;
            _pendingVisibility = null;
        }

        #endregion

        #region Join requests and rich presence

        private void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            // Overlay invite accepted / "Join game" on a friend while the game is running.
            JoinRequested?.Invoke(new SessionAddress(PROVIDER, request.m_steamIDLobby.m_SteamID.ToString()));
        }

        private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t request)
        {
            if (TryParseConnect(request.m_rgchConnect, out ulong lobbyId))
                JoinRequested?.Invoke(new SessionAddress(PROVIDER, lobbyId.ToString()));
        }

        public void Tick()
        {
            if (_takingOver && IsOwner) FlushTakeOver();

            if (_launchArgumentChecked) return;
            _launchArgumentChecked = true;

            // Game launched by Steam from an invite: "+connect_lobby <id>".
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == CONNECT_ARGUMENT && ulong.TryParse(args[i + 1], out ulong lobbyId))
                    JoinRequested?.Invoke(new SessionAddress(PROVIDER, lobbyId.ToString()));
            }
        }

        private void UpdateRichPresence()
        {
            if (!CurrentLobby.IsValid()) return;

            // "connect" makes "Join game" appear for friends; Steam starts the game with it if it isn't running.
            bool joinable = Current != null && Current.Joinable;
            SteamFriends.SetRichPresence("connect", joinable ? $"{CONNECT_ARGUMENT} {CurrentLobby.m_SteamID}" : null);
            SteamFriends.SetRichPresence("status", joinable ? "In a joinable game" : "In a game");
        }

        private static bool TryParseConnect(string connect, out ulong lobbyId)
        {
            lobbyId = 0;
            if (string.IsNullOrEmpty(connect)) return false;
            var parts = connect.Split(' ');
            return parts.Length >= 2 && parts[0] == CONNECT_ARGUMENT && ulong.TryParse(parts[1], out lobbyId);
        }

        #endregion

        private SessionInfo ReadLobby(CSteamID lobby)
        {
            var info = new SessionInfo
            {
                Address = new SessionAddress(PROVIDER, lobby.m_SteamID.ToString()),
                Name = SteamMatchmaking.GetLobbyData(lobby, NAME_KEY),
                Owner = new PlayerId(SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID.ToString()),
                MemberCount = SteamMatchmaking.GetNumLobbyMembers(lobby),
                MaxPlayers = SteamMatchmaking.GetLobbyMemberLimit(lobby),
                Joinable = true, // Steam doesn't expose it to members; non-joinable lobbies are excluded from searches and refuse joins.
                Visibility = SessionVisibility.FriendsOnly
            };

            // All room data (host address, version, open flag, game keys...).
            int count = SteamMatchmaking.GetLobbyDataCount(lobby);
            for (int i = 0; i < count; i++)
            {
                if (SteamMatchmaking.GetLobbyDataByIndex(lobby, i, out string key, Constants.k_nMaxLobbyKeyLength,
                        out string value, 8192) && !string.IsNullOrEmpty(value))
                    info.Data[key] = value;
            }

            return info;
        }

        public void Dispose()
        {
            Leave();
            _lobbyCreated.Dispose();
            _lobbyEntered.Dispose();
            _lobbyList.Dispose();
            _joinRequested.Dispose();
            _richPresenceJoinRequested.Dispose();
        }
    }
}
