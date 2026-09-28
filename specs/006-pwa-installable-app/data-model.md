# Data Model: PWA Installable App

**Date**: 2026-09-28 | **Plan**: [plan.md](plan.md)

## Overview

This feature introduces no new database entities, API models, or persistent data structures. All artifacts are static files served by the web server or browser-native data stores.

## Static Artifacts

### Web App Manifest (`manifest.json`)

A JSON file describing the app's identity to the browser and OS.

| Field | Value | Purpose |
| --- | --- | --- |
| `name` | `"InternalChat"` | Full app name shown in install prompts and app info |
| `short_name` | `"Chat"` | Abbreviated name for home screen icons |
| `start_url` | `"/"` | URL opened when the app is launched |
| `scope` | `"/"` | Navigation scope of the PWA |
| `display` | `"standalone"` | Removes browser chrome (URL bar, tabs) |
| `theme_color` | `"#0f1923"` | Status bar / title bar color on mobile/desktop |
| `background_color` | `"#0f1923"` | Splash screen background color |
| `icons` | Array of icon objects | Icons for home screen, splash, task switcher |
| `orientation` | `"any"` | Allow portrait and landscape |

### App Icons

| File | Size | Type | Purpose |
| --- | --- | --- | --- |
| `icon-192.png` | 192×192 | `image/png` | Chrome install minimum, home screen on Android |
| `icon-512.png` | 512×512 | `image/png` | Splash screen, high-DPI devices |
| `icon-512-mask.png` | 512×512 | `image/png`, `maskable` | Android adaptive icons (safe zone padding) |

### Service Worker Cache

The Cache API stores static assets. No schema — keys are request URLs, values are response bodies.

| Cache Name | Contents | Strategy |
| --- | --- | --- |
| `internalchat-shell-v1` | `index.html` | Network-first, cache fallback |
| `internalchat-assets-v1` | Hashed JS/CSS/fonts under `/assets/` | Cache-first (immutable) |
| `internalchat-icons-v1` | PWA icons, favicon | Cache-first |

**Cache versioning**: The `v1` suffix enables atomic cache replacement during service worker updates. The `activate` event deletes caches not matching the current version set.

## Relationships

```
manifest.json ──references──► icons/*.png
index.html ──links──► manifest.json
index.html ──links──► service-worker.js
service-worker.js ──manages──► Cache API stores
```

No database tables, no migrations, no API contracts affected.
