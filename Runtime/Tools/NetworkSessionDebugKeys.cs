using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BIG.Unity.Networking
{
    /// <summary>
    /// Dev-only shortcuts for testing sessions without any UI: F5 host, F6 search + join the first room,
    /// F7 leave. The same actions are in the component's context menu. Results go to the log.
    /// Remove from shipping scenes.
    /// </summary>
    public sealed class NetworkSessionDebugKeys : MonoBehaviour
    {
        private void Update()
        {
            if (Pressed(KeyCodeHost)) Host();
            if (Pressed(KeyCodeJoin)) JoinFirst();
            if (Pressed(KeyCodeLeave)) Leave();
        }

        [ContextMenu("Host")]
        public void Host() => NetworkSession.Instance.Host(); // Room defaults from the Network Config.

        [ContextMenu("Search and join first")]
        public void JoinFirst()
        {
            var session = NetworkSession.Instance;
            this.Log("[Network] Searching...");
            session.Search(results =>
            {
                foreach (var info in results)
                    this.Log($"[Network] Found '{info.Name}' {info.MemberCount}/{info.MaxPlayers} at {info.Address} (v{info.Version}, joinable: {info.Joinable})");

                if (results.Count == 0)
                    this.Log("[Network] No rooms found.", LogLevel.Warning);
                else if (session.State == SessionState.Offline)
                    session.Join(results[0]);
            });
        }

        [ContextMenu("Leave")]
        public void Leave() => NetworkSession.Instance.Leave();

#if ENABLE_INPUT_SYSTEM
        private static Key KeyCodeHost => Key.F5;
        private static Key KeyCodeJoin => Key.F6;
        private static Key KeyCodeLeave => Key.F7;
        private static bool Pressed(Key key) => Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
#else
        private static KeyCode KeyCodeHost => KeyCode.F5;
        private static KeyCode KeyCodeJoin => KeyCode.F6;
        private static KeyCode KeyCodeLeave => KeyCode.F7;
        private static bool Pressed(KeyCode key) => Input.GetKeyDown(key);
#endif
    }
}
