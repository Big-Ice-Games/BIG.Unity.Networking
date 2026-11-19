using System.Collections.Generic;
using UnityEngine;

namespace BIG.Unity.Networking.DevTools
{
    /// <summary> The plugin's own overlay modules. </summary>
    internal static class CorePanels
    {
        public static IEnumerable<DevPanel> Create()
        {
            yield return new SessionPanel();
            yield return new RoomsPanel();
            yield return new LobbyPanel();
            yield return new PlayersPanel();
            yield return new InteractionPanel();
        }

        public static string StateText(SessionState state) => state switch
        {
            SessionState.Offline => "not connected",
            SessionState.CreatingRoom => "creating room…",
            SessionState.StartingHost => "starting host…",
            SessionState.Hosting => "game running",
            SessionState.JoiningRoom => "joining room…",
            SessionState.Connecting => "connecting to host…",
            SessionState.Connected => "connected",
            SessionState.Migrating => "host migration…",
            _ => state.ToString()
        };

        public static string Role(NetworkSession session) => session.State switch
        {
            SessionState.Hosting => "HOST",
            SessionState.Connected => "CLIENT",
            SessionState.Offline => "OFFLINE",
            SessionState.Migrating => "MIGRATING",
            _ => "…"
        };
    }

    /// <summary> State, role, room, last disconnect, host migration, leave / hand over. </summary>
    internal sealed class SessionPanel : DevPanel
    {
        private string _lastMigration;
        private float _lastMigrationTime = -100f;

        public override string Title => "Session";
        public override int Order => 0;
        public override string Badge => CorePanels.Role(Session);
        public override string BadgeStyle => Session.IsInGame ? DevUI.GOOD : Session.State == SessionState.Offline ? null : DevUI.WARN;

        public override void Build(DevUI ui)
        {
            var session = Session;
            session.Migrated += migrated =>
            {
                _lastMigration = migrated.IsLocalHost ? "you are the new host" : $"new host {migrated.NewHost}";
                _lastMigrationTime = Time.unscaledTime;
            };

            ui.Text(() => $"{CorePanels.Role(session)}  ·  {CorePanels.StateText(session.State)}",
                () => session.IsInGame ? DevUI.GOOD : session.State == SessionState.Offline ? null : DevUI.WARN);
            ui.Text(() => $"{session.Platform.Name}  ·  you: {session.Platform.LocalDisplayName}", () => DevUI.MUTED);

            ui.Section(() => session.Sessions.Current != null).Text(() =>
            {
                var room = session.Sessions.Current;
                return room == null ? "" : $"Room: {room.Name}{(session.IsRoomLocked ? "  [locked]" : "")}  ·  {room.Address.Value}  ·  {(room.Joinable ? "joinable" : "closed")}";
            });
            ui.Text($"Version {NetworkVersion.Current}", DevUI.MUTED);

            ui.Section(() => session.State == SessionState.Offline && session.LastDisconnectReason != DisconnectReason.None)
                .Text(() => $"Last session ended: {session.LastDisconnectReason}" +
                            (string.IsNullOrEmpty(session.LastError) ? "" : $" — {session.LastError}"), () => DevUI.BAD);

            ui.Section(() => session.IsMigrating).Text(() => session.MigrationCandidate == session.Platform.LocalPlayer
                ? "Host lost — taking over as the new host…"
                : $"Host lost — reconnecting to the next host ({session.MigrationCandidate})…", () => DevUI.WARN);
            ui.Section(() => !session.IsMigrating && Time.unscaledTime - _lastMigrationTime < 15f)
                .Text(() => $"Host migrated: {_lastMigration}", () => DevUI.GOOD);

            var migration = ui.Row();
            migration.Text(() => $"Host migration: {session.HostMigration}");
            migration.Button("Change", () => session.HostMigration = (HostMigrationMode)(((int)session.HostMigration + 1) % 3));

            var actions = ui.Row();
            actions.Button("Quick play", session.QuickPlay, () => session.State == SessionState.Offline);
            actions.Button("Leave", session.Leave, () => session.State != SessionState.Offline);

            // Others carry on without us — like the host's game crashing, minus the wait for the timeout.
            ui.Section(() => session.IsHost && session.HostMigration != HostMigrationMode.Off)
                .Button("Leave & hand over (test host migration)", session.LeaveAndHandOver);
            ui.Section(() => session.IsInGame && session.Platform.SupportsInviteDialog)
                .Button($"Invite friends ({session.Platform.Name} overlay)", session.Platform.ShowInviteDialog);
        }
    }

    /// <summary> Offline: host a room (name, password) or find and join one. </summary>
    internal sealed class RoomsPanel : DevPanel
    {
        private readonly List<SessionInfo> _rooms = new List<SessionInfo>();
        private bool _searching;
        private bool _searched;
        private string _roomName = "";
        private string _roomPassword = "";
        private string _joinPassword = "";
        private string _address = "127.0.0.1:7770";

