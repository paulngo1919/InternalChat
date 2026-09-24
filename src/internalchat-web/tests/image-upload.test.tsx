import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  ImageUpload,
  type PendingAttachment,
  type UploadApi,
} from '../src/features/attachments/ImageUpload'
import { at } from './at'

/**
 * Image and video upload (T156, FR-021, FR-023, FR-026).
 *
 * <b>The assertion that matters most is that bytes never go through the API.</b> The ticket is a
 * presigned PUT straight to storage; a 500 MB video routed through .NET would occupy a request
 * thread for minutes on a host whose budget belongs to messaging (research.md D7). It is also the
 * easiest thing to regress, because routing the upload through the API would work perfectly in
 * every manual test anyone would run.
 *
 * Second is that a bearer token is never attached to the storage request. The presigned URL carries
 * its own signature, and sending the token there would hand it to MinIO's access log.
 */

/** A controlled wrapper, since the component is presentational over a caller-owned list. */
function Harness({
  api,
  initial = [],
  disabled,
}: {
  api: UploadApi
  initial?: readonly PendingAttachment[]
  disabled?: boolean
}) {
  const [attachments, setAttachments] = useState<readonly PendingAttachment[]>(initial)

  return (
    <ImageUpload
      conversationId="c1"
      api={api}
      attachments={attachments}
      onChange={setAttachments}
      disabled={disabled}
    />
  )
}

/** Captures every XMLHttpRequest the component makes, and lets the test drive it. */
interface FakeXhr {
  method: string
  url: string
  headers: Record<string, string>
  body: unknown
  status: number
  listeners: Map<string, (event: unknown) => void>
  uploadListeners: Map<string, (event: unknown) => void>
  fire(type: string, event?: unknown): void
  fireProgress(loaded: number, total: number): void
}

const requests: FakeXhr[] = []

function installXhr() {
  requests.length = 0

  class Fake implements FakeXhr {
    method = ''
    url = ''
    headers: Record<string, string> = {}
    body: unknown = null
    status = 200
    listeners = new Map<string, (event: unknown) => void>()
    uploadListeners = new Map<string, (event: unknown) => void>()

    upload = {
      addEventListener: (type: string, listener: (event: unknown) => void) => {
        this.uploadListeners.set(type, listener)
      },
    }

    constructor() {
      requests.push(this)
    }

    open(method: string, url: string) {
      this.method = method
      this.url = url
    }

    setRequestHeader(name: string, value: string) {
      this.headers[name] = value
    }

    addEventListener(type: string, listener: (event: unknown) => void) {
      this.listeners.set(type, listener)
    }

    send(body: unknown) {
      this.body = body
    }

    fire(type: string, event: unknown = {}) {
      this.listeners.get(type)?.(event)
    }

    fireProgress(loaded: number, total: number) {
      this.uploadListeners.get('progress')?.({ lengthComputable: true, loaded, total })
    }
  }

  vi.stubGlobal('XMLHttpRequest', Fake)
}

/** A file of a given type and size. */
function aFile(name: string, type: string, size = 1024): File {
  const file = new File(['x'], name, { type })

  Object.defineProperty(file, 'size', { value: size })

  return file
}

/** Chooses files through the hidden input. */
function choose(files: File[]) {
  const input = document.querySelector('input[type="file"]') as HTMLInputElement

  fireEvent.change(input, { target: { files } })
}

function fakeApi(overrides: Partial<UploadApi> = {}): UploadApi {
  return {
    requestUpload: vi.fn(() =>
      Promise.resolve({
        attachmentId: 'a1',
        uploadUrl: 'https://minio.test/quarantine/c1/a1?X-Amz-Signature=abc',
        expiresAt: '2026-09-22T10:00:00Z',
      }),
    ),
    ...overrides,
  }
}

beforeEach(() => {
  installXhr()

  // Only the two members the component uses are replaced. Spreading `URL` itself would drop
  // its prototype, and `new URL(...)` is used elsewhere in the tree.
  // Assigned rather than spread or spied. `crypto` is a class instance, so spreading it would
  // drop the prototype and take every other Web Crypto method with it — including the ones the
  // PKCE helpers use. And jsdom implements neither object-URL method at all, so there is
  // nothing to spy on until they exist.
  let nextId = 0

  vi.spyOn(globalThis.crypto, 'randomUUID').mockImplementation(
    () => `00000000-0000-4000-8000-${String(++nextId).padStart(12, '0')}`,
  )

  URL.createObjectURL = vi.fn(() => 'blob:preview')
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()

  Reflect.deleteProperty(URL, 'createObjectURL')
  Reflect.deleteProperty(URL, 'revokeObjectURL')

  cleanup()
})

