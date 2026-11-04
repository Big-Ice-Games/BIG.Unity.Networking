using UnityEngine;

namespace BIG.Unity.Networking.Voice
{
    /// <summary>
    /// BIG event, raised on the SERVER for every voice frame a player sends (50×/s while talking).
    /// Gameplay hook for "monsters hear you": loudness + where the player stood.
    /// </summary>
    public readonly struct PlayerSpoke
    {
        public PlayerSpoke(PlayerId playerId, float loudness, Vector3 position, bool radio)
        {
            PlayerId = playerId;
            Loudness = loudness;
            Position = position;
            Radio = radio;
        }

        public readonly PlayerId PlayerId;
        /// <summary> 0..1 (dB scale, see AudioLevel). </summary>
        public readonly float Loudness;
        public readonly Vector3 Position;
        /// <summary> Spoken into a walkie-talkie (also audible at every radio on the frequency). </summary>
        public readonly bool Radio;
    }
}
