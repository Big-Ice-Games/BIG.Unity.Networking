using UnityEngine;

namespace BIG.Unity.Networking
{
    public enum JoinInProgress
    {
        /// <summary> Anyone may join a running match (drop-in co-op). </summary>
        Anyone,
        /// <summary> Only players who were already in this match may come back (crash / disconnect recovery). </summary>
        ReturningPlayers,
        /// <summary> Nobody joins a running match — the room opens again in the lobby. </summary>
        Nobody
    }

    /// <summary>
    /// Room rules as an asset (Create > BIG > Networking > Lobby Rules) — one per game mode if needed, assigned to the
    /// NetworkLobby prefab; the host can also swap them at runtime (<see cref="NetworkLobby.Rules"/>).
    /// Enforced by the host when a player arrives; refused players see the reason (MatchInProgress / Banned).
    /// </summary>
    [CreateAssetMenu(fileName = "LobbyRules", menuName = "BIG/Networking/Lobby Rules")]
    public sealed class LobbyRules : ScriptableObject
    {
        [Tooltip("Who may join while a match is running.")]
        public JoinInProgress JoinInProgress = JoinInProgress.ReturningPlayers;
        [Tooltip("A kicked player can't come back to this room until the host closes it.")]
        public bool BanKickedPlayers = true;
        [Tooltip("Everyone becomes not-ready when the match ends.")]
        public bool ResetReadyAfterMatch = true;
    }
}
