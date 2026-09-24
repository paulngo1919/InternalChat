/**
 * T193 — the meetings feature's lazy entry point.
 *
 * **The entire purpose of this file is to be the only thing that imports the LiveKit SDK.**
 * plan.md constrains the initial JS bundle to 300 KB gzipped; `livekit-client` plus
 * `@livekit/components-react` is well over that on its own. A static import anywhere in the
 * always-loaded graph would blow the budget for every employee on every page load — including the
 * overwhelming majority who never start a meeting.
 *
 * `React.lazy` with a dynamic `import()` is what makes Rollup emit it as a separate chunk. The
 * import below must stay dynamic: changing it to a static one produces a build that works, passes
 * every test, and silently triples the initial download. `vite.config.ts` asserts the split held.
 */

import { lazy } from 'react'

/**
 * The meeting room, loaded on demand.
 *
 * Callers wrap it in `<Suspense>`; the fallback is what someone sees for the second or so the
 * chunk takes to arrive after they click "join".
 */
export const MeetingRoom = lazy(async () => {
  const module = await import('./MeetingRoom')

  return { default: module.MeetingRoom }
})

export type { MeetingRoomProps } from './MeetingRoom'
