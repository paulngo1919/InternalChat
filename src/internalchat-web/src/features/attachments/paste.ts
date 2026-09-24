/**
 * Clipboard handling for the composer (US5: "paste a screenshot").
 *
 * Its own module rather than an export from `ImageUpload.tsx` for a mechanical reason: a file that
 * exports both a component and a plain function breaks React Fast Refresh, which the project's
 * lint configuration enforces. The split is also honest — the paste happens on the text input,
 * which `ImageUpload` does not own.
 */

import type { ClipboardEvent } from 'react'

import { kindOf } from './fileConstraints'

/**
 * The attachable files carried by a paste.
 *
 * Returns them rather than consuming the event, leaving the caller to decide: a paste carrying both
 * text and an image should still paste the text, and only the caller knows whether its input had a
 * selection worth replacing.
 *
 * Files whose type is on neither allow-list are dropped here rather than surfaced as an error.
 * Pasting from a spreadsheet or a design tool routinely puts several representations on the
 * clipboard, and complaining about the ones we cannot use would make an ordinary paste look broken.
 */
export function imageFilesFromPaste(event: ClipboardEvent): readonly File[] {
  const items = Array.from(event.clipboardData.items)

  return items
    .filter((item) => item.kind === 'file')
    .map((item) => item.getAsFile())
    .filter((file): file is File => file !== null && kindOf(file.type) !== null)
}