        public override string Title => "Rooms";
        public override int Order => 10;
        public override bool IsAvailable => Session.State == SessionState.Offline;
        public override string Badge => _searched ? $"{_rooms.Count} found" : null;

        public override void Build(DevUI ui)
        {
            var session = Session;

            ui.Heading("HOST");
            ui.Field("Name", "", value => _roomName = value,
                NetworkConfig.FormatRoomName(session.Config.RoomName, session.Platform));
            ui.Field("Password", session.Config.Password, value => _roomPassword = value, "none — open room");
            _roomPassword = session.Config.Password ?? "";
            ui.Button("Host", () =>
            {
                var settings = session.Config.CreateRoomSettings(session.Platform);
                if (!string.IsNullOrWhiteSpace(_roomName)) settings.Name = _roomName.Trim();
                settings.Password = _roomPassword;
                session.Host(settings);
            });

            ui.Heading("JOIN");
            ui.Field("Password", "", value => _joinPassword = value, "for locked rooms");
            ui.Button(() => _searching ? "Searching…" : "Search rooms", Search, () => !_searching);
            ui.List(() => _rooms, room => room.Address.ToString(), (row, room) =>
            {
                var line = row.Row();
                line.Text($"{(room.IsLocked ? "[locked] " : "")}{room.Name}   {room.MemberCount}/{room.MaxPlayers}" +
                          (room.IsMatchInProgress ? "   match running (returning players only)" : ""));
                // Joinable but closed to newcomers: a player who was in the match may still come back.
                line.Button("Join", () => session.Join(room, _joinPassword), () => room.Joinable && !room.IsFull);
                row.Text($"{room.Address.Value}  ·  v{room.Version}", DevUI.MUTED);
            }, "No rooms yet — Search.");

            // Typed addresses are a LAN thing; Steam joins via the list / invites.
            var byAddress = ui.Section(() => session.Platform.Name == "LAN").Row();
            byAddress.Field(null, _address, value => _address = value);
            byAddress.Button("Join IP", () => session.Join(new SessionAddress("LAN", _address), _joinPassword));
        }

        private void Search()
        {
            _searching = true;
            Session.Search(results =>
            {
                _rooms.Clear();
                _rooms.AddRange(results);
                _searching = false;
                _searched = true;
            });
        }
    }

    /// <summary> In a session with a lobby: members, ready, host controls (name, password, visibility, rules, match). </summary>
    internal sealed class LobbyPanel : DevPanel
    {
        private string _newName = "";
        private string _newPassword = "";

        public override string Title => "Lobby";
        public override int Order => 20;
        public override bool IsAvailable => Session.IsInGame && NetworkLobby.Instance != null;
        public override string Badge => NetworkLobby.Instance != null ? NetworkLobby.Instance.Phase.ToString() : null;

        private static NetworkLobby Lobby => NetworkLobby.Instance;

        public override void Build(DevUI ui)
        {
            var session = Session;

            ui.Text(() => Lobby == null ? "" : Lobby.Phase == LobbyPhase.Lobby
                ? $"{Lobby.Members.Count} member(s) · {(Lobby.AllReady ? "everyone ready" : "waiting for ready")}"
                : $"Match '{Lobby.MatchScene}'  ·  seed {Lobby.Seed}", () => DevUI.MUTED);

            ui.List(() => Lobby != null ? Lobby.Members : (IEnumerable<LobbyMember>)new LobbyMember[0],
                member => $"{member.PlayerId}|{member.IsHost}|{session.IsHost}", (row, member) =>
                {
                    var line = row.Row();
                    bool you = member.PlayerId == session.Platform.LocalPlayer.Value;
                    line.Text(() =>
                    {
                        bool ready = Lobby != null && Lobby.TryGetMember(member.Id, out var now) && now.Ready;
                        return $"{(ready ? "●" : "○")}  {member.DisplayName}{(you ? " (you)" : "")}{(member.IsHost ? "  [host]" : "")}";
                    }, () => Lobby != null && Lobby.TryGetMember(member.Id, out var now) && now.Ready ? DevUI.GOOD : null);
                    if (session.IsHost && !member.IsHost)
                        line.Button("Kick", () => Lobby.Kick(member.Id));
                });

            ui.Section(() => Lobby != null && Lobby.Phase == LobbyPhase.Lobby)
                .Button(() => Lobby != null && Lobby.Local.Ready ? "Not ready" : "Ready", () => Lobby.SetReady(!Lobby.Local.Ready));

            var host = ui.Section(() => session.IsHost);
            host.Heading("HOST");

            var name = host.Row();
            name.Field(null, "", value => _newName = value, "new room name");
            name.Button("Rename", () => session.SetRoomName(_newName));

            var password = host.Row();
            password.Field(null, "", value => _newPassword = value, "new password (empty = open)");
            password.Button(() => _newPassword.Length > 0 ? "Lock" : "Open", () => session.SetRoomPassword(_newPassword));
            host.Text(() => session.IsRoomLocked ? "Room is locked with a password." : "Room is open (no password).", () => DevUI.MUTED);

            var visibility = host.Row();
            visibility.Text(() => $"Visibility: {session.Sessions.Current?.Visibility}");
            visibility.Button("Change", () =>
                session.Sessions.SetVisibility((SessionVisibility)(((int)session.Sessions.Current.Visibility + 1) % 3)));

            var joinDuring = host.Row();
            joinDuring.Text(() => $"Join during match: {Lobby?.Rules.JoinInProgress}");
            joinDuring.Button("Change", () => ChangeRules(rules => rules.JoinInProgress = (JoinInProgress)(((int)rules.JoinInProgress + 1) % 3)));

            var bans = host.Row();
            bans.Text(() => $"Ban kicked players: {(Lobby != null && Lobby.Rules.BanKickedPlayers ? "on" : "off")}");
            bans.Button("Change", () => ChangeRules(rules => rules.BanKickedPlayers = !rules.BanKickedPlayers));

            string matchScene = session.Config.DevMatchScene;
            if (!string.IsNullOrEmpty(matchScene))
                host.Section(() => Lobby != null && Lobby.Phase == LobbyPhase.Lobby)
                    .Button(() => Lobby != null && Lobby.AllReady ? $"Start match '{matchScene}'" : $"Start match '{matchScene}' (not everyone ready)",
                        () => Lobby.StartMatch(matchScene));
            host.Section(() => Lobby != null && Lobby.Phase == LobbyPhase.InGame)
                .Button("Back to lobby", () => Lobby.ReturnToLobby());
        }

