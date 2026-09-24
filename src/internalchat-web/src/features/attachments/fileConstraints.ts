/**
 * The per-kind limits, mirrored from the server's `FileConstraints` (FR-023).
 *
 * Duplicated on purpose, and the duplication is not a smell. The server is the authority — it
 * refuses anything that gets past this — but a client that only learned the limit from a 413 would
 * have to attempt the upload to discover it, which is precisely what FR-023 says must not happen:
 * "reject violations **before** upload with the limit stated".
 *
 * These values must track `src/InternalChat.Domain/Attachments/FileConstraints.cs`. When they
 * drift, the failure is benign in one direction (the server refuses something this allowed, and the
 * user sees a clear error) and merely annoying in the other (this refuses something the server
 * would have taken).
 */

/** What an attachment is. Decides which limits apply. */
export type AttachmentKind = 'image' | 'video'

/** Largest permitted image, in bytes (25 MB). */
export const MAXIMUM_IMAGE_BYTES = 25 * 1024 * 1024

/** Largest permitted video, in bytes (500 MB). */
export const MAXIMUM_VIDEO_BYTES = 500 * 1024 * 1024

/** Longest permitted video, in seconds. */
export const MAXIMUM_VIDEO_DURATION_SECONDS = 600

/**
 * Content types accepted per kind.
 *
 * `image/svg+xml` is absent deliberately: an SVG is a document with script, so a stored one served
 * from our origin is stored XSS — and a malware scan does not help, because it looks for signatures
 * rather than for an `onload` attribute.
 */
export const ALLOWED_CONTENT_TYPES: Readonly<Record<AttachmentKind, readonly string[]>> = {
  image: ['image/png', 'image/jpeg', 'image/gif', 'image/webp'],
  video: ['video/mp4', 'video/webm'],
}

/** The size ceiling for a kind. */
export function maximumBytesFor(kind: AttachmentKind): number {
  return kind === 'image' ? MAXIMUM_IMAGE_BYTES : MAXIMUM_VIDEO_BYTES
}

/** Renders a byte count the way a person reads one. */
export function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024 * 1024) {
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`
  }

  if (bytes >= 1024 * 1024) {
    return `${(bytes / (1024 * 1024)).toFixed(bytes % (1024 * 1024) === 0 ? 0 : 1)} MB`
  }

  return `${String(Math.max(1, Math.round(bytes / 1024)))} KB`
}

/** Renders a duration the way a player's scrub bar does. */
export function formatDuration(seconds: number): string {
  const whole = Math.max(0, Math.round(seconds))
  const minutes = Math.floor(whole / 60)
  const remainder = whole % 60

  return `${String(minutes)}:${remainder.toString().padStart(2, '0')}`
}

/** Which kind a media type belongs to, or `null` when it belongs to neither. */
export function kindOf(contentType: string): AttachmentKind | null {
  const essence = normalizeContentType(contentType)

  if (essence === null) {
    return null
  }

  if (ALLOWED_CONTENT_TYPES.image.includes(essence)) {
    return 'image'
  }

  return ALLOWED_CONTENT_TYPES.video.includes(essence) ? 'video' : null
}

/**
 * Reduces a media type to its essence — lowercased, parameters dropped.
 *
 * Per RFC 9110 the type and subtype are case-insensitive and parameters are not part of the type's
 * identity, so `image/PNG; charset=binary` is `image/png`. Browsers do occasionally hand back a
 * type with a parameter, and refusing it would look like a bug in one browser only.
 */
export function normalizeContentType(contentType: string | null | undefined): string | null {
  if (!contentType) {
    return null
  }

  const essence = contentType.split(';')[0]?.trim().toLowerCase() ?? ''

  return essence.includes('/') ? essence : null
}

/** Why a file was refused, phrased for the person who chose it. */
export interface Rejection {
  readonly reason: 'type' | 'size' | 'duration'
  readonly message: string
}

/**
 * Checks a file against the limits for its kind, before any bytes are sent.
 *
 * Returns `null` when the file is acceptable. Every message names the limit, because a refusal that
 * does not leaves someone guessing how much smaller the file has to be.
 */
export function rejectionFor(file: File, durationSeconds?: number | null): Rejection | null {
  const kind = kindOf(file.type)

  if (kind === null) {
    return {
      reason: 'type',
      message:
        `${file.name} is a ${file.type || 'unknown'} file. You can send ` +
        `${ALLOWED_CONTENT_TYPES.image.join(', ')}, ${ALLOWED_CONTENT_TYPES.video.join(' or ')}.`,
    }
  }

  const limit = maximumBytesFor(kind)

  if (file.size > limit) {
    return {
      reason: 'size',
      message:
        `${file.name} is ${formatBytes(file.size)}. The limit for a ${kind} is ` +
        `${formatBytes(limit)}.`,
    }
  }

  if (file.size <= 0) {
    return { reason: 'size', message: `${file.name} is empty.` }
  }

  if (kind === 'video' && typeof durationSeconds === 'number') {
    if (durationSeconds > MAXIMUM_VIDEO_DURATION_SECONDS) {
      return {
        reason: 'duration',
        message:
          `${file.name} is ${String(Math.round(durationSeconds))} seconds long. The limit is ` +
          `${String(MAXIMUM_VIDEO_DURATION_SECONDS)} seconds.`,
      }
    }
  }

  return null
}
