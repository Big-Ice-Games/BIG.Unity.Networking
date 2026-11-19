using System.Linq;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BIG.Unity.Networking.Editor
{
    /// <summary>
    /// FishNet setup chores that usually fail silently: scene ids, spawnable prefab list, prefab hashes.
    /// Shared by Make Networked, the NetworkBody inspector and the setup validator.
    /// </summary>
    internal static class FishNetEditorUtility
    {
        private const string REFRESH_PREFABS_MENU = "Tools/Fish-Networking/Utility/Refresh Default Prefabs";

        /// <summary>
        /// FishNet gives scene NetworkObjects their SceneId in OnValidate — not run for components added from code. SceneId 0 =
        /// never spawns. Runs FishNet's own generator (internal, behind "Reserialize NetworkObjects").
        /// </summary>
        public static void AssignSceneIds(Scene scene)
        {
            var createSceneId = typeof(NetworkObject).GetMethod("CreateSceneId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (createSceneId == null)
                throw new System.MissingMethodException("FishNet NetworkObject.CreateSceneId(Scene, bool, out int) not found — FishNet changed; " +
                                                        "run Tools > Fish-Networking > Utility > Reserialize NetworkObjects instead.");
            createSceneId.Invoke(null, new object[] { scene, true, 0 });
            EditorSceneManager.MarkSceneDirty(scene);
        }

        /// <summary> Scene object without its SceneId (0) — FishNet won't spawn it. </summary>
        public static bool HasNoSceneId(NetworkObject networkObject)
        {
            var property = new SerializedObject(networkObject).FindProperty("SceneId");
            return property != null && property.ulongValue == 0;
        }

        /// <summary> Full rebuild of FishNet's spawnable prefab list (also re-hashes prefabs that were re-saved). </summary>
        public static void RefreshSpawnablePrefabs()
        {
            AssetDatabase.SaveAssets();
            if (!EditorApplication.ExecuteMenuItem(REFRESH_PREFABS_MENU))
                Debug.LogError($"[Network] Couldn't run '{REFRESH_PREFABS_MENU}' — run it by hand.");
        }

        private static DefaultPrefabObjects[] _collections;
        private static double _collectionsTime = -10;

        /// <summary> On FishNet's spawnable list. (Collections cached ~2 s — inspectors ask every repaint.) </summary>
        public static bool IsSpawnable(NetworkObject prefab)
        {
            if (_collections == null || EditorApplication.timeSinceStartup - _collectionsTime > 2.0)
            {
                _collections = AssetDatabase.FindAssets("t:" + nameof(DefaultPrefabObjects))
                    .Select(guid => AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>(AssetDatabase.GUIDToAssetPath(guid)))
                    .Where(collection => collection != null)
                    .ToArray();
                _collectionsTime = EditorApplication.timeSinceStartup;
            }
            return _collections.Any(collection => collection.Prefabs.Contains(prefab));
        }

        /// <summary> The object is (part of) a prefab ASSET or open in prefab mode — not a scene instance. </summary>
        public static bool IsPrefabAsset(GameObject go) =>
            PrefabUtility.IsPartOfPrefabAsset(go) || PrefabStageUtility.GetPrefabStage(go) != null;

        /// <summary> The prefab asset behind an object in prefab mode / in the project window. </summary>
        public static NetworkObject PrefabAssetOf(NetworkObject networkObject)
        {
            if (PrefabUtility.IsPartOfPrefabAsset(networkObject)) return networkObject;
            var stage = PrefabStageUtility.GetPrefabStage(networkObject.gameObject);
            return stage != null ? AssetDatabase.LoadAssetAtPath<NetworkObject>(stage.assetPath) : null;
        }
    }
}
