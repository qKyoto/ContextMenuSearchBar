# Context Menu Search Bar

Brings back the search bar in the Project window context menu that Unity shipped briefly and then removed.

Right-click anywhere in the Project window (or press the toolbar **+** button) and just start typing:
every item of the `Assets` menu — including everything inside `Create` and its sub-submenus, such as your
`[CreateAssetMenu]` ScriptableObjects — is filtered as you type. `Enter` executes the highlighted item.

Without a search query the popup looks and behaves like the normal menu: same items, same order, same
separators, disabled states and shortcuts. Submenus open in place with a "back" header, like Unity's
*Add Component* menu.

## Keyboard

| Key | Action |
| --- | --- |
| Type | Filter all items (every word must match the item name or its parent path, e.g. `scr obj`) |
| `↑` / `↓`, `PgUp` / `PgDn`, `Home` / `End` | Move the highlight |
| `Enter` | Execute the highlighted item / open the highlighted submenu |
| `→` / `←` | Enter / leave a submenu (when the search field is empty) |
| `Backspace` | Leave a submenu when the search field is empty |
| `Esc` | Close |

## Installation

Requires Unity 2021.3 or newer (developed and tested on Unity 6).

**From a Git URL** — Package Manager → `+` → *Install package from git URL…*:

```
https://github.com/qKyoto/ContextMenuSearchBar.git
```

Or add it to `Packages/manifest.json`:

```json
"com.garchik.context-menu-search-bar": "https://github.com/qKyoto/ContextMenuSearchBar.git"
```

**From disk** — Package Manager → `+` → *Install package from disk…* and pick `package.json`,
or copy the folder into your project's `Packages/` directory (embedded package).

**From a tarball** — run `npm pack` inside the package folder (or zip it as `.tgz`) and use
*Install package from tarball…*.

## Preferences

`Edit → Preferences → Context Menu Search Bar`

- **Enabled** — master switch; when off the native menus are used.
- **Right-click menu (Project window)** — replace the context menu in the asset list and the folder trees.
- **Toolbar "+" (Create) button** — replace the Create menu shown by the toolbar button.
- **Show keyboard shortcuts** — draw shortcut hints (`Ctrl+R`) on the right, like the native menu.
- **Max popup height** — taller menus get a scrollbar.
- **When the list changes** — *Re-place at cursor* re-runs the native placement when a submenu or search changes the height (the popup may jump); *Keep top-left corner* keeps it where it opened and grows downwards, scrolling at the screen edge.
- **Appearance** — background, text, selected row, separator and border colors (kept separately for the dark and light editor themes), with a reset button.
- **Debug logging** — logs what is intercepted; useful when reporting an issue.

## How it works (and what can break)

Unity draws context menus natively, so a search field cannot be injected into them. Instead the package:

1. reads the menu tree with the same internal API Unity uses to build the native menu
   (`UnityEditor.Menu.GetMenuItems`), so the content is always identical, and
2. intercepts the exact moments where `ProjectBrowser` would call `EditorUtility.DisplayPopupMenu`:
   a trickle-down UI Toolkit callback on the window's panel for the asset list and the `+` button, and
   the `contextClickItemCallback` delegates of the folder/asset tree views.

Everything is accessed through reflection with fallbacks: if a future Unity version renames one of these
internals the tool logs a warning once and leaves the native menus untouched. Saved-search filters keep
their own native context menu.

## License

MIT
