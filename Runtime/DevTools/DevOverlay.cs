using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace BIG.Unity.Networking.DevTools
{
    /// <summary> When the developer overlay is there. </summary>
    public enum DevOverlayMode
    {
        Off,
        /// <summary>
        /// Editor, Development builds, and any build with the scripting define <c>DEVELOPMENT</c> (Player settings / a
        /// build profile's Scripting Defines) — a fast test build keeps the overlay; a release build without it doesn't.
        /// </summary>
        DevelopmentOnly,
        Always
    }

    /// <summary>
    /// Developer overlay (UI Toolkit): collapsible modules — Session, Rooms, Lobby, Players, plus whatever other
    /// assemblies and the game register (<see cref="Register"/>). Created by <see cref="NetworkSession"/> from the
    /// Network Config (Dev tools section) — no setup in scenes.
    /// <list type="bullet">
    /// <item>Click a module's header to collapse / expand it; remembered per module (PlayerPrefs).</item>
    /// <item>Toggle key (default F1) hides / shows the whole overlay; ⇆ docks it left / right.</item>
    /// <item>Look: Resources/BIG.Networking/DevOverlay.uxml, DevModule.uxml, DevOverlayStyle.uss.</item>
    /// </list>
    /// </summary>
    public sealed class DevOverlay : MonoBehaviour
    {
        private const string RESOURCES = "BIG.Networking/";
        private const string PREFS = "BIG.DevOverlay.";
        private const float REFRESH_INTERVAL = 0.1f;

        private static readonly List<DevPanel> Registered = new List<DevPanel>();

        public static DevOverlay Instance { get; private set; }

        /// <summary> A text field of the overlay has focus — game controls should ignore the keyboard. </summary>
        public static bool IsTyping => Instance != null && Instance._root?.panel?.focusController?.focusedElement is TextField;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _window;
        private VisualElement _modulesContainer;
        private VisualElement _viewport;
        private float _scroll;
        private VisualTreeAsset _moduleTemplate;
        private readonly List<Module> _modules = new List<Module>();
        private float _nextRefresh;
        private bool _visible = true;

        private sealed class Module
        {
            public DevPanel Panel;
            public VisualElement Element;
            public VisualElement Body;
            public Label Arrow;
            public Label Badge;
            public readonly List<Action> Bindings = new List<Action>();
            public bool Collapsed;
        }

        /// <summary>
        /// Add a module (any time — also before the overlay exists). Typical: from a
        /// <c>[RuntimeInitializeOnLoadMethod]</c> in your assembly, or when your game system starts.
        /// </summary>
        public static void Register(DevPanel panel)
        {
            Registered.Add(panel);
            if (Instance != null) Instance.AddModule(panel);
        }

        public static void Unregister(DevPanel panel)
        {
            Registered.Remove(panel);
            if (Instance != null) Instance.RemoveModule(panel);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Registered.Clear(); // Domain reload off in the editor.

        /// <summary> Editor, a Development build, or the DEVELOPMENT scripting define. </summary>
        public static bool IsDevelopment =>
#if DEVELOPMENT
            true;
#else
            Application.isEditor || Debug.isDebugBuild;
#endif

        /// <summary> Called by <see cref="NetworkSession"/>: create the overlay when the config wants one. </summary>
        internal static void CreateIfEnabled(NetworkConfig config, Transform parent)
        {
            bool enabled = config.DevOverlay == DevOverlayMode.Always ||
                           (config.DevOverlay == DevOverlayMode.DevelopmentOnly && IsDevelopment);
            if (!enabled || Instance != null) return;

            var go = new GameObject("BIG Dev Overlay");
            go.transform.SetParent(parent, false);
            go.AddComponent<DevOverlay>();
        }

        #region Unity

        private void Awake()
        {
            Instance = this;

            var layout = Load<VisualTreeAsset>("DevOverlay");
            _moduleTemplate = Load<VisualTreeAsset>("DevModule");
            // Not "DevOverlay": the UXML of that name carries an (empty) inline StyleSheet sub-asset Resources.Load would return.
            var style = Load<StyleSheet>("DevOverlayStyle");

            // A copy of the config's PanelSettings ASSET: it carries the text (ICU) data builds need. Copied so the
            // settings below never touch the asset.
            var source = Session.Config.DevOverlayPanel;
            if (source == null && !Application.isEditor)
                throw new InvalidOperationException("[Network] Network Config has no Dev Overlay Panel — open the project in the editor once " +
                                                    "(it creates Assets/BIG.Networking/DevOverlayPanelSettings.asset) and rebuild.");
            var settings = source != null ? Instantiate(source) : ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = "BIG Dev Overlay";
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 1f; // Follow height: same physical size on 1080p / 1440p / 4K.
            settings.sortingOrder = 10000;
            settings.themeStyleSheet = ScriptableObject.CreateInstance<ThemeStyleSheet>(); // Everything is styled by DevOverlay.uss.

            // Configured while inactive: UIDocument attaches to its panel in OnEnable.
            var documentObject = new GameObject("Document");
            documentObject.SetActive(false);
            documentObject.transform.SetParent(transform, false);
            _document = documentObject.AddComponent<UIDocument>();
            _document.panelSettings = settings;
            _document.visualTreeAsset = layout;
            documentObject.SetActive(true);

            _root = _document.rootVisualElement;
            _root.styleSheets.Add(style);
            _root.style.unityFontDefinition = FontDefinition.FromFont(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            _root.pickingMode = PickingMode.Ignore; // Clicks outside the window reach the game.
            _root.AddToClassList("big-overlay-root"); // Full screen — normally the (absent) theme does that.
            _root.style.position = Position.Absolute;
            _root.style.left = _root.style.top = _root.style.right = _root.style.bottom = 0;

            _window = _root.Q("overlay");
            _modulesContainer = _root.Q("modules");
            _viewport = _root.Q("viewport");
            _viewport.RegisterCallback<WheelEvent>(OnWheel);
            _viewport.RegisterCallback<GeometryChangedEvent>(_ => ScrollBy(0f));
            _modulesContainer.RegisterCallback<GeometryChangedEvent>(_ => ScrollBy(0f));
            _root.Q<Button>("collapse-all").clicked += () => SetAllCollapsed(true);
            _root.Q<Button>("expand-all").clicked += () => SetAllCollapsed(false);
            _root.Q<Button>("dock").clicked += ToggleDock;
            _root.Q<Label>("hint").text = $"{Session.Config.DevOverlayKey} hides this";
            SetDockRight(PlayerPrefs.GetInt(PREFS + "DockRight", 0) == 1);

            foreach (var panel in CorePanels.Create())
                AddModule(panel);
            foreach (var panel in Registered.ToArray())
                AddModule(panel);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private NetworkSession Session => NetworkSession.Instance;

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[Session.Config.DevOverlayKey].wasPressedThisFrame && !IsTyping)
                SetVisible(!_visible);

            if (!_visible || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + REFRESH_INTERVAL;
            Refresh();
        }

        #endregion

        #region Modules

        private void AddModule(DevPanel panel)
        {
            foreach (var existing in _modules)
                if (existing.Panel == panel) return;

            panel.Attach(gameObject);
            var element = _moduleTemplate.Instantiate();
            element.AddToClassList("big-module-root");
            var module = new Module
            {
                Panel = panel,
                Element = element,
                Body = element.Q("body"),
                Arrow = element.Q<Label>("arrow"),
                Badge = element.Q<Label>("badge"),
                Collapsed = PlayerPrefs.GetInt(PREFS + panel.Title, panel.CollapsedByDefault ? 1 : 0) == 1
            };
            element.Q<Label>("title").text = panel.Title;
            element.Q("header").RegisterCallback<ClickEvent>(_ => SetCollapsed(module, !module.Collapsed));

            panel.Build(new DevUI(module.Body, module.Bindings));
            ApplyCollapsed(module);

            // Keep the order: lower Order first, registration order within the same Order.
            int index = _modules.FindIndex(m => m.Panel.Order > panel.Order);
            if (index < 0) index = _modules.Count;
            _modules.Insert(index, module);
            _modulesContainer.Insert(index, element);
        }

        private void RemoveModule(DevPanel panel)
        {
            int index = _modules.FindIndex(m => m.Panel == panel);
            if (index < 0) return;
            _modules[index].Element.RemoveFromHierarchy();
            _modules.RemoveAt(index);
        }

        private void Refresh()
        {
            foreach (var module in _modules)
            {
                bool available = module.Panel.IsAvailable;
                module.Element.style.display = available ? DisplayStyle.Flex : DisplayStyle.None;
                if (!available) continue;

                string badge = module.Panel.Badge;
                module.Badge.text = badge ?? "";
                module.Badge.style.display = string.IsNullOrEmpty(badge) ? DisplayStyle.None : DisplayStyle.Flex;
                foreach (var style in new[] { DevUI.GOOD, DevUI.WARN, DevUI.BAD })
                    module.Badge.EnableInClassList($"big-module__badge--{style}", module.Panel.BadgeStyle == style);

                if (module.Collapsed) continue; // Collapsed modules cost nothing.
                module.Panel.Refresh();
                foreach (var binding in module.Bindings) binding();
            }
        }

        private void SetCollapsed(Module module, bool collapsed)
        {
            module.Collapsed = collapsed;
            PlayerPrefs.SetInt(PREFS + module.Panel.Title, collapsed ? 1 : 0);
            ApplyCollapsed(module);
            if (!collapsed) Refresh();
        }

        private static void ApplyCollapsed(Module module)
        {
            module.Body.style.display = module.Collapsed ? DisplayStyle.None : DisplayStyle.Flex;
            module.Arrow.text = module.Collapsed ? "►" : "▼";
            module.Element.EnableInClassList("big-module-root--collapsed", module.Collapsed);
        }

        private void SetAllCollapsed(bool collapsed)
        {
            foreach (var module in _modules) SetCollapsed(module, collapsed);
        }

        #endregion

        #region Scrolling

        private const float WHEEL_STEP = 40f;

        private void OnWheel(WheelEvent evt)
        {
            ScrollBy(-evt.delta.y * WHEEL_STEP);
            evt.StopPropagation();
        }

        /// <summary> Moves the modules column, clamped so it never scrolls past its ends (0 re-clamps after a resize). </summary>
        private void ScrollBy(float delta)
        {
            float overflow = Mathf.Max(0f, _modulesContainer.layout.height - _viewport.layout.height);
            _scroll = Mathf.Clamp(_scroll + delta, -overflow, 0f);
            _modulesContainer.style.top = _scroll;
        }

        #endregion

        private void SetVisible(bool visible)
        {
            _visible = visible;
            _window.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible) Refresh();
        }

        private void ToggleDock() => SetDockRight(!_window.ClassListContains("big-overlay--right"));

        private void SetDockRight(bool right)
        {
            _window.EnableInClassList("big-overlay--right", right);
            PlayerPrefs.SetInt(PREFS + "DockRight", right ? 1 : 0);
        }

        private static T Load<T>(string name) where T : UnityEngine.Object
        {
            var asset = Resources.Load<T>(RESOURCES + name);
            if (asset == null)
                throw new InvalidOperationException($"[Network] Dev overlay resource '{RESOURCES}{name}' ({typeof(T).Name}) is missing from the BIG.Unity.Networking package.");
            return asset;
        }
    }
}
