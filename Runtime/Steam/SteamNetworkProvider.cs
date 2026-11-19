using System;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting.Multipass;
using Steamworks;
using UnityEngine;
using Fishy = FishySteamworks.FishySteamworks;

namespace BIG.Unity.Networking.Steam
{
    /// <summary>
    /// Steam provider asset: Steam lobbies as rooms, invites through the overlay, joining from a friend's profile or the
    /// <c>+connect_lobby</c> launch argument, rich presence, and the FishySteamworks transport (P2P over the Steam relay —
    /// no ports, no NAT issues). Requires a FishySteamworks component on the NetworkManager (directly or in Multipass).
    /// </summary>
    [CreateAssetMenu(fileName = "SteamNetworkProvider", menuName = "BIG/Networking/Steam Provider")]
    public sealed class SteamNetworkProvider : NetworkProviderAsset
    {
        [Tooltip("Steam App ID. 480 = Valve's 'Spacewar' test app — works with any account, use it until you have your own.")]
        public uint AppId = 480;
        [Tooltip("Identifies this game in lobby searches (games sharing an App ID, e.g. Spacewar, don't see each other). " +
                 "Empty = Application.productName.")]
        public string GameId = "";
        [Tooltip("Builds only: if the game wasn't started by Steam, ask Steam to relaunch it (required for release builds " +
                 "so overlay/invites work). Off for development.")]
        public bool RestartThroughSteam;

        public override INetworkProvider CreateProvider() => new SteamProvider(this);
    }

    /// <summary> The local Steam user. Owns SteamAPI initialization for the game's lifetime. </summary>
    public sealed class SteamPlatform : IPlatformService, IDisposable
    {
        private readonly bool _initializedHere;
        private SteamSessionService _sessions;

        public string Name => "Steam";
        public PlayerId LocalPlayer { get; }
        public string LocalDisplayName => SteamFriends.GetPersonaName();
        public bool SupportsInviteDialog => true;

        public SteamPlatform(SteamNetworkProvider settings)
        {
            if (!Packsize.Test())
                throw new InvalidOperationException("[Steam] Steamworks.NET was built for the wrong platform (Packsize test failed).");
            if (!DllCheck.Test())
                throw new InvalidOperationException("[Steam] steam_api dll is missing or the wrong version.");

#if !UNITY_EDITOR
            if (settings.RestartThroughSteam && SteamAPI.RestartAppIfNecessary(new AppId_t(settings.AppId)))
            {
                // Steam relaunches the game — quit this instance.
                Application.Quit();
                throw new InvalidOperationException("[Steam] Relaunching through Steam.");
            }
#endif

            // Game started outside Steam (editor, local builds): tell the Steam client which app we are — no steam_appid.txt needed.
            Environment.SetEnvironmentVariable("SteamAppId", settings.AppId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", settings.AppId.ToString());

            if (!SteamAPI.Init())
                throw new InvalidOperationException("[Steam] SteamAPI.Init failed — is the Steam client running and logged in?");
            _initializedHere = true;

            LocalPlayer = new PlayerId(SteamUser.GetSteamID().m_SteamID.ToString());
            this.Log($"[Steam] Initialized: app {SteamUtils.GetAppID()}, user {LocalDisplayName} ({LocalPlayer}).");
        }

        internal void Attach(SteamSessionService sessions) => _sessions = sessions;

        public void ShowInviteDialog()
        {
            if (_sessions.CurrentLobby.IsValid())
                SteamFriends.ActivateGameOverlayInviteDialog(_sessions.CurrentLobby);
            else
                SteamFriends.ActivateGameOverlay("friends");
        }

        public void Dispose()
        {
            if (_initializedHere)
                SteamAPI.Shutdown();
        }
    }

    internal sealed class SteamProvider : INetworkProvider
    {
        private readonly SteamPlatform _platform;
        private readonly SteamSessionService _sessions;
        private NetworkManager _networkManager;
        private Fishy _transport;
        private Multipass _multipass;

        public IPlatformService Platform => _platform;
        public ISessionService Sessions => _sessions;

        public SteamProvider(SteamNetworkProvider settings)
        {
            _platform = new SteamPlatform(settings);
            _sessions = new SteamSessionService(settings, _platform);
            _platform.Attach(_sessions);
        }

        public void Bind(NetworkManager networkManager)
        {
            _networkManager = networkManager;
            _transport = networkManager.TransportManager.GetTransport<Fishy>();
            _multipass = networkManager.TransportManager.GetTransport<Multipass>();

            if (_transport == null)
                throw new InvalidOperationException("[Network] Steam provider needs a FishySteamworks transport on the NetworkManager (directly or inside Multipass).");
        }

        public void PrepareHost(SessionSettings settings)
        {
            _transport.PeerToPeer = true;
            _transport.SetMaximumClients(Mathf.Max(1, settings.MaxPlayers - 1)); // Host's own client uses the local client-host socket.
            _multipass?.SetClientTransport<Fishy>();
        }

        /// <summary> P2P over the Steam relay: the host is reached by its SteamID. </summary>
        public string GetHostAddress() => _platform.LocalPlayer.Value;

        public void PrepareClient(string hostAddress)
        {
            _transport.PeerToPeer = true;
            _transport.SetClientAddress(hostAddress);
            _multipass?.SetClientTransport<Fishy>();
        }

        /// <summary> Steam authenticates P2P peers — the remote SteamID can't be spoofed by the client. </summary>
        public bool TryGetVerifiedPlayerId(NetworkConnection connection, out PlayerId playerId)
        {
            if (connection.IsLocalClient)
            {
                playerId = _platform.LocalPlayer;
                return true;
            }

            string address = _networkManager.TransportManager.Transport.GetConnectionAddress(connection.ClientId);
            if (ulong.TryParse(address, out _))
            {
                playerId = new PlayerId(address);
                return true;
            }

            playerId = PlayerId.None;
            return false; // Connection came through another Multipass transport (e.g. LAN).
        }

        public void Tick()
        {
            SteamAPI.RunCallbacks();
            _sessions.Tick();
        }

        public void Dispose()
        {
            _sessions.Dispose();
            _platform.Dispose();
        }
    }
}