describe('validation before any request', () => {
  it('refuses an oversized image locally, stating the limit', async () => {
    const api = fakeApi()

    render(<Harness api={api} />)

    // 26 MB against a 25 MB ceiling. FR-023 wants the refusal before the upload, and the local
    // check makes it instant instead of a round trip that transfers nothing useful.
    choose([aFile('huge.png', 'image/png', 26 * 1024 * 1024)])

    expect(await screen.findByRole('status')).toHaveTextContent(/25/)
    expect(api.requestUpload).not.toHaveBeenCalled()
    expect(requests).toHaveLength(0)
  })

  it('refuses a type neither allow-list accepts', async () => {
    const api = fakeApi()

    render(<Harness api={api} />)

    // SVG is excluded deliberately — it is a script-bearing document, not an image.
    choose([aFile('chart.svg', 'image/svg+xml')])

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).not.toBe('')
    })

    expect(api.requestUpload).not.toHaveBeenCalled()
  })

  it('adds no row for a refused file', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('notes.pdf', 'application/pdf')])

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).not.toBe('')
    })

    // A row for a file that was never accepted is a row the person then has to dismiss.
    expect(screen.queryByRole('listitem')).not.toBeInTheDocument()
  })
})

describe('uploading', () => {
  it('reserves a ticket, then PUTs the bytes straight to storage', async () => {
    const api = fakeApi()

    render(<Harness api={api} />)

    const file = aFile('shot.png', 'image/png')

    choose([file])

    await waitFor(() => {
      expect(api.requestUpload).toHaveBeenCalledWith('c1', {
        kind: 'image',
        contentType: 'image/png',
        byteSize: 1024,
        fileName: 'shot.png',
      })
    })

    await waitFor(() => {
      expect(requests).toHaveLength(1)
    })

    // Straight to MinIO. Routing this through the API would work in every manual test and would
    // occupy a .NET request thread for the length of a 500 MB transfer in production.
    expect(at(requests, 0).method).toBe('PUT')
    expect(at(requests, 0).url).toContain('minio.test/quarantine')
    expect(at(requests, 0).body).toBe(file)
  })

  it('sends no authorization header to storage', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    await waitFor(() => {
      expect(requests).toHaveLength(1)
    })

    // The URL carries its own signature. A bearer token sent here lands in MinIO's access log, on
    // a host that has no business holding one.
    expect(Object.keys(at(requests, 0).headers)).toEqual(['Content-Type'])
    expect(at(requests, 0).headers['Content-Type']).toBe('image/png')
  })

  it('reports progress while the bytes move', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    await waitFor(() => {
      expect(requests).toHaveLength(1)
    })

    at(requests, 0).fireProgress(512, 1024)

    // FR-026. `fetch` still cannot report upload progress in any shipping browser, which is the
    // whole reason XMLHttpRequest is used here.
    const bar = await screen.findByRole('progressbar')

    expect(bar).toHaveAttribute('value', '50')
  })

  it('ignores a progress event that cannot be measured', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    await waitFor(() => {
      expect(requests).toHaveLength(1)
    })

    // A chunked transfer reports lengthComputable false, and total zero would divide by zero.
    at(requests, 0).uploadListeners.get('progress')?.({ lengthComputable: false, loaded: 5, total: 0 })
    at(requests, 0).uploadListeners.get('progress')?.({ lengthComputable: true, loaded: 5, total: 0 })

    expect(await screen.findByRole('progressbar')).toHaveAttribute('value', '0')
  })

  it('announces completion and drops the progress bar', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    await waitFor(() => {
      expect(requests).toHaveLength(1)
    })

    at(requests, 0).status = 204
    at(requests, 0).fire('load')

    expect(await screen.findByRole('status')).toHaveTextContent('shot.png uploaded.')
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('uploads several chosen files independently', async () => {
    const api = fakeApi()

    render(<Harness api={api} />)

    choose([aFile('one.png', 'image/png'), aFile('two.png', 'image/png')])

    await waitFor(() => {
      expect(requests).toHaveLength(2)
    })

    // Held in a ref as well as in props, so two files chosen in quick succession do not each
    // overwrite the other's row.
    expect(screen.getAllByRole('listitem')).toHaveLength(2)
  })

  it('clears the input so the same file can be chosen twice', () => {
    render(<Harness api={fakeApi()} />)

    const input = document.querySelector('input[type="file"]') as HTMLInputElement

    choose([aFile('shot.png', 'image/png')])

    // Without the reset, choosing the same file again fires no change event, which looks exactly
    // like the button having stopped working.
    expect(input.value).toBe('')
  })
})

