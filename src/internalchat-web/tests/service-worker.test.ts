import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Service worker unit tests covering push notifications and PWA caching handlers.
 */

type Listener = (event: unknown) => void;

function installScope() {
  const listeners = new Map<string, Listener>();
  const showNotification = vi.fn(() => Promise.resolve());
  const openWindow = vi.fn(() => Promise.resolve());
  const claim = vi.fn(() => Promise.resolve());
  const skipWaiting = vi.fn(() => Promise.resolve());

  const mockCache = {
    addAll: vi.fn(() => Promise.resolve()),
    match: vi.fn(() => Promise.resolve(undefined)),
    put: vi.fn(() => Promise.resolve()),
  };

  const caches = {
    open: vi.fn(() => Promise.resolve(mockCache)),
    keys: vi.fn(() => Promise.resolve(['internalchat-shell-v1', 'old-cache-v0'])),
    delete: vi.fn(() => Promise.resolve(true)),
  };

  Reflect.set(globalThis, 'registration', { showNotification });
  Reflect.set(globalThis, 'clients', { openWindow, claim });
  Reflect.set(globalThis, 'caches', caches);
  Reflect.set(globalThis, 'skipWaiting', skipWaiting);

  Object.assign(globalThis, {
    addEventListener: (type: string, listener: Listener) => listeners.set(type, listener),
  });

  return { listeners, showNotification, openWindow, claim, skipWaiting, caches, mockCache, clients: { openWindow, claim } };
}

function pushEvent(json: unknown) {
  const held: Promise<unknown>[] = [];

  return {
    event: { data: { json: () => json }, waitUntil: (p: Promise<unknown>) => held.push(p) },
    settled: () => Promise.all(held),
  };
}

beforeEach(() => {
  vi.resetModules();
  vi.restoreAllMocks();
});

describe('push', () => {
  it('shows the notification the server sent', async () => {
    const scope = installScope();
    await import('../src/lib/push/service-worker');

    const { event, settled } = pushEvent({
      title: 'An Nguyen',
      body: 'Can you look at the deploy?',
      deepLink: '/conversations/c1?seq=42',
    });

    scope.listeners.get('push')?.(event);
    await settled();

    expect(scope.showNotification).toHaveBeenCalledWith('An Nguyen', {
      body: 'Can you look at the deploy?',
      data: { deepLink: '/conversations/c1?seq=42' },
    });
  });

  it('falls back to a generic notification for a payload it does not recognise', async () => {
    const scope = installScope();
    await import('../src/lib/push/service-worker');

    const { event, settled } = pushEvent({ kind: 'something.new.v2' });

    scope.listeners.get('push')?.(event);
    await settled();

    expect(scope.showNotification).toHaveBeenCalledWith('New message', {
      body: 'Open InternalChat to see what changed.',
      data: { deepLink: '/' },
    });
  });

  it('falls back when title or body is not a string', async () => {
    const scope = installScope();
    await import('../src/lib/push/service-worker');

    const { event, settled } = pushEvent({ title: 42, body: 'text' });

    scope.listeners.get('push')?.(event);
    await settled();

    expect(scope.showNotification).toHaveBeenCalledWith(
      'New message',
      expect.objectContaining({ data: { deepLink: '/' } }) as unknown,
    );
  });

  it('falls back when the push carried no data at all', async () => {
    const scope = installScope();
    await import('../src/lib/push/service-worker');

    const held: Promise<unknown>[] = [];

    scope.listeners.get('push')?.({
      data: null,
      waitUntil: (p: Promise<unknown>) => held.push(p),
    });

    await Promise.all(held);

    expect(scope.showNotification).toHaveBeenCalledWith('New message', expect.anything());
  });

  it('defaults the deep link when the payload omits it', async () => {
    const scope = installScope();
    await import('../src/lib/push/service-worker');

    const { event, settled } = pushEvent({ title: 'An', body: 'hi', deepLink: 99 });

    scope.listeners.get('push')?.(event);
    await settled();

    expect(scope.showNotification).toHaveBeenCalledWith(
      'An',
      expect.objectContaining({ data: { deepLink: '/' } }) as unknown,
    );
  });
});

describe('notificationclick', () => {
  it('closes the notification and opens the triggering message', async () => {
    const scope = installScope();
    await import('../src/lib/push/service-worker');

    const close = vi.fn();
    const held: Promise<unknown>[] = [];

    scope.listeners.get('notificationclick')?.({
      notification: { data: { deepLink: '/conversations/c1?seq=42' }, close },
      waitUntil: (p: Promise<unknown>) => held.push(p),
    });

    await Promise.all(held);

    expect(close).toHaveBeenCalled();
    expect(scope.openWindow).toHaveBeenCalledWith('/conversations/c1?seq=42');
  });

  it('opens the root when the notification carries no deep link', async () => {
    const scope = installScope();
    await import('../src/lib/push/service-worker');

    const held: Promise<unknown>[] = [];

    scope.listeners.get('notificationclick')?.({
      notification: { data: null, close: vi.fn() },
      waitUntil: (p: Promise<unknown>) => held.push(p),
    });

    await Promise.all(held);

    expect(scope.openWindow).toHaveBeenCalledWith('/');
  });
});

describe('pwa service worker lifecycle', () => {
  it('precaches app shell on install and calls skipWaiting', async () => {
    const scope = installScope();
    await import('../src/lib/push/service-worker');

    const held: Promise<unknown>[] = [];
    scope.listeners.get('install')?.({
      waitUntil: (p: Promise<unknown>) => held.push(p),
    });

    await Promise.all(held);

    expect(scope.caches.open).toHaveBeenCalledWith('internalchat-shell-v1');
    expect(scope.mockCache.addAll).toHaveBeenCalledWith([
      '/',
      '/index.html',
      '/manifest.json',
      '/favicon.svg',
    ]);
    expect(scope.skipWaiting).toHaveBeenCalled();
  });

  it('cleans up obsolete internalchat caches on activate', async () => {
    const scope = installScope();
    await import('../src/lib/push/service-worker');

    const held: Promise<unknown>[] = [];
    scope.listeners.get('activate')?.({
      waitUntil: (p: Promise<unknown>) => held.push(p),
    });

    await Promise.all(held);

    expect(scope.caches.keys).toHaveBeenCalled();
    expect(scope.clients.claim).toHaveBeenCalled();
  });
});
