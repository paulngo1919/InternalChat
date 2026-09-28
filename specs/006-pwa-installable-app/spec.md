# Feature Specification: PWA Installable App

**Feature Branch**: `006-pwa-installable-app`

**Created**: 2026-09-28

**Status**: Draft

**Input**: User description: "implement pwa để cài app"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Install App from Browser (Priority: P1)

An employee visits `https://chat.benda.io.vn` on their phone or desktop browser. They see an "Install" prompt or a native browser install affordance (address bar icon, "Add to Home Screen" banner). They tap to install. The app icon appears on their home screen or desktop. Launching it opens the chat in a standalone window — no browser address bar, no tabs — feeling like a native app.

**Why this priority**: This is the fundamental PWA value proposition. Without installability, no other PWA feature matters. Employees get instant access to InternalChat without an app store.

**Independent Test**: Navigate to the app URL in Chrome or Safari, verify the "Install" prompt appears, install the app, launch from home screen, confirm it opens in standalone mode.

**Acceptance Scenarios**:

1. **Given** an employee visits the app URL on a supported browser (Chrome, Edge, Safari, Firefox) for the first time, **When** the page loads, **Then** the browser's native install prompt becomes available (address bar icon or "Add to Home Screen" banner).
2. **Given** the employee taps "Install" or "Add to Home Screen", **When** the installation completes, **Then** an app icon appears on their home screen (mobile) or application launcher (desktop) with the InternalChat branding.
3. **Given** the employee launches the installed app, **When** the app opens, **Then** it renders in standalone display mode without the browser's URL bar, tabs, or navigation controls.
4. **Given** the employee launches the installed app, **When** the app opens, **Then** the splash screen shows the InternalChat logo and brand colors matching the current theme (dark/light).

---

### User Story 2 - App-Like Experience After Install (Priority: P2)

After installing, the employee uses InternalChat as if it were a native app. The status bar color matches the app theme. The app appears in their OS task switcher with the InternalChat icon and name. On mobile, the app takes up the full screen without browser chrome.

**Why this priority**: A polished native feel is what distinguishes a PWA from a bookmark. It drives continued usage and positions InternalChat as a "real app" in the employee's mind.

**Independent Test**: Install the app, switch to the OS task switcher, verify the app appears with its own identity (icon + name). Check the status bar color matches the theme.

**Acceptance Scenarios**:

1. **Given** the employee has installed the PWA and opens it, **When** they view the OS task switcher, **Then** InternalChat appears as a separate entry with its own icon and name (not as a browser tab).
2. **Given** the employee is using the installed PWA on a mobile device, **When** they check the status bar, **Then** the theme color matches the app's current color scheme.
3. **Given** the employee opens the installed PWA on a device with a notch or dynamic island, **When** the app renders, **Then** it extends to the full viewport respecting safe areas properly.

---

### User Story 3 - Offline Awareness (Priority: P3)

When the employee opens the installed app and has no internet connection, the app shell loads instantly from cache and shows a clear "You're offline" message instead of a browser error page. When connectivity returns, the app reconnects automatically.

**Why this priority**: A blank "No Internet" browser error breaks the native-app illusion and confuses users. Showing the app shell with a helpful message is the minimum viable offline experience for a real-time chat app.

**Independent Test**: Install the app, enable airplane mode, launch the app. Verify the app shell renders and displays an offline indicator. Disable airplane mode, confirm reconnection.

**Acceptance Scenarios**:

1. **Given** the employee has the app installed and goes offline, **When** they launch the app, **Then** the app shell (header, sidebar structure, theme) loads from cache within 1 second.
2. **Given** the app is loaded offline, **When** the employee views the screen, **Then** a clear, non-alarming offline indicator is displayed (not a browser error page).
3. **Given** the employee is viewing the offline state, **When** connectivity returns, **Then** the app reconnects automatically without requiring a manual page reload.

---

### Edge Cases

