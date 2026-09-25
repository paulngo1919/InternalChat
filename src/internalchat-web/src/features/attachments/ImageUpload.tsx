/**
 * T156 — image upload with progress and clipboard paste (FR-021, FR-026).
 *
 * Three things here are deliberate and each has a failure it prevents:
 *
 * 1. **The file is validated before a ticket is requested.** FR-023 wants the refusal before the
 *    upload, and the local check makes it instant instead of a round trip.
 * 2. **Bytes go straight to MinIO, never through the API.** The ticket is a presigned PUT; a
 *    500 MB video routed through .NET would occupy a request thread for minutes on a host whose
 *    budget belongs to messaging (research.md D7).
 * 3. **Progress comes from `XMLHttpRequest`, not `fetch`.** `fetch` still cannot report upload
 *    progress in any shipping browser, and FR-026 asks for progress explicitly. This is the one
 *    place the older API earns its place.
 */

import {
  useCallback,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
  type ChangeEvent,
  type RefObject,
} from 'react'
import { Paperclip, X } from 'lucide-react'

import { type AttachmentKind, kindOf, rejectionFor } from './fileConstraints'

/** An upload in flight or finished, as the composer needs to see it. */
export interface PendingAttachment {
  /** Stable across the upload's life, so a re-render does not lose the row. */
  readonly localId: string
  readonly file: File
  readonly kind: AttachmentKind
  /** Set once the server has reserved a row. Quoted back on send. */
  readonly attachmentId: string | null
  /** 0–100. */
  readonly progress: number
  readonly status: 'validating' | 'reserving' | 'uploading' | 'uploaded' | 'failed'
  readonly error: string | null
  /** An object URL for the local preview, revoked when the row is removed. */
  readonly previewUrl: string | null
}

/** What the component needs from the API client, narrowed to the two calls it makes. */
export interface UploadApi {
  requestUpload(
    conversationId: string,
    request: {
      kind: AttachmentKind
      contentType: string
      byteSize: number
      durationSeconds?: number | null
      fileName: string
    },
  ): Promise<{ attachmentId: string; uploadUrl: string; expiresAt: string }>
}

interface ImageUploadProps {
  readonly conversationId: string
  readonly api: UploadApi
  readonly attachments: readonly PendingAttachment[]
  readonly onChange: (next: readonly PendingAttachment[]) => void
  readonly disabled?: boolean | undefined
  /**
   * A handle for uploading files that arrived from somewhere else — a paste, in practice.
   *
   * The paste lands on the composer's textarea, which this component does not own. An
   * imperative handle rather than a `pasted` prop plus an effect: handing files down as state
   * means the parent must then be told the batch was taken so it can clear it, and that round
   * trip is a setState inside an effect inside a render — which React's own lint rule flags,
   * and which re-uploads the batch on any render where the clearing has not landed yet.
   */
  readonly handleRef?: RefObject<ImageUploadHandle | null> | undefined
}

/** What a caller can ask this component to do. */
export interface ImageUploadHandle {
  /** Uploads files the caller obtained itself, with the same validation as the picker. */
  upload(files: readonly File[]): void
}

