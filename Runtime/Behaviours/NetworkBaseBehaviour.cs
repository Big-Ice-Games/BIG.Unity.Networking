using FishNet.Connection;
using FishNet.Object;
using FishNet.Serializing;

namespace BIG.Unity.Networking
{
    /// <summary> What this machine is for a networked object — passed to <see cref="NetworkBaseBehaviour.OnNetworkReady"/>. </summary>
    public readonly struct NetworkRole
    {
        public NetworkRole(bool isHostSide, bool isOwner)
        {
            IsHostSide = isHostSide;
            IsOwner = isOwner;
        }

        /// <summary> This machine is the host — it runs host-controlled logic (AI, doors, spawning). </summary>
        public readonly bool IsHostSide;
        /// <summary> This machine's player owns the object (moves / simulates it under client authority). </summary>
        public readonly bool IsOwner;

        public override string ToString() => IsHostSide ? (IsOwner ? "host, owner" : "host") : IsOwner ? "owner" : "observer";
    }

    /// <summary>
    /// <see cref="BaseBehaviour"/> for networked scripts (FishNet NetworkBehaviour underneath):
    /// <list type="bullet">
    /// <item><c>[Inject]</c> fields resolved in Awake (<see cref="OnBeforeInjection"/> first) — same as BaseBehaviour.</item>
    /// <item><c>[Subscribe]</c> BIG event handlers active while the object is on the network (start → stop).</item>
    /// <item><see cref="OnNetworkReady"/>: ONE entry point once the object is live on this machine, with its
    /// <see cref="NetworkRole"/> — instead of picking between OnStartServer / OnStartClient / OnOwnershipClient.</item>
    /// <item><c>[Migrate]</c> fields survive a host migration on their own (object needs <see cref="NetworkMigratable"/>);
    /// anything else: <see cref="OnWriteMigration"/> / <see cref="OnReadMigration"/>.</item>
    /// </list>
    /// Overriding a FishNet callback (OnStartClient...) — call base.
    /// </summary>
    public abstract class NetworkBaseBehaviour : NetworkBehaviour, IMigratable
    {
        private bool _injected;

        protected NetworkSession Session => NetworkSession.Instance;

        /// <summary> This machine is the host (server side initialized for this object). </summary>
        public bool IsHostSide => IsServerInitialized;

        /// <summary> Current role on this machine — valid once the object is live. </summary>
        public NetworkRole Role => new NetworkRole(IsServerInitialized, IsOwner);

        #region Lifecycle

        protected virtual void Awake()
        {
            if (_injected) return;
            _injected = true;
            OnBeforeInjection();
            this.ResolveMyDependencies();
        }

        /// <summary> Before [Inject] fields are filled (e.g. register something others will inject). </summary>
        protected virtual void OnBeforeInjection() { }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            this.Subscribe();
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();
            this.Unsubscribe();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            OnNetworkReady(Role); // Every playing machine (host included) runs its client side.
        }

        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            base.OnOwnershipClient(previousOwner);
            if (IsClientInitialized) OnOwnershipChanged(Role);
        }

        protected virtual void OnDestroy() => this.Unsubscribe();

        /// <summary>
        /// The object is live on this machine and its state is synced. Host-controlled logic: <c>if (role.IsHostSide)</c>;
        /// local player's things: <c>if (role.IsOwner)</c>.
        /// </summary>
        protected virtual void OnNetworkReady(NetworkRole role) { }

        /// <summary> Ownership moved (picked up, returned to the host, reconnect...) while live. </summary>
        protected virtual void OnOwnershipChanged(NetworkRole role) { }

        #endregion

        #region Host migration

        void IMigratable.WriteMigrationState(Writer writer)
        {
            MigrateFields.Write(this, writer);
            OnWriteMigration(writer);
        }

        /// <summary>
        /// New host: this object came back from a host migration with its state restored — skip first-time setup
        /// (picking a fresh AI target, spawning loot...) in <see cref="OnNetworkReady"/>.
        /// </summary>
        protected bool WasMigrated { get; private set; }

        void IMigratable.ReadMigrationState(Reader reader)
        {
            WasMigrated = true;
            MigrateFields.Read(this, reader);
            OnReadMigration(reader);
        }

        /// <summary> Host: extra state beyond [Migrate] fields (read it back in the same order in <see cref="OnReadMigration"/>). </summary>
        protected virtual void OnWriteMigration(Writer writer) { }

        /// <summary> New host after a migration: read what <see cref="OnWriteMigration"/> wrote. </summary>
        protected virtual void OnReadMigration(Reader reader) { }

        #endregion

        public void RaiseEvent<T>(T eventData) where T : struct => Events.Raise(eventData);

        public void Log(string message, LogLevel logLevel = LogLevel.Debug, bool withStackTrace = false, bool withTime = false) =>
            Logger.Log(this, message, logLevel, withStackTrace, withTime);
    }
}
