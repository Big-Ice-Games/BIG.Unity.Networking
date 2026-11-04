using System;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Platform-agnostic player identity (SteamID64, EOS ProductUserId, LAN guid...).
    /// Stable across reconnects and host migration — unlike FishNet connection ids. Use it as the key
    /// for everything that belongs to a player.
    /// </summary>
    [Serializable]
    public readonly struct PlayerId : IEquatable<PlayerId>
    {
        public static readonly PlayerId None = default;

        public readonly string Value;

        public PlayerId(string value) => Value = value;

        public bool IsValid => !string.IsNullOrEmpty(Value);

        public bool Equals(PlayerId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is PlayerId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public static bool operator ==(PlayerId a, PlayerId b) => a.Equals(b);
        public static bool operator !=(PlayerId a, PlayerId b) => !a.Equals(b);
        public override string ToString() => IsValid ? Value : "<none>";
    }
}
