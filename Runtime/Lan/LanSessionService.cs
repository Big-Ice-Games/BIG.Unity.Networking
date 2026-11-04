using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace BIG.Unity.Networking.Lan
{
    /// <summary>
    /// LAN "rooms": the host broadcasts a UDP beacon, searchers listen for it. There is no room server —
    /// joining a LAN room is connecting straight to the host, so Join only validates and resolves the address.
    /// The host's IP is taken from the beacon's SENDER address (reliable with VPN/virtual adapters).
    /// Main-thread only, non-blocking sockets polled from <see cref="Tick"/>.
    /// Launch argument <c>-big-join ip[:port]</c> joins on start (handy for test builds).
    /// </summary>
    internal sealed class LanSessionService : ISessionService, IDisposable
    {
        private const string PROVIDER = "LAN";
        private const string JOIN_ARGUMENT = "-big-join";
        private static readonly byte[] MAGIC = Encoding.ASCII.GetBytes("BIGL");
        private const byte FORMAT = 1;

        private readonly LanNetworkProvider _settings;
        private readonly LanPlatform _platform;
        private readonly string _gameId;
        private readonly byte[] _receiveBuffer = new byte[1024];

        private UdpClient _beaconSocket;
        private string _sessionId;
        private float _nextBeacon;

        private Socket _searchSocket;
        private float _searchEnd;
        private readonly Dictionary<string, SessionInfo> _found = new Dictionary<string, SessionInfo>();
        private readonly List<Action<IReadOnlyList<SessionInfo>>> _searchCallbacks = new List<Action<IReadOnlyList<SessionInfo>>>();
        private bool _launchArgumentChecked;

        public SessionInfo Current { get; private set; }
        public event Action<SessionAddress> JoinRequested;

        public LanSessionService(LanNetworkProvider settings, LanPlatform platform)
        {
            _settings = settings;
            _platform = platform;
            _gameId = string.IsNullOrEmpty(settings.GameId) ? Application.productName : settings.GameId;
        }

        #region ISessionService

        public void Create(SessionSettings settings, Action<SessionResult> done)
        {
            _sessionId = Guid.NewGuid().ToString("N");
            string hostAddress = $"{LanProvider.GetLocalIPv4()}:{_settings.GamePort}";

            Current = new SessionInfo
            {
                Address = new SessionAddress(PROVIDER, hostAddress),
                Name = string.IsNullOrEmpty(settings.Name) ? _platform.LocalDisplayName : settings.Name,
                Owner = _platform.LocalPlayer,
                MemberCount = 1,
                MaxPlayers = settings.MaxPlayers,
                Joinable = settings.Joinable,
                Visibility = settings.Visibility
            };

            if (settings.Visibility != SessionVisibility.Private)
            {
                _beaconSocket = new UdpClient { EnableBroadcast = true };
                _nextBeacon = 0f;
            }

            done(SessionResult.Ok(Current));
        }

        public void Join(SessionAddress address, Action<SessionResult> done)
        {
            if (address.Provider != PROVIDER)
            {
                done(SessionResult.Fail(SessionError.NotFound, $"Not a LAN address: {address}"));
                return;
            }

            SessionInfo info = FindKnown(address);
            if (info != null)
            {
                if (!info.Joinable) { done(SessionResult.Fail(SessionError.NotJoinable)); return; }
                if (info.IsFull) { done(SessionResult.Fail(SessionError.Full)); return; }
            }
            else
            {
                // Typed address / launch argument — nothing known up front; version is checked in the hello handshake.
                info = new SessionInfo { Address = address, Name = address.Value, Joinable = true };
                info.Data[SessionInfo.HOST_ADDRESS_KEY] = address.Value;
            }

            Current = info;
            done(SessionResult.Ok(info));
        }

        public void Leave()
        {
            Current = null;
            _sessionId = null;
            _beaconSocket?.Close();
            _beaconSocket = null;
        }

        public void Search(Action<IReadOnlyList<SessionInfo>> done)
        {
            _searchCallbacks.Add(done);
            if (_searchSocket != null) return; // Already listening — this caller gets the same results.

            _found.Clear();
            try
            {
                _searchSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // Several instances on one PC may search at once.
                _searchSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _searchSocket.Blocking = false;
                _searchSocket.Bind(new IPEndPoint(IPAddress.Any, _settings.DiscoveryPort));
                _searchEnd = Time.unscaledTime + _settings.SearchDuration;
            }
            catch (SocketException e)
            {
                this.Log($"[Network] LAN search failed to open port {_settings.DiscoveryPort}: {e.Message}", LogLevel.Error);
                CloseSearch();
                FinishSearch();
            }
        }

        public void SetJoinable(bool joinable)
        {
            if (IsOwner) Current.Joinable = joinable;
        }

        public void SetData(string key, string value)
        {
            if (IsOwner) Current.Data[key] = value;
        }

        public void SetMemberCount(int count)
        {
            if (IsOwner) Current.MemberCount = count;
        }

        public void Invite(PlayerId friend) =>
            this.Log("[Network] LAN has no invites — share the host address or use Search().", LogLevel.Warning);

        #endregion

        private bool IsOwner => Current != null && Current.Owner == _platform.LocalPlayer;

        public void Tick()
        {
            if (!_launchArgumentChecked)
            {
                _launchArgumentChecked = true;
                if (TryGetLaunchJoinAddress(out string launchAddress))
                    JoinRequested?.Invoke(new SessionAddress(PROVIDER, launchAddress));
            }

            TickBeacon();
            TickSearch();
        }

        #region Beacon

        private void TickBeacon()
        {
            // Publish only once the host is actually reachable (NetworkSession sets the host address when Hosting).
            if (_beaconSocket == null || !IsOwner || !Current.Data.ContainsKey(SessionInfo.HOST_ADDRESS_KEY)) return;
            if (Time.unscaledTime < _nextBeacon) return;
            _nextBeacon = Time.unscaledTime + _settings.BeaconInterval;

            byte[] packet = WriteBeacon();
            try
            {
                _beaconSocket.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, _settings.DiscoveryPort));
                // Loopback too: some adapters don't deliver broadcasts to listeners on the same machine.
                _beaconSocket.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, _settings.DiscoveryPort));
            }
            catch (SocketException e)
            {
                this.Log($"[Network] LAN beacon send failed: {e.Message}", LogLevel.Warning);
            }
        }

        private byte[] WriteBeacon()
        {
            using var stream = new MemoryStream(256);
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(MAGIC);
            writer.Write(FORMAT);
            writer.Write(_gameId);
            writer.Write(_sessionId);
            writer.Write(_settings.GamePort);
            writer.Write(Current.Name);
            writer.Write(Current.Owner.Value);
            writer.Write((byte)Current.MemberCount);
            writer.Write((byte)Current.MaxPlayers);
            writer.Write(Current.Joinable);
            writer.Write(NetworkVersion.Current);
            return stream.ToArray();
        }

        #endregion

        #region Search

        private void TickSearch()
        {
            if (_searchSocket == null) return;

            try
            {
                while (_searchSocket.Available > 0)
                {
                    EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    int length = _searchSocket.ReceiveFrom(_receiveBuffer, ref sender);
                    ReadBeacon(_receiveBuffer, length, (IPEndPoint)sender);
                }
            }
            catch (SocketException e)
            {
                this.Log($"[Network] LAN search receive failed: {e.Message}", LogLevel.Warning);
            }

            if (Time.unscaledTime >= _searchEnd)
            {
                CloseSearch();
                FinishSearch();
            }
        }

        private void ReadBeacon(byte[] buffer, int length, IPEndPoint sender)
        {
            try
            {
                using var stream = new MemoryStream(buffer, 0, length);
                using var reader = new BinaryReader(stream, Encoding.UTF8);

                for (int i = 0; i < MAGIC.Length; i++)
                    if (reader.ReadByte() != MAGIC[i]) return;
                if (reader.ReadByte() != FORMAT) return;
                if (reader.ReadString() != _gameId) return;

                string sessionId = reader.ReadString();
                if (sessionId == _sessionId) return; // Own beacon.

                ushort port = reader.ReadUInt16();
                string hostAddress = $"{sender.Address}:{port}";

                var info = new SessionInfo
                {
                    Address = new SessionAddress(PROVIDER, hostAddress),
                    Name = reader.ReadString(),
                    Owner = new PlayerId(reader.ReadString()),
                    MemberCount = reader.ReadByte(),
                    MaxPlayers = reader.ReadByte(),
                    Joinable = reader.ReadBoolean(),
                    Visibility = SessionVisibility.Public
                };
                info.Data[SessionInfo.HOST_ADDRESS_KEY] = hostAddress;
                info.Data[SessionInfo.VERSION_KEY] = reader.ReadString();

                // Same host heard via broadcast AND loopback — keep the first, it's reachable either way.
                if (!_found.ContainsKey(sessionId))
                    _found[sessionId] = info;
            }
            catch (EndOfStreamException)
            {
                // Truncated / foreign packet — ignore.
            }
        }

        private void FinishSearch()
        {
            var results = new List<SessionInfo>(_found.Values);
            var callbacks = _searchCallbacks.ToArray();
            _searchCallbacks.Clear();

            foreach (var callback in callbacks)
                callback(results);
        }

        private void CloseSearch()
        {
            _searchSocket?.Close();
            _searchSocket = null;
        }

        private SessionInfo FindKnown(SessionAddress address)
        {
            foreach (var info in _found.Values)
                if (info.Address.Equals(address))
                    return info;
            return null;
        }

        #endregion

        private static bool TryGetLaunchJoinAddress(out string address)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == JOIN_ARGUMENT)
                {
                    address = args[i + 1];
                    return true;
                }
            }

            address = null;
            return false;
        }

        public void Dispose()
        {
            Leave();
            CloseSearch();
            _searchCallbacks.Clear();
        }
    }
}
