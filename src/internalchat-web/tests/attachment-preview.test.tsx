import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ImagePreview } from '../src/features/attachments/ImagePreview'
import { VideoPlayer } from '../src/features/attachments/VideoPlayer'
import type { AttachmentResponse } from '../src/lib/api/messages'
import { at } from './at'

/**
 * Inline attachment rendering (T157, T177 — FR-021, FR-022, FR-024).
 *
 * <b>The scanning state is the reason `ImagePreview` exists.</b> FR-024 makes it unavoidable —
 * nothing is retrievable before a clean verdict — so a preview that only knew "image" and "broken
 * image" would show a broken image for a second or two on every single send. The three refused
 * states each say something different, and which one is shown is the difference between "wait" and
 * "this file is never coming".
 *
 * <b>`preload="metadata"` is the whole of FR-022 on the client side.</b> The default, `auto`, tells
 * the browser to buffer the entire file on mount, so a conversation with six videos in view starts
 * six 400 MB downloads nobody asked for — and the feature is defeated by markup rather than by the
 * transport everything else was built around.
 */

function anAttachment(overrides: Partial<AttachmentResponse> = {}): AttachmentResponse {
  return {
    id: 'a1',
    kind: 'image',
    contentType: 'image/png',
    byteSize: 2048,
    durationSeconds: null,
    fileName: 'shot.png',
    scanStatus: 'clean',
    contentUrl: '/api/v1/attachments/a1/content',
    posterUrl: null,
    ...overrides,
  }
}

afterEach(cleanup)

