using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary> Who decides where the object is. </summary>
    public enum BodyAuthority
    {
        /// <summary> Its owner (a player) moves it and the others follow — client authority. No owner = the host. </summary>
        Owner,
        /// <summary> Always the host (AI, doors, platforms). Players ask the host through RPCs. </summary>
        Host
    }

    /// <summary> Who runs the Rigidbody simulation; everyone else keeps it kinematic and follows the synced transform. </summary>
    public enum BodyPhysics
    {
        /// <summary> No simulation anywhere — kinematic, moved by code. </summary>
        None,
        /// <summary> The owner simulates (no lag for whoever pushes / throws it); no owner = the host. Needs Owner authority. </summary>
        Owner,
        /// <summary> The host always simulates; others see it with a ping of delay. </summary>
        Host
        // Predicted (FishNet CSP: replicate / reconcile) — planned, for host-authority games.
    }

    /// <summary>
    /// How one TYPE of networked object moves: authority, movement sync, physics, ownership return. What players can do
    /// with it (push, carry, collect, use) are separate behaviours (NetworkPushable, NetworkItem...).
    /// Put it on <see cref="NetworkBody"/>. Presets from the asset's context menu (⋮): Player, Host-controlled, Physics prop.
    /// </summary>
    [CreateAssetMenu(fileName = "NetworkBodyProfile", menuName = "BIG/Networking/Network Body Profile")]
    public sealed class NetworkBodyProfile : ScriptableObject
    {
        [Header("Authority")]
        public BodyAuthority Authority = BodyAuthority.Owner;

        [Header("Movement sync (NetworkTransform)")]
        public bool SyncPosition = true;
        public bool SyncRotation = true;
        public bool SyncScale;
        [Tooltip("Send every N ticks (1 = every tick).")]
        [Range(1, 30)] public byte SendInterval = 1;
        [Tooltip("Ticks the others stay behind to smooth movement. 2 = responsive, 4+ = smoother on bad connections.")]
        [Range(1, 10)] public ushort Interpolation = 2;
        [Tooltip("Ticks to keep moving along the last direction when updates are late (0 = stop and wait).")]
        [Range(0, 10)] public ushort Extrapolation = 2;

        [Header("Physics (Rigidbody)")]
        public BodyPhysics Physics = BodyPhysics.None;
        [Tooltip("Continuous collision detection while simulated — thin or fast objects (broom handle, thrown items) don't " +
                 "slip through floors. Costs a little more physics time.")]
        public bool ContinuousCollision = true;
        [Tooltip("Fell below this height (through the map)? Put back at its last resting place. Very low = off.")]
        public float RecoverBelowY = -50f;

        [Header("Ownership return (when players can take it — see Pushable, Item...)")]
        [Tooltip("The owner gives it back to the host once it rests — the host simulates it again. Off for avatars.")]
        public bool AutoReturnToHost = true;
        [Tooltip("Not held any more and nearly still (below Rest Speed) for this long → goes back to the host.")]
        public float ReturnWhenRestingFor = 0.5f;
        public float RestSpeed = 0.1f;
        [Tooltip("Back to the host at the latest this many seconds after release / last touch, even if still moving.")]
        public float ReturnAfter = 8f;

        private void OnValidate()
        {
            if (Physics == BodyPhysics.Owner && Authority != BodyAuthority.Owner)
            {
                Debug.LogWarning($"[Network] {name}: owner-simulated physics needs Owner authority — switched it.", this);
                Authority = BodyAuthority.Owner;
            }
        }

        [ContextMenu("Preset/Player (client authority)")]
        private void PresetPlayer()
        {
            Authority = BodyAuthority.Owner;
            SyncPosition = SyncRotation = true;
            SyncScale = false;
            SendInterval = 1;
            Interpolation = 2;
            Extrapolation = 2;
            Physics = BodyPhysics.None;
            AutoReturnToHost = false;
        }

        [ContextMenu("Preset/Host-controlled (AI, doors)")]
        private void PresetHost()
        {
            Authority = BodyAuthority.Host;
            SyncPosition = SyncRotation = true;
            SyncScale = false;
            SendInterval = 1;
            Interpolation = 3;
            Extrapolation = 2;
            Physics = BodyPhysics.Host;
            AutoReturnToHost = false;
        }

        [ContextMenu("Preset/Physics prop (owner-simulated)")]
        private void PresetPhysicsProp()
        {
            Authority = BodyAuthority.Owner;
            SyncPosition = SyncRotation = true;
            SyncScale = false;
            SendInterval = 1;
            Interpolation = 2;
            Extrapolation = 1;
            Physics = BodyPhysics.Owner;
            AutoReturnToHost = true;
            ReturnWhenRestingFor = 0.5f;
            RestSpeed = 0.1f;
            ReturnAfter = 8f;
        }

        public void ApplyPreset(BodyPreset preset)
        {
            switch (preset)
            {
                case BodyPreset.Player: PresetPlayer(); break;
                case BodyPreset.HostControlled: PresetHost(); break;
                case BodyPreset.PhysicsProp: PresetPhysicsProp(); break;
            }
        }

        /// <summary> For tools / tests that create profiles from code. </summary>
        public static NetworkBodyProfile Create(BodyPreset preset)
        {
            var profile = CreateInstance<NetworkBodyProfile>();
            profile.name = preset.ToString();
            profile.ApplyPreset(preset);
            return profile;
        }
    }

    public enum BodyPreset
    {
        Player,
        HostControlled,
        PhysicsProp
    }
}
