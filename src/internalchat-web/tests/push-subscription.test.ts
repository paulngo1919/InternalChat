/**
 * T139 — client notification capability detection (FR-040).
 *
 * jsdom (the test environment) has no `PushManager`, which is itself the useful case to pin: a
 * browser genuinely lacking push support must report `'unsupported'` rather than throwing or
 * silently reporting ready.
 */

import { describe, expect, it } from 'vitest'

import { detectClientNotificationCapability } from '../src/lib/push/pushSubscription'

describe('detectClientNotificationCapability', () => {
  it('reports unsupported when the browser has no PushManager', () => {
    expect(detectClientNotificationCapability()).toBe('unsupported')
  })
})
