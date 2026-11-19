using System;
using System.Collections.Generic;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace BIG.Unity.Networking.Editor
{
    /// <summary> One setup problem of a networked object, with the fix when there is one. </summary>
    internal sealed class SetupIssue
    {
        public SetupIssue(MessageType severity, string message, string fixLabel = null, Action fix = null)
        {
            Severity = severity;
            Message = message;
            FixLabel = fixLabel;
            Fix = fix;
        }

        public readonly MessageType Severity;
        public readonly string Message;
        public readonly string FixLabel;
        public readonly Action Fix;
    }

    /// <summary>
    /// Everything that can be wrong with one networked object — each pitfall we hit once, as a check with a fix.
    /// Used by the NetworkBody / NetworkMigratable inspectors (and the setup validator).
    /// </summary>
    internal static class NetworkSetupChecks
    {
        /// <summary> One-line summary: authority · physics · migration · spawn. </summary>
        public static string Summary(GameObject go)
        {
            var parts = new List<string>();
            if (go.TryGetComponent<NetworkBody>(out var body) && body.Profile != null)
            {
                var profile = body.Profile;
                parts.Add(profile.Authority == BodyAuthority.Owner ? "owner moves it (no owner = host)" : "host moves it");
                parts.Add(profile.Physics switch
                {
                    BodyPhysics.Owner => "owner simulates physics",
                    BodyPhysics.Host => "host simulates physics",
                    _ => "no physics"
                });
            }
            else if (!go.TryGetComponent<FishNet.Component.Transforming.NetworkTransform>(out _))
            {
                parts.Add("doesn't move over the network");
            }

            var can = new List<string>();
            if (go.TryGetComponent<NetworkPushable>(out _)) can.Add("push");
            if (go.TryGetComponent<NetworkCarryable>(out _)) can.Add("carry / throw / use");
            if (go.TryGetComponent<NetworkCollectible>(out _)) can.Add("collect");
            if (go.TryGetComponent<NetworkInteractable>(out _)) can.Add("interact");
            if (can.Count > 0) parts.Add("players can: " + string.Join(", ", can));

            parts.Add(go.TryGetComponent<NetworkPlayer>(out _) ? "player (respawned)"
                : go.TryGetComponent<NetworkMigratable>(out _) ? "survives host migration" : "NOT migrated");
            parts.Add(FishNetEditorUtility.IsPrefabAsset(go) ? "spawnable prefab" : go.scene.IsValid() ? "scene object" : "prefab");
            return string.Join("  ·  ", parts);
        }

        public static List<SetupIssue> Check(GameObject go)
        {
            var issues = new List<SetupIssue>();
            bool isPrefab = FishNetEditorUtility.IsPrefabAsset(go);

            if (!go.TryGetComponent<NetworkObject>(out var networkObject))
            {
                issues.Add(new SetupIssue(MessageType.Error, "No NetworkObject on this object — FishNet ignores it.",
                    "Add NetworkObject", () => Undo.AddComponent<NetworkObject>(go)));
                return issues;
            }

            var body = go.GetComponent<NetworkBody>();
            if (body != null) CheckBody(go, body, issues);

            if (go.TryGetComponent<NetworkMigratable>(out var migratable) && go.TryGetComponent<NetworkPlayer>(out _))
                issues.Add(new SetupIssue(MessageType.Warning,
                    "Player avatars are respawned by PlayerSpawner — NetworkMigratable here would spawn a second copy after a migration.",
                    "Remove NetworkMigratable", () => Undo.DestroyObjectImmediate(migratable)));

            bool hostThing = (body != null && body.Profile != null && body.Profile.Authority == BodyAuthority.Host) ||
                             go.TryGetComponent<NetworkPushable>(out _) || go.TryGetComponent<NetworkItem>(out _) ||
                             go.TryGetComponent<NetworkInteractable>(out _);
            if (hostThing && migratable == null && !go.TryGetComponent<NetworkPlayer>(out _))
                issues.Add(new SetupIssue(MessageType.Info,
                    "Without NetworkMigratable this object " + (isPrefab ? "disappears" : "resets") + " when the host changes.",
                    "Add NetworkMigratable", () => Undo.AddComponent<NetworkMigratable>(go)));

            if (isPrefab)
            {
                var asset = FishNetEditorUtility.PrefabAssetOf(networkObject);
                if (asset != null && !FishNetEditorUtility.IsSpawnable(asset))
                    issues.Add(new SetupIssue(MessageType.Error, "Not on FishNet's spawnable prefab list — spawning it fails.",
                        "Refresh spawnable prefabs", FishNetEditorUtility.RefreshSpawnablePrefabs));
            }
            else if (go.scene.IsValid() && FishNetEditorUtility.HasNoSceneId(networkObject))
            {
                issues.Add(new SetupIssue(MessageType.Error, "Scene object without a SceneId — FishNet never spawns it (added from code?).",
                    "Assign scene ids", () => FishNetEditorUtility.AssignSceneIds(go.scene)));
            }

            if (go.TryGetComponent<NetworkCarryable>(out _) && go.TryGetComponent<NetworkItem>(out var item) && item.Grip == null)
                issues.Add(new SetupIssue(MessageType.Info,
                    "No Grip — the hand holds it at its pivot. Add a Grip (where the fist closes), then move / turn it in the Scene view " +
                    "(green sphere = palm, blue line = the hand's forward).",
                    "Create Grip", () => CreateGrip(item)));

            if (!isPrefab && go.scene.IsValid() && IsBootstrapScene(go.scene))
                issues.Add(new SetupIssue(MessageType.Error,
                    $"'{go.scene.name}' starts the network (it has the NetworkSession) — players never load it through the network, " +
                    "so nobody observes networked objects here: hidden for the host, never sent to clients. Put them in the MATCH " +
                    "scene (loaded with Start match); keep this scene for the NetworkManager, menu and lobby UI."));

            return issues;
        }

        private static void CreateGrip(NetworkItem item)
        {
            var grip = new GameObject("Grip");
            Undo.RegisterCreatedObjectUndo(grip, "Create Grip");
            grip.transform.SetParent(item.transform, false);
            var serialized = new SerializedObject(item);
            serialized.FindProperty("_grip").objectReferenceValue = grip.transform;
            serialized.ApplyModifiedProperties();
            Selection.activeGameObject = grip; // Ready to be moved in the Scene view.
        }

        /// <summary> The scene with the NetworkSession: loaded before the network, so FishNet's SceneCondition never matches it. </summary>
        private static bool IsBootstrapScene(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.GetComponentInChildren<NetworkSession>(true) != null) return true;
            return false;
        }

        private static void CheckBody(GameObject go, NetworkBody body, List<SetupIssue> issues)
        {
            var profile = body.Profile;
            if (profile == null)
            {
                issues.Add(new SetupIssue(MessageType.Error, "NetworkBody has no profile — it throws on start.",
                    "Use Physics prop profile", () => AssignProfile(body, BodyPreset.PhysicsProp)));
                issues.Add(new SetupIssue(MessageType.None, "", "Use Host-controlled profile", () => AssignProfile(body, BodyPreset.HostControlled)));
                issues.Add(new SetupIssue(MessageType.None, "", "Use Player profile", () => AssignProfile(body, BodyPreset.Player)));
                return;
            }

            if (profile.Physics != BodyPhysics.None && !go.TryGetComponent<Rigidbody>(out _))
                issues.Add(new SetupIssue(MessageType.Warning, $"Profile '{profile.name}' simulates physics but there's no Rigidbody.",
                    "Add Rigidbody", () => Undo.AddComponent<Rigidbody>(go)));

            bool takeable = go.TryGetComponent<NetworkPushable>(out _) || go.TryGetComponent<NetworkItem>(out _);
            if (takeable && profile.Authority != BodyAuthority.Owner)
                issues.Add(new SetupIssue(MessageType.Error,
                    $"Players take this object, but profile '{profile.name}' has Host authority — the taker could never move it.",
                    "Use Physics prop profile", () => AssignProfile(body, BodyPreset.PhysicsProp)));
            if (takeable && !profile.AutoReturnToHost)
                issues.Add(new SetupIssue(MessageType.Info,
                    $"Profile '{profile.name}' never returns it to the host — whoever touched it last keeps simulating it."));

            if (go.TryGetComponent<NetworkPushable>(out _))
            {
                if (profile.Physics != BodyPhysics.Owner)
                    issues.Add(new SetupIssue(MessageType.Warning,
                        $"Pushable, but profile '{profile.name}' doesn't let the owner simulate physics — pushing would lag.",
                        "Use Physics prop profile", () => AssignProfile(body, BodyPreset.PhysicsProp)));

                var colliders = go.GetComponentsInChildren<Collider>();
                if (colliders.Length == 0)
                    issues.Add(new SetupIssue(MessageType.Warning, "Pushable needs a collider to detect the touch.",
                        "Add BoxCollider", () => Undo.AddComponent<BoxCollider>(go)));
                foreach (var collider in colliders)
                {
                    if (!(collider is MeshCollider mesh) || mesh.convex) continue;
                    issues.Add(new SetupIssue(MessageType.Warning, $"'{mesh.name}': non-convex MeshCollider — touch detection can't measure it.",
                        "Make convex", () => { Undo.RecordObject(mesh, "Make convex"); mesh.convex = true; }));
                }
            }
        }

        private static void AssignProfile(NetworkBody body, BodyPreset preset)
        {
            var serialized = new SerializedObject(body);
            serialized.FindProperty("_profile").objectReferenceValue = MakeNetworked.ProfileAsset(preset);
            serialized.ApplyModifiedProperties();
        }
    }

    /// <summary> Summary card + setup issues with Fix buttons above the component's own fields. </summary>
    internal abstract class NetworkSetupInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var go = ((Component)target).gameObject;

            // One card per object: on NetworkBody when there is one, otherwise on NetworkMigratable.
            if (target is NetworkMigratable && go.TryGetComponent<NetworkBody>(out _))
            {
                DrawDefaultInspector();
                return;
            }

            var issues = NetworkSetupChecks.Check(go);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(issues.Count == 0 ? "Network setup  ✓" : $"Network setup  —  {issues.Count} to fix",
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField(NetworkSetupChecks.Summary(go), EditorStyles.wordWrappedMiniLabel);
                DrawIssues(issues);
                if (go.TryGetComponent<NetworkBody>(out _)) DrawBehaviourButtons(go);
            }
            EditorGUILayout.Space(4);

            DrawDefaultInspector();
        }

        /// <summary> What players can do with it — each a separate component; buttons add the missing ones. </summary>
        private static void DrawBehaviourButtons(GameObject go)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Add what players can do with it:", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                AddButton<NetworkPushable>(go, "+ Push", "Walk into it to push it (you simulate the push, no lag).");
                AddButton<NetworkCarryable>(go, "+ Carry", "Pick up into the hand, throw, drop, use (IItemUse). Adds NetworkItem.");
                AddButton<NetworkCollectible>(go, "+ Collect", "Put into the inventory, take out, drop from it. Adds NetworkItem.");
                AddButton<NetworkInteractable>(go, "+ Interact", "Use it in place without picking it up (IInteractionHandler on the host).");
            }
        }

        private static void AddButton<T>(GameObject go, string label, string tooltip) where T : Component
        {
            using (new EditorGUI.DisabledScope(go.TryGetComponent<T>(out _)))
            {
                if (GUILayout.Button(new GUIContent(label, tooltip)))
                {
                    Undo.AddComponent<T>(go); // RequireComponent pulls NetworkItem in for Carry / Collect.
                    GUIUtility.ExitGUI();
                }
            }
        }

        private static void DrawIssues(System.Collections.Generic.List<SetupIssue> issues)
        {
            foreach (var issue in issues)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!string.IsNullOrEmpty(issue.Message))
                        EditorGUILayout.HelpBox(issue.Message, issue.Severity);
                    else
                        GUILayout.FlexibleSpace();
                    if (issue.Fix != null && GUILayout.Button(issue.FixLabel, GUILayout.Width(170), GUILayout.MinHeight(24)))
                    {
                        issue.Fix();
                        GUIUtility.ExitGUI(); // The component list may have changed.
                    }
                }
            }
        }
    }

    [CustomEditor(typeof(NetworkBody)), CanEditMultipleObjects]
    internal sealed class NetworkBodyInspector : NetworkSetupInspector { }

    [CustomEditor(typeof(NetworkMigratable)), CanEditMultipleObjects]
    internal sealed class NetworkMigratableInspector : NetworkSetupInspector { }
}
