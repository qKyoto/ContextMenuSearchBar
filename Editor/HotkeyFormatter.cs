using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ContextMenuSearchBar.Editor
{
    /// <summary>
    /// Converts Unity's MenuItem hotkey notation ("%r", "&amp;%c", "_&amp;P", "#LEFT") into the text shown by native menus ("Ctrl+R").
    /// </summary>
    internal static class HotkeyFormatter
    {
        private static readonly Dictionary<string, string> s_SpecialKeys = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "LEFT", "Left" }, { "RIGHT", "Right" }, { "UP", "Up" }, { "DOWN", "Down" },
            { "HOME", "Home" }, { "END", "End" }, { "PGUP", "Page Up" }, { "PGDN", "Page Down" },
            { "INS", "Insert" }, { "DEL", "Delete" }, { "DELETE", "Delete" }, { "BACKSPACE", "Backspace" },
            { "TAB", "Tab" }, { "SPACE", "Space" }, { "RETURN", "Enter" }, { "ENTER", "Enter" }, { "ESC", "Esc" },
            { "KP_PLUS", "Num +" }, { "KP_MINUS", "Num -" }, { "KP_MULTIPLY", "Num *" }, { "KP_DIVIDE", "Num /" },
            { "KP_PERIOD", "Num ." }, { "KP_ENTER", "Num Enter" }, { "KP_EQUALS", "Num =" },
        };

        public static string Format(string hotkey)
        {
            if (string.IsNullOrEmpty(hotkey))
                return string.Empty;

            bool mac = Application.platform == RuntimePlatform.OSXEditor;
            var sb = new StringBuilder();
            int i = 0;
            for (; i < hotkey.Length; i++)
            {
                char c = hotkey[i];
                string modifier;
                switch (c)
                {
                    case '%': modifier = mac ? "⌘" : "Ctrl"; break;   // ⌘ on macOS
                    case '^': modifier = mac ? "⌃" : "Ctrl"; break;   // ⌃ (ctrl on macOS)
                    case '#': modifier = mac ? "⇧" : "Shift"; break;  // ⇧
                    case '&': modifier = mac ? "⌥" : "Alt"; break;    // ⌥
                    case '_': continue;                                    // "no modifier" marker
                    default: modifier = null; break;
                }

                if (modifier == null)
                    break;

                if (sb.Length > 0 && !mac)
                    sb.Append('+');
                sb.Append(modifier);
            }

            string key = hotkey.Substring(i).Trim();
            if (key.Length == 0)
                return sb.ToString();

            if (s_SpecialKeys.TryGetValue(key, out var pretty))
                key = pretty;
            else if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key.Substring(1), out _))
                key = key.ToUpperInvariant();
            else if (key.Length >= 3 && key.StartsWith("KP", System.StringComparison.OrdinalIgnoreCase))
                key = "Num " + key.Substring(2);
            else if (key.Length == 1)
                key = key.ToUpperInvariant();

            if (sb.Length > 0 && !mac)
                sb.Append('+');
            sb.Append(key);
            return sb.ToString();
        }
    }
}
