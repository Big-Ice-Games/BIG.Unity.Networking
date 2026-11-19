using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.UIElements;

namespace BIG.Unity.Networking.Editor
{
    /// <summary>
    /// The dev overlay's PanelSettings must be an ASSET: only the editor fills in its text data (ICU) — a panel created at
    /// runtime renders text in the editor but throws "FindObjectsOfTypeAll can only be called from the main thread" in
    /// builds. Creates Assets/BIG.Networking/DevOverlayPanelSettings.asset and assigns it to every Network Config that
    /// uses the overlay — after each script reload and before every build.
    /// </summary>
    internal sealed class DevOverlayPanelAsset : IPreprocessBuildWithReport
    {
        private const string FOLDER = "Assets/BIG.Networking";
        private const string PATH = FOLDER + "/DevOverlayPanelSettings.asset";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => AssignToConfigs();

        [InitializeOnLoadMethod]
        private static void OnScriptsReloaded() => EditorApplication.delayCall += AssignToConfigs;

        private static void AssignToConfigs()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(NetworkConfig)))
            {
                var config = AssetDatabase.LoadAssetAtPath<NetworkConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (config == null || config.DevOverlay == DevTools.DevOverlayMode.Off) continue;
                if (config.DevOverlayPanel != null)
                {
                    EnsureTextData(config.DevOverlayPanel);
                    continue;
                }

                config.DevOverlayPanel = GetOrCreate();
                EditorUtility.SetDirty(config);
                Debug.Log($"[Network] Dev overlay panel assigned to '{config.name}' ({PATH}).", config);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary> The text (ICU) data must be on the asset — make sure, using the engine's own assign hook if needed. </summary>
        private static void EnsureTextData(PanelSettings settings)
        {
            var serialized = new SerializedObject(settings);
            var icu = serialized.FindProperty("m_ICUDataAsset");
            if (icu == null || icu.objectReferenceValue != null) return; // Field gone in this Unity, or already set.

            var assign = typeof(PanelSettings).GetField("s_AssignICUData",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            (assign?.GetValue(null) as System.Delegate)?.DynamicInvoke(settings);

            serialized.Update();
            if (serialized.FindProperty("m_ICUDataAsset").objectReferenceValue == null)
                Debug.LogWarning($"[Network] {PATH} has no ICU text data — the dev overlay's text may fail in builds. " +
                                 "Select the asset once in the inspector or recreate it (delete + reload scripts).", settings);
            else
                EditorUtility.SetDirty(settings);
        }

        private static PanelSettings GetOrCreate()
        {
            var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(PATH);
            if (existing != null)
            {
                EnsureTextData(existing);
                return existing;
            }

            Directory.CreateDirectory(FOLDER);
            var settings = ScriptableObject.CreateInstance<PanelSettings>(); // The editor assigns the ICU text data here.
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 1f;
            settings.sortingOrder = 10000;
            AssetDatabase.CreateAsset(settings, PATH);
            EnsureTextData(settings);
            return settings;
        }
    }
}
