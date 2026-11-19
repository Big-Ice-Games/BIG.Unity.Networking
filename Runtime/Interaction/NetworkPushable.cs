using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Players push it by walking into it: touching it with your avatar asks the host for ownership, so you simulate the
    /// push yourself — no lag. Back to the host once it rests (body profile: Auto Return To Host).
    /// Needs <see cref="NetworkBody"/> with owner-simulated physics and a Rigidbody; your avatar needs a collider
    /// (ideally a kinematic Rigidbody). Touch uses Collider.ClosestPoint — primitive / convex colliders.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkBody))]
    public sealed class NetworkPushable : MonoBehaviour, IOwnershipRule
    {
        private const float REQUEST_COOLDOWN = 0.5f;

        [SerializeField, Tooltip("Gap between your avatar's collider and this object's that counts as touching.")]
        private float _touchDistance = 0.15f;
        [SerializeField, Tooltip("Host refuses pushes from avatars farther than this (lag / cheating guard).")]
        private float _maxDistance = 4f;

        private NetworkBody _body;
        private NetworkItem _item;
        private Collider[] _colliders;
        private float _nextRequest;

        private void Awake()
        {
            _body = GetComponent<NetworkBody>();
            _item = GetComponent<NetworkItem>();
            _colliders = GetComponentsInChildren<Collider>();
        }

        /// <summary> Host: only free objects lying in the world, only from an avatar close by. </summary>
        public bool AllowsTake(PlayerId player, NetworkPlayer avatar)
        {
            if (_item != null && _item.State != ItemState.World) return false;
            return avatar == null || Vector3.Distance(avatar.transform.position, transform.position) <= _maxDistance;
        }

        private void Update()
        {
            if (!_body.IsClientInitialized || (_item != null && _item.State != ItemState.World)) return;
            if (!TouchedByLocalAvatar()) return;

            if (_body.IsOwner)
            {
                _body.KeepOwnership(); // Still pushing — don't hand it back yet.
            }
            else if (!_body.IsTaken && Time.time >= _nextRequest)
            {
                _nextRequest = Time.time + REQUEST_COOLDOWN;
                _body.RequestOwnership();
            }
        }

        private bool TouchedByLocalAvatar()
        {
            var avatar = NetworkSession.Instance.Players.Local;
            if (avatar == null || _colliders.Length == 0) return false;
            var avatarCollider = avatar.GetComponentInChildren<Collider>();
            if (avatarCollider == null) return false;

            var avatarBounds = avatarCollider.bounds;
            foreach (var collider in _colliders)
            {
                // Cheap reject first (bounds), then closest points both ways.
                var bounds = collider.bounds;
                bounds.Expand(_touchDistance * 2f);
                if (!bounds.Intersects(avatarBounds)) continue;

                var onBody = collider.ClosestPoint(avatarBounds.center);
                var onAvatar = avatarCollider.ClosestPoint(onBody);
                if (Vector3.Distance(onBody, onAvatar) <= _touchDistance) return true;
            }
            return false;
        }
    }
}
