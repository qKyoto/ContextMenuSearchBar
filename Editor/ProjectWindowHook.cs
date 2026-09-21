using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ContextMenuSearchBar.Editor
{
    /// <summary>
    /// Watches every Project window and replaces the moments where Unity would show the native
    /// "Assets" context menu (or the "+" Create menu) with <see cref="SearchableMenuWindow"/>.
    ///
    /// Two interception points are used, mirroring how ProjectBrowser itself dispatches them:
    /// 1. A trickle-down UI Toolkit callback on the dock area's panel root sees ContextClick / MouseDown
    ///    before the IMGUI code does. It handles right-clicks in the asset list area (two-column mode)
    ///    and clicks on the toolbar "+" button, and stops the event so the native menu never opens.
    /// 2. The folder tree and the one-column asset tree report context clicks through delegates on
    ///    TreeViewController; those are wrapped so we know exactly which item was clicked.
    /// Anything unknown falls through to Unity's original behaviour.
    /// </summary>
    [InitializeOnLoad]
    internal static class ProjectWindowHook
    {
        private const double ScanInterval = 1.0;

        private static readonly List<WindowHook> s_Hooks = new List<WindowHook>();
        private static double s_NextScan;
        private static bool s_WarnedTreeUnavailable;

        public static bool IsSupported { get; }
        public static string UnsupportedReason { get; }

        static ProjectWindowHook()
        {
            if (ProjectBrowserReflection.ProjectBrowserType == null || ProjectBrowserReflection.MissingMembers != null)
            {
                IsSupported = false;
                UnsupportedReason = "missing " + (ProjectBrowserReflection.MissingMembers ?? "UnityEditor.ProjectBrowser");
                CmsLog.Warning("Disabled: " + UnsupportedReason + " (Unity " + Application.unityVersion + ").");
                return;
            }

            IsSupported = true;
            EditorApplication.update += Update;
            EditorApplication.delayCall += ScanWindows;
        }

        private static void Update()
        {
            if (EditorApplication.timeSinceStartup < s_NextScan)
                return;
            s_NextScan = EditorApplication.timeSinceStartup + ScanInterval;
            ScanWindows();
        }

        private static void ScanWindows()
        {
            for (int i = s_Hooks.Count - 1; i >= 0; i--)
            {
                if (s_Hooks[i].Window == null)
                {
                    s_Hooks[i].Dispose();
                    s_Hooks.RemoveAt(i);
                }
            }

            foreach (var window in ProjectBrowserReflection.FindProjectBrowsers())
            {
                bool known = false;
                foreach (var hook in s_Hooks)
                {
                    if (hook.Window == window)
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    CmsLog.Debug("Hooking Project window \"" + window.titleContent.text + "\"");
                    s_Hooks.Add(new WindowHook(window));
                }
            }

            foreach (var hook in s_Hooks)
                hook.Refresh();
        }

        // ------------------------------------------------------------------ menu display

        /// <summary>Builds and shows the searchable menu. Returns false when the native menu should be used instead.</summary>
        internal static bool ShowMenu(EditorWindow browser, string menuPath, Rect anchorScreenRect, bool fromFolderTree = false)
        {
            MenuNode tree;
            try
            {
                tree = MenuTreeBuilder.Build(menuPath);
            }
            catch (Exception e)
            {
                CmsLog.Error("Could not read the \"" + menuPath + "\" menu, showing the native menu instead: " + e);
                return false;
            }

            if (tree == null)
            {
                if (!s_WarnedTreeUnavailable)
                {
                    s_WarnedTreeUnavailable = true;
                    CmsLog.Warning("Could not read the \"" + menuPath + "\" menu on this Unity version; native menus are used.");
                }
                return false;
            }

            CmsLog.Debug("Showing \"" + menuPath + "\" at " + anchorScreenRect);
            SearchableMenuWindow.Open(tree, anchorScreenRect, node => Execute(browser, node, fromFolderTree));
            return true;
        }

        private static void Execute(EditorWindow browser, MenuNode node, bool fromFolderTree)
        {
            if (node == null || string.IsNullOrEmpty(node.Path))
                return;

            if (browser != null)
            {
                // Items such as Create/Folder or Rename start editing inside the last interacted Project window.
                ProjectBrowserReflection.SetAsLastInteracted(browser);
                browser.Focus();
                if (fromFolderTree)
                    ProjectBrowserReflection.MarkFolderTreeContextClicked(browser); // cleared by OnLostFocus when the popup opened
            }

            CmsLog.Debug("Executing " + node.Path);
            if (!EditorApplication.ExecuteMenuItem(node.Path))
                CmsLog.Debug("ExecuteMenuItem returned false for " + node.Path);
        }

        // ------------------------------------------------------------------ per window state

        private sealed class WindowHook
        {
            private const double ReopenGuardSeconds = 0.15;

            public EditorWindow Window { get; private set; }

            private VisualElement m_WindowRoot;
            private VisualElement m_PanelRoot;
            private double m_LastOpen;

            private readonly EventCallback<AttachToPanelEvent> m_OnAttach;
            private readonly EventCallback<DetachFromPanelEvent> m_OnDetach;
            private readonly EventCallback<ContextClickEvent> m_OnContextClick;
            private readonly EventCallback<MouseDownEvent> m_OnMouseDown;

            public WindowHook(EditorWindow window)
            {
                Window = window;
                m_OnAttach = OnAttachToPanel;
                m_OnDetach = OnDetachFromPanel;
                m_OnContextClick = OnContextClick;
                m_OnMouseDown = OnMouseDown;

                m_WindowRoot = window.rootVisualElement;
                m_WindowRoot.RegisterCallback(m_OnAttach);
                m_WindowRoot.RegisterCallback(m_OnDetach);
                Refresh();
            }

            public void Dispose()
            {
                RegisterOnPanel(null);
                if (m_WindowRoot != null)
                {
                    m_WindowRoot.UnregisterCallback(m_OnAttach);
                    m_WindowRoot.UnregisterCallback(m_OnDetach);
                    m_WindowRoot = null;
                }
            }

            /// <summary>Re-syncs with the panel the window currently lives in and the tree views it currently owns.</summary>
            public void Refresh()
            {
                if (Window == null)
                    return;
                try
                {
                    RegisterOnPanel(m_WindowRoot?.panel?.visualTree);
                    EnsureTreeHooks();
                }
                catch (Exception e)
                {
                    CmsLog.Debug("Refresh failed: " + e.Message);
                }
            }

            private void OnAttachToPanel(AttachToPanelEvent evt) => RegisterOnPanel(evt.destinationPanel?.visualTree);

            private void OnDetachFromPanel(DetachFromPanelEvent evt) => RegisterOnPanel(null);

            private void RegisterOnPanel(VisualElement panelRoot)
            {
                if (panelRoot == m_PanelRoot)
                    return;

                if (m_PanelRoot != null)
                {
                    m_PanelRoot.UnregisterCallback(m_OnContextClick, TrickleDown.TrickleDown);
                    m_PanelRoot.UnregisterCallback(m_OnMouseDown, TrickleDown.TrickleDown);
                }

                m_PanelRoot = panelRoot;

                if (m_PanelRoot != null)
                {
                    m_PanelRoot.RegisterCallback(m_OnContextClick, TrickleDown.TrickleDown);
                    m_PanelRoot.RegisterCallback(m_OnMouseDown, TrickleDown.TrickleDown);
                }
            }

            private bool IsActive => Window != null && m_WindowRoot != null && m_WindowRoot.panel != null && m_WindowRoot.panel.visualTree == m_PanelRoot;

            // Right-click in the asset list area of the two-column layout. Tree views are handled by the wrapped delegates.
            private void OnContextClick(ContextClickEvent evt)
            {
                if (!CmsSettings.Enabled || !CmsSettings.ReplaceContextMenu || !IsActive)
                    return;

                try
                {
                    EnsureTreeHooks();

                    if (!ProjectBrowserReflection.IsTwoColumns(Window))
                        return;

                    Vector2 local = evt.mousePosition - ProjectBrowserReflection.PanelToWindowOffset(Window);
                    Rect listRect = ProjectBrowserReflection.ListAreaRect(Window);
                    if (!listRect.Contains(local))
                        return;

                    // Unity shows no menu either when non-script assets are not selectable in safe mode.
                    if (ProjectBrowserReflection.IsInSafeMode() && Selection.count == 0)
                        return;

                    ProjectBrowserReflection.SetAsLastInteracted(Window);
                    Vector2 screen = Window.position.position + local;
                    if (!ShowMenu(Window, "Assets", new Rect(screen.x, screen.y, 0f, 0f)))
                        return;

                    m_LastOpen = EditorApplication.timeSinceStartup;
                    Consume(evt);
                }
                catch (Exception e)
                {
                    CmsLog.Error("Context click handling failed, native menu is used: " + e);
                }
            }

            // Left click on the toolbar "+" button.
            private void OnMouseDown(MouseDownEvent evt)
            {
                if (evt.button != 0 || !CmsSettings.Enabled || !CmsSettings.ReplaceCreateButton || !IsActive)
                    return;

                try
                {
                    if (evt.imguiEvent != null && evt.imguiEvent.type == EventType.Used)
                        return;
                    if (EditorApplication.timeSinceStartup - m_LastOpen < ReopenGuardSeconds)
                        return;

                    Vector2 local = evt.mousePosition - ProjectBrowserReflection.PanelToWindowOffset(Window);
                    Rect button = CreateButtonRect();
                    var hitRect = new Rect(button.x - 1f, button.y - 1f, button.width + 2f, button.height + 2f);
                    if (!hitRect.Contains(local))
                        return;

                    if (ProjectBrowserReflection.IsCreateDisabled())
                        return;

                    ProjectBrowserReflection.SetAsLastInteracted(Window);
                    var screenRect = new Rect(Window.position.position + button.position, button.size);
                    if (!ShowMenu(Window, "Assets/Create", screenRect))
                        return;

                    m_LastOpen = EditorApplication.timeSinceStartup;
                    Consume(evt);
                }
                catch (Exception e)
                {
                    CmsLog.Error("Create button handling failed, native menu is used: " + e);
                }
            }

            private static void Consume(EventBase evt)
            {
                evt.imguiEvent?.Use();
                evt.StopPropagation();
#if !UNITY_2023_2_OR_NEWER
                evt.PreventDefault();
#endif
            }

            // Same layout math as ProjectBrowser.CreateDropdown(): first control of a GUILayout toolbar row.
            private static Rect CreateButtonRect()
            {
                GUIStyle toolbar, button;
                try
                {
                    toolbar = EditorStyles.toolbar;
                    button = EditorStyles.toolbarButton;
                }
                catch
                {
                    toolbar = button = null; // EditorStyles throw before the skin is loaded
                }
                if (toolbar == null || button == null)
                    return new Rect(0f, 0f, 32f, 21f);
                GUIContent content;
                try { content = EditorGUIUtility.IconContent("Toolbar Plus More"); }
                catch { content = null; }

                Vector2 size = content != null && content.image != null ? button.CalcSize(content) : new Vector2(32f, 20f);
                float x = toolbar.margin.left + toolbar.padding.left + button.margin.left;
                float y = toolbar.margin.top + toolbar.padding.top + button.margin.top;
                float height = Mathf.Max(size.y, toolbar.fixedHeight > 0f ? toolbar.fixedHeight - toolbar.padding.vertical : 0f);
                return new Rect(x, y, size.x, height);
            }

            // ------------------------------------------------------------------ tree view delegates

            private void EnsureTreeHooks()
            {
                HookTree(ProjectBrowserReflection.FolderTree(Window), true);
                HookTree(ProjectBrowserReflection.AssetTree(Window), false);
            }

            private void HookTree(object tree, bool isFolderTree)
            {
                if (tree == null)
                    return;

                var itemMember = ProjectBrowserReflection.ContextClickItemMember(tree);
                if (itemMember != null && !IsWrapped(itemMember.Get(tree)))
                {
                    Type idType = ProjectBrowserReflection.IdType(tree);
                    Type wrapperType = typeof(ItemClickWrapper<>).MakeGenericType(idType);
                    object wrapper = Activator.CreateInstance(wrapperType);
                    wrapperType.GetField("Original").SetValue(wrapper, itemMember.Get(tree));
                    wrapperType.GetField("Handler").SetValue(wrapper,
                        isFolderTree ? (Func<object, bool>)OnFolderTreeItemContextClick : OnAssetTreeItemContextClick);
                    var invoke = wrapperType.GetMethod("Invoke");
                    itemMember.Set(tree, Delegate.CreateDelegate(itemMember.DelegateType, wrapper, invoke));
                    CmsLog.Debug("Wrapped contextClickItemCallback of " + (isFolderTree ? "folder" : "asset") + " tree (" + idType.Name + ")");
                }

                if (isFolderTree)
                    return; // Unity has no "outside items" menu for the folder tree

                var outsideMember = ProjectBrowserReflection.ContextClickOutsideMember(tree);
                if (outsideMember != null && outsideMember.DelegateType == typeof(Action) && !IsWrapped(outsideMember.Get(tree)))
                {
                    var wrapper = new OutsideClickWrapper
                    {
                        Original = outsideMember.Get(tree) as Action,
                        Handler = OnAssetTreeOutsideContextClick,
                    };
                    outsideMember.Set(tree, (Action)wrapper.Invoke);
                }
            }

            private static bool IsWrapped(Delegate current)
            {
                if (current == null)
                    return false;
                foreach (var d in current.GetInvocationList())
                {
                    var target = d.Target;
                    if (target is OutsideClickWrapper)
                        return true;
                    if (target != null && target.GetType().IsGenericType && target.GetType().GetGenericTypeDefinition() == typeof(ItemClickWrapper<>))
                        return true;
                }
                return false;
            }

            private bool OnFolderTreeItemContextClick(object id)
            {
                if (!CmsSettings.Enabled || !CmsSettings.ReplaceContextMenu || Window == null)
                    return false;
                if (ProjectBrowserReflection.IsSavedFilterItem(id))
                    return false; // saved searches have their own small menu

                ProjectBrowserReflection.MarkFolderTreeContextClicked(Window);
                return ShowContextMenuFromIMGUI(true);
            }

            private bool OnAssetTreeItemContextClick(object id)
            {
                if (!CmsSettings.Enabled || !CmsSettings.ReplaceContextMenu || Window == null)
                    return false;
                if (IsNoneId(id))
                    return false; // non-selectable asset: Unity just clears the selection

                return ShowContextMenuFromIMGUI();
            }

            private bool OnAssetTreeOutsideContextClick()
            {
                if (!CmsSettings.Enabled || !CmsSettings.ReplaceContextMenu || Window == null)
                    return false;

                var tree = ProjectBrowserReflection.AssetTree(Window);
                if (tree != null)
                    ProjectBrowserReflection.ClearAssetTreeSelection(Window, tree);
                return ShowContextMenuFromIMGUI();
            }

            // Called from inside ProjectBrowser.OnGUI, so IMGUI helpers are available.
            private bool ShowContextMenuFromIMGUI(bool fromFolderTree = false)
            {
                var e = Event.current;
                Vector2 screen = e != null ? GUIUtility.GUIToScreenPoint(e.mousePosition) : Window.position.position;

                ProjectBrowserReflection.SetAsLastInteracted(Window);
                if (!ShowMenu(Window, "Assets", new Rect(screen.x, screen.y, 0f, 0f), fromFolderTree))
                    return false;

                m_LastOpen = EditorApplication.timeSinceStartup;
                e?.Use();
                return true;
            }

            private static bool IsNoneId(object id)
            {
                if (id == null)
                    return true;
                var type = id.GetType();
                if (!type.IsValueType)
                    return false;

                // EntityId.None is a static property, older int ids use 0 (== default).
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                object none;
                var noneProperty = type.GetProperty("None", flags);
                var noneField = type.GetField("None", flags);
                if (noneProperty != null)
                    none = noneProperty.GetValue(null);
                else if (noneField != null)
                    none = noneField.GetValue(null);
                else
                    none = Activator.CreateInstance(type);
                return id.Equals(none) || id.Equals(Activator.CreateInstance(type));
            }
        }

        /// <summary>Replacement for TreeViewController.contextClickItemCallback; falls back to the original delegate.</summary>
        internal sealed class ItemClickWrapper<T>
        {
            public Delegate Original;
            public Func<object, bool> Handler;

            public void Invoke(T id)
            {
                bool handled = false;
                try
                {
                    handled = Handler != null && Handler(id);
                }
                catch (Exception e)
                {
                    CmsLog.Error("Tree context click handling failed, native menu is used: " + e);
                }

                if (!handled && Original is Action<T> original)
                    original(id);
            }
        }

        internal sealed class OutsideClickWrapper
        {
            public Action Original;
            public Func<bool> Handler;

            public void Invoke()
            {
                bool handled = false;
                try
                {
                    handled = Handler != null && Handler();
                }
                catch (Exception e)
                {
                    CmsLog.Error("Tree context click handling failed, native menu is used: " + e);
                }

                if (!handled)
                    Original?.Invoke();
            }
        }
    }
}
