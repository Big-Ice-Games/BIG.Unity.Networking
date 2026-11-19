using System.Collections.Generic;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// On the player avatar: what the LOCAL player can act on right now and the actions themselves. It reads no input —
    /// bind your keys to <see cref="Primary"/>, <see cref="Collect"/>, <see cref="Use"/>, <see cref="Throw"/>...
    /// <list type="bullet">
    /// <item><see cref="Focus"/>: the best target in range and in view — a <see cref="NetworkInteractable"/> or an item
    /// lying in the world; <see cref="FocusPrompt"/> / <see cref="FocusAltPrompt"/> for the UI ("Pick up Flashlight").</item>
    /// <item><see cref="Held"/>: the item in my hand; <see cref="Inventory"/>: items in my inventory.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkInteractor : MonoBehaviour
    {
        [SerializeField, Tooltip("Where the player looks from (camera). Empty = Camera.main.")]
        private Transform _view;
        [SerializeField, Tooltip("Targets farther than this from the view direction are ignored. 180 = nearest wins (top-down games).")]
        [Range(10f, 180f)] private float _maxAngle = 60f;

        private NetworkPlayer _player;
        private readonly List<NetworkItem> _inventory = new List<NetworkItem>();

        /// <summary> NetworkInteractable or NetworkItem (lying in the world), or null. </summary>
        public Component Focus { get; private set; }

        /// <summary> Primary action on <see cref="Focus"/> ("Pick up Flashlight", "Open"), or "" when none. </summary>
        public string FocusPrompt { get; private set; } = "";

        /// <summary> Second action on <see cref="Focus"/> ("Collect Battery" when it can also be picked up), or "". </summary>
        public string FocusAltPrompt { get; private set; } = "";

        public NetworkItem Held => IsLocal ? NetworkItem.HeldBy(_player.Id) : null;

        /// <summary> Items in my inventory (hidden), in the order they were collected... as listed by the item registry. </summary>
        public IReadOnlyList<NetworkItem> Inventory
        {
            get
            {
                _inventory.Clear();
                if (!IsLocal) return _inventory;
                foreach (var item in NetworkItem.ItemsOf(_player.Id))
                    if (item.State == ItemState.Stowed) _inventory.Add(item);
                return _inventory;
            }
        }

        private bool IsLocal => _player != null && _player.IsLocal;

        private void Awake() => _player = GetComponent<NetworkPlayer>();

        #region Actions

        /// <summary> E: interact with the focus, or pick it up (or collect it, when it can't be held). </summary>
        public void Primary()
        {
            if (!IsLocal) return;
            switch (Focus)
            {
                case NetworkInteractable interactable:
                    interactable.Interact();
                    break;
                case NetworkItem item when item.TryGetComponent<NetworkCarryable>(out var carryable):
                    FreeHand();
                    carryable.PickUp();
                    break;
                case NetworkItem item when item.TryGetComponent<NetworkCollectible>(out var collectible):
                    collectible.Collect();
                    break;
            }
        }

        /// <summary> Put the focused item straight into the inventory. </summary>
        public void Collect()
        {
            if (IsLocal && Focus is NetworkItem item && item.TryGetComponent<NetworkCollectible>(out var collectible))
                collectible.Collect();
        }

        public void Use()
        {
            if (Held != null && Held.TryGetComponent<NetworkCarryable>(out var carryable)) carryable.Use();
        }

        /// <summary> Throw the held item where the view looks. </summary>
        public void Throw()
        {
            if (Held != null && Held.TryGetComponent<NetworkCarryable>(out var carryable)) carryable.Throw(ViewForward());
        }

        public void Drop()
        {
            if (Held != null && Held.TryGetComponent<NetworkCarryable>(out var carryable)) carryable.Drop();
        }

        /// <summary> Held item into the inventory. </summary>
        public void PutAway()
        {
            if (Held != null && Held.TryGetComponent<NetworkCollectible>(out var collectible)) collectible.PutAway();
        }

        /// <summary> Next inventory item into the hand (the held one goes into the inventory first). </summary>
        public void TakeOutNext()
        {
            var inventory = Inventory;
            if (inventory.Count == 0) return;
            var held = Held;
            int start = held != null ? Mathf.Max(0, IndexOf(inventory, held)) : 0;
            FreeHand();
            for (int i = 0; i < inventory.Count; i++)
            {
                var candidate = inventory[(start + i) % inventory.Count];
                if (candidate == held || !candidate.TryGetComponent<NetworkCollectible>(out var collectible)) continue;
                if (!candidate.TryGetComponent<NetworkCarryable>(out _)) continue;
                collectible.TakeOut();
                return;
            }
        }

        public void TakeOut(NetworkItem item)
        {
            if (item.TryGetComponent<NetworkCollectible>(out var collectible) && collectible.IsInMyInventory)
            {
                FreeHand();
                collectible.TakeOut();
            }
        }

        public void DropFromInventory(NetworkItem item)
        {
            if (item.TryGetComponent<NetworkCollectible>(out var collectible)) collectible.DropFromInventory();
        }

        /// <summary> Make the hand free: held item into the inventory if it can go there, otherwise put down. </summary>
        private void FreeHand()
        {
            var held = Held;
            if (held == null) return;
            if (held.TryGetComponent<NetworkCollectible>(out var collectible)) collectible.PutAway();
            else if (held.TryGetComponent<NetworkCarryable>(out var carryable)) carryable.Drop();
        }

        private static int IndexOf(IReadOnlyList<NetworkItem> list, NetworkItem item)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == item) return i;
            return -1;
        }

        #endregion

        #region Focus

        private void Update()
        {
            if (!IsLocal)
            {
                Focus = null;
                FocusPrompt = FocusAltPrompt = "";
                return;
            }

            Focus = FindFocus();
            switch (Focus)
            {
                case NetworkInteractable interactable:
                    FocusPrompt = interactable.Prompt;
                    FocusAltPrompt = "";
                    break;
                case NetworkItem item:
                    bool carry = item.TryGetComponent<NetworkCarryable>(out _);
                    bool collect = item.TryGetComponent<NetworkCollectible>(out _);
                    FocusPrompt = carry ? $"Pick up {item.DisplayName}" : $"Collect {item.DisplayName}";
                    FocusAltPrompt = carry && collect ? $"Collect {item.DisplayName}" : "";
                    break;
                default:
                    FocusPrompt = FocusAltPrompt = "";
                    break;
            }
        }

        private Component FindFocus()
        {
            var origin = transform.position;
            var forward = ViewForward();
            Component best = null;
            float bestScore = float.MaxValue;

            foreach (var interactable in NetworkInteractable.All)
            {
                if (!interactable.IsClientInitialized || (interactable.CanInteract != null && !interactable.CanInteract())) continue;
                Consider(interactable, interactable.transform.position, interactable.Range);
            }

            foreach (var item in NetworkItem.All)
            {
                if (!item.IsClientInitialized || item.State != ItemState.World) continue;
                bool canTake = (item.TryGetComponent<NetworkCarryable>(out var carryable) && carryable.CanBePickedUp) ||
                               (item.TryGetComponent<NetworkCollectible>(out var collectible) && collectible.CanBeCollected);
                if (canTake) Consider(item, item.transform.position, item.Reach);
            }

            return best;

            void Consider(Component target, Vector3 position, float range)
            {
                var toTarget = position - origin;
                float distance = toTarget.magnitude;
                if (distance > range) return;
                float angle = distance < 0.01f ? 0f : Vector3.Angle(forward, toTarget);
                if (angle > _maxAngle) return;
                float score = distance + angle * 0.02f; // Close and in front wins.
                if (score >= bestScore) return;
                bestScore = score;
                best = target;
            }
        }

        private Vector3 ViewForward()
        {
            var view = _view != null ? _view : Camera.main != null ? Camera.main.transform : transform;
            var forward = view.forward;
            forward.y = 0f; // Horizontal: a camera looking down shouldn't push targets out of view.
            return forward.sqrMagnitude > 0.001f ? forward.normalized : transform.forward;
        }

        #endregion
    }
}
