using System;
using System.Collections.Generic;

namespace ContextMenuSearchBar.Editor
{
    /// <summary>
    /// Filters the leaf items of a menu tree by a free-text query.
    /// Every whitespace separated word must occur (case-insensitive) in the item's name or in its parent path,
    /// so "scr obj" finds "Create › Scripting › ScriptableObject Script".
    /// </summary>
    internal static class MenuSearch
    {
        public static List<MenuNode> Filter(MenuNode root, string query)
        {
            var results = new List<MenuNode>();
            if (root == null)
                return results;

            string[] words = (query ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return results;

            var scored = new List<KeyValuePair<int, MenuNode>>();
            int index = 0;
            foreach (var leaf in root.Leaves())
            {
                int score = Score(leaf, words);
                if (score >= 0)
                    scored.Add(new KeyValuePair<int, MenuNode>(score * 100000 + index, leaf));
                index++;
            }

            scored.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var pair in scored)
                results.Add(pair.Value);
            return results;
        }

        // Lower is better, negative means "no match".
        private static int Score(MenuNode node, string[] words)
        {
            string name = node.Name;
            string parents = node.ParentPath;
            int worst = 0;

            foreach (var word in words)
            {
                int nameIndex = name.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                int rank;
                if (nameIndex == 0)
                    rank = 0;                                   // name starts with the word
                else if (nameIndex > 0 && IsWordStart(name, nameIndex))
                    rank = 1;                                   // a word inside the name starts with it
                else if (nameIndex > 0)
                    rank = 2;                                   // somewhere in the name
                else if (parents.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                    rank = 3;                                   // only in the parent path
                else
                    return -1;

                if (rank > worst)
                    worst = rank;
            }

            return worst;
        }

        private static bool IsWordStart(string text, int index)
        {
            char previous = text[index - 1];
            if (char.IsWhiteSpace(previous) || previous == '/' || previous == '(' || previous == '-' || previous == '_' || previous == '.')
                return true;
            // CamelCase boundary: "ScriptableObject" -> "Object"
            return char.IsUpper(text[index]) && char.IsLower(previous);
        }
    }
}
