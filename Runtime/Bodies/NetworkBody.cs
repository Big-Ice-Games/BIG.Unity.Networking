using System;
using System.Reflection;
using FishNet.Component.Transforming;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Server-side gate for <see cref="NetworkBody.RequestOwnership"/>: implemented by behaviours that let players take an
    /// object (<see cref="NetworkPushable"/>, <see cref="NetworkItem"/>...). A body with no rule can't be taken at all.
    /// </summary>
    public interface IOwnershipRule
    {
        /// <summary> Host: may <paramref name="player"/> (avatar may be null — not spawned) take this object now? </summary>
        bool AllowsTake(PlayerId player, NetworkPlayer avatar);
    }

    /// <summary>
    /// How a networked object moves and who simulates it — by its <see cref="NetworkBodyProfile"/>. Generic: what players
    /// can DO with the object (push, carry, collect, use) are separate behaviours added next to it.
    /// Needs NetworkObject + NetworkTransform; Rigidbody optional.
    /// <list type="bullet">
    /// <item>Profile → FishNet's NetworkTransform in Awake (authority, what's synced, interval, interpolation).</item>
    /// <item>Physics: only the simulating machine has a dynamic Rigidbody; everyone else keeps it kinematic and follows the
    /// synced transform. Recomputed on every ownership change.</item>
    /// <item>Ownership: <see cref="RequestOwnership"/> (allowed by the object's <see cref="IOwnershipRule"/>s),
    /// <see cref="ReturnToHost"/>; with the profile's auto return it goes back to the host once it rests, and always when
    /// its owner disconnects (it isn't despawned with them).</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkTransform))]
    public sealed class NetworkBody : NetworkBehaviour
    {
        [SerializeField] private NetworkBodyProfile _profile;

        private NetworkSession _session;
        private NetworkTransform _networkTransform;
        private Rigidbody _rigidbody;
        private IOwnershipRule[] _rules;

        private bool _held;
        private bool _returning;
        private float _lastActivity;
        private float _restingSince = -1f;

        public NetworkBodyProfile Profile => _profile;
        public Rigidbody Rigidbody => _rigidbody;
        public NetworkTransform NetworkTransform => _networkTransform;
        /// <summary> This machine runs the Rigidbody simulation of this object right now. </summary>
        public bool IsSimulating { get; private set; }
        /// <summary> Held by a behaviour (in a hand, in an inventory): kinematic, no auto return. </summary>
        public bool IsHeld => _held;
        /// <summary> A player owns it. False = the host has it. </summary>
        public bool IsTaken => Owner.IsValid;
        /// <summary> Some behaviour lets players take it. </summary>
        public bool CanBeTaken => _rules.Length > 0;

        /// <summary> Every machine: the owner changed (taken / returned). </summary>
        public event Action<NetworkBody> OwnershipChanged;

        #region Setup

        private void Awake()
        {
            if (_profile == null)
                throw new InvalidOperationException($"[Network] NetworkBody on '{name}' has no profile (Create > BIG > Networking > Network Body Profile).");
            _session = NetworkSession.Instance;
            _networkTransform = GetComponent<NetworkTransform>();
            _rigidbody = GetComponent<Rigidbody>();
            _rules = GetComponents<IOwnershipRule>();
            ApplyProfile();
        }

        /// <summary> Profile → FishNet components, before the object goes on the network. </summary>
        private void ApplyProfile()
        {
            SetField(_networkTransform, "_clientAuthoritative", _profile.Authority == BodyAuthority.Owner);
            SetField(_networkTransform, "_interval", _profile.SendInterval);
            ApplySync();
            _networkTransform.SetInterpolation(_profile.Interpolation);
            _networkTransform.SetExtrapolation(_profile.Extrapolation);

            // FishNet despawns what a disconnecting player owns — something players can take must stay (back to the host).
            // The NetworkObject may sit on a parent: a rope link, a ragdoll limb.
            if (CanBeTaken) PreventDespawnOnDisconnect(GetComponentInParent<NetworkObject>(true));

            if (_rigidbody != null) _rigidbody.isKinematic = true; // Until we know who simulates.
        }

        /// <summary> Movement sync as the profile says — or off (<paramref name="paused"/>) while a behaviour attaches it to a player. </summary>
        public void ApplySync(bool paused = false)
        {
            _networkTransform.SetSynchronizePosition(!paused && _profile.SyncPosition);
            _networkTransform.SetSynchronizeRotation(!paused && _profile.SyncRotation);
            _networkTransform.SetSynchronizeScale(!paused && _profile.SyncScale);
        }

        /// <summary> FishNet despawns objects of a player who disconnects — not this one (it goes back to the host). </summary>
        public static void PreventDespawnOnDisconnect(NetworkObject networkObject) =>
            SetField(networkObject, "_preventDespawnOnDisconnect", true);

        private static void SetField(object target, string field, object value)
        {
            var info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (info == null)
                throw new MissingFieldException($"[Network] FishNet {target.GetType().Name}.{field} not found — FishNet changed, update NetworkBody.");
            info.SetValue(target, value);
        }

        #endregion

        #region Network callbacks

        public override void OnStartNetwork()
        {
            transform.GetPositionAndRotation(out _safePosition, out _safeRotation);
            UpdateSimulation();
        }

        public override void OnStartServer() =>
            NetworkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;

        public override void OnStopServer() =>
            NetworkManager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;

        public override void OnOwnershipServer(NetworkConnection previousOwner) => OnOwnerChanged(asServer: true);
        public override void OnOwnershipClient(NetworkConnection previousOwner) => OnOwnerChanged(asServer: false);

        private void OnOwnerChanged(bool asServer)
        {
            _returning = false;
            _restingSince = -1f;
            _lastActivity = Time.time;
            UpdateSimulation();

            // Host runs both sides: notify once.
            if (asServer && IsClientInitialized) return;
            OwnershipChanged?.Invoke(this);
        }

        /// <summary> The owner left the game: the object stays (prevent-despawn) and goes back to the host. </summary>
        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState == RemoteConnectionState.Stopped && Owner == connection)
                RemoveOwnership();
        }

        private void UpdateSimulation()
        {
            IsSimulating = _profile.Physics switch
            {
                BodyPhysics.Host => IsServerInitialized,
                BodyPhysics.Owner => Owner.IsValid ? IsOwner : IsServerInitialized,
                _ => false
            };

            if (_rigidbody == null) return;
            bool dynamic = IsSimulating && !_held;
            // Order matters: kinematic bodies don't take ContinuousDynamic (Unity warns), so switch the mode around it.
            if (!dynamic) _rigidbody.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _rigidbody.isKinematic = !dynamic;
            if (dynamic && _profile.ContinuousCollision) _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rigidbody.interpolation = dynamic ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
        }

        #region Fall recovery

        private Vector3 _safePosition;
        private Quaternion _safeRotation;

        /// <summary> Simulating machine: remember where it last rested; fell through the map → back there. </summary>
        private void TickFallRecovery()
        {
            if (!IsSimulating || _held || _rigidbody == null || _rigidbody.isKinematic) return;

            if (transform.position.y < _profile.RecoverBelowY)
            {
                this.Log($"[Network] '{name}' fell through the map (y {transform.position.y:0}) — back to {_safePosition}.", LogLevel.Warning);
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
                _rigidbody.position = _safePosition + Vector3.up * 0.2f;
                _rigidbody.rotation = _safeRotation;
                transform.SetPositionAndRotation(_rigidbody.position, _safeRotation);
                return;
            }

            if (_rigidbody.linearVelocity.sqrMagnitude < 0.0025f) // Resting (< 5 cm/s).
                transform.GetPositionAndRotation(out _safePosition, out _safeRotation);
        }

        #endregion

        #endregion

        #region Ownership

        /// <summary>
        /// Any player: ask the host for this object. Granted when no one else owns it and one of the object's
        /// <see cref="IOwnershipRule"/>s allows it; arrives as <see cref="OwnershipChanged"/> with IsOwner.
        /// </summary>
        public void RequestOwnership()
        {
            if (IsOwner || !CanBeTaken) return;
            ServerRequestOwnership();
        }

        [ServerRpc(RequireOwnership = false)]
        private void ServerRequestOwnership(NetworkConnection sender = null)
        {
            if (Owner.IsValid) return; // Someone holds / pushes it — first come, first served.
            if (ServerAllows(sender)) GiveOwnership(sender);
        }

        /// <summary> Host: does any rule of this object let the player behind <paramref name="connection"/> take it? </summary>
        public bool ServerAllows(NetworkConnection connection)
        {
            if (!_session.Players.TryGetIdentity(connection, out var identity)) return false;
            _session.Players.TryGet(identity.PlayerId, out var avatar);
            foreach (var rule in _rules)
                if (rule.AllowsTake(identity.PlayerId, avatar)) return true;
            return false;
        }

        /// <summary> Owner: give it back to the host now. </summary>
        public void ReturnToHost()
        {
            if (!IsOwner || _returning) return;
            _returning = true;
            _held = false;
            ServerReturn();
        }

        [ServerRpc]
        private void ServerReturn() => RemoveOwnership();

        /// <summary> Owner: still in use (touching, aiming...) — postpones the auto return. </summary>
        public void KeepOwnership() => _lastActivity = Time.time;

        #endregion

        #region Physics control (for behaviours)

        /// <summary> Held in a hand / an inventory: kinematic, moved by its behaviour, never auto-returned. Every machine. </summary>
        public void SetHeld(bool held)
        {
            _held = held;
            _restingSince = -1f;
            _lastActivity = Time.time;
            UpdateSimulation();
        }

        /// <summary> Simulating machine: start moving with this velocity (throw, kick). </summary>
        public void Launch(Vector3 velocity, Vector3 angularVelocity = default)
        {
            _lastActivity = Time.time;
            _restingSince = -1f;
            if (_rigidbody == null || _rigidbody.isKinematic) return;
            _rigidbody.linearVelocity = velocity;
            _rigidbody.angularVelocity = angularVelocity;
        }

        #endregion

        private void Update()
        {
            if (IsServerInitialized || IsClientInitialized) TickFallRecovery();

            // Owner, not held: give it back once it rests — or after ReturnAfter at the latest.
            if (!IsOwner || !IsClientInitialized || _held || _returning || !_profile.AutoReturnToHost || !CanBeTaken) return;

            float speed = _rigidbody != null && !_rigidbody.isKinematic ? _rigidbody.linearVelocity.magnitude : 0f;
            if (speed < _profile.RestSpeed && Time.time - _lastActivity > 0.1f)
            {
                if (_restingSince < 0f) _restingSince = Time.time;
                else if (Time.time - _restingSince >= _profile.ReturnWhenRestingFor) ReturnToHost();
            }
            else
            {
                _restingSince = -1f;
            }

            if (Time.time - _lastActivity >= _profile.ReturnAfter) ReturnToHost();
        }
    }
}