        /// <summary> Runtime copy — never modify the rules asset itself. </summary>
        private static void ChangeRules(System.Action<LobbyRules> change)
        {
            var rules = Object.Instantiate(Lobby.Rules);
            change(rules);
            Lobby.Rules = rules;
        }
    }

    /// <summary> Local player's NetworkInteractor: what's in focus, the hand, the inventory (with buttons). </summary>
    internal sealed class InteractionPanel : DevPanel
    {
        public override string Title => "Interaction";
        public override int Order => 40;
        public override bool IsAvailable => Interactor != null;

        public override string Badge
        {
            get
            {
                var interactor = Interactor;
                if (interactor == null) return null;
                int count = interactor.Inventory.Count;
                return interactor.Held != null ? $"holding · {count} in inv." : $"{count} in inv.";
            }
        }

        private NetworkInteractor Interactor =>
            Session.Players.Local != null ? Session.Players.Local.GetComponent<NetworkInteractor>() : null;

        public override void Build(DevUI ui)
        {
            ui.Text(() =>
            {
                var interactor = Interactor;
                if (interactor == null || interactor.Focus == null) return "Nothing in reach.";
                return string.IsNullOrEmpty(interactor.FocusAltPrompt)
                    ? $"Focus: {interactor.FocusPrompt}"
                    : $"Focus: {interactor.FocusPrompt}  /  {interactor.FocusAltPrompt}";
            }, () => Interactor != null && Interactor.Focus != null ? DevUI.GOOD : DevUI.MUTED);

            var hand = ui.Row();
            hand.Text(() => Interactor?.Held != null ? $"Hand: {Interactor.Held.DisplayName}" : "Hand: empty");
            hand.Button("Use", () => Interactor.Use(), () => Interactor?.Held != null);
            hand.Button("Put away", () => Interactor.PutAway(),
                () => Interactor?.Held != null && Interactor.Held.TryGetComponent<NetworkCollectible>(out _));
            hand.Button("Drop", () => Interactor.Drop(), () => Interactor?.Held != null);

            ui.Heading("INVENTORY");
            ui.List(() => Interactor != null ? Interactor.Inventory : (IEnumerable<NetworkItem>)new NetworkItem[0],
                item => item.ObjectId.ToString(), (row, item) =>
                {
                    var line = row.Row();
                    line.Text(item.DisplayName + (string.IsNullOrEmpty(item.ItemId) ? "" : $"  ({item.ItemId})"));
                    if (item.TryGetComponent<NetworkCarryable>(out _)) line.Button("Take out", () => Interactor.TakeOut(item));
                    line.Button("Drop", () => Interactor.DropFromInventory(item));
                }, "Empty.");
        }
    }

    /// <summary> In a session: who's here (from spawned avatars). </summary>
    internal sealed class PlayersPanel : DevPanel
    {
        public override string Title => "Players";
        public override int Order => 30;
        public override bool IsAvailable => Session.IsInGame;
        public override string Badge => $"{Session.Players.Count}";

        public override void Build(DevUI ui)
        {
            var session = Session;
            ui.List(() => session.Players.All, player => player.Id.Value, (row, player) =>
            {
                bool isHost = session.Sessions.Current != null && session.Sessions.Current.Owner == player.Id;
                row.Text($"{player.DisplayName}{(player.IsLocal ? " (you)" : "")}{(isHost ? "  [host]" : "")}");
                row.Text(player.Id.Value, DevUI.MUTED);
            }, "No avatars spawned (lobby phase).");
        }
    }
}
