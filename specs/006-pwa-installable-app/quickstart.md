# Quickstart Validation: PWA Installable App

**Date**: 2026-09-28 | **Plan**: [plan.md](plan.md)

## Prerequisites

- Docker Desktop running
- Chrome browser (or Edge) for testing install
- Mobile device or Chrome DevTools mobile emulation for mobile testing

## 1. Build and Deploy

```bash
# From repo root — deploy to https://chat.benda.io.vn
.\deploy\scripts\Start-Public.ps1
```

Or for local development:

```bash
# From src/internalchat-web/
npm run dev
# Open http://localhost:8080
```

## 2. Validate Manifest

1. Open Chrome DevTools → **Application** tab → **Manifest** section
2. Verify all fields are populated:
   - Name: "InternalChat"
   - Short Name: "Chat"
   - Start URL: "/"
   - Display: "standalone"
   - Theme Color: "#0f1923"
   - Icons: 192px and 512px visible with previews

**Expected**: No warnings or errors in the Manifest panel.

## 3. Validate Service Worker

1. In DevTools → **Application** → **Service Workers**
2. Verify the service worker is registered and active
3. Check **Cache Storage** — verify three caches:
   - `internalchat-shell-v1`
   - `internalchat-assets-v1`
   - `internalchat-icons-v1`
4. Verify API routes (`/api/*`) are NOT in any cache

**Expected**: Service worker is active. Static assets cached. No API responses cached.

## 4. Validate Installation (Desktop)

1. In Chrome, look for the install icon in the address bar (⊕ or install prompt)
2. Click to install
3. The app opens in a standalone window — no URL bar, no tabs
4. Check the OS task bar — InternalChat appears with its own icon

**Expected**: App installs and launches in standalone mode.

## 5. Validate Installation (Mobile)

1. On Android Chrome: tap the three-dot menu → "Add to Home screen" or "Install app"
2. On iOS Safari (16.4+): tap Share → "Add to Home Screen"
3. Launch from home screen
4. Verify: no browser chrome, full-screen app experience

**Expected**: App icon on home screen. Launches in standalone mode.

## 6. Validate Offline Behavior

1. Install the app (or visit it once to populate the cache)
2. Enable Airplane Mode (or DevTools → Network → Offline)
3. Launch the app or reload
4. Verify: the app shell renders (header, sidebar skeleton), and an offline banner is visible
5. Disable Airplane Mode
6. Verify: the offline banner disappears, the app reconnects

**Expected**: Cached app shell loads in <1s. Offline banner visible. Auto-reconnects.

## 7. Run Lighthouse PWA Audit

1. DevTools → **Lighthouse** tab
2. Select "Progressive Web App" category
3. Run audit

**Expected**: All PWA checks pass (installable, themed, HTTPS, service worker registered).

## 8. Validate Push Notifications Still Work

1. If push notifications were previously enabled, verify they still arrive
2. Send a test message from another user
3. If the tab is backgrounded, an OS notification should appear

**Expected**: No regression in push notification functionality.

## 9. Run Unit Tests

```bash
cd src/internalchat-web
npm test
```

**Expected**: All tests pass, including new cache and OfflineBanner tests.
