# Tasks: PWA Installable App

**Input**: Design documents from `/specs/006-pwa-installable-app/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Frontend**: `src/internalchat-web/src/`
- **Public Assets**: `src/internalchat-web/public/`
- **Tests**: `src/internalchat-web/tests/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and static assets

- [x] T001 Create icon assets `icon-192.png`, `icon-512.png`, and `icon-512-mask.png` in `src/internalchat-web/public/icons/`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core web app manifest and HTML meta tags required before PWA features function

**⚠️ CRITICAL**: Must be completed before user story implementation

- [x] T002 Create Web App Manifest in `src/internalchat-web/public/manifest.json` per manifest contract specification
- [x] T003 Update `src/internalchat-web/index.html` to include manifest link, apple-touch-icon, and theme-color meta tag

**Checkpoint**: Foundation ready - PWA metadata is available to browsers.

---

## Phase 3: User Story 1 - Install App from Browser (Priority: P1) 🎯 MVP

**Goal**: Enable users to install InternalChat directly from the browser onto mobile home screen or desktop launcher, opening in standalone mode.

**Independent Test**: Open app URL in Chrome or Safari, verify install prompt/button appears, complete install, launch from launcher, verify standalone display mode without browser chrome.

### Tests for User Story 1

- [x] T004 [P] [US1] Unit test manifest link and meta tag presence in `src/internalchat-web/tests/manifest.test.ts`
- [x] T005 [P] [US1] Unit test service worker caching strategy logic in `src/internalchat-web/tests/service-worker.test.ts`

### Implementation for User Story 1

- [x] T006 [US1] Extend service worker in `src/internalchat-web/src/lib/push/service-worker.ts` with cache installation, activation cleanup, cache-first for static assets, and network-first bypass for `/api/`, `/hubs/`, `/realms/`

**Checkpoint**: User Story 1 complete. App is fully installable as a PWA and caches static app shell.

---

## Phase 4: User Story 2 - App-Like Experience After Install (Priority: P2)

**Goal**: Provide native app aesthetics after installation, including dynamic theme status bar integration and task switcher branding.

**Independent Test**: Launch installed PWA, switch between light and dark modes, verify OS theme color / status bar color updates dynamically and app appears with distinct name and icon in OS task switcher.

### Tests for User Story 2

- [x] T007 [P] [US2] Unit test dynamic theme-color meta tag updates in `src/internalchat-web/tests/theme-meta.test.ts`

### Implementation for User Story 2

- [x] T008 [US2] Update theme hook/provider in `src/internalchat-web/src/features/layout/useTheme.ts` to update `<meta name="theme-color">` dynamically when active theme changes

**Checkpoint**: User Story 2 complete. Installed app dynamically syncs theme colors with browser/OS status bar.

---

## Phase 5: User Story 3 - Offline Awareness (Priority: P3)

**Goal**: Render cached app shell with a clear offline banner when network connection is lost, and auto-reconnect when connection returns.

**Independent Test**: Enable airplane mode in installed PWA, verify app shell loads from cache with OfflineBanner visible. Disable airplane mode, verify banner disappears and app reconnects automatically.

### Tests for User Story 3

- [x] T009 [P] [US3] Unit test OfflineBanner component in `src/internalchat-web/tests/offline-banner.test.tsx`

### Implementation for User Story 3

- [x] T010 [P] [US3] Create OfflineBanner component in `src/internalchat-web/src/features/pwa/OfflineBanner.tsx` with online/offline event listeners and reconnect indicator
- [x] T011 [US3] Integrate OfflineBanner into main application shell in `src/internalchat-web/src/App.tsx`

**Checkpoint**: User Story 3 complete. Offline status is gracefully indicated without browser error screens.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Validation and final verification

- [x] T012 [P] Run frontend test suite (`npm run test`) to ensure zero regressions
- [x] T013 Validate PWA installation and offline behavior against `specs/006-pwa-installable-app/quickstart.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: Can start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 - BLOCKS user stories
- **User Story 1 (Phase 3)**: Depends on Phase 2
- **User Story 2 (Phase 4)**: Depends on Phase 3
- **User Story 3 (Phase 5)**: Depends on Phase 3
- **Polish (Phase 6)**: Depends on completion of user stories

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 1: Setup icon assets
2. Phase 2: Foundational (manifest + html)
3. Phase 3: Service worker caching and US1 tests
4. Validate PWA install prompt in browser

### Incremental Delivery

1. Deliver MVP (US1 - Installable app)
2. Add US2 (Dynamic theme bar & task switcher polish)
3. Add US3 (Offline awareness banner)
