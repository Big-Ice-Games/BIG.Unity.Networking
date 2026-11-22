using System.IO;
using FishNet.Component.Transforming;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace BIG.Unity.Networking.Editor
{
    /// <summary> What kind of networked object to make. </summary>
    public enum NetworkedPreset
    {
        /// <summary> Player avatar: owner moves it (client authority), respawned by PlayerSpawner, interacts with things. </summary>
        Player,
        /// <summary> AI / moving thing the host controls; survives host migration. </summary>
        HostControlled,
        /// <summary> Physics object players push by walking into it (owner-simulated); survives host migration. </summary>
        PushableProp,
        /// <summary> Item players pick up into the hand, throw, drop, use — and push when it lies around. </summary>
        CarryableItem,
        /// <summary> Item players collect into the inventory, take out into the hand, use, throw. </summary>
        InventoryItem,
        /// <summary> Used in place, not picked up: switch, door, terminal (IInteractionHandler on the host). </summary>
        Interactable,
        /// <summary> Doesn't move, nobody interacts — only its state is networked (e.g. destructible wall). </summary>
        SceneState
    }

    /// <summary>
    /// One click from a plain object to a correctly set up networked one: adds NetworkObject, NetworkTransform, NetworkBody
    /// with the preset's profile, the behaviours (push / carry / collect / interact), NetworkMigratable, Rigidbody /
    /// collider, NetworkPlayer + NetworkInteractor — whatever the preset needs — then gives scene objects their SceneId and
    /// puts prefabs on FishNet's spawnable list. Undoable.
    /// Hierarchy / scene: right click > BIG > Networking > Make Networked. Prefab assets: Assets > BIG > Networking > Make Networked.
    /// </summary>
    public static class MakeNetworked
    {
        private const string PROFILES_FOLDER = "Assets/BIG.Networking/Profiles";
        private const string SCENE_MENU = "GameObject/BIG/Networking/Make Networked/";
        private const string PREFAB_MENU = "Assets/BIG/Networking/Make Networked/";

        private const string PLAYER = "Player";
        private const string HOST = "Host-controlled (AI, platform)";
        private const string PUSHABLE = "Pushable prop";
        private const string CARRYABLE = "Carryable item (hand, throw, use)";
        private const string INVENTORY = "Inventory item (collect, hand, use)";
        private const string INTERACTABLE = "Interactable (switch, door)";
        private const string STATE = "Scene state only";

        [MenuItem(SCENE_MENU + PLAYER, false, 10)] private static void S0() => ApplyToSelection(NetworkedPreset.Player);
        [MenuItem(SCENE_MENU + HOST, false, 11)] private static void S1() => ApplyToSelection(NetworkedPreset.HostControlled);
        [MenuItem(SCENE_MENU + PUSHABLE, false, 30)] private static void S2() => ApplyToSelection(NetworkedPreset.PushableProp);
        [MenuItem(SCENE_MENU + CARRYABLE, false, 31)] private static void S3() => ApplyToSelection(NetworkedPreset.CarryableItem);
        [MenuItem(SCENE_MENU + INVENTORY, false, 32)] private static void S4() => ApplyToSelection(NetworkedPreset.InventoryItem);
        [MenuItem(SCENE_MENU + INTERACTABLE, false, 50)] private static void S5() => ApplyToSelection(NetworkedPreset.Interactable);
        [MenuItem(SCENE_MENU + STATE, false, 51)] private static void S6() => ApplyToSelection(NetworkedPreset.SceneState);

        [MenuItem(SCENE_MENU + PLAYER, true)]
        [MenuItem(SCENE_MENU + HOST, true)]
        [MenuItem(SCENE_MENU + PUSHABLE, true)]
        [MenuItem(SCENE_MENU + CARRYABLE, true)]
        [MenuItem(SCENE_MENU + INVENTORY, true)]
        [MenuItem(SCENE_MENU + INTERACTABLE, true)]
        [MenuItem(SCENE_MENU + STATE, true)]
        private static bool HasSceneSelection() => Selection.gameObjects.Length > 0 && !EditorApplication.isPlaying;

        [MenuItem(PREFAB_MENU + PLAYER, false, 10)] private static void P0() => ApplyToPrefabs(NetworkedPreset.Player);
        [MenuItem(PREFAB_MENU + HOST, false, 11)] private static void P1() => ApplyToPrefabs(NetworkedPreset.HostControlled);
        [MenuItem(PREFAB_MENU + PUSHABLE, false, 30)] private static void P2() => ApplyToPrefabs(NetworkedPreset.PushableProp);
        [MenuItem(PREFAB_MENU + CARRYABLE, false, 31)] private static void P3() => ApplyToPrefabs(NetworkedPreset.CarryableItem);
        [MenuItem(PREFAB_MENU + INVENTORY, false, 32)] private static void P4() => ApplyToPrefabs(NetworkedPreset.InventoryItem);
        [MenuItem(PREFAB_MENU + INTERACTABLE, false, 50)] private static void P5() => ApplyToPrefabs(NetworkedPreset.Interactable);
        [MenuItem(PREFAB_MENU + STATE, false, 51)] private static void P6() => ApplyToPrefabs(NetworkedPreset.SceneState);

        [MenuItem(PREFAB_MENU + PLAYER, true)]
        [MenuItem(PREFAB_MENU + HOST, true)]
        [MenuItem(PREFAB_MENU + PUSHABLE, true)]
        [MenuItem(PREFAB_MENU + CARRYABLE, true)]
        [MenuItem(PREFAB_MENU + INVENTORY, true)]
        [MenuItem(PREFAB_MENU + INTERACTABLE, true)]
        [MenuItem(PREFAB_MENU + STATE, true)]
        private static bool HasPrefabSelection()
        {
            foreach (var obj in Selection.objects)
                if (obj is GameObject go && PrefabUtility.IsPartOfPrefabAsset(go)) return true;
            return false;
        }

        #region Entry points

        private static void ApplyToSelection(NetworkedPreset preset)
        {
            bool prefabsChanged = false;
            foreach (var go in Selection.gameObjects)
            {
                if (PrefabUtility.IsPartOfPrefabAsset(go)) continue; // Project window — handled by the Assets menu.
                Apply(go, preset);
                prefabsChanged |= FishNetEditorUtility.IsPrefabAsset(go); // Open in prefab mode.
            }
            if (prefabsChanged) FishNetEditorUtility.RefreshSpawnablePrefabs();
        }

        private static void ApplyToPrefabs(NetworkedPreset preset)
        {
            foreach (var obj in Selection.objects)
            {
                if (!(obj is GameObject asset) || !PrefabUtility.IsPartOfPrefabAsset(asset)) continue;
                string path = AssetDatabase.GetAssetPath(asset);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    Apply(root, preset);
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            FishNetEditorUtility.RefreshSpawnablePrefabs(); // New prefabs on the list + re-hash re-saved ones.
        }

        #endregion

        /// <summary> Make <paramref name="go"/> networked as <paramref name="preset"/> (scene object, prefab contents or prefab mode). </summary>
        public static void Apply(GameObject go, NetworkedPreset preset)
        {
            Undo.SetCurrentGroupName($"Make Networked ({preset})");
            int group = Undo.GetCurrentGroup();

            Ensure<NetworkObject>(go);

            switch (preset)
            {
                case NetworkedPreset.Player:
                    Ensure<NetworkTransform>(go);
                    SetProfile(Ensure<NetworkBody>(go), BodyPreset.Player);
                    Ensure<NetworkPlayer>(go);
                    Ensure<NetworkInteractor>(go);
                    EnsureCollider(go);
                    Remove<NetworkMigratable>(go); // Avatars are respawned by PlayerSpawner, not migrated.
                    break;

                case NetworkedPreset.HostControlled:
                    Ensure<NetworkTransform>(go);
                    SetProfile(Ensure<NetworkBody>(go), BodyPreset.HostControlled);
                    Ensure<NetworkMigratable>(go);
                    break;

                case NetworkedPreset.PushableProp:
                    MakePhysical(go);
                    Ensure<NetworkPushable>(go);
                    break;

                case NetworkedPreset.CarryableItem:
                    MakePhysical(go);
                    Ensure<NetworkPushable>(go);
                    Ensure<NetworkCarryable>(go);
                    break;

                case NetworkedPreset.InventoryItem:
                    MakePhysical(go);
                    Ensure<NetworkPushable>(go);
                    Ensure<NetworkCarryable>(go);
                    Ensure<NetworkCollectible>(go);
                    break;

                case NetworkedPreset.Interactable:
                    EnsureCollider(go);
                    Ensure<NetworkInteractable>(go);
                    Ensure<NetworkMigratable>(go);
                    break;

                case NetworkedPreset.SceneState:
                    Ensure<NetworkMigratable>(go);
                    break;
            }

            if (!FishNetEditorUtility.IsPrefabAsset(go) && go.scene.IsValid())
                FishNetEditorUtility.AssignSceneIds(go.scene);

            Undo.CollapseUndoOperations(group);
            EditorUtility.SetDirty(go);
            Debug.Log($"[Network] '{go.name}' is networked as {preset}. Check the summary on its NetworkBody / NetworkMigratable.", go);
        }

        /// <summary> Owner-simulated physics object that survives host migration — the base of props and items. </summary>
        private static void MakePhysical(GameObject go)
        {
            EnsureCollider(go);
            MakeConvex(go);
            Ensure<Rigidbody>(go);
            Ensure<NetworkTransform>(go);
            SetProfile(Ensure<NetworkBody>(go), BodyPreset.PhysicsProp);
            Ensure<NetworkMigratable>(go);
        }

        #region Helpers

        private static T Ensure<T>(GameObject go) where T : Component =>
            go.TryGetComponent<T>(out var existing) ? existing : Undo.AddComponent<T>(go);

        private static void Remove<T>(GameObject go) where T : Component
        {
            if (go.TryGetComponent<T>(out var component)) Undo.DestroyObjectImmediate(component);
        }

        private static void EnsureCollider(GameObject go)
        {
            if (go.GetComponentInChildren<Collider>() == null) Undo.AddComponent<BoxCollider>(go);
        }

        /// <summary> Touch detection uses Collider.ClosestPoint; dynamic Rigidbodies need convex mesh colliders anyway. </summary>
        private static void MakeConvex(GameObject go)
        {
            foreach (var mesh in go.GetComponentsInChildren<MeshCollider>())
            {
                if (mesh.convex) continue;
                Undo.RecordObject(mesh, "Make convex");
                mesh.convex = true;
            }
        }

        private static void SetProfile(NetworkBody body, BodyPreset preset)
        {
            var serialized = new SerializedObject(body);
            serialized.FindProperty("_profile").objectReferenceValue = ProfileAsset(preset);
            serialized.ApplyModifiedProperties();
        }

        /// <summary> Shared profile per preset: Assets/BIG.Networking/Profiles/&lt;preset&gt;.asset, created on first use. </summary>
        public static NetworkBodyProfile ProfileAsset(BodyPreset preset)
        {
            string path = $"{PROFILES_FOLDER}/{preset}.asset";
            var profile = AssetDatabase.LoadAssetAtPath<NetworkBodyProfile>(path);
            if (profile != null) return profile;

            Directory.CreateDirectory(PROFILES_FOLDER);
            profile = NetworkBodyProfile.Create(preset);
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            return profile;
        }

        #endregion
    }
}
