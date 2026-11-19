using UnityEngine;

namespace BIG.Unity.Networking.DevTools
{
    /// <summary>
    /// One collapsible module of the <see cref="DevOverlay"/> (Session, Lobby, Voice, your game's own...).
    /// <para>
    /// <see cref="Build"/> runs once and lays the module out with <see cref="DevUI"/>: static parts as values, changing
    /// parts as lambdas (<c>ui.Text(() => ...)</c>) — the overlay re-reads them a few times per second.
    /// Register with <see cref="DevOverlay.Register"/>; the plugin's own modules are registered automatically.
    /// </para>
    /// </summary>
    public abstract class DevPanel
    {
        /// <summary> Header text — also the key under which collapsed / expanded is remembered. </summary>
        public abstract string Title { get; }

        /// <summary> Position in the overlay: lower first. Plugin modules use 0–90, voice 100–120, games 200+. </summary>
        public virtual int Order => 200;

        /// <summary> Collapsed the very first time it's shown (before the player toggled it). </summary>
        public virtual bool CollapsedByDefault => false;

        /// <summary> Whole module hidden while false (e.g. Lobby only in a session). Checked on every refresh. </summary>
        public virtual bool IsAvailable => true;

        /// <summary> Short status in the header, visible while collapsed (e.g. "HOST", "3 players"). Null = none. </summary>
        public virtual string Badge => null;

        /// <summary> Badge color: null, "good", "warn" or "bad". </summary>
        public virtual string BadgeStyle => null;

        /// <summary> The overlay's object — for helpers that need a GameObject (AudioSource...). </summary>
        protected GameObject Owner { get; private set; }

        protected NetworkSession Session => NetworkSession.Instance;

        internal void Attach(GameObject owner) => Owner = owner;

        /// <summary> Lay out the module once. </summary>
        public abstract void Build(DevUI ui);

        /// <summary> Optional per-refresh work beyond the bindings (polling something expensive...). </summary>
        public virtual void Refresh() { }
    }
}