/** Picks files, uploads them, and reports progress. */
export function ImageUpload({
  conversationId,
  api,
  attachments,
  onChange,
  disabled,
  handleRef,
}: ImageUploadProps) {
  const inputRef = useRef<HTMLInputElement>(null)

  // Held in a ref as well as in props so the async upload callbacks update the latest list rather
  // than the one captured when the upload started. Two files chosen in quick succession would
  // otherwise each overwrite the other's row.
  const latest = useRef<readonly PendingAttachment[]>(attachments)

  // Synced in an effect rather than assigned during render. Writing a ref while rendering is a
  // React rule violation and, more practically, makes the value depend on whether a render was
  // discarded. Uploads start from event handlers, which always run after the effect has committed.
  useEffect(() => {
    latest.current = attachments
  }, [attachments])

  const [announcement, setAnnouncement] = useState('')

  const update = useCallback(
    (localId: string, patch: Partial<PendingAttachment>) => {
      const next = latest.current.map((item) =>
        item.localId === localId ? { ...item, ...patch } : item,
      )

      latest.current = next
      onChange(next)
    },
    [onChange],
  )

  const upload = useCallback(
    async (file: File) => {
      const rejection = rejectionFor(file)

      if (rejection !== null) {
        // Refused without a request. The person is told the limit, not merely that it failed.
        setAnnouncement(rejection.message)
        return
      }

      const kind = kindOf(file.type)

      if (kind === null) {
        return
      }

      const localId = crypto.randomUUID()
      const previewUrl = kind === 'image' ? URL.createObjectURL(file) : null

      const entry: PendingAttachment = {
        localId,
        file,
        kind,
        attachmentId: null,
        progress: 0,
        status: 'reserving',
        error: null,
        previewUrl,
      }

      const withEntry = [...latest.current, entry]
      latest.current = withEntry
      onChange(withEntry)

      try {
        const ticket = await api.requestUpload(conversationId, {
          kind,
          contentType: file.type,
          byteSize: file.size,
          fileName: file.name,
        })

        update(localId, { attachmentId: ticket.attachmentId, status: 'uploading' })

        await putWithProgress(ticket.uploadUrl, file, (progress) => {
          update(localId, { progress })
        })

        update(localId, { status: 'uploaded', progress: 100 })
        setAnnouncement(`${file.name} uploaded.`)
      } catch (error) {
        // Left in the list rather than removed, so the person can see which file failed and retry
        // it. Silently dropping the row is how someone ends up believing they shared something.
        update(localId, {
          status: 'failed',
          error: error instanceof Error ? error.message : 'The upload failed.',
        })

        setAnnouncement(`${file.name} failed to upload.`)
      }
    },
    [api, conversationId, onChange, update],
  )

  // Files handed in from outside go through exactly the same path as picked ones, validation
  // included — a pasted 40 MB screenshot is refused with the same message as a chosen one.
  useImperativeHandle(
    handleRef,
    () => ({
      upload: (files: readonly File[]) => {
        for (const file of files) {
          void upload(file)
        }
      },
    }),
    [upload],
  )

  const onFilesChosen = useCallback(
    (event: ChangeEvent<HTMLInputElement>) => {
      const files = Array.from(event.target.files ?? [])

      // Reset first: choosing the same file twice in a row fires no change event otherwise, which
      // looks like the button has stopped working.
      event.target.value = ''

      for (const file of files) {
        void upload(file)
      }
    },
    [upload],
  )

  const remove = useCallback(
    (localId: string) => {
      const entry = latest.current.find((item) => item.localId === localId)

      if (entry?.previewUrl) {
        // Object URLs are not garbage collected while the document lives. A composer used all day
        // would otherwise hold every image anyone previewed.
        URL.revokeObjectURL(entry.previewUrl)
      }

      const next = latest.current.filter((item) => item.localId !== localId)
      latest.current = next
      onChange(next)
    },
    [onChange],
  )

  return (
    <div className="attachment-upload">
      <button
        type="button"
        className="btn-secondary btn-icon"
        onClick={() => inputRef.current?.click()}
        disabled={disabled ?? false}
        aria-label="Attach an image or video"
      >
        <Paperclip size={20} />
      </button>

      <input
        ref={inputRef}
        type="file"
        multiple
        hidden
        accept="image/png,image/jpeg,image/gif,image/webp,video/mp4,video/webm"
        onChange={onFilesChosen}
      />

      <ul className="attachment-upload__list">
        {attachments.map((item) => (
          <li key={item.localId} className={`attachment-upload__item is-${item.status}`}>
            {item.previewUrl !== null && (
              <img src={item.previewUrl} alt="" className="attachment-upload__thumb" />
            )}

            <span className="attachment-upload__name">{item.file.name}</span>

            {item.status === 'uploading' && (
              <progress value={item.progress} max={100}>
                {item.progress}%
              </progress>
            )}

            {item.error !== null && (
              <span role="alert" className="attachment-upload__error">
                {item.error}
              </span>
            )}

            <button
              type="button"
              className="btn-danger-text btn-sm"
              onClick={() => {
                remove(item.localId)
              }}
              aria-label={`Remove ${item.file.name}`}
            >
              <X size={14} />
            </button>
          </li>
        ))}
      </ul>

      {/* Errors and completions reach a screen reader without stealing focus from the composer. */}
      <span role="status" aria-live="polite" className="visually-hidden">
        {announcement}
      </span>
    </div>
  )
}

/**
 * PUTs a file to a presigned URL, reporting progress.
 *
 * `XMLHttpRequest` rather than `fetch`: upload progress events are still not available on `fetch`
 * in any shipping browser, and FR-026 requires progress. No authorization header is attached — the
 * URL carries its own signature, and sending a bearer token to storage would leak it there.
 */
function putWithProgress(
  url: string,
  file: File,
  onProgress: (percent: number) => void,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest()

    request.open('PUT', url, true)
    request.setRequestHeader('Content-Type', file.type)

    request.upload.addEventListener('progress', (event) => {
      if (event.lengthComputable && event.total > 0) {
        onProgress(Math.round((event.loaded / event.total) * 100))
      }
    })

    request.addEventListener('load', () => {
      if (request.status >= 200 && request.status < 300) {
        resolve()
      } else {
        reject(new Error(`Storage refused the upload (${String(request.status)}).`))
      }
    })

    request.addEventListener('error', () => {
      reject(new Error('The upload could not reach storage.'))
    })

    request.addEventListener('abort', () => {
      reject(new Error('The upload was cancelled.'))
    })

    request.send(file)
  })
}