- What happens when the user clears browser cache or storage? The app remains installed but assets must re-download on next launch.
- What happens on browsers that don't support PWA installation (e.g., in-app browsers, older Firefox on iOS)? The app remains fully functional as a regular web app; no install prompt appears but no errors are thrown either.
- What happens when the app is updated (new deployment)? The service worker detects the update and refreshes cached assets on the next launch.
- What happens when multiple tabs/windows of the installed PWA are open? Each instance operates independently; the existing SignalR connection model handles multi-tab scenarios already.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The app MUST serve a valid Web App Manifest (`manifest.json`) linked from `index.html`.
- **FR-002**: The manifest MUST include: `name`, `short_name`, `start_url`, `display: standalone`, `theme_color`, `background_color`, and at least one icon at each of 192×192 and 512×512 pixel resolutions.
- **FR-003**: The manifest `start_url` MUST be `/` and `scope` MUST be `/`.
- **FR-004**: The app MUST register a service worker that caches the app shell (HTML, CSS, JS bundles, fonts) using a cache-first strategy for static assets.
- **FR-005**: The service worker MUST use a network-first strategy for API calls (`/api/`, `/hubs/`, `/realms/`) — it MUST NOT cache or intercept API responses.
- **FR-006**: The service worker MUST NOT interfere with the existing push notification service worker (`service-worker.js`). Both must coexist or be merged into one.
- **FR-007**: The app MUST provide icons in PNG format at 192×192 and 512×512 resolutions, plus a maskable variant at 512×512 for Android adaptive icons.
- **FR-008**: The manifest `theme_color` MUST align with the app's current dark-mode default palette (Midnight Teal).
- **FR-009**: The app MUST display a meaningful offline fallback page when the network is unavailable, using the cached app shell.
- **FR-010**: The service worker MUST detect when a new version of the app is deployed and update the cache on the next app launch without manual user action.

### Key Entities

- **Web App Manifest**: A JSON file describing the app's identity (name, icons, colors, display mode) to the browser/OS.
- **Service Worker**: A background script that intercepts network requests, caches assets, and enables offline app shell loading.
- **App Icons**: PNG image files in required sizes that the OS uses for the home screen, task switcher, and splash screen.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The app passes all Lighthouse PWA audit checks (installability, splash screen, themed address bar, redirects HTTP to HTTPS, uses HTTPS, registers a service worker).
- **SC-002**: Users can install the app on Chrome (Android), Safari (iOS 16.4+), Edge (Windows), and Chrome (macOS) and launch it in standalone mode.
- **SC-003**: The installed app shell loads from cache within 1 second on repeat visits when online.
- **SC-004**: When offline, the installed app displays the cached app shell with an offline indicator within 1 second instead of a browser error page.
- **SC-005**: The existing push notification functionality continues to work unchanged after PWA implementation.

**Required by the project constitution:**

- **Responsiveness**: The installed PWA app shell loads from cache in under 1 second at the 95th percentile on repeat visits.
- **Scale**: PWA manifest and service worker add no measurable overhead to the existing system's 10,000 concurrent connection capacity.
- **Access control**: The service worker MUST NOT cache any authenticated API responses. All cached content is static, public assets only (HTML shell, CSS, JS, fonts, icons).
- **Auditability**: Service worker registration and update events MUST be logged to the browser console in development and available for debugging.
- **Data durability**: No user data is stored in the service worker cache. If the service worker cache is cleared, the app simply re-downloads static assets on the next launch — no data is lost.

## Assumptions

- The app is already served over HTTPS via Cloudflare Tunnel (`https://chat.benda.io.vn`), which is a prerequisite for PWA.
- The existing service worker at `/service-worker.js` handles only push notifications and will be extended or run alongside the new PWA caching service worker.
- iOS Safari supports PWA installation from iOS 16.4+; older iOS versions will use the app as a regular website without install capability.
- App icons will be generated as static PNG files included in the build output, not dynamically generated.
- The splash screen appearance is determined by the OS from the manifest fields (name, background_color, icon) — no custom splash screen HTML is needed.
- The offline experience is limited to showing the cached app shell with an offline message; offline message sending/reading is explicitly out of scope for this feature.
