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
        Connected
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
        VersionMismatch
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
