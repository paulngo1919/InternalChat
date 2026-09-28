import { useState } from 'react'

import './loading.css'

/**
 * Whether a splash is already on screen: the static one in index.html (checked once, at module load,
 * before React first renders over it), or an earlier LoadingScreen. Signing in and loading the
 * profile are separate trees, so each wait remounts this component; fading in again every time would
 * blink the screen between them. Only the very first appearance of a page load fades.
 */
let splashOnScreen =
  typeof document !== 'undefined' && document.querySelector('#root > .loading-screen') !== null

interface LoadingScreenProps {
  /** What the app is waiting for, in words — announced to screen readers and shown under the mark. */
  readonly message: string
}

/**
 * The full-screen wait: signing in, then loading the profile.
 *
 * The same markup is inlined in index.html so the first paint, before any script runs, already looks
 * like this — React then replaces it with an identical tree and there is no flash in between. Keep
 * the two in step.
 *
 * It fades in after a short delay (see loading.css), so a fast load never flashes a splash at all.
 */
export function LoadingScreen({ message }: LoadingScreenProps) {
  const [instant] = useState(() => {
    const already = splashOnScreen
    splashOnScreen = true
    return already
  })

  return (
    <div className={instant ? 'loading-screen loading-screen--instant' : 'loading-screen'}>
      <div className="loading-screen__mark" aria-hidden="true">
        <span className="loading-screen__orbit" />
        <span className="loading-screen__logo">
          <BrandGlyph />
        </span>
      </div>
      <p className="loading-screen__brand">InternalChat</p>
      <p className="loading-screen__message" role="status" aria-live="polite">
        {message}
      </p>
      <div className="loading-screen__bar" aria-hidden="true">
        <span />
      </div>
    </div>
  )
}

/**
 * A speech bubble whose three dots bounce in turn, the way a typing indicator does. Drawn inline so
 * the splash needs no request of its own.
 */
function BrandGlyph() {
  return (
    <svg viewBox="0 0 32 32" width="34" height="34" fill="none" focusable="false">
      <path
        d="M16 5c-6.35 0-11.5 4.37-11.5 9.75 0 2.9 1.5 5.5 3.88 7.28L7.5 27l5.2-2.7c1.05.28 2.16.45 3.3.45 6.35 0 11.5-4.37 11.5-9.75S22.35 5 16 5z"
        fill="currentColor"
      />
      <circle className="loading-screen__dot" cx="11" cy="14.75" r="1.9" />
      <circle className="loading-screen__dot" cx="16" cy="14.75" r="1.9" />
      <circle className="loading-screen__dot" cx="21" cy="14.75" r="1.9" />
    </svg>
  )
}
