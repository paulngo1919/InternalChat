/**
 * 002 T049 — reports client-observed delivery lag, in aggregate (FR-011,
 * contracts/delivery-telemetry.openapi.yaml).
 *
 * The server measures delivery up to the hub send; only the browser sees the rest — the network,
 * the backplane hop to this socket, and the frame arriving. This counts every live
 * `MessageReceived` into fixed buckets and posts the counts once a minute. Nothing identifying goes
 * with them: no ids, no names, no content.
 *
 * **The clock problem.** Lag is `receivedAt − sentAt` across the browser's clock and the server's.
 * The offset between them is estimated from this employee's own sends: the response carries the
 * server's `sentAt` to the millisecond, and assuming it was stamped halfway through the round trip
 * bounds the error at half the round trip. The fastest round trip seen is kept, because it bounds
 * the error most tightly. Until one send has been made there is no estimate, and nothing is
 * reported — a skewed number is worse than none.
 *
 * Best-effort throughout: a failed post is dropped, never retried, and never surfaced.
 */

import type { ChatTransport } from './chatConnection'

/** Upper bounds in ms — the server's bucket boundaries (002 data-model §6). */
const BOUNDS = [5, 10, 25, 50, 100, 200, 300, 500, 1000, 2000, 5000] as const

const OVERFLOW = '+Inf'

/** How often counts are posted. The server allows two a minute per employee. */
const FLUSH_INTERVAL_MS = 60_000

/** Posts a report. Given the authorized fetch, so the token handling is shared with every other call. */
export type TelemetryPost = (path: string, init: RequestInit) => Promise<Response>

export class DeliveryTelemetry {
  private readonly post: TelemetryPost

  /** Client clock minus server clock, in ms. Undefined until an own send has been timed. */
  private offsetMs: number | undefined

  private bestRoundTripMs = Number.POSITIVE_INFINITY
  private counts = new Map<string, number>()
  private transport: ChatTransport = 'webSockets'
  private windowStartedAt = Date.now()
  private timer: ReturnType<typeof setInterval> | undefined

  constructor(post: TelemetryPost) {
    this.post = post
  }

  /** Starts the once-a-minute flush. */
  start(): void {
    this.stop()
    this.windowStartedAt = Date.now()
    this.timer = setInterval(() => {
      void this.flush()
    }, FLUSH_INTERVAL_MS)
  }

  /** Stops flushing. Counts still held are discarded with the screen. */
  stop(): void {
    if (this.timer !== undefined) {
      clearInterval(this.timer)
      this.timer = undefined
    }
  }

  /** The transport the next report is attributed to (from `ConnectionInfo`). */
  setTransport(transport: ChatTransport): void {
    this.transport = transport
  }

  /**
   * One timed request whose response carried a server timestamp — an own message's `sentAt`.
   *
   * @param startedAt Client time just before the request.
   * @param finishedAt Client time just after the response.
   * @param serverTime The server's timestamp from the response.
   */
  observeRoundTrip(startedAt: number, finishedAt: number, serverTime: string): void {
    const server = Date.parse(serverTime)
    const roundTrip = finishedAt - startedAt

    if (Number.isNaN(server) || roundTrip < 0 || roundTrip >= this.bestRoundTripMs) {
      return
    }

    this.bestRoundTripMs = roundTrip
    this.offsetMs = Math.round(startedAt + roundTrip / 2 - server)
  }

  /** A message pushed live — not recovered by `Resync`, whose lag is the outage, not delivery. */
  recordDelivery(sentAt: string, receivedAt: number = Date.now()): void {
    if (this.offsetMs === undefined) {
      return
    }

    const sent = Date.parse(sentAt)
    if (Number.isNaN(sent)) {
      return
    }

    // Clamped at zero: a small negative is estimate error, and the server would clamp it anyway.
    const lag = Math.max(0, receivedAt - this.offsetMs - sent)
    const bound = BOUNDS.find((b) => lag <= b)
    const key = bound === undefined ? OVERFLOW : String(bound)

    this.counts.set(key, (this.counts.get(key) ?? 0) + 1)
  }

  /** Posts and clears the current window. Resolves whether or not the post succeeded. */
  async flush(): Promise<void> {
    if (this.counts.size === 0 || this.offsetMs === undefined) {
      return
    }

    const buckets = Object.fromEntries(this.counts)
    const windowSeconds = Math.min(300, Math.max(1, Math.round((Date.now() - this.windowStartedAt) / 1000)))

    this.counts = new Map()
    this.windowStartedAt = Date.now()

    try {
      await this.post('/telemetry/delivery', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          transport: this.transport,
          windowSeconds,
          clockOffsetMs: this.offsetMs,
          buckets,
        }),
      })
    } catch {
      // Best-effort by contract. The next window starts clean.
    }
  }
}