describe('ImagePreview — scan states', () => {
  it('renders nothing when a message has no attachments', () => {
    const { container } = render(<ImagePreview attachments={[]} />)

    expect(container).toBeEmptyDOMElement()
  })

  it('says a pending attachment is being scanned', () => {
    render(<ImagePreview attachments={[anAttachment({ scanStatus: 'pending', contentUrl: null })]} />)

    // A correct wait made explicable. Without this the person sees a broken image and concludes the
    // upload failed.
    expect(screen.getByText(/Scanning shot\.png/)).toBeInTheDocument()
  })

  it('states plainly that an infected attachment was blocked', () => {
    render(
      <ImagePreview attachments={[anAttachment({ scanStatus: 'infected', contentUrl: null })]} />,
    )

    // The person may have been expecting this file. Silence would look like a failed upload rather
    // than a deliberate block (FR-024).
    expect(screen.getByRole('alert')).toHaveTextContent('blocked by a malware scan')
  })

  it('distinguishes a scan that failed from one that found something', () => {
    render(<ImagePreview attachments={[anAttachment({ scanStatus: 'failed', contentUrl: null })]} />)

    // "Could not be scanned" is a different thing to do about than "was blocked" — one is worth
    // retrying and the other never will be.
    expect(screen.getByText(/could not be scanned/)).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('treats a clean attachment with no content URL as unavailable', () => {
    render(<ImagePreview attachments={[anAttachment({ scanStatus: 'clean', contentUrl: null })]} />)

    // A clean verdict with nothing to serve is a server-side inconsistency. Rendering an <img> with
    // a null src would produce a request to the current page URL.
    expect(screen.getByText(/could not be scanned/)).toBeInTheDocument()
  })

  it('renders a clean image with the uploader file name as alt text', () => {
    render(<ImagePreview attachments={[anAttachment()]} />)

    const image = screen.getByRole('img', { name: 'shot.png' })

    expect(image).toHaveAttribute('src', '/api/v1/attachments/a1/content')

    // Lazy, so a conversation full of images does not fetch all of them on mount.
    expect(image).toHaveAttribute('loading', 'lazy')
  })
})

describe('ImagePreview — polling', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('polls only while something is pending', async () => {
    const refresh = vi.fn(() => Promise.resolve(anAttachment()))

    render(<ImagePreview attachments={[anAttachment()]} refresh={refresh} />)

    await act(async () => {
      vi.advanceTimersByTime(10_000)
    })

    // An unconditional poll would issue one request every two seconds per rendered message, on a
    // conversation full of images.
    expect(refresh).not.toHaveBeenCalled()
  })

  it('polls a pending attachment and overlays the newer verdict', async () => {
    const pending = anAttachment({ scanStatus: 'pending', contentUrl: null })
    const refresh = vi.fn(() => Promise.resolve(anAttachment()))

    render(<ImagePreview attachments={[pending]} refresh={refresh} />)

    await act(async () => {
      vi.advanceTimersByTime(2_000)
    })

    await waitFor(() => {
      expect(screen.getByRole('img', { name: 'shot.png' })).toBeInTheDocument()
    })

    expect(refresh).toHaveBeenCalledWith('a1')
  })

  it('stops polling once the verdict arrives', async () => {
    const refresh = vi.fn(() => Promise.resolve(anAttachment()))

    render(
      <ImagePreview
        attachments={[anAttachment({ scanStatus: 'pending', contentUrl: null })]}
        refresh={refresh}
      />,
    )

    await act(async () => {
      vi.advanceTimersByTime(2_000)
    })

    await waitFor(() => {
      expect(screen.getByRole('img', { name: 'shot.png' })).toBeInTheDocument()
    })

    const afterVerdict = refresh.mock.calls.length

    await act(async () => {
      vi.advanceTimersByTime(20_000)
    })

    expect(refresh).toHaveBeenCalledTimes(afterVerdict)
  })

  it('gives up rather than polling forever', async () => {
    // Found a real defect: every poll called setPolled with a fresh Map, which gave the effect a
    // new dependency identity, which restarted it along with its deadline. The timeout could never
    // be reached, so a stuck scan was polled for as long as the tab stayed open.
    const refresh = vi.fn(() => Promise.resolve(anAttachment({ scanStatus: 'pending' })))

    render(
      <ImagePreview
        attachments={[anAttachment({ scanStatus: 'pending', contentUrl: null })]}
        refresh={refresh}
      />,
    )

    await act(async () => {
      vi.advanceTimersByTime(121_000)
    })

    const afterTimeout = refresh.mock.calls.length

    await act(async () => {
      vi.advanceTimersByTime(20_000)
    })

    // A scan this slow means something is wrong with the scanner, not with this file. Polling
    // forever would keep a tab busy indefinitely for an answer that is not coming.
    expect(refresh).toHaveBeenCalledTimes(afterTimeout)
  })

  it('survives a failed poll and keeps trying', async () => {
    const refresh = vi
      .fn()
      .mockRejectedValueOnce(new Error('network'))
      .mockResolvedValue(anAttachment())

    render(
      <ImagePreview
        attachments={[anAttachment({ scanStatus: 'pending', contentUrl: null })]}
        refresh={refresh}
      />,
    )

    await act(async () => {
      vi.advanceTimersByTime(4_000)
    })

    // An error banner for a transient metadata read would be noisier than the wait it describes.
    await waitFor(() => {
      expect(screen.getByRole('img', { name: 'shot.png' })).toBeInTheDocument()
    })
  })

  it('does not poll at all without a refresh function', async () => {
    render(<ImagePreview attachments={[anAttachment({ scanStatus: 'pending', contentUrl: null })]} />)

    await act(async () => {
      vi.advanceTimersByTime(10_000)
    })

    // A context that cannot poll leaves the attachment pending until the next render from
    // elsewhere, rather than crashing on an undefined call.
    expect(screen.getByText(/Scanning/)).toBeInTheDocument()
  })

  it('lets the props win over a stale overlay', async () => {
    const refresh = vi.fn(() => Promise.resolve(anAttachment({ fileName: 'from-poll.png' })))

    const { rerender } = render(
      <ImagePreview
        attachments={[anAttachment({ scanStatus: 'pending', contentUrl: null })]}
        refresh={refresh}
      />,
    )

    await act(async () => {
      vi.advanceTimersByTime(2_000)
    })

    await waitFor(() => {
      expect(screen.getByRole('img', { name: 'from-poll.png' })).toBeInTheDocument()
    })

    rerender(<ImagePreview attachments={[]} refresh={refresh} />)

    // Polled results are an overlay on the props, not a copy of them. Copying the prop into state
    // would leave this rendering an attachment the parent no longer has.
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })
})

describe('ImagePreview — lightbox', () => {
  it('opens in a modal rather than leaving the conversation', () => {
    render(<ImagePreview attachments={[anAttachment()]} />)

    fireEvent.click(screen.getByRole('button'))

    const dialog = screen.getByRole('dialog')

    // Opening the content URL directly would work — it is authorized per request — but it leaves
    // the conversation, and somebody clicking a screenshot expects to come back to it.
    expect(dialog).toHaveAttribute('aria-modal', 'true')
    expect(dialog).toHaveAccessibleName('shot.png')
  })

  it('closes on Escape', () => {
    render(<ImagePreview attachments={[anAttachment()]} />)

    fireEvent.click(screen.getByRole('button'))
    expect(screen.getByRole('dialog')).toBeInTheDocument()

    fireEvent.keyDown(window, { key: 'Escape' })

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('ignores other keys', () => {
    render(<ImagePreview attachments={[anAttachment()]} />)

    fireEvent.click(screen.getByRole('button'))
    fireEvent.keyDown(window, { key: 'a' })

    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('closes on the close button and on the backdrop', () => {
    const { container } = render(<ImagePreview attachments={[anAttachment()]} />)

    const open = () => {
      fireEvent.click(at(screen.getAllByRole('button'), 0))
    }

    open()

    // Two elements are named "Close": the visible button and the backdrop, which carries the same
    // label because it does the same thing. Selected by class rather than by name, since the
    // ambiguity is deliberate.
    fireEvent.click(container.querySelector('.lightbox__close') as HTMLElement)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    open()

    // A button rather than a click handler on a div. Clicking outside to close is expected, but a
    // div that responds to a mouse and not to a keyboard is unusable without one.
    const backdrop = container.querySelector('.lightbox__backdrop') as HTMLElement

    expect(backdrop.tagName).toBe('BUTTON')

    fireEvent.click(backdrop)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('removes the key listener once closed', () => {
    const remove = vi.spyOn(window, 'removeEventListener')

    render(<ImagePreview attachments={[anAttachment()]} />)

    fireEvent.click(screen.getByRole('button'))
    fireEvent.keyDown(window, { key: 'Escape' })

    // A listener left behind on every image anyone ever opened would accumulate for the life of
    // the tab.
    expect(remove).toHaveBeenCalledWith('keydown', expect.any(Function) as unknown)
  })
})

describe('VideoPlayer', () => {
  const video = anAttachment({
    id: 'a2',
    kind: 'video',
    contentType: 'video/mp4',
    fileName: 'demo.mp4',
    byteSize: 400 * 1024 * 1024,
    durationSeconds: 125,
    contentUrl: '/api/v1/attachments/a2/content',
  })

  it('never buffers more than metadata on mount', () => {
    render(<VideoPlayer attachment={video} />)

    // THE line. `auto` would start buffering every video in the conversation on mount, which is
    // FR-022 defeated by markup rather than by the transport.
    expect(screen.getByTestId('attachment-video')).toHaveAttribute('preload', 'metadata')
  })

  it('uses a generated poster when there is one, and none otherwise', () => {
    const { rerender } = render(<VideoPlayer attachment={video} />)

    // No placeholder image: a generic film icon tells the person less than the browser's own first
    // frame does.
    expect(screen.getByTestId('attachment-video')).not.toHaveAttribute('poster')

    rerender(<VideoPlayer attachment={{ ...video, posterUrl: '/api/v1/attachments/a2/poster' }} />)

    expect(screen.getByTestId('attachment-video')).toHaveAttribute(
      'poster',
      '/api/v1/attachments/a2/poster',
    )
  })

  it('declares the absence of captions rather than claiming they exist', () => {
    const { container } = render(<VideoPlayer attachment={video} />)

    const track = container.querySelector('track')

    expect(track).toHaveAttribute('kind', 'captions')
    expect(track).not.toHaveAttribute('src')
  })

  it('shows the file name, size and duration', () => {
    render(<VideoPlayer attachment={video} />)

    const caption = screen.getByText(/demo\.mp4/)

    expect(caption).toHaveTextContent('400 MB')
    expect(caption).toHaveTextContent('2:05')
  })

  it('omits the duration when the probe produced none', () => {
    render(<VideoPlayer attachment={{ ...video, durationSeconds: null }} />)

    expect(screen.getByText(/demo\.mp4/).textContent).not.toMatch(/\d+:\d\d/)
  })

  it('offers a download when the browser cannot play it', () => {
    render(<VideoPlayer attachment={video} />)

    fireEvent.error(screen.getByTestId('attachment-video'))

    const alert = screen.getByRole('alert')

    expect(alert).toHaveTextContent('cannot be played in this browser')

    // The same authorized route. Following it re-checks membership exactly as the player's own
    // request does — it is a route, not a capability — so offering it takes nothing from FR-025.
    const link = screen.getByRole('link')

    expect(link).toHaveAttribute('href', '/api/v1/attachments/a2/content')
    expect(link).toHaveAttribute('download', 'demo.mp4')
  })

  it('renders nothing without a content URL', () => {
    const { container } = render(<VideoPlayer attachment={{ ...video, contentUrl: null }} />)

    expect(container).toBeEmptyDOMElement()
  })

  it('is reached through ImagePreview for a video attachment', () => {
    render(<ImagePreview attachments={[video]} />)

    // Delegated rather than inlined. Duplicating preload, the poster fallback and the unplayable
    // path here is how one of the two copies quietly loses one of them.
    expect(screen.getByTestId('attachment-video')).toBeInTheDocument()
  })
})
