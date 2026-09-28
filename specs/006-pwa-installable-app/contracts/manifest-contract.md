# Contract: Web App Manifest

**Date**: 2026-09-28 | **Plan**: [../plan.md](../plan.md)

## manifest.json Schema

The manifest follows the [W3C Web Application Manifest](https://www.w3.org/TR/appmanifest/) specification.

```json
{
  "name": "InternalChat",
  "short_name": "Chat",
  "description": "Internal team chat platform",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "orientation": "any",
  "theme_color": "#0f1923",
  "background_color": "#0f1923",
  "icons": [
    {
      "src": "/icons/icon-192.png",
      "sizes": "192x192",
      "type": "image/png"
    },
    {
      "src": "/icons/icon-512.png",
      "sizes": "512x512",
      "type": "image/png"
    },
    {
      "src": "/icons/icon-512-mask.png",
      "sizes": "512x512",
      "type": "image/png",
      "purpose": "maskable"
    }
  ]
}
```

## index.html Changes

The following tags are added to `<head>`:

```html
<link rel="manifest" href="/manifest.json" />
<meta name="theme-color" content="#0f1923" />
<meta name="apple-mobile-web-app-capable" content="yes" />
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
<link rel="apple-touch-icon" href="/icons/icon-192.png" />
```

## Service Worker Cache Contract

### Events Handled

| Event | Behavior |
| --- | --- |
| `install` | Pre-cache `index.html`. Call `skipWaiting()`. |
| `activate` | Delete old cache versions. Call `clients.claim()`. |
| `fetch` | Route requests by URL pattern (see below). |
| `push` | (Existing) Show OS notification. |
| `notificationclick` | (Existing) Open deep link. |

### Fetch Routing Rules

| URL Pattern | Strategy | Cache Name |
| --- | --- | --- |
| `/api/*`, `/hubs/*`, `/realms/*`, `/resources/*` | Network only (passthrough) | — |
| `/assets/*` | Cache-first | `internalchat-assets-v1` |
| `/icons/*`, `/favicon.svg` | Cache-first | `internalchat-icons-v1` |
| `/` (HTML navigation) | Network-first, cache fallback | `internalchat-shell-v1` |
| All other | Network only | — |

### No-Cache Guarantee

The service worker MUST NOT intercept or cache:
- Any request with an `Authorization` header
- Any request to `/api/`, `/hubs/`, `/realms/`, or `/resources/`
- WebSocket upgrade requests