describe('failure', () => {
  it('keeps the failed row so the person can see which file it was', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    await waitFor(() => {
      expect(requests).toHaveLength(1)
    })

    at(requests, 0).status = 403
    at(requests, 0).fire('load')

    // Silently dropping the row is how someone ends up believing they shared something.
    expect(await screen.findByRole('alert')).toHaveTextContent('Storage refused the upload (403).')
    expect(screen.getByText('shot.png')).toBeInTheDocument()
  })

  it('reports a transport failure distinctly from a refusal', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    await waitFor(() => {
      expect(requests).toHaveLength(1)
    })

    at(requests, 0).fire('error')

    expect(await screen.findByRole('alert')).toHaveTextContent('could not reach storage')
  })

  it('reports a cancelled upload', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    await waitFor(() => {
      expect(requests).toHaveLength(1)
    })

    at(requests, 0).fire('abort')

    expect(await screen.findByRole('alert')).toHaveTextContent('cancelled')
  })

  it('reports a refused ticket without ever reaching storage', async () => {
    const api = fakeApi({
      requestUpload: vi.fn(() => Promise.reject(new Error('Attachment storage is at capacity.'))),
    })

    render(<Harness api={api} />)

    choose([aFile('shot.png', 'image/png')])

    // FR-028: a 507 tells the person storage is full, which is a different thing to do about than
    // a file that was too large.
    expect(await screen.findByRole('alert')).toHaveTextContent('Attachment storage is at capacity.')
    expect(requests).toHaveLength(0)
  })

  it('announces the failure to a screen reader too', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    await waitFor(() => {
      expect(requests).toHaveLength(1)
    })

    at(requests, 0).fire('error')

    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('shot.png failed to upload.')
    })
  })
})

describe('removal', () => {
  it('removes a row and revokes its object URL', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    const remove = await screen.findByRole('button', { name: 'Remove shot.png' })

    fireEvent.click(remove)

    expect(screen.queryByText('shot.png')).not.toBeInTheDocument()

    // Object URLs are not garbage collected while the document lives. A composer used all day
    // would otherwise hold every image anyone previewed, for the whole day.
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:preview')
  })

  it('revokes nothing for a video, which has no local preview', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('clip.mp4', 'video/mp4')])

    const remove = await screen.findByRole('button', { name: 'Remove clip.mp4' })

    fireEvent.click(remove)

    expect(URL.revokeObjectURL).not.toHaveBeenCalled()
  })
})

describe('affordances', () => {
  it('labels the attach button and hides the file input', () => {
    render(<Harness api={fakeApi()} />)

    const button = screen.getByRole('button', { name: 'Attach an image or video' })
    const input = document.querySelector('input[type="file"]') as HTMLInputElement

    // The native input is unstyleable and its default label is browser-specific, so it is hidden
    // behind a button that can be labelled properly.
    expect(input).not.toBeVisible()
    expect(button).toBeEnabled()

    // The accept list mirrors the server's allow-list, so the file picker does not offer types the
    // upload would then refuse.
    expect(input.accept).toContain('image/png')
    expect(input.accept).toContain('video/mp4')
    expect(input.accept).not.toContain('svg')
  })

  it('can be disabled', () => {
    render(<Harness api={fakeApi()} disabled />)

    expect(screen.getByRole('button', { name: 'Attach an image or video' })).toBeDisabled()
  })

  it('opens the picker when the button is pressed', () => {
    render(<Harness api={fakeApi()} />)

    const input = document.querySelector('input[type="file"]') as HTMLInputElement
    const click = vi.spyOn(input, 'click')

    fireEvent.click(screen.getByRole('button', { name: 'Attach an image or video' }))

    expect(click).toHaveBeenCalled()
  })

  it('shows a local preview for an image with an empty alt', async () => {
    render(<Harness api={fakeApi()} />)

    choose([aFile('shot.png', 'image/png')])

    const image = await screen.findByRole('presentation')

    // Decorative: the file name is right next to it as text, and a screen reader announcing both
    // would say the same thing twice.
    expect(image).toHaveAttribute('src', 'blob:preview')
    expect(image).toHaveAttribute('alt', '')
  })
})
