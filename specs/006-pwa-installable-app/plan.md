# Implementation Plan: PWA Installable App

**Branch**: `006-pwa-installable-app` | **Date**: 2026-09-28 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/006-pwa-installable-app/spec.md`

## Summary

Make InternalChat installable as a Progressive Web App (PWA) on mobile and desktop. This requires: a Web App Manifest (`manifest.json`), app icons in required sizes, extending the existing push-notification service worker with app-shell caching, and an offline fallback page. The approach merges PWA cache logic into the single existing service worker to avoid scope conflicts, and generates icons from the existing `favicon.svg`.

## Technical Context

**Language/Version**: TypeScript (strict) / React 19 / Vite 8

**Primary Dependencies**: No new runtime dependencies. The existing service worker (`src/lib/push/service-worker.ts`) is extended. Icon generation uses a one-time offline script (sharp or svg-to-png CLI).

**Storage**: Service worker Cache API for static assets only (HTML shell, CSS, JS bundles, fonts). No IndexedDB, no API response caching.

**Testing**: Vitest (existing); Lighthouse PWA audit as a manual validation step.

**Target Platform**: Chrome (Android/Desktop), Safari (iOS 16.4+), Edge (Windows), Firefox (Desktop)

**Project Type**: Web application (SPA) — frontend-only change

**Performance Goals**: App shell loads from cache in <1s on repeat visits (p95). No measurable impact on existing bundle size budget (manifest + icons are static, not in the JS bundle).

**Constraints**: Must coexist with existing push notification service worker at `/service-worker.js`. Must not cache API responses. Must pass Lighthouse PWA audit.

**Scale/Scope**: No backend changes. ~6 files touched in `src/internalchat-web`.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

*Source: `.specify/memory/constitution.md` v1.2.0.*

| # | Gate | Status | Notes |
| --- | --- | --- | --- |
| I | **Clean Architecture** | PASS | Frontend-only change. No new layers or cross-layer dependencies. Service worker stays in `src/lib/push/`. |
| II | **SOLID** | PASS | Service worker gains one focused responsibility (cache management) alongside its existing push handler. Separated by event listener, not by mixing concerns. |
| III | **Test-First** | PASS | Unit tests for cache logic with mocked Cache API. Existing push notification tests remain unchanged. React coverage floor (80%) maintained. |
| IV | **Security by Default** | PASS | Service worker MUST NOT cache authenticated API responses. Only static, public assets cached. No new auth surfaces. |
| V | **Performance Budgets** | PASS | App shell cache-first serves in <1s p95. No new API endpoints. No impact on existing latency budgets. Static manifest/icons excluded from JS bundle budget. |
| VI | **Messaging Contracts** | N/A | No new message bus consumers or producers. |
| VII | **Data Authority & Cache** | PASS | Service worker cache stores only immutable static assets (hashed filenames). No user data in cache. Cache cleared on SW update — no staleness risk. |
| VIII | **Zero-Cost & Self-Hosted** | PASS | No new dependencies. Manifest is a static JSON file. Icons generated one-time from existing SVG. All browser-native APIs. |
| — | **Stack** | PASS | React 19 + TypeScript + Vite. No new runtime libraries. |
| — | **Docker-First** | PASS | No new services. Manifest and icons ship as static files in the existing `dist/` output. No Compose changes needed. |

**Declared performance budget for this feature**: App shell load from cache < 1s p95 on repeat visits. Manifest fetch < 50ms (static file, 1 KB). No regression to existing bundle budget (300 KB gzipped JS).

**Declared security surface for this feature**: No new auth flows. No API caching. Service worker cache stores only public static assets (HTML, CSS, JS, fonts, icons). No new endpoints, no new data classes, no audit events.

## Project Structure

### Documentation (this feature)

```text
specs/006-pwa-installable-app/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output (minimal — no new entities)
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (manifest schema)
└── tasks.md             # Phase 2 output (/speckit-tasks command)
```

### Source Code (repository root)

```text
src/internalchat-web/
├── public/
│   ├── manifest.json          # NEW — Web App Manifest
│   ├── icons/
│   │   ├── icon-192.png       # NEW — PWA icon 192×192
│   │   ├── icon-512.png       # NEW — PWA icon 512×512
│   │   └── icon-512-mask.png  # NEW — Maskable icon 512×512
│   ├── favicon.svg            # EXISTING
│   └── icons.svg              # EXISTING
├── index.html                 # MODIFIED — link to manifest, theme-color meta
├── src/
│   ├── lib/
│   │   └── push/
│   │       └── service-worker.ts  # MODIFIED — add cache management alongside push
│   ├── features/
│   │   └── pwa/
│   │       └── OfflineBanner.tsx   # NEW — offline indicator component
│   └── App.tsx                    # MODIFIED — render OfflineBanner when offline
└── tests/
    ├── service-worker.test.ts     # MODIFIED — add cache tests
    └── offline-banner.test.tsx    # NEW — OfflineBanner unit tests
```

**Structure Decision**: All PWA artifacts live within the existing `internalchat-web` project. No new projects, packages, or services. The service worker is extended in-place to avoid scope registration conflicts.

## Complexity Tracking

> No violations. All gates PASS. No complexity exceptions needed.

| Violation | Why Needed | Simpler Alternative Rejected Because | Owner | Removal Date |
| --- | --- | --- | --- | --- |
| *(none)* | | | | |
