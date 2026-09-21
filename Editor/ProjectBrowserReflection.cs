using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ContextMenuSearchBar.Editor
{
    /// <summary>
    /// Cached reflection access to UnityEditor.ProjectBrowser internals. Every member is optional;
    /// callers must handle nulls so a Unity version that renamed something degrades to the native menu.
    /// </summary>
    internal static class ProjectBrowserReflection
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static readonly Type ProjectBrowserType;
        public static readonly Type HostViewType;

        private static readonly FieldInfo s_ListAreaRect;
        private static readonly FieldInfo s_TreeViewRect;
        private static readonly FieldInfo s_ViewMode;
        private static readonly FieldInfo s_FolderTree;
        private static readonly FieldInfo s_AssetTree;
        private static readonly FieldInfo s_FolderTreeContextClicked;
        private static readonly FieldInfo s_LastInteracted;
        private static readonly FieldInfo s_ParentHostView;             // EditorWindow.m_Parent
        private static readonly PropertyInfo s_HostViewScreenPosition;  // GUIView.screenPosition
        private static readonly PropertyInfo s_HostViewBorderSize;      // HostView.borderSize
        private static readonly MethodInfo s_IsTwoColumns;
        private static readonly MethodInfo s_SetAsLastInteracted;
        private static readonly MethodInfo s_GetItemType;               // static ItemType GetItemType(id)
        private static readonly MethodInfo s_GetAllProjectBrowsers;
        private static readonly MethodInfo s_SelectionIsPackagesRoot;
        private static readonly MethodInfo s_SelectionHasImmutable;     // AssetsMenuUtility.SelectionHasImmutable()
        private static readonly MethodInfo s_AssetTreeSelectionCallback;

        public static string MissingMembers { get; }

        static ProjectBrowserReflection()
        {
            var editorAssembly = typeof(EditorWindow).Assembly;
            ProjectBrowserType = editorAssembly.GetType("UnityEditor.ProjectBrowser");
            HostViewType = editorAssembly.GetType("UnityEditor.HostView");
            var missing = new List<string>();

            if (ProjectBrowserType == null)
            {
                MissingMembers = "UnityEditor.ProjectBrowser";
                return;
            }

            s_ListAreaRect = Field("m_ListAreaRect", missing);
            s_TreeViewRect = Field("m_TreeViewRect", missing);
            s_ViewMode = Field("m_ViewMode", null);
            s_FolderTree = Field("m_FolderTree", missing);
            s_AssetTree = Field("m_AssetTree", missing);
            s_FolderTreeContextClicked = Field("isFolderTreeViewContextClicked", null);
            s_LastInteracted = Field("s_LastInteractedProjectBrowser", null);
            s_IsTwoColumns = Method("IsTwoColumns", null);
            s_SetAsLastInteracted = Method("SetAsLastInteractedProjectBrowser", null);
            s_GetItemType = Method("GetItemType", null);
            s_GetAllProjectBrowsers = Method("GetAllProjectBrowsers", null);
            s_SelectionIsPackagesRoot = Method("SelectionIsPackagesRootFolder", null);
            s_AssetTreeSelectionCallback = Method("AssetTreeSelectionCallback", null);

            if (s_ViewMode == null && s_IsTwoColumns == null)
                missing.Add("m_ViewMode/IsTwoColumns");

            s_ParentHostView = typeof(EditorWindow).GetField("m_Parent", Any);
            if (s_ParentHostView == null)
                missing.Add("EditorWindow.m_Parent");
            if (HostViewType != null)
            {
                s_HostViewScreenPosition = HostViewType.GetProperty("screenPosition", Any);
                s_HostViewBorderSize = HostViewType.GetProperty("borderSize", Any);
            }

            var assetsMenuUtility = editorAssembly.GetType("UnityEditor.AssetsMenuUtility");
            s_SelectionHasImmutable = assetsMenuUtility?.GetMethod("SelectionHasImmutable", Any, null, Type.EmptyTypes, null);

            MissingMembers = missing.Count == 0 ? null : string.Join(", ", missing);
        }

        private static FieldInfo Field(string name, List<string> missing)
        {
            var field = ProjectBrowserType.GetField(name, Any);
            if (field == null && missing != null)
                missing.Add(name);
            return field;
        }

        private static MethodInfo Method(string name, List<string> missing)
        {
            MethodInfo method = null;
            try
            {
                method = ProjectBrowserType.GetMethod(name, Any);
            }
            catch (AmbiguousMatchException)
            {
                foreach (var candidate in ProjectBrowserType.GetMethods(Any))
                {
                    if (candidate.Name == name)
                    {
                        method = candidate;
                        break;
                    }
                }
            }
            if (method == null && missing != null)
                missing.Add(name);
            return method;
        }

        public static bool IsProjectBrowser(EditorWindow window) => window != null && ProjectBrowserType != null && ProjectBrowserType.IsInstanceOfType(window);

        public static IEnumerable<EditorWindow> FindProjectBrowsers()
        {
            if (ProjectBrowserType == null)
                yield break;

            IEnumerable list = null;
            if (s_GetAllProjectBrowsers != null)
            {
                try { list = s_GetAllProjectBrowsers.Invoke(null, null) as IEnumerable; }
                catch { list = null; }
            }
            if (list == null)
                list = Resources.FindObjectsOfTypeAll(ProjectBrowserType);

            foreach (var item in list)
                if (item is EditorWindow window && window != null)
                    yield return window;
        }

        public static Rect ListAreaRect(EditorWindow browser) => s_ListAreaRect != null ? (Rect)s_ListAreaRect.GetValue(browser) : Rect.zero;

        public static Rect TreeViewRect(EditorWindow browser) => s_TreeViewRect != null ? (Rect)s_TreeViewRect.GetValue(browser) : Rect.zero;

        public static bool IsTwoColumns(EditorWindow browser)
        {
            if (s_IsTwoColumns != null)
                return (bool)s_IsTwoColumns.Invoke(browser, null);
            if (s_ViewMode != null)
                return s_ViewMode.GetValue(browser)?.ToString() == "TwoColumns";
            return false;
        }

        public static object FolderTree(EditorWindow browser) => s_FolderTree?.GetValue(browser);

        public static object AssetTree(EditorWindow browser) => s_AssetTree?.GetValue(browser);

        public static void MarkFolderTreeContextClicked(EditorWindow browser)
        {
            try { s_FolderTreeContextClicked?.SetValue(browser, true); }
            catch (Exception e) { CmsLog.Debug("isFolderTreeViewContextClicked: " + e.Message); }
        }

        public static void SetAsLastInteracted(EditorWindow browser)
        {
            try
            {
                if (s_SetAsLastInteracted != null)
                    s_SetAsLastInteracted.Invoke(browser, null);
                else
                    s_LastInteracted?.SetValue(null, browser);
            }
            catch (Exception e)
            {
                CmsLog.Debug("SetAsLastInteractedProjectBrowser: " + e.Message);
            }
        }

        /// <summary>True when the tree item is a saved search filter (those have their own context menu).</summary>
        public static bool IsSavedFilterItem(object itemId)
        {
            if (s_GetItemType == null || itemId == null)
                return false;
            try
            {
                return s_GetItemType.Invoke(null, new[] { itemId })?.ToString() == "SavedFilter";
            }
            catch (Exception e)
            {
                CmsLog.Debug("GetItemType: " + e.Message);
                return false;
            }
        }

        public static void ClearAssetTreeSelection(EditorWindow browser, object tree)
        {
            try
            {
                var setSelection = tree.GetType().GetMethod("SetSelection", Any, null,
                    new[] { IdArrayType(tree), typeof(bool) }, null);
                var empty = Array.CreateInstance(IdType(tree), 0);
                setSelection?.Invoke(tree, new object[] { empty, false });
                s_AssetTreeSelectionCallback?.Invoke(browser, new object[] { empty });
            }
            catch (Exception e)
            {
                CmsLog.Debug("ClearAssetTreeSelection: " + e.Message);
            }
        }

        private static PropertyInfo s_IsInSafeMode;
        private static bool s_IsInSafeModeResolved;

        public static bool IsInSafeMode()
        {
            if (!s_IsInSafeModeResolved)
            {
                s_IsInSafeModeResolved = true;
                s_IsInSafeMode = typeof(EditorUtility).GetProperty("isInSafeMode", Any);
            }
            try
            {
                return s_IsInSafeMode != null && (bool)s_IsInSafeMode.GetValue(null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Whether the toolbar "+" button would be disabled (read-only selection such as package folders).</summary>
        public static bool IsCreateDisabled()
        {
            try
            {
                if (s_SelectionHasImmutable != null && (bool)s_SelectionHasImmutable.Invoke(null, null))
                    return true;
                if (s_SelectionIsPackagesRoot != null && (bool)s_SelectionIsPackagesRoot.Invoke(null, null))
                    return true;
            }
            catch (Exception e)
            {
                CmsLog.Debug("IsCreateDisabled: " + e.Message);
            }
            return false;
        }

        /// <summary>
        /// Offset between the UI Toolkit panel of the dock area (tab bar included) and the window's own IMGUI space.
        /// </summary>
        public static Vector2 PanelToWindowOffset(EditorWindow window)
        {
            try
            {
                // HostView.InvokeOnGUI begins the window's IMGUI area at rootVisualElement.worldBound.
                var root = window.rootVisualElement;
                if (root != null && root.panel != null)
                {
                    var bound = root.worldBound;
                    if (!float.IsNaN(bound.x) && !float.IsNaN(bound.y))
                        return bound.position;
                }

                var hostView = s_ParentHostView?.GetValue(window);
                if (hostView != null)
                {
                    if (s_HostViewScreenPosition != null)
                    {
                        var screen = (Rect)s_HostViewScreenPosition.GetValue(hostView);
                        return window.position.position - screen.position;
                    }
                    if (s_HostViewBorderSize?.GetValue(hostView) is RectOffset border)
                        return new Vector2(border.left, border.top);
                }
            }
            catch (Exception e)
            {
                CmsLog.Debug("PanelToWindowOffset: " + e.Message);
            }
            return Vector2.zero;
        }

        // --- TreeViewController helpers (generic TreeViewController<T> in newer versions, non-generic before) ---

        public static Type IdType(object tree)
        {
            var type = tree.GetType();
            while (type != null)
            {
                if (type.IsGenericType)
                    return type.GetGenericArguments()[0];
                type = type.BaseType;
            }
            return typeof(int);
        }

        private static Type IdArrayType(object tree) => IdType(tree).MakeArrayType();

        public static DelegateMember ContextClickItemMember(object tree) => DelegateMember.Find(tree, "contextClickItemCallback");

        public static DelegateMember ContextClickOutsideMember(object tree) => DelegateMember.Find(tree, "contextClickOutsideItemsCallback");

        /// <summary>A delegate-typed property or field of a TreeViewController (it changed between Unity versions).</summary>
        public sealed class DelegateMember
        {
            private readonly PropertyInfo m_Property;
            private readonly FieldInfo m_Field;

            private DelegateMember(PropertyInfo property, FieldInfo field)
            {
                m_Property = property;
                m_Field = field;
            }

            public static DelegateMember Find(object owner, string name)
            {
                var type = owner.GetType();
                var property = type.GetProperty(name, Any);
                if (property != null && property.CanRead && property.CanWrite && typeof(Delegate).IsAssignableFrom(property.PropertyType))
                    return new DelegateMember(property, null);
                var field = type.GetField(name, Any);
                if (field != null && typeof(Delegate).IsAssignableFrom(field.FieldType))
                    return new DelegateMember(null, field);
                return null;
            }

            public Type DelegateType => m_Property != null ? m_Property.PropertyType : m_Field.FieldType;

            public Delegate Get(object owner) => (m_Property != null ? m_Property.GetValue(owner) : m_Field.GetValue(owner)) as Delegate;

            public void Set(object owner, Delegate value)
            {
                if (m_Property != null)
                    m_Property.SetValue(owner, value);
                else
                    m_Field.SetValue(owner, value);
            }
        }
    }
}
