# Changelog

## [1.0.3] - 2026-09-27

### Changed
- Whether an item is enabled or checked is now asked for only when the item is actually shown, instead of validating the whole menu on open. Opening is faster in projects with many menu items, and validate methods of other packages run no more often than with the native menu.
- Validate methods are given the assets the menu was opened for, the way Unity does for a native context menu. Without that context a third-party validate method could fail — an IndexOutOfRangeException from FImpossible Creations' asset tools was reported this way.

## [1.0.2] - 2026-09-21

### Added
- Preference "When the list changes": re-place the popup at the cursor (native-like) or keep its top-left corner and grow downwards, scrolling at the screen edge.

## [1.0.1] - 2026-09-20

### Fixed
- A scrollbar could appear on first open (and overlap the shortcut column) when the OS rounded the popup height down by a fraction of a point.

### Added
- Appearance section in Preferences: background, text, selected row, selected row text, separator and border colors, stored per editor theme.

## [1.0.0] - 2026-09-20

### Added
- Searchable replacement for the Project window context menu (right-click in the asset list, folder tree and one-column tree).
- Searchable replacement for the toolbar "+" (Create) button menu.
- Keyboard navigation: Up/Down, Enter, Right/Left (enter/leave submenu), Backspace (back when the search field is empty), Esc.
- Preferences page (Edit > Preferences > Context Menu Search Bar) with per-feature toggles.
