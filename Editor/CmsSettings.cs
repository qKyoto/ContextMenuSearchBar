using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ContextMenuSearchBar.Editor
{
    /// <summary>
    /// User preferences (stored in EditorPrefs, i.e. per machine) and the Preferences page.
    /// </summary>
    internal static class CmsSettings
    {
        private const string Prefix = "ContextMenuSearchBar.";

        private static bool? s_Enabled;
        private static bool? s_ReplaceContextMenu;
        private static bool? s_ReplaceCreateButton;
        private static bool? s_ShowShortcuts;
        private static int? s_MaxHeight;
        private static bool? s_DebugLogging;
        private static int? s_AnchorMode;

        public static bool Enabled
        {
            get => Get(ref s_Enabled, "Enabled", true);
            set => Set(ref s_Enabled, "Enabled", value);
        }

        /// <summary>Replace the right-click menu of the Project window.</summary>
        public static bool ReplaceContextMenu
        {
            get => Get(ref s_ReplaceContextMenu, "ReplaceContextMenu", true);
            set => Set(ref s_ReplaceContextMenu, "ReplaceContextMenu", value);
        }

        /// <summary>Replace the menu shown by the toolbar "+" (Create) button of the Project window.</summary>
        public static bool ReplaceCreateButton
        {
            get => Get(ref s_ReplaceCreateButton, "ReplaceCreateButton", true);
            set => Set(ref s_ReplaceCreateButton, "ReplaceCreateButton", value);
        }

        public static bool ShowShortcuts
        {
            get => Get(ref s_ShowShortcuts, "ShowShortcuts", true);
            set => Set(ref s_ShowShortcuts, "ShowShortcuts", value);
        }

        /// <summary>Maximum popup height in points; taller menus scroll.</summary>
        /// <summary>What happens to the popup when its content height changes (submenu entered, search results).</summary>
        public enum PopupAnchorMode
        {
            /// <summary>Keep the top-left corner where the popup first appeared; grow downwards, scroll when out of room.</summary>
            KeepTopLeft = 0,
            /// <summary>Re-run Unity's drop-down placement relative to the click point (may move the window).</summary>
            ReplaceAtCursor = 1,
        }

        public static PopupAnchorMode AnchorMode
        {
            get
            {
                if (!s_AnchorMode.HasValue)
                    s_AnchorMode = EditorPrefs.GetInt(Prefix + "AnchorMode", (int)PopupAnchorMode.ReplaceAtCursor);
                return (PopupAnchorMode)s_AnchorMode.Value;
            }
            set
            {
                if (s_AnchorMode == (int)value) return;
                s_AnchorMode = (int)value;
                EditorPrefs.SetInt(Prefix + "AnchorMode", (int)value);
            }
        }

        public static int MaxHeight
        {
            get
            {
                if (!s_MaxHeight.HasValue)
                    s_MaxHeight = EditorPrefs.GetInt(Prefix + "MaxHeight", 640);
                return s_MaxHeight.Value;
            }
            set
            {
                if (s_MaxHeight == value) return;
                s_MaxHeight = value;
                EditorPrefs.SetInt(Prefix + "MaxHeight", value);
            }
        }

        public static bool DebugLogging
        {
            get => Get(ref s_DebugLogging, "DebugLogging", false);
            set => Set(ref s_DebugLogging, "DebugLogging", value);
        }

        // ------------------------------------------------------------------ appearance (stored per editor theme)

        /// <summary>Incremented whenever a color changes so cached GUI styles get rebuilt.</summary>
        public static int AppearanceVersion { get; private set; }

        public static Color BackgroundColor
        {
            get => GetColor("Background", IsDark ? new Color32(0x38, 0x38, 0x38, 0xFF) : new Color32(0xF3, 0xF3, 0xF3, 0xFF));
            set => SetColor("Background", value);
        }

        public static Color TextColor
        {
            get => GetColor("Text", IsDark ? new Color32(0xE4, 0xE4, 0xE4, 0xFF) : new Color32(0x1A, 0x1A, 0x1A, 0xFF));
            set => SetColor("Text", value);
        }

        /// <summary>Background of the hovered / keyboard-selected row.</summary>
        public static Color HighlightColor
        {
            get => GetColor("Highlight", IsDark ? new Color32(0x2C, 0x5D, 0x87, 0xFF) : new Color32(0x3A, 0x72, 0xB0, 0xFF));
            set => SetColor("Highlight", value);
        }

        public static Color HighlightTextColor
        {
            get => GetColor("HighlightText", Color.white);
            set => SetColor("HighlightText", value);
        }

        public static Color SeparatorColor
        {
            get => GetColor("Separator", IsDark ? new Color32(0x5A, 0x5A, 0x5A, 0xFF) : new Color32(0xC0, 0xC0, 0xC0, 0xFF));
            set => SetColor("Separator", value);
        }

        public static Color BorderColor
        {
            get => GetColor("Border", IsDark ? new Color32(0x1E, 0x1E, 0x1E, 0xFF) : new Color32(0xA8, 0xA8, 0xA8, 0xFF));
            set => SetColor("Border", value);
        }

        public static void ResetColors()
        {
            foreach (var key in new[] { "Background", "Text", "Highlight", "HighlightText", "Separator", "Border" })
                EditorPrefs.DeleteKey(ColorKey(key));
            AppearanceVersion++;
        }

        private static bool IsDark => EditorGUIUtility.isProSkin;

        private static string ColorKey(string key) => Prefix + (IsDark ? "Dark." : "Light.") + key;

        private static Color GetColor(string key, Color defaultValue)
        {
            string stored = EditorPrefs.GetString(ColorKey(key), string.Empty);
            if (stored.Length > 0 && ColorUtility.TryParseHtmlString("#" + stored, out var color))
                return color;
            return defaultValue;
        }

        private static void SetColor(string key, Color value)
        {
            string html = ColorUtility.ToHtmlStringRGBA(value);
            if (EditorPrefs.GetString(ColorKey(key), string.Empty) == html)
                return;
            EditorPrefs.SetString(ColorKey(key), html);
            AppearanceVersion++;
        }

        private static bool Get(ref bool? cache, string key, bool defaultValue)
        {
            if (!cache.HasValue)
                cache = EditorPrefs.GetBool(Prefix + key, defaultValue);
            return cache.Value;
        }

        private static void Set(ref bool? cache, string key, bool value)
        {
            if (cache == value) return;
            cache = value;
            EditorPrefs.SetBool(Prefix + key, value);
        }

        [SettingsProvider]
        private static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Preferences/Context Menu Search Bar", SettingsScope.User)
            {
                label = "Context Menu Search Bar",
                keywords = new HashSet<string>(new[] { "context", "menu", "search", "project", "create", "asset" }),
                guiHandler = _ => DrawPreferences(),
            };
        }

        private static readonly GUIContent[] s_AnchorModeLabels =
        {
            new GUIContent("Keep top-left corner"),
            new GUIContent("Re-place at cursor")
        };

        private static void DrawPreferences()
        {
            EditorGUIUtility.labelWidth = 260f;
            GUILayout.Space(8f);

            using (new EditorGUI.IndentLevelScope())
            {
                Enabled = EditorGUILayout.Toggle("Enabled", Enabled);

                using (new EditorGUI.DisabledScope(!Enabled))
                {
                    GUILayout.Space(8f);
                    EditorGUILayout.LabelField("Where to use the searchable menu", EditorStyles.boldLabel);
                    ReplaceContextMenu = EditorGUILayout.Toggle(
                        new GUIContent("Right-click in the Project window",
                            "The context menu you get by right-clicking an asset, a folder or empty space in the Project window (the \"Assets\" menu)."),
                        ReplaceContextMenu);
                    ReplaceCreateButton = EditorGUILayout.Toggle(
                        new GUIContent("\"+\" button in the Project window toolbar",
                            "The small \"+\" dropdown at the top-left of the Project window. It opens the same \"Create\" submenu; with this on it gets the search field too."),
                        ReplaceCreateButton);

                    GUILayout.Space(8f);
                    EditorGUILayout.LabelField("Popup", EditorStyles.boldLabel);
                    ShowShortcuts = EditorGUILayout.Toggle(
                        new GUIContent("Show keyboard shortcuts", "Draw shortcut hints (Ctrl+R) on the right, like the native menu does."),
                        ShowShortcuts);
                    MaxHeight = EditorGUILayout.IntSlider(
                        new GUIContent("Max popup height", "Taller menus get a scrollbar."), MaxHeight, 200, 1200);
                    AnchorMode = (PopupAnchorMode)EditorGUILayout.Popup(
                        new GUIContent("When the list changes",
                            "Re-place at cursor: the popup is positioned again relative to the click point, like a native menu would be, so it may jump.\n" +
                            "Keep top-left corner: the popup stays where it opened and only grows downwards; when it reaches the screen edge the list scrolls."),
                        (int)AnchorMode, s_AnchorModeLabels);

                    GUILayout.Space(8f);
                    DrawAppearance();
                }

                GUILayout.Space(8f);
                DebugLogging = EditorGUILayout.Toggle(
                    new GUIContent("Debug logging", "Log what the tool intercepts to the Console. Useful when reporting issues."),
                    DebugLogging);
            }

            if (!ProjectWindowHook.IsSupported)
            {
                EditorGUILayout.HelpBox(
                    "The internal Unity API this tool relies on was not found in this Unity version. " +
                    "The native menus are left untouched. Details: " + ProjectWindowHook.UnsupportedReason,
                    MessageType.Warning);
            }
        }

        private static void DrawAppearance()
        {
            EditorGUILayout.LabelField("Appearance (" + (IsDark ? "dark" : "light") + " editor theme)", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                BackgroundColor = EditorGUILayout.ColorField(new GUIContent("Background"), BackgroundColor, true, true, false);
                TextColor = EditorGUILayout.ColorField(new GUIContent("Text"), TextColor, true, true, false);
                HighlightColor = EditorGUILayout.ColorField(new GUIContent("Selected row"), HighlightColor, true, true, false);
                HighlightTextColor = EditorGUILayout.ColorField(new GUIContent("Selected row text"), HighlightTextColor, true, true, false);
                SeparatorColor = EditorGUILayout.ColorField(new GUIContent("Separator lines"), SeparatorColor, true, true, false);
                BorderColor = EditorGUILayout.ColorField(new GUIContent("Window border"), BorderColor, true, true, false);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUI.indentLevel * 15f);
                    if (GUILayout.Button("Reset colors to defaults", GUILayout.Width(180f)))
                        ResetColors();
                }
            }
            EditorGUILayout.LabelField("Disabled and secondary text (shortcuts, paths in search results) are derived from Text and Background.", EditorStyles.miniLabel);
        }
    }

    internal static class CmsLog
    {
        private const string Tag = "[Context Menu Search Bar] ";

        public static void Debug(string message)
        {
            if (CmsSettings.DebugLogging)
                UnityEngine.Debug.Log(Tag + message);
        }

        public static void Warning(string message) => UnityEngine.Debug.LogWarning(Tag + message);

        public static void Error(string message) => UnityEngine.Debug.LogError(Tag + message);
    }
}
