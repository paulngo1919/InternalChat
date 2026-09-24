/**
 * T156 — the client-side half of FR-023.
 *
 * The server is the authority and refuses anything that gets past this. What is tested here is the
 * part the server cannot do: refusing *before* the upload, with the limit stated. A regression in
 * this module would not produce a security hole — it would produce a 600 MB upload that fails after
 * several minutes instead of instantly, which is precisely the experience FR-023 rules out.
 */

import { describe, expect, it } from 'vitest'

import {
  ALLOWED_CONTENT_TYPES,
  MAXIMUM_IMAGE_BYTES,
  MAXIMUM_VIDEO_BYTES,
  MAXIMUM_VIDEO_DURATION_SECONDS,
  formatBytes,
  kindOf,
  maximumBytesFor,
  normalizeContentType,
  rejectionFor,
} from '../src/features/attachments/fileConstraints'

/** A File of a given declared size, without allocating the bytes. */
function fileOf(name: string, type: string, size: number): File {
  const file = new File([], name, { type })

  // `size` is read-only on File and derives from the parts. Overridden rather than allocating
  // 500 MB of zeroes, which is what a faithful construction would cost for the boundary cases.
  Object.defineProperty(file, 'size', { value: size })

  return file
}

describe('kind detection', () => {
  it.each(ALLOWED_CONTENT_TYPES.image)('treats %s as an image', (type) => {
    expect(kindOf(type)).toBe('image')
  })

  it.each(ALLOWED_CONTENT_TYPES.video)('treats %s as a video', (type) => {
    expect(kindOf(type)).toBe('video')
  })

  it.each(['image/svg+xml', 'text/html', 'application/pdf', 'video/quicktime', ''])(
    'refuses %s',
    (type) => {
      expect(kindOf(type)).toBeNull()
    },
  )

  it('matches case-insensitively and ignores parameters', () => {
    // RFC 9110. A browser that appends a charset is not attacking anything, and refusing it would
    // be a defect visible in one browser only.
    expect(kindOf('IMAGE/PNG')).toBe('image')
    expect(kindOf('image/png; charset=binary')).toBe('image')
    expect(normalizeContentType('  Image/PNG ; q=1 ')).toBe('image/png')
  })

  it('refuses a value that is not a media type at all', () => {
    expect(normalizeContentType('png')).toBeNull()
    expect(normalizeContentType(null)).toBeNull()
    expect(normalizeContentType(undefined)).toBeNull()
  })
})

describe('size limits', () => {
  it('accepts an image at the limit and refuses one byte over', () => {
    expect(rejectionFor(fileOf('a.png', 'image/png', MAXIMUM_IMAGE_BYTES))).toBeNull()

    const rejection = rejectionFor(fileOf('a.png', 'image/png', MAXIMUM_IMAGE_BYTES + 1))

    expect(rejection?.reason).toBe('size')

    // FR-023: the refusal names the limit. Without it the person guesses how much to shrink by.
    expect(rejection?.message).toContain('25 MB')
  })

  it('applies the limit per kind rather than globally', () => {
    const hundredMegabytes = 100 * 1024 * 1024

    // The whole point of per-kind: 100 MB is a legitimate video and an absurd image. One global
    // ceiling would have to be the video one, which would wave through a 100 MB PNG.
    expect(rejectionFor(fileOf('a.png', 'image/png', hundredMegabytes))?.reason).toBe('size')
    expect(rejectionFor(fileOf('a.mp4', 'video/mp4', hundredMegabytes))).toBeNull()
  })

  it('accepts a video at the limit and refuses one byte over', () => {
    expect(rejectionFor(fileOf('v.mp4', 'video/mp4', MAXIMUM_VIDEO_BYTES))).toBeNull()
    expect(rejectionFor(fileOf('v.mp4', 'video/mp4', MAXIMUM_VIDEO_BYTES + 1))?.reason).toBe('size')
  })

  it('refuses an empty file', () => {
    expect(rejectionFor(fileOf('a.png', 'image/png', 0))?.reason).toBe('size')
  })

  it('exposes the ceiling without needing a violation to learn it', () => {
    expect(maximumBytesFor('image')).toBe(MAXIMUM_IMAGE_BYTES)
    expect(maximumBytesFor('video')).toBe(MAXIMUM_VIDEO_BYTES)
  })
})

describe('type refusal', () => {
  it('names what is accepted instead', () => {
    const rejection = rejectionFor(fileOf('note.pdf', 'application/pdf', 1024))

    expect(rejection?.reason).toBe('type')
    expect(rejection?.message).toContain('image/png')
    expect(rejection?.message).toContain('video/mp4')
  })

  it('refuses an SVG, which no size limit would have caught', () => {
    // An SVG is a document with script. Serving one from our own origin is stored XSS, and a
    // malware scan does not help — it looks for signatures, not for an onload attribute.
    expect(rejectionFor(fileOf('logo.svg', 'image/svg+xml', 512))?.reason).toBe('type')
  })
})

describe('video duration', () => {
  it('accepts a video at the duration limit and refuses one second over', () => {
    const file = fileOf('v.mp4', 'video/mp4', 1024)

    expect(rejectionFor(file, MAXIMUM_VIDEO_DURATION_SECONDS)).toBeNull()

    const rejection = rejectionFor(file, MAXIMUM_VIDEO_DURATION_SECONDS + 1)

    expect(rejection?.reason).toBe('duration')
    expect(rejection?.message).toContain('600')
  })

  it('ignores a duration supplied for an image', () => {
    // A client that sends a duration for everything is sloppy, not hostile, and an image has no
    // duration to violate. Refusing would turn a harmless extra field into a failed upload.
    expect(rejectionFor(fileOf('a.png', 'image/png', 1024), 42)).toBeNull()
  })

  it('does not refuse a video whose duration is not yet known', () => {
    // The browser reports duration asynchronously. Blocking the upload until it arrives would
    // stall every video pick; the server refuses a duration-less video regardless, which is where
    // the rule is actually enforced.
    expect(rejectionFor(fileOf('v.mp4', 'video/mp4', 1024))).toBeNull()
  })
})

describe('byte formatting', () => {
  it.each([
    [25 * 1024 * 1024, '25 MB'],
    [500 * 1024 * 1024, '500 MB'],
    [1536, '2 KB'],
  ])('renders %i as %s', (bytes, expected) => {
    expect(formatBytes(bytes)).toBe(expected)
  })
})
