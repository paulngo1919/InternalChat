# Data Model: Unique Theme

## LocalStorage Entity: `theme_preference`

This feature does not require database changes. It relies strictly on browser LocalStorage.

- **Key**: `theme_preference`
- **Type**: String
- **Valid Values**:
  - `"light"`: Explicitly requested light mode.
  - `"dark"`: Explicitly requested dark mode.
  - `"system"` (default): Defer to OS `prefers-color-scheme`.
- **Validation**: If the value does not match the valid string literals, it defaults to `"system"`.
