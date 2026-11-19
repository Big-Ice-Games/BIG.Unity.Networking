using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BIG.Unity.Networking.Editor
{
    /// <summary>
    /// Project Settings > BIG Networking: the project's <see cref="NetworkConfig"/> asset(s) in one place —
    /// pick one, edit it (also in play mode: live sections apply at once), or create the first one.
    /// </summary>
    internal sealed class NetworkConfigSettingsProvider : SettingsProvider
    {
        private const string DEFAULT_PATH = "Assets/NetworkConfig.asset";
        private const string SELECTED_KEY = "BIG.Networking.SelectedConfig";

        private NetworkConfig[] _configs = new NetworkConfig[0];
        private UnityEditor.Editor _editor;
        private Vector2 _scroll;

        private NetworkConfigSettingsProvider() : base("Project/BIG Networking", SettingsScope.Project) =>
            keywords = new[] { "network", "networking", "fishnet", "lobby", "host", "migration", "tick", "password", "room" };

        [SettingsProvider]
        public static SettingsProvider Create() => new NetworkConfigSettingsProvider();

        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement) => Refresh();

        public override void OnDeactivate()
        {
            if (_editor != null) Object.DestroyImmediate(_editor);
            _editor = null;
        }

        private void Refresh()
        {
            _configs = AssetDatabase.FindAssets("t:" + nameof(NetworkConfig))
                .Select(guid => AssetDatabase.LoadAssetAtPath<NetworkConfig>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(config => config != null)
                .OrderBy(config => config.name)
                .ToArray();
        }

        public override void OnGUI(string searchContext)
        {
            EditorGUILayout.Space();
            if (_configs.Length == 0)
            {
                EditorGUILayout.HelpBox("No Network Config in the project yet. It holds every BIG.Unity.Networking setting: " +
                                        "provider, tick rate, room defaults (name, password), lobby, host migration.", MessageType.Info);
                if (GUILayout.Button($"Create {DEFAULT_PATH}", GUILayout.Height(28)))
                {
                    AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<NetworkConfig>(), DEFAULT_PATH);
                    AssetDatabase.SaveAssets();
                    Refresh();
                }
                return;
            }

            var selected = SelectedConfig();
            using (new EditorGUILayout.HorizontalScope())
            {
                int index = System.Array.IndexOf(_configs, selected);
                int picked = EditorGUILayout.Popup("Config", index, _configs.Select(c => c.name).ToArray());
                if (picked != index)
                {
                    selected = _configs[picked];
                    EditorPrefs.SetString(SELECTED_KEY, AssetDatabase.GetAssetPath(selected));
                }
                if (GUILayout.Button("Select", GUILayout.Width(60))) EditorGUIUtility.PingObject(selected);
                if (GUILayout.Button("Refresh", GUILayout.Width(60))) Refresh();
            }

            var session = Application.isPlaying ? NetworkSession.Instance : Object.FindAnyObjectByType<NetworkSession>();
            if (session != null && session.Config != null)
            {
                bool inUse = session.Config == selected;
                EditorGUILayout.HelpBox(inUse
                        ? $"Used by NetworkSession on '{session.name}'." + (Application.isPlaying ? " Play mode: \"live\" sections apply at once, the rest at the next Host / Join." : "")
                        : $"The open scene's NetworkSession uses '{session.Config.name}', not this one.",
                    inUse ? MessageType.None : MessageType.Warning);
            }

            EditorGUILayout.Space();
            UnityEditor.Editor.CreateCachedEditor(selected, null, ref _editor);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            _editor.OnInspectorGUI();
            EditorGUILayout.EndScrollView();
        }

        private NetworkConfig SelectedConfig()
        {
            string path = EditorPrefs.GetString(SELECTED_KEY, "");
            var remembered = _configs.FirstOrDefault(c => AssetDatabase.GetAssetPath(c) == path);
            return remembered != null ? remembered : _configs[0];
        }
    }
}
