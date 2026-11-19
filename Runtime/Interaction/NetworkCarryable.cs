using System;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Implement on a component of an item to react to <see cref="NetworkCarryable.Use"/> (flashlight on/off, fire, eat).
    /// Called on the user's machine (instant feedback) and on the host (authoritative changes: SyncVars, spawning).
    /// </summary>
    public interface IItemUse
    {
        void OnItemUsed(PlayerId user, bool isHostSide);
    }

    /// <summary>
    /// Players can hold the item in hand: <see cref="PickUp"/>, <see cref="Use"/>, <see cref="Throw"/>, <see cref="Drop"/>.
    /// Needs <see cref="NetworkItem"/> (added with it). Combine with <see cref="NetworkCollectible"/> for "inventory ⇄ hand",
    /// with <see cref="NetworkPushable"/> for "push it around when it's lying".
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkItem))]
    public sealed class NetworkCarryable : NetworkBehaviour
    {
        [SerializeField, Tooltip("Speed of Throw() along the given direction (plus a little lift).")]
        private float _throwSpeed = 8f;
        [SerializeField] private float _throwLift = 2.5f;
        [SerializeField, Tooltip("Drop() puts it this far in front of the holder.")]
        private float _dropDistance = 0.8f;

        private NetworkItem _item;
        private IItemUse[] _uses;

        public NetworkItem Item => _item;
        /// <summary> In this machine's player's hand. </summary>
        public bool IsInMyHand => _item.State == ItemState.Held && _item.IsMine;
        public bool CanBePickedUp => _item.State == ItemState.World && (!_item.Body.IsTaken || _item.Body.IsOwner);

        /// <summary> User's machine and host: someone used it (also goes to every <see cref="IItemUse"/> on the item). </summary>
        public event Action<NetworkCarryable, PlayerId, bool> Used;

        private void Awake()
        {
            _item = GetComponent<NetworkItem>();
            _uses = GetComponentsInChildren<IItemUse>(true);
        }

        /// <summary> Any player: from the world into my hand. </summary>
        public void PickUp()
        {
            if (CanBePickedUp) _item.RequestTake(ItemState.Held);
        }

        /// <summary> Holder: throw along <paramref name="direction"/> (e.g. camera forward). </summary>
        public void Throw(Vector3 direction)
        {
            if (!IsInMyHand) return;
            direction.Normalize();
            _item.ReleaseByOwner(transform.position, transform.rotation, direction * _throwSpeed + Vector3.up * _throwLift);
        }

        /// <summary> Holder: put it down in front of me. </summary>
        public void Drop()
        {
            if (!IsInMyHand || !Session.Players.TryGet(_item.Holder, out var avatar)) return;
            var position = avatar.transform.position + avatar.transform.forward * _dropDistance + Vector3.up * 0.5f;
            _item.ReleaseByOwner(position, transform.rotation, Vector3.zero);
        }

        /// <summary> Holder: use it — <see cref="IItemUse"/> runs here at once and on the host. </summary>
        public void Use()
        {
            if (!IsInMyHand) return;
            var user = Session.Platform.LocalPlayer;
            Notify(user, isHostSide: IsServerInitialized);
            if (!IsServerInitialized) ServerUse(); // Host user: already ran host-side.
        }

        [ServerRpc]
        private void ServerUse(NetworkConnection sender = null)
        {
            if (_item.State != ItemState.Held || !Session.Players.TryGetIdentity(sender, out var identity)) return;
            Notify(identity.PlayerId, isHostSide: true);
        }

        private void Notify(PlayerId user, bool isHostSide)
        {
            foreach (var use in _uses) use.OnItemUsed(user, isHostSide);
            Used?.Invoke(this, user, isHostSide);
        }

        private NetworkSession Session => NetworkSession.Instance;
    }
}
