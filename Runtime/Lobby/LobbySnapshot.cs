using System.Collections.Generic;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Copy of the lobby kept on every client, refreshed on each change — all that survives when the host disappears.
    /// The successor's <see cref="NetworkLobby"/> restores the room from it (host migration).
    /// </summary>
    internal sealed class LobbySnapshot
    {
        /// <summary> In lobby order (join order) — the order successors are picked in. </summary>
        public readonly List<LobbyMember> Members = new List<LobbyMember>();
        public readonly Dictionary<string, string> Settings = new Dictionary<string, string>();
        public readonly List<string> Banned = new List<string>();
        public LobbyPhase Phase;
        public int Seed;
        public string MatchScene = "";

        public PlayerId Host
        {
            get
            {
                foreach (var member in Members)
                    if (member.IsHost) return member.Id;
                return PlayerId.None;
            }
        }

        public bool TryGetMember(PlayerId playerId, out LobbyMember member)
        {
            foreach (var m in Members)
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
    }
}
