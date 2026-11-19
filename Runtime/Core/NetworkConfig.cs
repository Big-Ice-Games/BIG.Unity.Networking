using System;
using FishNet.Managing;
using FishNet.Managing.Timing;
using FishNet.Object;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// ALL settings of BIG.Unity.Networking in one asset, assigned to <see cref="NetworkSession"/>
    /// (Create > BIG > Networking > Network Config; also under Project Settings > BIG Networking).
    /// <para>
    /// Editable in play mode: sections marked "live" apply immediately, the others with the next Host / Join.
    /// Every machine must use the same values (tick rate, migration) — it ships with the build.
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "NetworkConfig", menuName = "BIG/Networking/Network Config")]
    public sealed class NetworkConfig : ScriptableObject
    {
        public const string ROOM_NAME_PLAYER_TOKEN = "{player}";

        [Header("Platform — next start")]
        [Tooltip("Provider used in builds (and in the editor when no editor provider is set).")]
        public NetworkProviderAsset Provider;
        [Tooltip("Optional provider for play mode in the editor, e.g. LAN while builds use Steam.")]
        public NetworkProviderAsset EditorProvider;

        [Header("Timing — live")]
        [Tooltip("Network ticks per second: how often state is sent. 30 suits co-op; higher = smoother, more bandwidth.")]
        [Range(10, 128)] public ushort TickRate = 30;
        [Tooltip("Unity: physics in FixedUpdate as usual (client authority / owner-simulated bodies). " +
                 "TimeManager: physics stepped on network ticks — for predicted (CSP) bodies later.")]
        public PhysicsMode PhysicsMode = PhysicsMode.Unity;

        [Header("Connection — next Host / Join")]
        [Tooltip("Seconds to wait for the host to start / the client to connect.")]
        [Min(1f)] public float ConnectTimeout = 15f;

        [Header("Room defaults — next Host()")]
        [Tooltip("Name shown in room lists. " + ROOM_NAME_PLAYER_TOKEN + " = the host's display name.")]
        public string RoomName = ROOM_NAME_PLAYER_TOKEN + "'s game";
        [Range(1, 64)] public int MaxPlayers = 4;
        public SessionVisibility Visibility = SessionVisibility.FriendsOnly;
        [Tooltip("Empty = open room. Set = players must type it to get in (rooms show a lock).")]
        public string Password = "";

        [Header("Lobby — next Host")]
        [Tooltip("Prefab with NetworkObject (Is Global) + NetworkLobby — the host spawns it when hosting starts, so players " +
                 "gather in a lobby before the match. Empty = no lobby phase (and no host migration).")]
        public NetworkObject LobbyPrefab;

        [Header("Host migration — live")]
        [Tooltip("What happens to the others when the host goes away. Needs the lobby prefab.")]
        public HostMigrationMode HostMigration = HostMigrationMode.Always;
        [Tooltip("Seconds each successor gets to come up before the next one in line takes over.")]
        [Min(2f)] public float MigrationRoundTimeout = 12f;
        [Tooltip("How often the host checks NetworkMigratable state and sends what changed to the clients.")]
        [Min(0.1f)] public float MigrationStateInterval = 1f;

        [Header("Dev tools — live")]
        [Tooltip("Developer overlay with collapsible modules. Development only = editor, Development builds and builds with the " +
                 "DEVELOPMENT scripting define (Player settings / build profile).")]
        public DevTools.DevOverlayMode DevOverlay = DevTools.DevOverlayMode.DevelopmentOnly;
        [Tooltip("Hides / shows the overlay.")]
        public UnityEngine.InputSystem.Key DevOverlayKey = UnityEngine.InputSystem.Key.F1;
        [Tooltip("Scene the overlay's \"Start match\" button loads (must be in Build Profiles). Empty = no button.")]
        public string DevMatchScene = "";
        [Tooltip("Created and assigned by the editor (Assets/BIG.Networking/). Must be an ASSET: the editor fills in the text " +
                 "data (ICU) builds need — a panel made at runtime renders text only in the editor.")]
        public UnityEngine.UIElements.PanelSettings DevOverlayPanel;

        /// <summary> Raised when the asset is edited (inspector in play mode) — live sections re-apply. </summary>
        public event Action<NetworkConfig> Changed;

        /// <summary> Settings for <see cref="NetworkSession.Host()"/> from the room defaults. </summary>
        public SessionSettings CreateRoomSettings(IPlatformService platform) => new SessionSettings
        {
            Name = FormatRoomName(RoomName, platform),
            MaxPlayers = MaxPlayers,
            Visibility = Visibility,
            Password = Password ?? ""
        };

        public static string FormatRoomName(string template, IPlatformService platform) =>
            string.IsNullOrWhiteSpace(template)
                ? platform.LocalDisplayName
                : template.Replace(ROOM_NAME_PLAYER_TOKEN, platform.LocalDisplayName);

        internal NetworkProviderAsset ActiveProvider => Application.isEditor && EditorProvider != null ? EditorProvider : Provider;

        internal void ApplyTiming(NetworkManager networkManager)
        {
            networkManager.TimeManager.SetTickRate(TickRate);
            if (networkManager.TimeManager.PhysicsMode != PhysicsMode)
                networkManager.TimeManager.SetPhysicsMode(PhysicsMode);
        }

        private void OnValidate()
        {
            if (Application.isPlaying) Changed?.Invoke(this);
        }
    }
}
