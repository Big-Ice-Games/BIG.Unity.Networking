using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;
using FishNet.Transporting.Tugboat;
using UnityEngine;

namespace BIG.Unity.Networking.Lan
{
    /// <summary>
    /// LAN provider asset: FishNet Tugboat transport + UDP broadcast discovery. No accounts, no internet.
    /// Also the provider for local testing (several instances on one PC).
    /// Requires a Tugboat component on the NetworkManager (directly or inside Multipass).
    /// </summary>
    [CreateAssetMenu(fileName = "LanNetworkProvider", menuName = "BIG/Networking/LAN Provider")]
    public sealed class LanNetworkProvider : NetworkProviderAsset
    {
        [Tooltip("Tugboat game port.")]
        public ushort GamePort = 7770;
        [Tooltip("UDP port for discovery beacons. Must match between players and be free on the machine.")]
        public ushort DiscoveryPort = 47777;
        [Tooltip("Identifies this game in beacons — other games on the same port are ignored. Empty = Application.productName.")]
        public string GameId = "";
        [Tooltip("Seconds between host beacons.")]
        public float BeaconInterval = 1f;
        [Tooltip("How long Search() listens for beacons.")]
        public float SearchDuration = 1.5f;

        public override INetworkProvider CreateProvider() => new LanProvider(this);
    }

    /// <summary>
    /// LAN identity is per PROCESS (random guid each run), not persisted: two instances on the same PC
    /// share PlayerPrefs, so a persisted id would collide. Reconnects within one run keep the identity.
    /// </summary>
    public sealed class LanPlatform : IPlatformService
    {
        public string Name => "LAN";
        public PlayerId LocalPlayer { get; }
        public string LocalDisplayName { get; set; }
        public bool SupportsInviteDialog => false;

        public LanPlatform()
        {
            string guid = Guid.NewGuid().ToString("N");
            LocalPlayer = new PlayerId("lan-" + guid);
            LocalDisplayName = $"{Environment.UserName}#{guid.Substring(0, 4)}";
        }

        public void ShowInviteDialog() { }
    }

    internal sealed class LanProvider : INetworkProvider
    {
        private readonly LanNetworkProvider _settings;
        private readonly LanPlatform _platform = new LanPlatform();
        private readonly LanSessionService _sessions;
        private Tugboat _tugboat;
        private Multipass _multipass;

        public IPlatformService Platform => _platform;
        public ISessionService Sessions => _sessions;

        public LanProvider(LanNetworkProvider settings)
        {
            _settings = settings;
            _sessions = new LanSessionService(settings, _platform);
        }

        public void Bind(NetworkManager networkManager)
        {
            _tugboat = networkManager.TransportManager.GetTransport<Tugboat>();
            _multipass = networkManager.TransportManager.GetTransport<Multipass>();

            if (_tugboat == null)
                throw new InvalidOperationException("[Network] LAN provider needs a Tugboat transport on the NetworkManager (directly or inside Multipass).");
        }

        public void PrepareHost(SessionSettings settings)
        {
            _tugboat.SetPort(_settings.GamePort);
            _tugboat.SetMaximumClients(Mathf.Max(1, settings.MaxPlayers)); // The host's own client is a Tugboat connection too.
            _tugboat.SetClientAddress("localhost");
            _multipass?.SetClientTransport<Tugboat>();
        }

        public string GetHostAddress() => $"{GetLocalIPv4()}:{_settings.GamePort}";

        public void PrepareClient(string hostAddress)
        {
            LanAddress.Parse(hostAddress, _settings.GamePort, out string ip, out ushort port);
            _tugboat.SetClientAddress(ip);
            _tugboat.SetPort(port);
            _multipass?.SetClientTransport<Tugboat>();
        }

        public bool TryGetVerifiedPlayerId(NetworkConnection connection, out PlayerId playerId)
        {
            playerId = PlayerId.None;
            return false; // LAN can't vouch for anyone — the hello claim is used.
        }

        public void Tick() => _sessions.Tick();

        public void Dispose() => _sessions.Dispose();

        /// <summary> Best guess for display / typed joins. Discovery uses the beacon's sender address instead. </summary>
        internal static string GetLocalIPv4()
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.GetIPProperties().GatewayAddresses.Count == 0) continue; // Skips most virtual adapters.

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                        return address.Address.ToString();
                }
            }

            return IPAddress.Loopback.ToString();
        }
    }

    internal static class LanAddress
    {
        /// <summary> "ip" or "ip:port" → parts; missing port → default. </summary>
        public static void Parse(string address, ushort defaultPort, out string ip, out ushort port)
        {
            int colon = address.LastIndexOf(':');
            if (colon > 0 && ushort.TryParse(address.Substring(colon + 1), out port))
            {
                ip = address.Substring(0, colon);
                return;
            }

            ip = address;
            port = defaultPort;
        }
    }
}
