using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ContextMenuSearchBar.Editor
{
    /// <summary>
    /// Builds a <see cref="MenuNode"/> tree for a Unity menu (e.g. "Assets") using the same internal data
    /// the native menus are built from, so order, submenus, separators, enabled state and shortcuts match.
    /// </summary>
    internal static class MenuTreeBuilder
    {
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Unity inserts a separator between two neighbours when their priorities differ by more than this.
        private const int SeparatorPriorityGap = 10;

        private static bool s_Resolved;
        private static MethodInfo s_GetMenuItems;      // ScriptingMenuItem[] GetMenuItems(string menuPath, bool includeSeparators, bool localized)
        private static MethodInfo s_GetEnabled;        // bool GetEnabled(string)
        private static MethodInfo s_GetChecked;        // bool GetChecked(string)
        private static MethodInfo s_GetHotkey;         // string GetHotkey(string)
        private static MethodInfo s_UpdateAllMenus;    // EditorUtility.Internal_UpdateAllMenus()
        private static MethodInfo s_UpdateContextMenu; // Menu.UpdateContextMenu(Object[] context, int userData)
        private static PropertyInfo s_ItemPath, s_ItemPriority, s_ItemIsSeparator;

        private static void Resolve()
        {
            if (s_Resolved) return;
            s_Resolved = true;

            var editorAssembly = typeof(EditorUtility).Assembly;
            var menuType = editorAssembly.GetType("UnityEditor.Menu");
            if (menuType == null) return;

            s_GetMenuItems = menuType.GetMethod("GetMenuItems", AnyStatic, null, new[] { typeof(string), typeof(bool), typeof(bool) }, null);
            s_GetEnabled = menuType.GetMethod("GetEnabled", AnyStatic, null, new[] { typeof(string) }, null);
            s_GetChecked = menuType.GetMethod("GetChecked", AnyStatic, null, new[] { typeof(string) }, null);
            s_GetHotkey = menuType.GetMethod("GetHotkey", AnyStatic, null, new[] { typeof(string) }, null);
            s_UpdateContextMenu = menuType.GetMethod("UpdateContextMenu", AnyStatic, null, new[] { typeof(UnityEngine.Object[]), typeof(int) }, null);
            s_UpdateAllMenus = typeof(EditorUtility).GetMethod("Internal_UpdateAllMenus", AnyStatic, null, Type.EmptyTypes, null);

            var itemType = editorAssembly.GetType("UnityEditor.ScriptingMenuItem");
            if (itemType != null)
            {
                const BindingFlags anyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                s_ItemPath = itemType.GetProperty("path", anyInstance);
                s_ItemPriority = itemType.GetProperty("priority", anyInstance);
                s_ItemIsSeparator = itemType.GetProperty("isSeparator", anyInstance);
            }
        }

        /// <summary>
        /// Builds the tree below <paramref name="rootPath"/> ("Assets" or "Assets/Create").
        /// Returns null when the menu cannot be read on this Unity version.
        /// </summary>
        public static MenuNode Build(string rootPath)
        {
            Resolve();

            rootPath = rootPath.TrimEnd('/');
            var root = new MenuNode { Path = rootPath, Name = LastSegment(rootPath), Children = new List<MenuNode>() };

            // Runs every menu validation method so enabled/checked states reflect the current selection,
            // exactly like Unity does right before it shows a native context menu.
            RefreshMenuStates();

            bool built = false;
            if (s_GetMenuItems != null && s_ItemPath != null && s_ItemPriority != null && s_ItemIsSeparator != null)
            {
                try
                {
                    built = BuildFromMenuItems(root);
                }
                catch (Exception e)
                {
                    CmsLog.Warning("Menu.GetMenuItems failed, falling back to Unsupported.GetSubmenus: " + e.Message);
                }
            }

            if (!built)
                built = BuildFromSubmenus(root);

            if (!built)
                return null;

            InsertPrioritySeparators(root);
            CleanupSeparators(root);
            FillItemStates(root, string.Empty);
            return root;
        }

        private static void RefreshMenuStates()
        {
            try
            {
                if (s_UpdateAllMenus != null)
                    s_UpdateAllMenus.Invoke(null, null);
                else
                    s_UpdateContextMenu?.Invoke(null, new object[] { Array.Empty<UnityEngine.Object>(), 0 });
            }
            catch (Exception e)
            {
                CmsLog.Debug("Refreshing menu states failed: " + e.Message);
            }
        }

        // Uses Menu.GetMenuItems which returns the whole subtree depth-first in display order.
        // Entries flagged as "separator" are either submenu roots (path below the current menu)
        // or real separator lines (path equal to the menu that contains them).
        private static bool BuildFromMenuItems(MenuNode root)
        {
            var items = s_GetMenuItems.Invoke(null, new object[] { root.Path + "/", true, false }) as Array;
            if (items == null)
                return false;

            Array localizedItems = null;
            try
            {
                localizedItems = s_GetMenuItems.Invoke(null, new object[] { root.Path + "/", true, true }) as Array;
                if (localizedItems != null && localizedItems.Length != items.Length)
                    localizedItems = null;
            }
            catch
            {
                localizedItems = null;
            }

            var stack = new List<MenuNode> { root };
            for (int i = 0; i < items.Length; i++)
            {
                object item = items.GetValue(i);
                string path = (string)s_ItemPath.GetValue(item);
                if (string.IsNullOrEmpty(path))
                    continue;
                path = path.TrimEnd('/');
                int priority = (int)s_ItemPriority.GetValue(item);
                bool isSeparator = (bool)s_ItemIsSeparator.GetValue(item);

                // Unwind to the deepest menu that contains this path.
                while (stack.Count > 1 && !(path == Top(stack).Path || path.StartsWith(Top(stack).Path + "/", StringComparison.Ordinal)))
                    stack.RemoveAt(stack.Count - 1);

                var parent = Top(stack);
                if (isSeparator && path == parent.Path)
                {
                    parent.Children.Add(MenuNode.Separator());
                    continue;
                }

                if (!path.StartsWith(root.Path + "/", StringComparison.Ordinal))
                    continue; // not part of this menu at all

                string localizedPath = null;
                if (localizedItems != null)
                    localizedPath = s_ItemPath.GetValue(localizedItems.GetValue(i)) as string;

                var node = new MenuNode
                {
                    Path = path,
                    Priority = priority,
                    Parent = parent,
                    Name = DisplayName(path, localizedPath),
                };

                if (isSeparator)
                {
                    node.Children = new List<MenuNode>();
                    parent.Children.Add(node);
                    stack.Add(node);
                }
                else
                {
                    parent.Children.Add(node);
                }
            }

            RemoveEmptySubmenus(root);
            return true;
        }

        // Fallback for Unity versions without Menu.GetMenuItems: only leaf paths are available, no priorities.
        private static bool BuildFromSubmenus(MenuNode root)
        {
            string[] paths;
            try
            {
                paths = Unsupported.GetSubmenus(root.Path);
            }
            catch (Exception e)
            {
                CmsLog.Warning("Unsupported.GetSubmenus failed: " + e.Message);
                return false;
            }
            if (paths == null)
                return false;

            foreach (var rawPath in paths)
            {
                if (string.IsNullOrEmpty(rawPath) || !rawPath.StartsWith(root.Path + "/", StringComparison.Ordinal))
                    continue;

                string relative = rawPath.Substring(root.Path.Length + 1);
                string[] segments = relative.Split('/');
                var parent = root;
                string currentPath = root.Path;
                for (int s = 0; s < segments.Length; s++)
                {
                    currentPath += "/" + segments[s];
                    bool isLeaf = s == segments.Length - 1;
                    MenuNode existing = null;
                    if (!isLeaf)
                        existing = parent.Children.Find(c => c.IsSubmenu && c.Path == currentPath);

                    if (existing == null)
                    {
                        existing = new MenuNode
                        {
                            Path = currentPath,
                            Name = segments[s],
                            Parent = parent,
                            Children = isLeaf ? null : new List<MenuNode>(),
                        };
                        parent.Children.Add(existing);
                    }
                    parent = existing;
                }
            }
            return true;
        }

        private static void RemoveEmptySubmenus(MenuNode node)
        {
            if (node.Children == null) return;
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                if (!child.IsSubmenu) continue;
                RemoveEmptySubmenus(child);
                if (child.Children.Count == 0)
                    node.Children.RemoveAt(i);
            }
        }

        private static void InsertPrioritySeparators(MenuNode node)
        {
            if (node.Children == null) return;

            for (int i = node.Children.Count - 1; i >= 1; i--)
            {
                var current = node.Children[i];
                var previous = node.Children[i - 1];
                if (current.IsSeparator || previous.IsSeparator)
                    continue;
                if (current.Priority - previous.Priority > SeparatorPriorityGap)
                    node.Children.Insert(i, MenuNode.Separator());
            }

            foreach (var child in node.Children)
                if (child.IsSubmenu)
                    InsertPrioritySeparators(child);
        }

        // Drops leading, trailing and doubled separators (empty submenus were removed already).
        private static void CleanupSeparators(MenuNode node)
        {
            if (node.Children == null) return;

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                if (!child.IsSeparator) continue;
                bool first = i == 0;
                bool last = i == node.Children.Count - 1;
                bool doubled = !first && node.Children[i - 1].IsSeparator;
                if (first || last || doubled)
                    node.Children.RemoveAt(i);
            }

            foreach (var child in node.Children)
                if (child.IsSubmenu)
                    CleanupSeparators(child);
        }

        private static void FillItemStates(MenuNode node, string parentPath)
        {
            if (node.Children == null) return;

            foreach (var child in node.Children)
            {
                if (child.IsSeparator) continue;

                child.ParentPath = parentPath;
                child.IsEnabled = Query(s_GetEnabled, child.Path, true);
                child.IsChecked = child.IsSubmenu ? false : Query(s_GetChecked, child.Path, false);
                if (!child.IsSubmenu && s_GetHotkey != null)
                {
                    try
                    {
                        child.Shortcut = HotkeyFormatter.Format(s_GetHotkey.Invoke(null, new object[] { child.Path }) as string);
                    }
                    catch
                    {
                        child.Shortcut = string.Empty;
                    }
                }

                if (child.IsSubmenu)
                    FillItemStates(child, string.IsNullOrEmpty(parentPath) ? child.Name : parentPath + " › " + child.Name);
            }
        }

        private static bool Query(MethodInfo method, string path, bool fallback)
        {
            if (method == null) return fallback;
            try
            {
                return (bool)method.Invoke(null, new object[] { path });
            }
            catch
            {
                return fallback;
            }
        }

        private static MenuNode Top(List<MenuNode> stack) => stack[stack.Count - 1];

        private static string LastSegment(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        private static string DisplayName(string path, string localizedPath)
        {
            string name = LastSegment(path);
            if (!string.IsNullOrEmpty(localizedPath))
            {
                string localized = LastSegment(localizedPath.TrimEnd('/'));
                if (!string.IsNullOrEmpty(localized))
                    name = localized;
            }
            return name;
        }
    }
}
