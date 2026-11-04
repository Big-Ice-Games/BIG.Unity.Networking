using System;
using FishNet.Connection;
using FishNet.Managing;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary> Local player's platform: identity, display name, overlay. </summary>
    public interface IPlatformService
    {
        /// <summary> "LAN", "Steam", "EOS"... </summary>
        string Name { get; }
        PlayerId LocalPlayer { get; }
        string LocalDisplayName { get; }

        bool SupportsInviteDialog { get; }
        /// <summary> Opens the platform's invite UI (Steam overlay). No-op where unsupported. </summary>
        void ShowInviteDialog();
    }

    /// <summary>
    /// One platform's whole networking stack: identity, rooms and the matching FishNet transport.
    /// The ONLY place that knows Steam/EOS/LAN — game code talks to <see cref="NetworkSession"/>.
    /// <para>
    /// FishNet initializes its transport once, in NetworkManager.Awake, so transports can't be swapped at runtime:
    /// the transport component (Tugboat, FishySteamworks...) must already be on the NetworkManager — directly
    /// or inside Multipass (a Multipass host accepts players from all its transports at once).
    /// The provider finds its own transport there and configures it.
    /// </para>
    /// </summary>
    public interface INetworkProvider : IDisposable
    {
        IPlatformService Platform { get; }
        ISessionService Sessions { get; }

        /// <summary> Called once by <see cref="NetworkSession"/>: find and configure this provider's transport. </summary>
        void Bind(NetworkManager networkManager);

        /// <summary> Before starting the server + local client on this machine. </summary>
        void PrepareHost(SessionSettings settings);

        /// <summary> What remote clients need to reach this host — published in the room data. </summary>
        string GetHostAddress();

        /// <summary> Before connecting a client to a host address taken from the room. </summary>
        void PrepareClient(string hostAddress);

        /// <summary>
        /// Server side: identity the TRANSPORT guarantees for a connection (Steam knows the remote SteamID).
        /// False when the transport can't tell (LAN) — then the client's claimed identity is used.
        /// </summary>
        bool TryGetVerifiedPlayerId(NetworkConnection connection, out PlayerId playerId);

        /// <summary> Pumped every frame by <see cref="NetworkSession"/> (sockets, platform callbacks). </summary>
        void Tick();
    }

    /// <summary> Build compatibility tag — players on different versions can't join each other. </summary>
    public static class NetworkVersion
    {
        /// <summary> Bump when the network protocol of the PLUGIN changes incompatibly. </summary>
        public const int PROTOCOL = 1;

        /// <summary> Game version + plugin protocol. Override <see cref="GameVersion"/> to decouple from Application.version. </summary>
        public static string Current => $"{GameVersion}/{PROTOCOL}";

        public static string GameVersion
        {
            get => _gameVersion ??= Application.version;
            set => _gameVersion = value;
        }

        private static string _gameVersion;
    }
}
