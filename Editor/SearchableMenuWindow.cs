using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace ContextMenuSearchBar.Editor
{
    /// <summary>
    /// The popup that replaces a native context menu: a search field on top, then the menu items.
    /// Submenus open in place (with a "back" header) so everything stays in one window; typing filters
    /// every leaf item of the whole tree and shows matches as a flat list.
    /// </summary>
    internal sealed class SearchableMenuWindow : EditorWindow
    {
        private const float RowHeight = 20f;
        private const float SeparatorHeight = 7f;
        private const float SearchBarHeight = 28f;
        private const float HeaderHeight = 22f;
        private const float ListPadding = 3f;
        private const float LeftPadding = 24f;
        private const float RightPadding = 10f;
        private const float ArrowWidth = 14f;
        private const float MinWidth = 230f;
        private const float MaxWidth = 440f;
        private const double ClickGuardSeconds = 0.12;

        private static SearchableMenuWindow s_Current;

        private MenuNode m_Root;
        private MenuNode m_Current;
        private readonly List<MenuNode> m_Rows = new List<MenuNode>();
        private readonly List<float> m_RowTops = new List<float>();
        private float m_ContentHeight;
        private string m_Search = string.Empty;
        private int m_Selected = -1;
        private Vector2 m_Scroll;
        private float m_Width;
        private Rect m_Anchor;
        private Vector2 m_TopLeft;
        private bool m_TopLeftKnown;
        private Action<MenuNode> m_OnExecute;
        private SearchField m_SearchField;
        private double m_OpenTime;
        private bool m_ResizePending;
        private bool m_ScrollToSelected;
        private bool m_HeaderHovered;

        private bool SearchMode => !string.IsNullOrWhiteSpace(m_Search);
        private bool InSubmenu => m_Current != null && m_Current != m_Root;

        public static bool IsOpen => s_Current != null;

        /// <param name="root">Menu tree to display.</param>
        /// <param name="anchorScreenRect">Screen rect the popup drops down from (zero-size rect at the mouse for context menus).</param>
        /// <param name="onExecute">Called (after the popup closed) with the chosen leaf item.</param>
        public static void Open(MenuNode root, Rect anchorScreenRect, Action<MenuNode> onExecute)
        {
            CloseCurrent();
            if (root == null || root.Children == null || root.Children.Count == 0)
                return;

            var window = CreateInstance<SearchableMenuWindow>();
            window.m_Root = root;
            window.m_Current = root;
            window.m_OnExecute = onExecute;
            window.m_Anchor = anchorScreenRect;
            window.m_SearchField = new SearchField { autoSetFocusOnFindCommand = false };
            window.m_OpenTime = EditorApplication.timeSinceStartup;
            window.m_Width = window.ComputeWidth();
            window.RefreshRows(false);
            window.wantsMouseMove = true;

            var size = new Vector2(window.m_Width, window.ComputeDesiredHeight());
            window.ShowAsDropDown(anchorScreenRect, size);
            window.m_SearchField.SetFocus();
            window.Focus();
            s_Current = window;
        }

        public static void CloseCurrent()
        {
            if (s_Current == null) return;
            var window = s_Current;
            s_Current = null;
            try { window.Close(); } catch { /* already closed */ }
        }

        private void OnDisable()
        {
            if (s_Current == this)
                s_Current = null;
        }

        // ------------------------------------------------------------------ layout

        private float ComputeWidth()
        {
            var styles = Styles.Instance;
            float widest = 0f;
            Measure(m_Root, styles, ref widest);
            return Mathf.Clamp(Mathf.Ceil(widest), MinWidth, MaxWidth);
        }

        private static void Measure(MenuNode node, Styles styles, ref float widest)
        {
            if (node.Children == null) return;
            foreach (var child in node.Children)
            {
                if (child.IsSeparator) continue;
                float w = LeftPadding + styles.Item.CalcSize(Temp(child.Name)).x + RightPadding;
                if (child.IsSubmenu)
                    w += ArrowWidth + 12f;
                else if (CmsSettings.ShowShortcuts && child.Shortcut.Length > 0)
                    w += styles.Shortcut.CalcSize(Temp(child.Shortcut)).x + 28f;
                if (w > widest) widest = w;
                if (child.IsSubmenu)
                    Measure(child, styles, ref widest);
            }
        }

        private static readonly GUIContent s_TempContent = new GUIContent();
        private static GUIContent Temp(string text)
        {
            s_TempContent.text = text;
            s_TempContent.image = null;
            s_TempContent.tooltip = string.Empty;
            return s_TempContent;
        }

        private float TopAreaHeight => SearchBarHeight + (InSubmenu && !SearchMode ? HeaderHeight : 0f);

        private float ComputeDesiredHeight()
        {
            float content = Mathf.Max(m_ContentHeight, RowHeight + ListPadding * 2f);
            // Whole points plus 2pt of slack: the OS rounds the window to device pixels, and a window that
            // ends up a fraction of a point too small must not produce a scrollbar.
            float desired = Mathf.Ceil(TopAreaHeight + content + 1f) + 2f;
            float screenLimit = Screen.currentResolution.height / Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint) - 40f;
            float limit = Mathf.Min(CmsSettings.MaxHeight, screenLimit);
            return Mathf.Max(Mathf.Min(desired, limit), TopAreaHeight + RowHeight + ListPadding * 2f);
        }

        private void RefreshRows(bool keepSelection)
        {
            var previouslySelected = keepSelection && m_Selected >= 0 && m_Selected < m_Rows.Count ? m_Rows[m_Selected] : null;

            m_Rows.Clear();
            if (SearchMode)
                m_Rows.AddRange(MenuSearch.Filter(m_Root, m_Search));
            else if (m_Current?.Children != null)
                m_Rows.AddRange(m_Current.Children);

            m_RowTops.Clear();
            float y = ListPadding;
            foreach (var row in m_Rows)
            {
                m_RowTops.Add(y);
                y += RowHeightOf(row);
            }
            m_ContentHeight = y + ListPadding;

            m_Selected = -1;
            if (previouslySelected != null)
                m_Selected = m_Rows.IndexOf(previouslySelected);
            if (m_Selected < 0 && SearchMode)
                m_Selected = FirstEnabledIndex();

            m_Scroll = Vector2.zero;
            m_ScrollToSelected = m_Selected >= 0;
            m_ResizePending = true;
        }

        private static float RowHeightOf(MenuNode node) => node.IsSeparator ? SeparatorHeight : RowHeight;

        private int FirstEnabledIndex()
        {
            for (int i = 0; i < m_Rows.Count; i++)
                if (IsSelectable(m_Rows[i]))
                    return i;
            return -1;
        }

        private static bool IsSelectable(MenuNode node) => node != null && !node.IsSeparator && node.IsEnabled;

        private void ApplyPendingResize()
        {
            m_ResizePending = false;
            float height = ComputeDesiredHeight();
            if (Mathf.Abs(height - position.height) < 1f && Mathf.Abs(m_Width - position.width) < 1f)
                return;

            // The placement may crop the window when the menu does not fit on screen; the list then scrolls.
            var size = new Vector2(m_Width, height);
            Rect target = CmsSettings.AnchorMode == CmsSettings.PopupAnchorMode.KeepTopLeft ? FitKeepTopLeft(size) : FitDropDown(size);
            if (Mathf.Abs(target.height - position.height) < 1f && Mathf.Abs(target.width - position.width) < 1f
                && Mathf.Abs(target.y - position.y) < 1f && Mathf.Abs(target.x - position.x) < 1f)
                return;

            minSize = target.size;
            maxSize = target.size;
            position = target;
        }

        // Keeps the corner where the popup first appeared and only changes the height: grows downwards until the
        // screen edge, after which the list scrolls. Only when even a few rows would not fit is it nudged up.
        private Rect FitKeepTopLeft(Vector2 size)
        {
            if (!m_TopLeftKnown)
                return FitDropDown(size);

            float minHeight = TopAreaHeight + RowHeight * 3f + ListPadding * 2f;
            var rect = new Rect(m_TopLeft.x, m_TopLeft.y, size.x, size.y);

            Rect cropped = FitBelowPoint(m_TopLeft, size);
            if (cropped.height > 0f)
            {
                // FitBelowPoint returns the room available below the corner (never more than requested).
                rect.height = Mathf.Min(size.y, cropped.height);
                rect.x = cropped.x;
            }

            if (rect.height < minHeight)
            {
                rect.y -= minHeight - rect.height;
                rect.height = minHeight;
            }
            return rect;
        }

        // Unity's drop-down placement restricted to "below this point": the height is cropped to the screen.
        private Rect FitBelowPoint(Vector2 point, Vector2 size)
        {
            var anchor = new Rect(point.x, point.y, 0f, 0f);
            try
            {
                var method = typeof(EditorWindow).GetMethod("ShowAsDropDownFitToScreen", BindingFlags.Instance | BindingFlags.NonPublic);
                var locationType = typeof(EditorWindow).Assembly.GetType("UnityEditor.PopupLocation");
                if (method != null && locationType != null && method.GetParameters().Length == 3)
                {
                    object below = Enum.IsDefined(locationType, "BelowAlignLeft") ? Enum.Parse(locationType, "BelowAlignLeft") : Enum.Parse(locationType, "Below");
                    var order = Array.CreateInstance(locationType, 1);
                    order.SetValue(below, 0);
                    if (method.Invoke(this, new object[] { anchor, size, order }) is Rect fitted && fitted.width > 0f)
                        return fitted;
                }
            }
            catch (Exception e)
            {
                CmsLog.Debug("FitBelowPoint failed: " + e.Message);
            }

            // Fallback: clamp to the main editor window.
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            float available = main.height > 0f ? main.yMax - point.y : size.y;
            return new Rect(point.x, point.y, size.x, Mathf.Min(size.y, Mathf.Max(0f, available)));
        }

        // Re-runs Unity's own drop-down placement (below the anchor, or above when there is no room) for a new size.
        private Rect FitDropDown(Vector2 size)
        {
            try
            {
                var method = typeof(EditorWindow).GetMethod("ShowAsDropDownFitToScreen", BindingFlags.Instance | BindingFlags.NonPublic);
                if (method != null)
                {
                    var parameters = method.GetParameters();
                    object[] args;
                    if (parameters.Length == 3)
                        args = new object[] { m_Anchor, size, null };
                    else if (parameters.Length == 2)
                        args = new object[] { m_Anchor, size };
                    else
                        args = null;

                    if (args != null && method.Invoke(this, args) is Rect fitted && fitted.width > 0f && fitted.height > 0f)
                        return fitted;
                }
            }
            catch (Exception e)
            {
                CmsLog.Debug("ShowAsDropDownFitToScreen failed: " + e.Message);
            }

            // Fallback: keep the top-left corner, but stay inside the main editor window.
            var rect = new Rect(position.x, position.y, size.x, size.y);
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            if (main.width > 0f && main.height > 0f)
            {
                rect.y = Mathf.Min(rect.y, main.yMax - rect.height);
                rect.y = Mathf.Max(rect.y, main.y);
                rect.x = Mathf.Min(rect.x, main.xMax - rect.width);
                rect.x = Mathf.Max(rect.x, main.x);
            }
            return rect;
        }

        // ------------------------------------------------------------------ GUI

        private void OnGUI()
        {
            if (m_Root == null)
            {
                Close();
                return;
            }

            var styles = Styles.Instance;
            var e = Event.current;

            // EditorWindow.position is only converted to screen coordinates by the HostView right before OnGUI,
            // so the corner where the popup actually appeared can be recorded no earlier than here.
            if (!m_TopLeftKnown && e.type == EventType.Layout)
            {
                m_TopLeft = position.position;
                m_TopLeftKnown = true;
            }

            if (e.type == EventType.Layout && m_ResizePending)
                ApplyPendingResize();

            HandleKeyboard(e);

            var windowRect = new Rect(0f, 0f, position.width, position.height);
            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(windowRect, styles.Background);
                DrawBorder(windowRect, styles.Border);
            }

            DrawSearchBar(styles);

            float top = SearchBarHeight;
            if (InSubmenu && !SearchMode)
            {
                DrawHeader(new Rect(1f, top, windowRect.width - 2f, HeaderHeight), styles, e);
                top += HeaderHeight;
            }

            var listRect = new Rect(1f, top, windowRect.width - 2f, windowRect.height - top - 1f);
            DrawList(listRect, styles, e);

            if (e.type == EventType.MouseDown)
                m_SearchField.SetFocus();
        }

        private static void DrawBorder(Rect r, Color color)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), color);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), color);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1f, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), color);
        }

        private void DrawSearchBar(Styles styles)
        {
            var fieldRect = new Rect(6f, 5f, position.width - 12f, 18f);
            string newText = m_SearchField.OnGUI(fieldRect, m_Search);
            if (newText == null) newText = string.Empty;
            if (newText != m_Search)
            {
                m_Search = newText;
                RefreshRows(false);
            }

            if (m_Search.Length == 0 && Event.current.type == EventType.Repaint)
                styles.Placeholder.Draw(fieldRect, Temp("Search..."), false, false, false, false);
        }

        private void DrawHeader(Rect rect, Styles styles, Event e)
        {
            bool hovered = rect.Contains(e.mousePosition);
            if (e.type == EventType.MouseMove && hovered != m_HeaderHovered)
            {
                m_HeaderHovered = hovered;
                Repaint();
            }

            if (e.type == EventType.Repaint)
            {
                if (hovered)
                    EditorGUI.DrawRect(rect, styles.HeaderHover);
                var arrowRect = new Rect(rect.x + 5f, rect.y + (rect.height - 16f) * 0.5f, 16f, 16f);
                if (styles.ArrowLeft.image != null)
                    GUI.DrawTexture(arrowRect, styles.ArrowLeft.image, ScaleMode.ScaleToFit);
                else
                    GUI.Label(arrowRect, "◂", styles.Item);
                var labelRect = new Rect(rect.x + LeftPadding, rect.y, rect.width - LeftPadding - RightPadding, rect.height);
                GUI.Label(labelRect, Temp(m_Current.Name), styles.Header);
                EditorGUI.DrawRect(new Rect(rect.x + 4f, rect.yMax - 1f, rect.width - 8f, 1f), styles.Separator);
            }

            if (e.type == EventType.MouseDown && e.button == 0 && hovered && !ClickGuardActive())
            {
                GoBack();
                e.Use();
            }
        }

        private void DrawList(Rect listRect, Styles styles, Event e)
        {
            // Decide about the scrollbar ourselves: a content height that exceeds the list by less than the
            // bottom padding just loses that padding. If the scroll view were left to decide, a window that
            // the OS rounded down by a fraction of a point would show a bar over the rows.
            bool needScroll = m_ContentHeight > listRect.height + ListPadding;
            GUIStyle scrollbarStyle = needScroll ? GUI.skin.verticalScrollbar : GUIStyle.none;
            float scrollbarWidth = needScroll ? Mathf.Max(scrollbarStyle.fixedWidth, 12f) + scrollbarStyle.margin.left + 2f : 0f;
            float viewWidth = listRect.width - scrollbarWidth;
            var viewRect = new Rect(0f, 0f, viewWidth, needScroll ? m_ContentHeight : listRect.height);

            if (m_ScrollToSelected && m_Selected >= 0 && m_Selected < m_Rows.Count)
            {
                m_ScrollToSelected = false;
                float rowTop = m_RowTops[m_Selected];
                float rowBottom = rowTop + RowHeightOf(m_Rows[m_Selected]);
                if (rowTop < m_Scroll.y + ListPadding)
                    m_Scroll.y = rowTop - ListPadding;
                else if (rowBottom > m_Scroll.y + listRect.height - ListPadding)
                    m_Scroll.y = rowBottom - listRect.height + ListPadding;
                m_Scroll.y = Mathf.Clamp(m_Scroll.y, 0f, Mathf.Max(0f, m_ContentHeight - listRect.height));
            }

            m_Scroll = GUI.BeginScrollView(listRect, m_Scroll, viewRect, false, needScroll, GUIStyle.none, scrollbarStyle);

            if (m_Rows.Count == 0)
            {
                var emptyRect = new Rect(0f, ListPadding, viewWidth, RowHeight);
                GUI.Label(new Rect(emptyRect.x + LeftPadding, emptyRect.y, emptyRect.width - LeftPadding, emptyRect.height),
                    Temp(SearchMode ? "No matching items" : "(empty)"), styles.Disabled);
            }

            int hovered = -1;
            bool trackHover = e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown;

            for (int i = 0; i < m_Rows.Count; i++)
            {
                var node = m_Rows[i];
                var rowRect = new Rect(0f, m_RowTops[i], viewWidth, RowHeightOf(node));

                if (trackHover && rowRect.Contains(e.mousePosition))
                    hovered = i;

                if (e.type == EventType.Repaint)
                    DrawRow(rowRect, node, i == m_Selected, styles);

                if (e.type == EventType.MouseDown && e.button == 0 && rowRect.Contains(e.mousePosition) && !ClickGuardActive())
                {
                    if (IsSelectable(node))
                    {
                        m_Selected = i;
                        Activate(node, false);
                    }
                    e.Use();
                }
            }

            GUI.EndScrollView();

            if ((e.type == EventType.MouseMove || e.type == EventType.MouseDrag) && hovered != -1 && hovered != m_Selected)
            {
                m_Selected = hovered;
                Repaint();
            }
        }

        private void DrawRow(Rect rowRect, MenuNode node, bool selected, Styles styles)
        {
            if (node.IsSeparator)
            {
                EditorGUI.DrawRect(new Rect(rowRect.x + 8f, rowRect.y + Mathf.Floor(rowRect.height * 0.5f), rowRect.width - 16f, 1f), styles.Separator);
                return;
            }

            bool enabled = node.IsEnabled;
            bool highlight = selected && enabled;
            if (highlight)
                EditorGUI.DrawRect(new Rect(rowRect.x + 2f, rowRect.y, rowRect.width - 4f, rowRect.height), styles.Highlight);

            GUIStyle labelStyle = !enabled ? styles.Disabled : (highlight ? styles.ItemSelected : styles.Item);
            GUIStyle dimStyle = highlight ? styles.DimSelected : styles.Dim;

            if (node.IsChecked)
            {
                var checkRect = new Rect(rowRect.x + 6f, rowRect.y, 16f, rowRect.height);
                GUI.Label(checkRect, "✓", labelStyle);
            }

            float right = rowRect.xMax - RightPadding;
            if (node.IsSubmenu)
            {
                var arrowRect = new Rect(right - ArrowWidth, rowRect.y + (rowRect.height - ArrowWidth) * 0.5f, ArrowWidth, ArrowWidth);
                if (styles.ArrowRight.image != null)
                {
                    var previous = GUI.color;
                    GUI.color = enabled ? previous : new Color(previous.r, previous.g, previous.b, previous.a * 0.4f);
                    GUI.DrawTexture(arrowRect, styles.ArrowRight.image, ScaleMode.ScaleToFit);
                    GUI.color = previous;
                }
                else
                {
                    GUI.Label(arrowRect, "▸", labelStyle);
                }
                right = arrowRect.x - 6f;
            }
            else if (!SearchMode && CmsSettings.ShowShortcuts && node.Shortcut.Length > 0)
            {
                float w = styles.Shortcut.CalcSize(Temp(node.Shortcut)).x;
                var shortcutRect = new Rect(right - w, rowRect.y, w, rowRect.height);
                GUI.Label(shortcutRect, Temp(node.Shortcut), highlight ? styles.ShortcutSelected : styles.Shortcut);
                right = shortcutRect.x - 10f;
            }

            var labelRect = new Rect(rowRect.x + LeftPadding, rowRect.y, Mathf.Max(0f, right - rowRect.x - LeftPadding), rowRect.height);
            if (SearchMode && node.ParentPath.Length > 0)
            {
                float nameWidth = Mathf.Min(labelStyle.CalcSize(Temp(node.Name)).x, labelRect.width);
                GUI.Label(new Rect(labelRect.x, labelRect.y, nameWidth, labelRect.height), Temp(node.Name), labelStyle);
                float pathX = labelRect.x + nameWidth + 10f;
                if (right - pathX > 30f)
                    GUI.Label(new Rect(pathX, labelRect.y, right - pathX, labelRect.height), Temp(node.ParentPath), enabled ? dimStyle : styles.Disabled);
            }
            else
            {
                GUI.Label(labelRect, Temp(node.Name), labelStyle);
            }
        }

        private bool ClickGuardActive() => EditorApplication.timeSinceStartup - m_OpenTime < ClickGuardSeconds;

        // ------------------------------------------------------------------ input

        private void HandleKeyboard(Event e)
        {
            if (e.type != EventType.KeyDown)
                return;

            switch (e.keyCode)
            {
                case KeyCode.DownArrow:
                    MoveSelection(1);
                    e.Use();
                    return;
                case KeyCode.UpArrow:
                    MoveSelection(-1);
                    e.Use();
                    return;
                case KeyCode.PageDown:
                    MoveSelection(VisibleRowCount());
                    e.Use();
                    return;
                case KeyCode.PageUp:
                    MoveSelection(-VisibleRowCount());
                    e.Use();
                    return;
                case KeyCode.Home:
                    if (m_Search.Length == 0) { SelectEdge(true); e.Use(); }
                    return;
                case KeyCode.End:
                    if (m_Search.Length == 0) { SelectEdge(false); e.Use(); }
                    return;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    ActivateSelected();
                    e.Use();
                    return;
                case KeyCode.Escape:
                    e.Use();
                    Close();
                    GUIUtility.ExitGUI();
                    return;
                case KeyCode.RightArrow:
                    if (!SearchMode && m_Selected >= 0 && m_Selected < m_Rows.Count && m_Rows[m_Selected].IsSubmenu && m_Rows[m_Selected].IsEnabled)
                    {
                        Activate(m_Rows[m_Selected], true);
                        e.Use();
                    }
                    return;
                case KeyCode.LeftArrow:
                    if (!SearchMode && InSubmenu)
                    {
                        GoBack();
                        e.Use();
                    }
                    return;
                case KeyCode.Backspace:
                    if (m_Search.Length == 0 && InSubmenu)
                    {
                        GoBack();
                        e.Use();
                    }
                    return;
                case KeyCode.Tab:
                    e.Use();
                    return;
            }

            // Second "character" event Unity sends for Enter/Tab: keep it away from the text field.
            if (e.character == '\n' || e.character == '\r' || e.character == '\t')
                e.Use();
        }

        private int VisibleRowCount()
        {
            float listHeight = position.height - TopAreaHeight;
            return Mathf.Max(1, Mathf.FloorToInt(listHeight / RowHeight) - 1);
        }

        private void MoveSelection(int delta)
        {
            if (m_Rows.Count == 0) return;
            int step = delta > 0 ? 1 : -1;
            int remaining = Mathf.Abs(delta);
            int index = m_Selected;
            int last = index;
            while (remaining > 0)
            {
                index += step;
                if (index < 0 || index >= m_Rows.Count)
                    break;
                if (IsSelectable(m_Rows[index]))
                {
                    last = index;
                    remaining--;
                }
            }

            if (last < 0 || !IsSelectable(m_Rows[Mathf.Clamp(last, 0, m_Rows.Count - 1)]))
                last = delta > 0 ? FirstEnabledIndex() : LastEnabledIndex();

            if (last != m_Selected && last >= 0)
            {
                m_Selected = last;
                m_ScrollToSelected = true;
                Repaint();
            }
        }

        private int LastEnabledIndex()
        {
            for (int i = m_Rows.Count - 1; i >= 0; i--)
                if (IsSelectable(m_Rows[i]))
                    return i;
            return -1;
        }

        private void SelectEdge(bool first)
        {
            int index = first ? FirstEnabledIndex() : LastEnabledIndex();
            if (index >= 0)
            {
                m_Selected = index;
                m_ScrollToSelected = true;
                Repaint();
            }
        }

        private void ActivateSelected()
        {
            int index = m_Selected;
            if (index < 0 || index >= m_Rows.Count || !IsSelectable(m_Rows[index]))
                index = FirstEnabledIndex();
            if (index < 0)
                return;
            Activate(m_Rows[index], true);
        }

        private void Activate(MenuNode node, bool viaKeyboard)
        {
            if (!IsSelectable(node))
                return;

            if (node.IsSubmenu)
            {
                m_Current = node;
                m_Search = string.Empty;
                RefreshRows(false);
                if (viaKeyboard)
                {
                    m_Selected = FirstEnabledIndex();
                    m_ScrollToSelected = m_Selected >= 0;
                }
                m_SearchField.SetFocus();
                Repaint();
                return;
            }

            var callback = m_OnExecute;
            s_Current = null;
            Close();
            EditorApplication.delayCall += () =>
            {
                try
                {
                    callback?.Invoke(node);
                }
                catch (Exception e)
                {
                    CmsLog.Error("Executing \"" + node.Path + "\" failed: " + e);
                }
            };
            GUIUtility.ExitGUI();
        }

        private void GoBack()
        {
            if (!InSubmenu)
                return;
            var previous = m_Current;
            m_Current = m_Current.Parent ?? m_Root;
            m_Search = string.Empty;
            RefreshRows(false);
            m_Selected = m_Rows.IndexOf(previous);
            m_ScrollToSelected = m_Selected >= 0;
            m_SearchField.SetFocus();
            Repaint();
        }

        // ------------------------------------------------------------------ styles

        private sealed class Styles
        {
            private static Styles s_Instance;

            public static Styles Instance
            {
                get
                {
                    if (s_Instance == null || s_Instance.m_Version != CmsSettings.AppearanceVersion || s_Instance.m_Dark != EditorGUIUtility.isProSkin)
                        s_Instance = new Styles();
                    return s_Instance;
                }
            }

            private readonly int m_Version;
            private readonly bool m_Dark;

            public readonly GUIStyle Item;
            public readonly GUIStyle ItemSelected;
            public readonly GUIStyle Disabled;
            public readonly GUIStyle Dim;
            public readonly GUIStyle DimSelected;
            public readonly GUIStyle Shortcut;
            public readonly GUIStyle ShortcutSelected;
            public readonly GUIStyle Header;
            public readonly GUIStyle Placeholder;
            public readonly GUIContent ArrowRight;
            public readonly GUIContent ArrowLeft;
            public readonly Color Background;
            public readonly Color Border;
            public readonly Color Highlight;
            public readonly Color HeaderHover;
            public readonly Color Separator;

            private Styles()
            {
                m_Version = CmsSettings.AppearanceVersion;
                m_Dark = EditorGUIUtility.isProSkin;

                // User-facing colors come from Preferences; the secondary shades are mixed from them so a
                // custom palette stays consistent.
                Background = CmsSettings.BackgroundColor;
                Border = CmsSettings.BorderColor;
                Highlight = CmsSettings.HighlightColor;
                Separator = CmsSettings.SeparatorColor;
                Color text = CmsSettings.TextColor;
                Color selectedText = CmsSettings.HighlightTextColor;
                Color dim = Color.Lerp(text, Background, 0.35f);
                Color disabled = Color.Lerp(text, Background, 0.6f);
                HeaderHover = Color.Lerp(Background, text, 0.08f);

                // EditorStyles are unavailable very early and in -nographics batch mode (the getter throws).
                GUIStyle baseLabel = null;
                try { baseLabel = EditorStyles.label; } catch { baseLabel = null; }
                Item = baseLabel != null ? new GUIStyle(baseLabel) : new GUIStyle();
                Item.alignment = TextAnchor.MiddleLeft;
                Item.clipping = TextClipping.Clip;
                Item.wordWrap = false;
                Item.richText = false;
                Item.padding = new RectOffset(0, 0, 0, 0);
                Item.margin = new RectOffset(0, 0, 0, 0);
                Item.normal.textColor = text;
                Item.hover.textColor = text;
                Item.active.textColor = text;
                Item.focused.textColor = text;

                ItemSelected = new GUIStyle(Item);
                SetAllTextColors(ItemSelected, selectedText);

                Disabled = new GUIStyle(Item);
                SetAllTextColors(Disabled, disabled);

                Dim = new GUIStyle(Item) { fontSize = Item.fontSize > 0 ? Item.fontSize - 1 : 0 };
                SetAllTextColors(Dim, dim);

                DimSelected = new GUIStyle(Dim);
                SetAllTextColors(DimSelected, new Color(selectedText.r, selectedText.g, selectedText.b, selectedText.a * 0.75f));

                Shortcut = new GUIStyle(Item) { alignment = TextAnchor.MiddleRight };
                SetAllTextColors(Shortcut, dim);

                ShortcutSelected = new GUIStyle(Shortcut);
                SetAllTextColors(ShortcutSelected, new Color(selectedText.r, selectedText.g, selectedText.b, selectedText.a * 0.8f));

                Header = new GUIStyle(Item) { fontStyle = FontStyle.Bold };

                // SearchField.OnGUI draws a magnifier icon at the left; keep the placeholder right of it.
                Placeholder = new GUIStyle(Item) { padding = new RectOffset(18, 0, 0, 0) };
                SetAllTextColors(Placeholder, dim);

                ArrowRight = LoadIcon("ArrowNavigationRight");
                ArrowLeft = LoadIcon("ArrowNavigationLeft");
            }

            private static void SetAllTextColors(GUIStyle style, Color color)
            {
                style.normal.textColor = color;
                style.hover.textColor = color;
                style.active.textColor = color;
                style.focused.textColor = color;
            }

            private static GUIContent LoadIcon(string name)
            {
                try
                {
                    var content = EditorGUIUtility.IconContent(name);
                    return content ?? new GUIContent();
                }
                catch
                {
                    return new GUIContent();
                }
            }
        }
    }
}
