using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Players can put the item into their inventory: <see cref="Collect"/> (world → inventory), <see cref="TakeOut"/>
    /// (inventory → hand, needs <see cref="NetworkCarryable"/>), <see cref="PutAway"/> (hand → inventory),
    /// <see cref="DropFromInventory"/> (inventory → world at the player's feet).
    /// Needs <see cref="NetworkItem"/> (added with it). The inventory itself (slots, UI, limits) is the game's:
    /// <c>NetworkItem.ItemsOf(player)</c>, <c>NetworkItem.AnyChanged</c>, <c>NetworkItem.ServerTakeRule</c>.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkItem))]
    public sealed class NetworkCollectible : MonoBehaviour
    {
        private NetworkItem _item;
        private NetworkCarryable _carryable;

        public NetworkItem Item => _item;
        public bool CanBeCollected => _item.State == ItemState.World && (!_item.Body.IsTaken || _item.Body.IsOwner);
        /// <summary> In this machine's player's inventory. </summary>
        public bool IsInMyInventory => _item.State == ItemState.Stowed && _item.IsMine;

        private void Awake()
        {
            _item = GetComponent<NetworkItem>();
            _carryable = GetComponent<NetworkCarryable>();
        }

        /// <summary> Any player: from the world into my inventory. </summary>
        public void Collect()
        {
            if (CanBeCollected) _item.RequestTake(ItemState.Stowed);
        }

        /// <summary> Holder: from my inventory into my hand. </summary>
        public void TakeOut()
        {
            if (IsInMyInventory && _carryable != null) _item.MoveByOwner(ItemState.Held);
        }

        /// <summary> Holder: from my hand into my inventory. </summary>
        public void PutAway()
        {
            if (_item.State == ItemState.Held && _item.IsMine) _item.MoveByOwner(ItemState.Stowed);
        }

        /// <summary> Holder: from my inventory back into the world, at my feet. </summary>
        public void DropFromInventory()
        {
            if (!IsInMyInventory || !NetworkSession.Instance.Players.TryGet(_item.Holder, out var avatar)) return;
            var position = avatar.transform.position + avatar.transform.forward * 0.8f + Vector3.up * 0.3f;
            _item.ReleaseByOwner(position, Quaternion.identity, Vector3.zero);
        }
    }
}
