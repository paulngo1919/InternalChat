# Research: PWA Installable App

**Date**: 2026-09-28 | **Plan**: [plan.md](plan.md)

## R1: Service Worker Strategy — Extend vs. Separate

**Decision**: Extend the existing service worker at `src/lib/push/service-worker.ts`.

**Rationale**: A PWA can only have one active service worker per scope. The app already registers `service-worker.js` at `/` for push notifications. Registering a second service worker at the same scope would replace the first, breaking push. Extending the existing one avoids this entirely.

**Alternatives considered**:
- **Separate service workers at different scopes**: Rejected — the PWA manifest `scope` must match the service worker scope. Using `/` for both is the only viable option.
- **vite-plugin-pwa (Workbox)**: Rejected — adds a runtime dependency (Workbox library ~12 KB), generates a separate service worker that conflicts with the existing one, and violates the "no new dependencies" principle. The caching needs are simple enough for hand-written Cache API calls.

## R2: Cache Strategy for Static Assets

**Decision**: Use a "cache-first with network update" (stale-while-revalidate) strategy for hashed static assets, and "network-first with cache fallback" for the HTML shell.

**Rationale**:
- **Hashed assets** (`assets/main-[hash].js`, `assets/main-[hash].css`, fonts): Content-hashed filenames guarantee immutability. Cache-first is safe and instant.
- **`index.html`**: Not content-hashed. Network-first ensures the user gets the latest HTML (which references the latest hashed JS/CSS), falling back to cache when offline.
- **API routes** (`/api/`, `/hubs/`, `/realms/`): MUST NOT be cached. The fetch handler explicitly passes these through to the network.

**Alternatives considered**:
- **Cache everything with Workbox**: Rejected — over-engineered for this use case. Hand-written `install`/`activate`/`fetch` handlers are ~50 lines.
- **Precache manifest**: Rejected — requires build-time generation of an asset list and Workbox integration. The app's hashed filenames already provide cache-busting; caching on first fetch is sufficient.

## R3: Manifest Configuration

**Decision**: Static `manifest.json` in `public/`, linked from `index.html`.

**Rationale**: The manifest is a static file that rarely changes. Placing it in `public/` means Vite copies it as-is to `dist/`. No build plugin needed.

**Key fields**:
- `name`: "InternalChat"
- `short_name`: "Chat"
- `start_url`: "/"
- `scope`: "/"
- `display`: "standalone"
- `theme_color`: "#0f1923" (Midnight Teal dark mode default)
- `background_color`: "#0f1923"
- `icons`: 192px, 512px, 512px maskable

## R4: Icon Generation

**Decision**: Generate PNG icons from the existing `public/favicon.svg` using a one-time script (sharp CLI or browser-based SVG-to-PNG), committed as static files.

**Rationale**: The icons only change when the branding changes. Generating at build time adds complexity; committing PNGs is simpler and zero-dependency.

**Required sizes**:
- `icon-192.png` (192×192) — minimum for Chrome installability
- `icon-512.png` (512×512) — required for splash screen on Android
- `icon-512-mask.png` (512×512, maskable) — safe zone for Android adaptive icons

**Alternatives considered**:
- **Build-time generation with `vite-plugin-pwa`**: Rejected — adds a dev dependency and build complexity for 3 static files.
- **SVG icon in manifest**: Not supported — browsers require raster (PNG/WebP) icons.

## R5: Offline Indicator UX

**Decision**: A non-intrusive banner at the top of the app using `navigator.onLine` + `online`/`offline` events, auto-dismissing when connectivity returns.

**Rationale**: Chat is inherently a real-time feature. The offline indicator should be visible but not blocking — the user can still read cached content (the app shell). When online status returns, the banner disappears and the existing SignalR reconnection logic handles the rest.

**Alternatives considered**:
- **Full-screen offline page**: Rejected — too aggressive; the user can still see the app UI even if data is stale.
- **Toast notification**: Rejected — too easy to miss on mobile; a persistent banner is clearer.

## R6: Service Worker Update Flow

**Decision**: Use the `install` event to cache new assets and `activate` event with `clients.claim()` to take control immediately. The `skipWaiting()` call ensures the new service worker activates on the next page load rather than waiting for all tabs to close.

**Rationale**: Users should get the latest app version on their next visit without manual intervention. `skipWaiting()` + `clients.claim()` is the standard pattern for this.

**Risk**: A mid-session update could theoretically serve mismatched assets (old HTML referencing new or missing JS). Mitigated by content-hashed filenames — old JS files remain in the cache until the next `activate` cleans them up.
