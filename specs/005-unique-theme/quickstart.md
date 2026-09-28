# Quickstart: Unique Theme

## Prerequisites

- Frontend must be running (`npm run dev` in `src/internalchat-web`).
- Accessible via `http://localhost:5173`.

## Validation Steps

1. **Verify Default Theme**:
   - Open `http://localhost:5173` in a browser.
   - The theme should match your OS-level Dark/Light preference.
   - Check the Developer Console: `localStorage.getItem('theme_preference')` should return `null` or `"system"`.

2. **Verify Theme Toggle**:
   - Locate the Theme Toggle button in the settings/sidebar.
   - Click to switch from System to Dark.
   - The UI should instantly change.
   - Check LocalStorage: `theme_preference` should be `"dark"`.
   - Click to switch to Light.
   - Check LocalStorage: `theme_preference` should be `"light"`.

3. **Verify Persistence**:
   - Refresh the page.
   - The page should immediately render in the selected theme with no flashing of the previous theme (FOUC).
