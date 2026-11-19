using System;
using System.Collections.Generic;

namespace BIG.Unity.Networking
{
    public enum SessionVisibility
    {
        /// <summary> Anyone can find and join (LAN: visible in discovery). </summary>
        Public,
        /// <summary> Friends can find and join; others only by invite (LAN: same as Public). </summary>
        FriendsOnly,
        /// <summary> Invite only (LAN: hidden from discovery, join by address). </summary>
        Private
    }

    public sealed class SessionSettings
    {
        public string Name = "";
        public int MaxPlayers = 4;
        public SessionVisibility Visibility = SessionVisibility.FriendsOnly;
        public bool Joinable = true;
        /// <summary> Empty = open. Set = joiners must give it (<see cref="NetworkSession.Join(SessionAddress, string)"/>). Never published. </summary>
        public string Password = "";
    }

    /// <summary> Opaque address of a room, meaningful only to the provider that produced it. </summary>
    [Serializable]
    public readonly struct SessionAddress : IEquatable<SessionAddress>
    {
        public readonly string Provider;
        public readonly string Value;

        public SessionAddress(string provider, string value)
        {
            Provider = provider;
            Value = value;
        }

        public bool IsValid => !string.IsNullOrEmpty(Provider) && !string.IsNullOrEmpty(Value);

        public bool Equals(SessionAddress other) => Provider == other.Provider && Value == other.Value;
        public override bool Equals(object obj) => obj is SessionAddress other && Equals(other);
        public override int GetHashCode() => (Provider, Value).GetHashCode();
        public override string ToString() => $"{Provider}:{Value}";
    }

    /// <summary> Snapshot of a room — from search results, or the room the player is in. </summary>
    public sealed class SessionInfo
    {
        /// <summary> Data key where the host publishes its transport address. </summary>
        public const string HOST_ADDRESS_KEY = "big.host";
        /// <summary> Data key with <see cref="NetworkVersion.Current"/> of the host. </summary>
        public const string VERSION_KEY = "big.version";
        /// <summary> Data key: "0" while a running match doesn't take new players (set by <see cref="NetworkLobby"/>). </summary>
        public const string OPEN_KEY = "big.open";
        /// <summary> Data key: "1" when the room has a password (the password itself is never published). </summary>
        public const string LOCKED_KEY = "big.locked";

        public SessionAddress Address;
        public string Name = "";
        public PlayerId Owner;
        public int MemberCount;
        public int MaxPlayers;
        public bool Joinable;
        public SessionVisibility Visibility;
        public readonly Dictionary<string, string> Data = new Dictionary<string, string>();

        public string HostAddress => Data.TryGetValue(HOST_ADDRESS_KEY, out var value) ? value : "";
        public string Version => Data.TryGetValue(VERSION_KEY, out var value) ? value : "";
        public bool IsFull => MaxPlayers > 0 && MemberCount >= MaxPlayers;
        /// <summary> A NEW player can get in now: joinable, not full, and not a running match closed to newcomers. </summary>
        public bool IsOpenForNewPlayers => Joinable && !IsFull && !(Data.TryGetValue(OPEN_KEY, out var open) && open == "0");
        /// <summary> A running match that only takes players who were already in it (room still joinable for them). </summary>
        public bool IsMatchInProgress => Data.TryGetValue(OPEN_KEY, out var open) && open == "0";
        /// <summary> Needs a password to join. </summary>
        public bool IsLocked => Data.TryGetValue(LOCKED_KEY, out var locked) && locked == "1";
    }

    public enum SessionError
    {
        None,
        NotFound,
        Full,
        NotJoinable,
        VersionMismatch,
        PlatformError,
        Timeout
    }

    public readonly struct SessionResult
    {
        public readonly SessionError Error;
        public readonly SessionInfo Info;
        public readonly string Message;

        private SessionResult(SessionError error, SessionInfo info, string message)
        {
            Error = error;
            Info = info;
            Message = message;
        }

        public bool Success => Error == SessionError.None;

        public static SessionResult Ok(SessionInfo info) => new SessionResult(SessionError.None, info, "");
        public static SessionResult Fail(SessionError error, string message = "") => new SessionResult(error, null, message);
    }

    /// <summary>
    /// Rooms ("lobbies"): create, find, join, invite. Steam lobby / EOS lobby / LAN beacon behind one API.
    /// A room is only the directory of a game — the game itself runs over the FishNet transport,
    /// whose host address the room publishes under <see cref="SessionInfo.HOST_ADDRESS_KEY"/>.
    /// Implementations are main-thread only and are pumped by <see cref="INetworkProvider.Tick"/>.
    /// </summary>
    public interface ISessionService
    {
        /// <summary> Room the local player is in, null when outside of any room. </summary>
        SessionInfo Current { get; }

        void Create(SessionSettings settings, Action<SessionResult> done);
        void Join(SessionAddress address, Action<SessionResult> done);
        void Leave();
        void Search(Action<IReadOnlyList<SessionInfo>> done);

        /// <summary> Owner only. Hub = joinable, mission = not. </summary>
        void SetJoinable(bool joinable);
        /// <summary> Owner only. Turn a private party into a public room (or back) without recreating it. </summary>
        void SetVisibility(SessionVisibility visibility);
        /// <summary> Owner only. Public room data visible before joining. </summary>
        void SetData(string key, string value);
        /// <summary> Owner only. Room name shown in search results. </summary>
        void SetName(string name);
        /// <summary> Owner only. Current number of players — published in search results. </summary>
        void SetMemberCount(int count);

        void Invite(PlayerId friend);

        /// <summary>
        /// Host migration, called on every machine that stays: <paramref name="newHost"/> (maybe this machine) hosts the room
        /// from now on. The room is kept — no Leave/Join. When it's this machine, owner calls (SetData...) must work once
        /// the platform agrees (queue them until then); the machine that owns the room at platform level hands it over.
        /// </summary>
        void SetHost(PlayerId newHost);

        /// <summary> The player accepted an invite (overlay, launch argument...) — the session should join it. </summary>
        event Action<SessionAddress> JoinRequested;
    }
}
