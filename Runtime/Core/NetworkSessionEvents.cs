namespace BIG.Unity.Networking
{
    public enum SessionState
    {
        /// <summary> Not in any room, nothing connected. </summary>
        Offline,
        /// <summary> Host: creating the room. </summary>
        CreatingRoom,
        /// <summary> Host: room ready, starting server + local client. </summary>
        StartingHost,
        /// <summary> Host: game is running and (if joinable) open for others. </summary>
        Hosting,
        /// <summary> Client: joining the room. </summary>
        JoiningRoom,
        /// <summary> Client: in the room, connecting to the host's transport. </summary>
        Connecting,
        /// <summary> Client: connected to the host. </summary>
        Connected,
        /// <summary>
        /// The host was lost and the room carries on: a successor starts hosting, the others reconnect to it
        /// (see <see cref="HostMigrationMode"/>). Ends in Hosting / Connected — or Offline when no successor made it.
        /// </summary>
        Migrating
    }

    /// <summary> What happens to the others when the host goes away. Needs the lobby (it knows who succeeds). </summary>
    public enum HostMigrationMode
    {
        /// <summary> Host gone = session over for everyone. </summary>
        Off,
        /// <summary>
        /// The room carries on when the host DROPS (crash, lost connection). A host that leaves on purpose
        /// (<see cref="NetworkSession.Leave"/>, quitting the game) ends the session for everyone —
        /// unless it calls <see cref="NetworkSession.LeaveAndHandOver"/>.
        /// </summary>
        WhenHostDrops,
        /// <summary> The room always carries on, also when the host leaves on purpose (default). Two players: the one left hosts alone. </summary>
        Always
    }

    public enum DisconnectReason
    {
        None,
        /// <summary> The local player left (or switched to another session). </summary>
        LocalLeft,
        /// <summary> Client: the host closed the game or dropped. </summary>
        HostLeft,
        /// <summary> Transport failed to start / connect / stay connected. </summary>
        ConnectionFailed,
        /// <summary> Connecting or starting took too long. </summary>
        Timeout,
        /// <summary> Room could not be created or joined (see <see cref="NetworkSession.LastError"/>). </summary>
        RoomError,
        /// <summary> Host runs a different game/plugin version. </summary>
        VersionMismatch,
        /// <summary> The host removed this player (<see cref="NetworkLobby.Kick"/>). </summary>
        Kicked,
        /// <summary> Kicked earlier from this room — not allowed back (<see cref="LobbyRules.BanKickedPlayers"/>). </summary>
        Banned,
        /// <summary> A match is running and the room's rules don't let this player in (<see cref="LobbyRules.JoinInProgress"/>). </summary>
        MatchInProgress,
        /// <summary> The room has a password and the one given was wrong / missing. </summary>
        WrongPassword
    }

    /// <summary> Host → clients: the room password changed — kept for taking over hosting after a migration. </summary>
    public struct RoomPasswordBroadcast : FishNet.Broadcast.IBroadcast
    {
        public string Password;
    }

    /// <summary> Host → client right before it disconnects the client, so the client knows why. </summary>
    public struct SessionEndBroadcast : FishNet.Broadcast.IBroadcast
    {
        public byte Reason;
    }

    /// <summary> BIG event: <see cref="NetworkSession"/> changed state. </summary>
    public readonly struct SessionStateChanged
    {
        public SessionStateChanged(SessionState previous, SessionState current, DisconnectReason reason)
        {
            Previous = previous;
            Current = current;
            Reason = reason;
        }

        public readonly SessionState Previous;
        public readonly SessionState Current;
        /// <summary> Why the session ended — set when <see cref="Current"/> is <see cref="SessionState.Offline"/>. </summary>
        public readonly DisconnectReason Reason;
    }

    /// <summary>
    /// BIG event (every machine that stayed): the host changed and this machine is back in the game — as the new host
    /// (<see cref="IsLocalHost"/>) or connected to it. Avatars respawn (at their last pose), the match scene reloads from
    /// the lobby's seed; restore any other state the local player owns here.
    /// </summary>
    public readonly struct HostMigrated
    {
        public HostMigrated(PlayerId previousHost, PlayerId newHost, bool isLocalHost)
        {
            PreviousHost = previousHost;
            NewHost = newHost;
            IsLocalHost = isLocalHost;
        }

        public readonly PlayerId PreviousHost;
        public readonly PlayerId NewHost;
        public readonly bool IsLocalHost;
    }

    /// <summary> BIG event (every machine): a player's <see cref="NetworkPlayer"/> became available. </summary>
    public readonly struct NetworkPlayerJoined
    {
        public NetworkPlayerJoined(NetworkPlayer player) => Player = player;
        public readonly NetworkPlayer Player;
    }

    /// <summary> BIG event (every machine): a player's <see cref="NetworkPlayer"/> went away. </summary>
    public readonly struct NetworkPlayerLeft
    {
        public NetworkPlayerLeft(PlayerId playerId) => PlayerId = playerId;
        public readonly PlayerId PlayerId;
    }
}
