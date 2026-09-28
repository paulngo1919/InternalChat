/**
 * Service worker: handles push notifications (FR-034, FR-039) and PWA app-shell caching (FR-004 to FR-006, FR-010).
 */

interface PushMessagePayload {
  readonly title: string;
  readonly body: string;
  readonly deepLink: string;
}

interface NotificationOptionsLike {
  readonly body?: string;
  readonly data?: unknown;
}

interface NotificationLike {
  readonly data: unknown;
  close(): void;
}

interface PushEventLike {
  readonly data: { json(): unknown } | null;
  waitUntil(promise: Promise<unknown>): void;
}

interface NotificationClickEventLike {
  readonly notification: NotificationLike;
  waitUntil(promise: Promise<unknown>): void;
}

interface ExtendableEventLike {
  waitUntil(promise: Promise<unknown>): void;
}

interface FetchEventLike {
  readonly request: Request;
  respondWith(response: Promise<Response> | Response): void;
  waitUntil(promise: Promise<unknown>): void;
}

interface ClientListOptionsLike {
  readonly type?: 'window' | 'worker' | 'sharedworker' | 'all';
  readonly includeUncontrolled?: boolean;
}

interface ClientsLike {
  openWindow(url: string): Promise<unknown>;
  claim(): Promise<void>;
  matchAll(options?: ClientListOptionsLike): Promise<unknown[]>;
}

interface CacheLike {
  match(request: RequestInfo | URL): Promise<Response | undefined>;
  put(request: RequestInfo | URL, response: Response): Promise<void>;
  addAll(requests: (RequestInfo | URL)[]): Promise<void>;
}

interface CacheStorageLike {
  open(cacheName: string): Promise<CacheLike>;
  keys(): Promise<string[]>;
  delete(cacheName: string): Promise<boolean>;
}

interface ServiceWorkerScopeLike {
  readonly caches: CacheStorageLike;
  registration: {
    showNotification(title: string, options: NotificationOptionsLike): Promise<void>;
  };
  clients: ClientsLike;
  skipWaiting(): Promise<void>;
  addEventListener(type: 'push', listener: (event: PushEventLike) => void): void;
  addEventListener(
    type: 'notificationclick',
    listener: (event: NotificationClickEventLike) => void,
  ): void;
  addEventListener(
    type: 'install' | 'activate',
    listener: (event: ExtendableEventLike) => void,
  ): void;
  addEventListener(type: 'fetch', listener: (event: FetchEventLike) => void): void;
}

const scope = self as unknown as ServiceWorkerScopeLike;

const CACHE_SHELL = 'internalchat-shell-v1';
const CACHE_ASSETS = 'internalchat-assets-v1';
const CACHE_ICONS = 'internalchat-icons-v1';
const PRECACHE_URLS = ['/', '/index.html', '/manifest.json', '/favicon.svg'];

// --- Push Notification Handlers ---

function readPayload(event: PushEventLike): PushMessagePayload {
  const raw: unknown = event.data?.json();

  if (
    typeof raw === 'object' &&
    raw !== null &&
    'title' in raw &&
    'body' in raw &&
    typeof (raw as { title: unknown }).title === 'string' &&
    typeof (raw as { body: unknown }).body === 'string'
  ) {
    const candidate = raw as { title: string; body: string; deepLink?: unknown };

    return {
      title: candidate.title,
      body: candidate.body,
      deepLink: typeof candidate.deepLink === 'string' ? candidate.deepLink : '/',
    };
  }

  return { title: 'New message', body: 'Open InternalChat to see what changed.', deepLink: '/' };
}

scope.addEventListener('push', (event) => {
  const payload = readPayload(event);

  event.waitUntil(
    scope.registration.showNotification(payload.title, {
      body: payload.body,
      data: { deepLink: payload.deepLink },
    }),
  );
});

scope.addEventListener('notificationclick', (event) => {
  event.notification.close();

  const data = event.notification.data;
  const deepLink =
    typeof data === 'object' && data !== null && 'deepLink' in data ? String(data.deepLink) : '/';

  event.waitUntil(scope.clients.openWindow(deepLink));
});

// --- PWA Cache Management Handlers ---

scope.addEventListener('install', (event) => {
  event.waitUntil(
    scope.caches
      .open(CACHE_SHELL)
      .then((cache) => cache.addAll(PRECACHE_URLS))
      .then(() => scope.skipWaiting()),
  );
});

scope.addEventListener('activate', (event) => {
  const validCaches = new Set([CACHE_SHELL, CACHE_ASSETS, CACHE_ICONS]);
  event.waitUntil(
    scope.caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys
            .filter((key) => key.startsWith('internalchat-') && !validCaches.has(key))
            .map((key) => scope.caches.delete(key)),
        ),
      )
      .then(() => scope.clients.claim()),
  );
});

scope.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;

  const url = new URL(req.url);

  // Never intercept or cache API, hub, or realm requests or authenticated calls
  if (
    url.pathname.startsWith('/api/') ||
    url.pathname.startsWith('/hubs/') ||
    url.pathname.startsWith('/realms/') ||
    url.pathname.startsWith('/resources/') ||
    req.headers.has('Authorization')
  ) {
    return;
  }

  // Icons and manifest -> Cache First
  if (
    url.pathname.startsWith('/icons/') ||
    url.pathname === '/favicon.svg' ||
    url.pathname === '/manifest.json'
  ) {
    event.respondWith(
      scope.caches.open(CACHE_ICONS).then((cache) =>
        cache.match(req).then((cached) => {
          if (cached) return cached;
          return fetch(req).then((response) => {
            if (response.ok) {
              void cache.put(req, response.clone());
            }
            return response;
          });
        }),
      ),
    );
    return;
  }

  // Static assets (JS/CSS/fonts/build assets) -> Cache First
  if (url.pathname.startsWith('/assets/')) {
    event.respondWith(
      scope.caches.open(CACHE_ASSETS).then((cache) =>
        cache.match(req).then((cached) => {
          if (cached) return cached;
          return fetch(req).then((response) => {
            if (response.ok) {
              void cache.put(req, response.clone());
            }
            return response;
          });
        }),
      ),
    );
    return;
  }

  // Navigation / HTML Shell -> Network First with Cache Fallback
  if (req.mode === 'navigate' || req.headers.get('accept')?.includes('text/html')) {
    event.respondWith(
      fetch(req)
        .then((response) => {
          if (response.ok) {
            const clone = response.clone();
            void scope.caches.open(CACHE_SHELL).then((cache) => cache.put('/', clone));
          }
          return response;
        })
        .catch(() =>
          scope.caches.open(CACHE_SHELL).then(async (cache) => {
            const main = await cache.match('/');
            if (main) return main;
            const index = await cache.match('/index.html');
            return index ?? Response.error();
          }),
        ),
    );
  }
});
