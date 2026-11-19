using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary> Implement on a component next to <see cref="NetworkInteractable"/>: what interacting does — runs on the host. </summary>
    public interface IInteractionHandler
    {
        void OnInteract(PlayerId player);
    }

    /// <summary>
    /// Interaction without picking up: switch, door, window, terminal, battery charger. A player in range asks the host
    /// (<see cref="Interact"/>), the host checks the range and runs every <see cref="IInteractionHandler"/> on the object
    /// (and <see cref="InteractedOnHost"/>) — change SyncVars there, everyone sees the result.
    /// Any NetworkObject; no NetworkBody needed. <see cref="NetworkInteractor"/> finds it and shows <see cref="Prompt"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkInteractable : NetworkBehaviour
    {
        private static readonly List<NetworkInteractable> Interactables = new List<NetworkInteractable>();

        [SerializeField, Tooltip("Action shown to the player: \"Open\", \"Break\", \"Charge\". Changeable at runtime.")]
        private string _prompt = "Use";
        [SerializeField] private float _range = 2.5f;
        [SerializeField, Tooltip("Seconds before the same player can interact again (spam guard).")]
        private float _cooldown = 0.25f;

        private IInteractionHandler[] _handlers;
        private readonly Dictionary<PlayerId, float> _lastUse = new Dictionary<PlayerId, float>();

        public static IReadOnlyList<NetworkInteractable> All => Interactables;

        /// <summary> Shown by the interactor ("Open", "Close" — set it when the state changes). </summary>
        public string Prompt
        {
            get => _prompt;
            set => _prompt = value;
        }

        public float Range => _range;

        /// <summary> Every machine: may the local player interact now? Null = yes. (E.g. "only with a key".) </summary>
        public Func<bool> CanInteract { get; set; }

        /// <summary> Host: someone interacted (after the range check). </summary>
        public event Action<NetworkInteractable, PlayerId> InteractedOnHost;

        private void Awake() => _handlers = GetComponentsInChildren<IInteractionHandler>(true);
        private void OnEnable() => Interactables.Add(this);
        private void OnDisable() => Interactables.Remove(this);

        /// <summary> Any player: interact (the host decides). </summary>
        public void Interact()
        {
            if (CanInteract == null || CanInteract()) ServerInteract();
        }

        [ServerRpc(RequireOwnership = false)]
        private void ServerInteract(NetworkConnection sender = null)
        {
            var session = NetworkSession.Instance;
            if (!session.Players.TryGetIdentity(sender, out var identity)) return;
            if (session.Players.TryGet(identity.PlayerId, out var avatar) &&
                Vector3.Distance(avatar.transform.position, transform.position) > _range + 1f) return; // +1: their view lags.
            if (_lastUse.TryGetValue(identity.PlayerId, out float last) && Time.time - last < _cooldown) return;
            _lastUse[identity.PlayerId] = Time.time;

            foreach (var handler in _handlers) handler.OnInteract(identity.PlayerId);
            InteractedOnHost?.Invoke(this, identity.PlayerId);
        }
    }
}
