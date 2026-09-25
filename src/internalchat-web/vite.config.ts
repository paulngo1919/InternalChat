import { gzipSync } from 'node:zlib'
import { fileURLToPath } from 'node:url'

import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'

// Test configuration lives in vitest.config.ts, not here. Vitest resolves its own copy of Vite,
// and declaring `test` through Vite's own `defineConfig` makes the two plugin type graphs meet —
// which is a wall of structurally-incompatible `Plugin` errors that says nothing about the actual
// problem. Keeping the two files apart keeps each typed against the Vite it actually uses.

/**
 * plan.md: "Initial JS bundle under 300 KB gzipped."
 *
 * Measured gzipped rather than raw because that is what crosses the network, and it is the number
 * the constraint is written in.
 */
const INITIAL_BUNDLE_LIMIT_BYTES = 300 * 1024

/**
 * Modules that must never reach the initial bundle.
 *
 * The LiveKit SDK alone exceeds the whole budget. It is loaded through `React.lazy` in
 * `src/features/meetings/index.ts`, and the split is one accidental static import away from
 * disappearing — at which point everything still builds, every test still passes, and every
 * employee downloads a megabyte they will never use.
 */
const MUST_BE_LAZY = ['livekit-client', '@livekit/components-react']

/**
 * T195 — fails the build when the initial bundle breaks its budget or swallows a lazy dependency.
 *
 * A build-time assertion rather than a CI script, deliberately: a size regression that only fails
 * in CI is a size regression that lands on a branch and is discovered by somebody else. This fails
 * on the machine that caused it.
 */
function bundleBudget(): Plugin {
  return {
    name: 'internalchat-bundle-budget',
    apply: 'build',

    // `writeBundle` rather than `generateBundle`: the assertion needs the emitted chunks, and
    // failing after the write still fails the process — Vite propagates a throw here as a
    // non-zero exit, which is what makes this a gate rather than a warning.
    writeBundle(_options, bundle) {
      const entry = Object.values(bundle).find(
        (chunk) => chunk.type === 'chunk' && chunk.isEntry && chunk.name === 'main',
      )

      if (entry?.type !== 'chunk') {
        throw new Error(
          'The bundle-size assertion could not find the main entry chunk. If the entry was ' +
            'renamed, update vite.config.ts — a check that silently finds nothing is worse than ' +
            'no check, because it reports success.',
        )
      }

      // The entry plus everything it statically pulls in. A chunk reached only through a dynamic
      // import is excluded, which is exactly the distinction being enforced.
      const initial = new Set<string>()

      const walk = (fileName: string) => {
        if (initial.has(fileName)) {
          return
        }

        initial.add(fileName)

        const chunk = bundle[fileName]

        if (chunk?.type === 'chunk') {
          for (const imported of chunk.imports) {
            walk(imported)
          }
        }
      }

      walk(entry.fileName)

      let gzippedBytes = 0
      const offenders: string[] = []

      for (const fileName of initial) {
        const chunk = bundle[fileName]

        if (chunk?.type !== 'chunk') {
          continue
        }

        gzippedBytes += gzipSync(Buffer.from(chunk.code)).byteLength

        for (const dependency of MUST_BE_LAZY) {
          if (chunk.moduleIds.some((id) => id.includes(`node_modules/${dependency}`))) {
            offenders.push(`${dependency} (in ${fileName})`)
          }
        }
      }

      if (offenders.length > 0) {
        throw new Error(
          `These modules must be loaded lazily and are in the initial bundle:\n` +
            `  ${offenders.join('\n  ')}\n\n` +
            'Meetings are reached through React.lazy in src/features/meetings/index.ts. Something ' +
            'now imports that module statically — check for an import of ./MeetingRoom or of the ' +
            'SDK outside that file. The build works either way, which is why this check exists.',
        )
      }

      if (gzippedBytes > INITIAL_BUNDLE_LIMIT_BYTES) {
        throw new Error(
          `The initial bundle is ${(gzippedBytes / 1024).toFixed(1)} KB gzipped; the budget is ` +
            `${(INITIAL_BUNDLE_LIMIT_BYTES / 1024).toFixed(0)} KB (plan.md, Constraints).\n\n` +
            'Either reduce it or load the new dependency lazily. Raising this number is a ' +
            'decision about what every employee downloads on every page load, not a build fix.',
        )
      }

      // Printed on success too. A budget nobody sees the headroom on is a budget that is breached
      // by surprise.
      this.info(
        `Initial bundle: ${(gzippedBytes / 1024).toFixed(1)} KB gzipped of ` +
          `${(INITIAL_BUNDLE_LIMIT_BYTES / 1024).toFixed(0)} KB.`,
      )
    },
  }
}

// https://vite.dev/config/
export default defineConfig({
  server: {
    port: 8080,
    strictPort: true
  },
  plugins: [react(), bundleBudget()],
  build: {
    rollupOptions: {
      // A second entry, not the `new Worker(new URL(...))` pattern Vite handles automatically —
      // that special-cased asset-URL analysis covers the `Worker`/`SharedWorker` constructors,
      // not `ServiceWorkerContainer.register` (pushSubscription.ts explains why). This is the
      // reliable alternative: build the service worker as its own Rollup entry.
      input: {
        main: fileURLToPath(new URL('./index.html', import.meta.url)),
        'service-worker': fileURLToPath(
          new URL('./src/lib/push/service-worker.ts', import.meta.url),
        ),
      },
      output: {
        // The service worker must be served at a stable, root-relative path — its registration
        // scope is everything under wherever it lives — so it is exempted from the content-hashed
        // naming every other entry gets.
        entryFileNames: (chunk) =>
          chunk.name === 'service-worker' ? 'service-worker.js' : 'assets/[name]-[hash].js',
      },
    },
  },
})
