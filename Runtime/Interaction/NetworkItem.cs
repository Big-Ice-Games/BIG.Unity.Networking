using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary> Where an item is. </summary>
    public enum ItemState : byte
    {
        /// <summary> Lying in the world — visible, collides, physics as its body profile says. </summary>
        World,
        /// <summary> In a player's hand — visible, follows the hand, no collisions. </summary>
        Held,
        /// <summary> In a player's inventory — hidden, travels with the player. </summary>
        Stowed
    }

    /// <summary> Optional on the player avatar (or a child): where held / stowed items attach. Without it: in front of the avatar. </summary>
    public interface IItemAnchors
    {
        /// <summary> Inside of the palm (a hand bone, or an empty child of it). The item's Grip is placed exactly here. </summary>
        Transform HandAnchor { get; }
        /// <summary> Null = the avatar itself (stowed items are hidden anyway). </summary>
        Transform StowAnchor { get; }
    }

    /// <summary>
    /// On the player avatar (any component, e.g. the animation script): told on EVERY machine when this player's held item
    /// changes — set the hold animation from <see cref="NetworkItem.HoldPose"/>, IK the off hand to
    /// <see cref="NetworkItem.OffHandGrip"/>. Also called for late joiners / after a host migration when the avatar spawns
    /// while already holding something (previous = null).
    /// </summary>
    public interface IItemHolder
    {
        void OnHeldItemChanged(NetworkItem previous, NetworkItem current);
    }

    /// <summary>
    /// Something a player can own as an item: its <see cref="ItemState"/> (world / hand / inventory) and holder, synced and
    /// applied on every machine — visibility, colliders, physics, following the holder's hand. What players can DO with
    /// it comes from behaviours next to it: <see cref="NetworkCarryable"/> (hold, throw, use), <see cref="NetworkCollectible"/>
    /// (inventory). Inventory UI / slots / limits are the game's: list a player's items with <see cref="ItemsOf"/>, refuse
    /// takes with <see cref="ServerTakeRule"/>.
    /// <para>The item object stays alive in an inventory — it keeps its own state (charge, ammo) and survives host migration
    /// with <see cref="NetworkMigratable"/>. Its holder disconnecting drops it where they stood.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkBody))]
    public sealed class NetworkItem : NetworkBaseBehaviour, IOwnershipRule
    {
        private static readonly List<NetworkItem> Items = new List<NetworkItem>();

        [SerializeField, Tooltip("The game's item type (\"flashlight\", \"battery\") — for inventory UI / rules.")]
        private string _itemId = "";
        [SerializeField] private string _displayName = "";
        [SerializeField, Tooltip("Host refuses takes from avatars farther than this.")]
        private float _reach = 2.5f;
        [Header("Holding")]
        [SerializeField, Tooltip("Where the hand grabs it (child transform: position = inside of the fist, rotation = the hand's). " +
                                 "Placed exactly on the holder's HandAnchor. Empty = the item's pivot.")]
        private Transform _grip;
        [SerializeField, Tooltip("Optional second grab point (child) for the other hand — an IK target for two-handed items.")]
        private Transform _offHandGrip;
        [SerializeField, Tooltip("Which hold animation the holder plays (\"Broom\", \"OneHandSmall\", \"TwoHanded\") — read it in IItemHolder.")]
        private string _holdPose = "";
        [SerializeField, Tooltip("Fine-tune on top of the grip, in the hand anchor's space.")]
        private Vector3 _holdOffset = Vector3.zero;
        [SerializeField] private Vector3 _holdRotation = Vector3.zero;

        [Migrate] private readonly SyncVar<ItemState> _state = new SyncVar<ItemState>(ItemState.World);
        [Migrate] private readonly SyncVar<string> _holder = new SyncVar<string>("");

        private NetworkBody _body;
        private Renderer[] _renderers;
        private Collider[] _colliders;
        private ItemState? _predicted; // Owner: state applied locally before the host confirms (throw / drop).
        private ItemState _applied = (ItemState)255;
        private Vector3 _gripPosition = Vector3.zero;         // Grip in the item's local space (cached — it doesn't move).
        private Quaternion _gripRotation = Quaternion.identity;
        private PlayerId _inHandOf = PlayerId.None;           // As applied on this machine — drives IItemHolder.

        // Every machine: whose hand holds which item (to tell holders previous → current).
        private static readonly Dictionary<PlayerId, NetworkItem> HandOf = new Dictionary<PlayerId, NetworkItem>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => HandOf.Clear(); // Domain reload off in the editor.

        /// <summary> Every spawned item on this machine. </summary>
        public static IReadOnlyList<NetworkItem> All => Items;

        /// <summary> Host: extra rule for takes (inventory full, quest item...). Null = allowed. </summary>
        public static Func<PlayerId, NetworkItem, ItemState, bool> ServerTakeRule { get; set; }

        /// <summary> Every machine: some item changed state or holder (refresh inventory UI here). </summary>
        public static event Action<NetworkItem> AnyChanged;

        public string ItemId => _itemId;
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? name : _displayName;
        public float Reach => _reach;
        public NetworkBody Body => _body;
        /// <summary> Where the hand grabs it (null = the pivot). </summary>
        public Transform Grip => _grip;
        /// <summary> Second grab point for the other hand (IK target), or null. </summary>
        public Transform OffHandGrip => _offHandGrip;
        /// <summary> The game's hold animation id ("Broom"...), "" = default. </summary>
        public string HoldPose => _holdPose;
        public ItemState State => _predicted ?? _state.Value;
        public PlayerId Holder => new PlayerId(_holder.Value);
        /// <summary> In this machine's player's hand or inventory. </summary>
        public bool IsMine => State != ItemState.World && Holder == Session.Platform.LocalPlayer;

        /// <summary> Items <paramref name="player"/> holds or carries (their inventory + hand). </summary>
        public static IEnumerable<NetworkItem> ItemsOf(PlayerId player)
        {
            foreach (var item in Items)
                if (item.State != ItemState.World && item.Holder == player) yield return item;
        }

        /// <summary> The item <paramref name="player"/> has in hand, or null. </summary>
        public static NetworkItem HeldBy(PlayerId player)
        {
            foreach (var item in Items)
                if (item.State == ItemState.Held && item.Holder == player) return item;
            return null;
        }

        #region Lifecycle

        protected override void Awake()
        {
            base.Awake();
            _body = GetComponent<NetworkBody>();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _colliders = GetComponentsInChildren<Collider>(true);
            if (_grip != null)
            {
                _gripPosition = transform.InverseTransformPoint(_grip.position);
                _gripRotation = Quaternion.Inverse(transform.rotation) * _grip.rotation;
            }
            _state.OnChange += (previous, next, asServer) => OnSyncChanged(asServer);
            _holder.OnChange += (previous, next, asServer) => OnSyncChanged(asServer);
        }

        private void OnEnable() => Items.Add(this);
        private void OnDisable() => Items.Remove(this);

        protected override void OnNetworkReady(NetworkRole role) => Apply();

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();
            TrackHand(PlayerId.None); // Despawned while held: the holder's hand is empty now.
        }

        public override void OnOwnershipServer(NetworkConnection previousOwner)
        {
            base.OnOwnershipServer(previousOwner);
            // Holder gone (disconnected / ownership taken back): it falls where they were.
            if (!Owner.IsValid && _state.Value != ItemState.World)
            {
                _holder.Value = "";
                _state.Value = ItemState.World;
            }
        }

        private void OnSyncChanged(bool asServer)
        {
            if (asServer && IsClientInitialized) return; // Host: once, from the client side.
            if (_predicted == _state.Value) _predicted = null;
            Apply();
            AnyChanged?.Invoke(this);
        }

        #endregion

        #region Requests

        /// <summary> Any player: take it from the world into the hand (<see cref="ItemState.Held"/>) or inventory (<see cref="ItemState.Stowed"/>). </summary>
        internal void RequestTake(ItemState into) => ServerTake(into);

        [ServerRpc(RequireOwnership = false)]
        private void ServerTake(ItemState into, NetworkConnection sender = null)
        {
            if (_state.Value != ItemState.World || into == ItemState.World) return;
            if (Owner.IsValid && Owner != sender) return; // Someone is pushing / throwing it right now.
            if (into == ItemState.Held && !TryGetComponent<NetworkCarryable>(out _)) return;
            if (into == ItemState.Stowed && !TryGetComponent<NetworkCollectible>(out _)) return;
            if (!Session.Players.TryGetIdentity(sender, out var identity)) return;
            if (Session.Players.TryGet(identity.PlayerId, out var avatar) &&
                Vector3.Distance(avatar.transform.position, transform.position) > _reach + 1f) return; // +1: their view lags.
            if (ServerTakeRule != null && !ServerTakeRule(identity.PlayerId, this, into)) return;

            if (Owner != sender) GiveOwnership(sender);
            _holder.Value = identity.PlayerId.Value;
            _state.Value = into;
        }

        /// <summary> Owner: hand ⇄ inventory. </summary>
        internal void MoveByOwner(ItemState to)
        {
            if (!IsOwner || State == ItemState.World || to == ItemState.World || State == to) return;
            ServerMove(to);
        }

        [ServerRpc]
        private void ServerMove(ItemState to)
        {
            if (_state.Value == ItemState.World || to == ItemState.World) return;
            _state.Value = to;
        }

        /// <summary> Owner: back into the world at a pose, with a velocity — applied here at once, the host confirms. </summary>
        internal void ReleaseByOwner(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            if (!IsOwner || State == ItemState.World) return;

            _predicted = ItemState.World;
            transform.SetPositionAndRotation(position, rotation);
            if (_body.Rigidbody != null)
            {
                _body.Rigidbody.position = position;
                _body.Rigidbody.rotation = rotation;
            }
            Apply();
            _body.Launch(velocity); // This machine simulates the flight; the body returns to the host once it rests.
            ServerRelease(position, rotation);
        }

        [ServerRpc]
        private void ServerRelease(Vector3 position, Quaternion rotation)
        {
            if (_state.Value == ItemState.World) return;
            if (!IsClientInitialized) transform.SetPositionAndRotation(position, rotation); // Dedicated host has no follow.
            _holder.Value = "";
            _state.Value = ItemState.World;
            AnyChanged?.Invoke(this);
        }

        /// <summary> Items aren't taken through <see cref="NetworkBody.RequestOwnership"/> — only through their behaviours. </summary>
        public bool AllowsTake(PlayerId player, NetworkPlayer avatar) => false;

        #endregion

        #region Applying the state (every machine)

        private void Apply()
        {
            var state = State;
            if (state != _applied)
            {
                _applied = state;
                bool inWorld = state == ItemState.World;
                foreach (var renderer in _renderers) renderer.enabled = state != ItemState.Stowed;
                foreach (var collider in _colliders) collider.enabled = inWorld;

                // In a hand / inventory: kinematic, no movement sync — every machine attaches it to the holder itself.
                _body.SetHeld(!inWorld);
                _body.ApplySync(paused: !inWorld);
            }

            TrackHand(state == ItemState.Held ? Holder : PlayerId.None);
        }

        #endregion

        #region Hands (IItemHolder)

        /// <summary> This item is now in <paramref name="player"/>'s hand (None = in nobody's) — tell the holders that changed. </summary>
        private void TrackHand(PlayerId player)
        {
            if (player == _inHandOf) return;
            var left = _inHandOf;
            _inHandOf = player;

            // Out of a hand — unless that hand already took something else (that item told them already).
            if (left.IsValid && HandOf.TryGetValue(left, out var current) && current == this)
            {
                HandOf.Remove(left);
                NotifyHolder(left, this, null);
            }

            if (player.IsValid)
            {
                HandOf.TryGetValue(player, out var previous);
                HandOf[player] = this;
                NotifyHolder(player, previous == this ? null : previous, this);
            }
        }

        private static void NotifyHolder(PlayerId player, NetworkItem previous, NetworkItem current)
        {
            if (NetworkSession.Instance.Players.TryGet(player, out var avatar)) NotifyHolder(avatar, previous, current);
        }

        private static void NotifyHolder(NetworkPlayer avatar, NetworkItem previous, NetworkItem current)
        {
            foreach (var holder in avatar.GetComponentsInChildren<IItemHolder>(true))
                holder.OnHeldItemChanged(previous, current);
        }

        /// <summary> An avatar appeared (late join, respawn, host migration) — tell it what it already holds. </summary>
        internal static void OnAvatarSpawned(NetworkPlayer avatar)
        {
            if (HandOf.TryGetValue(avatar.Id, out var item) && item != null) NotifyHolder(avatar, null, item);
        }

        #endregion

        #region Following the holder

        private void LateUpdate()
        {
            var state = State;
            if (state == ItemState.World || !Session.Players.TryGet(Holder, out var avatar)) return;

            var anchors = avatar.GetComponentInChildren<IItemAnchors>();
            Vector3 position;
            Quaternion rotation;
            if (state == ItemState.Held)
            {
                // Where the grip goes: the hand anchor (+ fine-tune) — default right, in front of the avatar.
                var hand = anchors?.HandAnchor;
                var gripRotation = (hand != null ? hand.rotation : avatar.transform.rotation) * Quaternion.Euler(_holdRotation);
                var gripPosition = hand != null
                    ? hand.TransformPoint(_holdOffset)
                    : avatar.transform.TransformPoint(new Vector3(0.35f, 0.6f, 0.6f) + _holdOffset);

                // The item itself: so that its Grip lands exactly there.
                rotation = gripRotation * Quaternion.Inverse(_gripRotation);
                position = gripPosition - rotation * Vector3.Scale(transform.lossyScale, _gripPosition);
            }
            else
            {
                var stow = anchors?.StowAnchor;
                (position, rotation) = stow != null ? (stow.position, stow.rotation) : (avatar.transform.position, avatar.transform.rotation);
            }

            transform.SetPositionAndRotation(position, rotation);
            if (_body.Rigidbody != null)
            {
                _body.Rigidbody.position = position;
                _body.Rigidbody.rotation = rotation;
            }
        }

#if UNITY_EDITOR
        /// <summary> Drawn while the item OR one of its children (the Grip you're moving) is selected. </summary>
        private void OnDrawGizmos()
        {
            var selected = UnityEditor.Selection.activeTransform;
            if (selected == null || !selected.IsChildOf(transform)) return;
            DrawGrip(_grip, new Color(0.3f, 1f, 0.4f));
            DrawGrip(_offHandGrip, new Color(0.3f, 0.8f, 1f));
        }
#endif

        /// <summary> Scene view: where the hand closes (sphere) and how it's turned (forward = blue line, up = green). </summary>
        private static void DrawGrip(Transform grip, Color color)
        {
            if (grip == null) return;
            Gizmos.color = color;
            Gizmos.DrawWireSphere(grip.position, 0.04f);
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(grip.position, grip.position + grip.forward * 0.12f);
            Gizmos.color = Color.green;
            Gizmos.DrawLine(grip.position, grip.position + grip.up * 0.08f);
        }

        #endregion
    }
}
