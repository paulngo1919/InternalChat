import { afterEach, describe, expect, it, vi } from 'vitest'

import { DeliveryTelemetry } from '../src/lib/realtime/deliveryTelemetry'

/**
 * 002 T049 — the browser half of FR-011: what delivery felt like, reported in aggregate.
 *
 * The server's histograms stop at the hub send. This is the only measurement that includes the
 * network and the browser, and it has one hard problem: the lag is `receivedAt − sentAt` across two
 * clocks. The offset is estimated from the employee's own sends — the response carries the
 * server's `sentAt` to the millisecond, so the round trip bounds the error at half its length. The
 * HTTP `Date` header was considered and rejected: one-second resolution cannot measure a 300 ms
 * budget.
 */

afterEach(() => {
  vi.useRealTimers()
})

function reporter(post = vi.fn(() => Promise.resolve(new Response(null, { status: 202 })))) {
  return { telemetry: new DeliveryTelemetry(post), post }
}

describe('clock offset', () => {
  it('reports nothing until an own send has established the offset', async () => {
    const { telemetry, post } = reporter()

    telemetry.recordDelivery('2026-09-25T09:00:00.000Z', Date.parse('2026-09-25T09:00:00.100Z'))
    await telemetry.flush()

    // Without an offset the number would be clock skew, not delivery lag.
    expect(post).not.toHaveBeenCalled()
  })

  it('corrects for a client clock that runs ahead of the server', async () => {
    const { telemetry, post } = reporter()

    // Client is 5 s ahead. The request went out at client 10.000 and came back at 10.040, so the
    // server stamped it at about client 10.020, i.e. server 05.020.
    telemetry.observeRoundTrip(Date.parse('2026-09-25T09:00:10.000Z'), Date.parse('2026-09-25T09:00:10.040Z'), '2026-09-25T09:00:05.020Z')

    // A message the server sent at 20.000 arrives at client 25.080: 80 ms of real lag.
    telemetry.recordDelivery('2026-09-25T09:00:20.000Z', Date.parse('2026-09-25T09:00:25.080Z'))
    await telemetry.flush()

    const body = JSON.parse((post.mock.calls[0] as unknown as [string, RequestInit])[1].body as string) as {
      buckets: Record<string, number>
      clockOffsetMs: number
    }
    expect(body.buckets).toEqual({ '100': 1 })
    expect(body.clockOffsetMs).toBe(5000)
  })

  it('keeps the estimate from the fastest round trip, which has the smallest error', async () => {
    const { telemetry, post } = reporter()

    telemetry.observeRoundTrip(0, 20, new Date(10).toISOString()) // offset 0, rtt 20
    telemetry.observeRoundTrip(100, 900, new Date(100).toISOString()) // offset 400, rtt 800 — ignored

    telemetry.recordDelivery(new Date(1000).toISOString(), 1040)
    await telemetry.flush()

    const body = JSON.parse((post.mock.calls[0] as unknown as [string, RequestInit])[1].body as string) as {
      buckets: Record<string, number>
    }
    expect(body.buckets).toEqual({ '50': 1 })
  })
})

describe('reporting', () => {
  function primed() {
    const r = reporter()
    r.telemetry.observeRoundTrip(0, 2, new Date(1).toISOString())
    return r
  }

  it('buckets into the fixed bounds, with +Inf beyond the last', async () => {
    const { telemetry, post } = primed()

    for (const lag of [3, 7, 240, 300, 301, 9000]) {
      telemetry.recordDelivery(new Date(10_000).toISOString(), 10_000 + lag)
    }

    await telemetry.flush()

    const [path, init] = post.mock.calls[0] as unknown as [string, RequestInit]
    expect(path).toBe('/telemetry/delivery')
    expect(init.method).toBe('POST')

    const body = JSON.parse(init.body as string) as { buckets: Record<string, number>; transport: string }
    expect(body.buckets).toEqual({ '5': 1, '10': 1, '300': 2, '500': 1, '+Inf': 1 })
    expect(body.transport).toBe('webSockets')
  })

  it('carries nothing that identifies a person, a conversation, or a message', async () => {
    const { telemetry, post } = primed()
    telemetry.recordDelivery(new Date(10_000).toISOString(), 10_050)
    await telemetry.flush()

    const body = JSON.parse((post.mock.calls[0] as unknown as [string, RequestInit])[1].body as string) as object
    expect(Object.keys(body).sort()).toEqual(['buckets', 'clockOffsetMs', 'transport', 'windowSeconds'])
  })

  it('reports the current transport', async () => {
    const { telemetry, post } = primed()
    telemetry.setTransport('longPolling')
    telemetry.recordDelivery(new Date(10_000).toISOString(), 10_050)
    await telemetry.flush()

    const body = JSON.parse((post.mock.calls[0] as unknown as [string, RequestInit])[1].body as string) as {
      transport: string
    }
    expect(body.transport).toBe('longPolling')
  })

  it('sends nothing for an empty window', async () => {
    const { telemetry, post } = primed()
    await telemetry.flush()
    expect(post).not.toHaveBeenCalled()
  })

  it('drops a failed report rather than retrying it', async () => {
    const post = vi.fn(() => Promise.reject(new Error('offline')))
    const { telemetry } = reporter(post)
    telemetry.observeRoundTrip(0, 2, new Date(1).toISOString())
    telemetry.recordDelivery(new Date(10_000).toISOString(), 10_050)

    await expect(telemetry.flush()).resolves.toBeUndefined()
    await telemetry.flush()

    expect(post).toHaveBeenCalledTimes(1)
  })

  it('flushes on its interval once started, and not after stop', async () => {
    vi.useFakeTimers()
    const { telemetry, post } = primed()

    telemetry.start()
    telemetry.recordDelivery(new Date(10_000).toISOString(), 10_050)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(post).toHaveBeenCalledTimes(1)

    telemetry.stop()
    telemetry.recordDelivery(new Date(10_000).toISOString(), 10_050)
    await vi.advanceTimersByTimeAsync(120_000)
    expect(post).toHaveBeenCalledTimes(1)
  })
})
