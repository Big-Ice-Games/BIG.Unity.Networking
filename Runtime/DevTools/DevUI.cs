using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace BIG.Unity.Networking.DevTools
{
    /// <summary>
    /// Builds a <see cref="DevPanel"/>'s content: rows of text, buttons, fields and level bars. Every visual property
    /// lives in DevOverlay.uss — this only places elements and binds data (USS classes: big-text, big-button...).
    /// Lambda overloads are bindings: re-evaluated on each overlay refresh.
    /// </summary>
    public sealed class DevUI
    {
        public const string GOOD = "good";
        public const string WARN = "warn";
        public const string BAD = "bad";
        public const string MUTED = "muted";

        private readonly List<Action> _bindings;

        internal DevUI(VisualElement root, List<Action> bindings)
        {
            Root = root;
            _bindings = bindings;
        }

        /// <summary> Container this builder adds to. </summary>
        public VisualElement Root { get; }

        #region Text

        public Label Text(string text, string style = null)
        {
            var label = Add(new Label(text), "big-text");
            SetStyle(label, "big-text", null, style);
            return label;
        }

        /// <summary> Text (and optionally its color: <see cref="GOOD"/>, <see cref="WARN"/>...) re-read on every refresh. </summary>
        public Label Text(Func<string> text, Func<string> style = null)
        {
            var label = Add(new Label(), "big-text");
            string current = null;
            Bind(() =>
            {
                label.text = text();
                if (style == null) return;
                string next = style();
                SetStyle(label, "big-text", current, next);
                current = next;
            });
            return label;
        }

        /// <summary> Bold sub-heading inside a module. </summary>
        public Label Heading(string text) => Add(new Label(text), "big-heading");

        #endregion

        #region Controls

        public Button Button(string text, Action click, Func<bool> enabled = null) =>
            Button(() => text, click, enabled);

        /// <summary> Button whose caption changes (e.g. "Ready" / "Not ready"). </summary>
        public Button Button(Func<string> text, Action click, Func<bool> enabled = null)
        {
            var button = Add(new Button(click), "big-button");
            Bind(() =>
            {
                button.text = text();
                if (enabled != null) button.SetEnabled(enabled());
            });
            return button;
        }

        /// <summary> Text input with a small label in front. <paramref name="changed"/> gets every edit. </summary>
        public TextField Field(string label, string value, Action<string> changed, string placeholder = null)
        {
            var row = Row();
            if (!string.IsNullOrEmpty(label)) row.Add(new Label(label), "big-field-label");
            var field = row.Add(new TextField { value = value ?? "" }, "big-field");
            if (!string.IsNullOrEmpty(placeholder)) field.textEdition.placeholder = placeholder;
            field.RegisterValueChangedCallback(evt => changed(evt.newValue));
            return field;
        }

        /// <summary> Horizontal level bar 0..1 (mic level, voice activity...). </summary>
        public VisualElement Bar(Func<float> level)
        {
            var bar = Add(new VisualElement(), "big-bar");
            var fill = new VisualElement();
            fill.AddToClassList("big-bar__fill");
            bar.Add(fill);
            Bind(() =>
            {
                float value = Math.Clamp(level(), 0f, 1f);
                fill.style.width = Length.Percent(value * 100f);
                fill.EnableInClassList("big-bar__fill--hot", value > 0.8f);
            });
            return bar;
        }

        #endregion

        #region Layout

        /// <summary> Horizontal row; children share the width. </summary>
        public DevUI Row() => Child("big-row");

        /// <summary> Block shown only while <paramref name="visible"/> is true. </summary>
        public DevUI Section(Func<bool> visible)
        {
            var section = Child("big-section");
            Bind(() => section.Root.style.display = visible() ? DisplayStyle.Flex : DisplayStyle.None);
            return section;
        }

        /// <summary>
        /// One row per item (rooms, members, players). Rows are rebuilt only when the set of keys changes; inside a row
        /// use lambda bindings for values that change (ready, level...).
        /// </summary>
        public void List<T>(Func<IEnumerable<T>> items, Func<T, string> key, Action<DevUI, T> buildRow, string emptyText = null)
        {
            var container = Child("big-list");
            var rowBindings = new List<Action>();
            var keys = new List<string>();
            var current = new List<string>();
            Label empty = emptyText != null ? container.Text(emptyText, MUTED) : null;

            Bind(() =>
            {
                var list = new List<T>(items());
                current.Clear();
                foreach (var item in list) current.Add(key(item));

                if (!SameKeys(current, keys))
                {
                    keys.Clear();
                    keys.AddRange(current);
                    rowBindings.Clear();
                    container.Root.Clear();
                    if (empty != null && list.Count == 0) container.Root.Add(empty);
                    foreach (var item in list)
                    {
                        var row = new DevUI(new VisualElement(), rowBindings);
                        row.Root.AddToClassList("big-list__item");
                        container.Root.Add(row.Root);
                        buildRow(row, item);
                    }
                }

                foreach (var binding in rowBindings) binding();
            });
        }

        private static bool SameKeys(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private DevUI Child(string cssClass)
        {
            var element = Add(new VisualElement(), cssClass);
            return new DevUI(element, _bindings);
        }

        #endregion

        /// <summary> Add any element (with an optional USS class) — for things the helpers don't cover. </summary>
        public T Add<T>(T element, string cssClass = null) where T : VisualElement
        {
            if (cssClass != null) element.AddToClassList(cssClass);
            Root.Add(element);
            return element;
        }

        /// <summary> Register your own binding, re-run on every refresh. </summary>
        public void Bind(Action binding)
        {
            _bindings.Add(binding);
            binding();
        }

        private static void SetStyle(VisualElement element, string block, string previous, string next)
        {
            if (previous == next) return;
            if (previous != null) element.RemoveFromClassList($"{block}--{previous}");
            if (next != null) element.AddToClassList($"{block}--{next}");
        }
    }
}
