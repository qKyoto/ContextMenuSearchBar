using System.Collections.Generic;

namespace ContextMenuSearchBar.Editor
{
    /// <summary>
    /// One entry of a menu tree: a leaf item, a submenu (has <see cref="Children"/>) or a separator line.
    /// Paths are raw (non-localized) Unity menu paths such as "Assets/Create/Folder".
    /// </summary>
    internal sealed class MenuNode
    {
        /// <summary>Full raw menu path. Null for separators.</summary>
        public string Path;

        /// <summary>Display name (last path segment, localized when available).</summary>
        public string Name = string.Empty;

        /// <summary>Parent chain relative to the tree root, e.g. "Create › Scripting". Empty for top-level items.</summary>
        public string ParentPath = string.Empty;

        public int Priority;
        public bool IsSeparator;
        public bool IsEnabled = true;
        public bool IsChecked;

        /// <summary>Human readable shortcut ("Ctrl+R"), empty when none.</summary>
        public string Shortcut = string.Empty;

        public MenuNode Parent;
        public List<MenuNode> Children;

        public bool IsSubmenu => Children != null;

        public static MenuNode Separator() => new MenuNode { IsSeparator = true };

        /// <summary>Depth-first enumeration of all leaf (executable) items below this node.</summary>
        public IEnumerable<MenuNode> Leaves()
        {
            if (Children == null)
                yield break;

            foreach (var child in Children)
            {
                if (child.IsSeparator)
                    continue;
                if (child.IsSubmenu)
                {
                    foreach (var leaf in child.Leaves())
                        yield return leaf;
                }
                else
                {
                    yield return child;
                }
            }
        }

        public override string ToString() => IsSeparator ? "---" : (Path ?? Name);
    }
}
