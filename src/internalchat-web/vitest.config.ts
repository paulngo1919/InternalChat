import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    // jsdom, not node: the auth library is built on browser APIs — sessionStorage, location,
    // Web Crypto — and testing it without them would mean testing stubs of the things most
    // likely to be wrong.
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./tests/setup.ts'],
    include: ['tests/**/*.test.{ts,tsx}'],

    coverage: {
      provider: 'v8',

      // json-summary is what tools/Check-Coverage.ps1 reads to enforce the constitution's 80%
      // React floor; text is for whoever ran the command; html is for finding the gaps.
      reporter: ['text', 'json-summary', 'html'],
      reportsDirectory: './coverage',

      // Reported even when no test touches them. Without this, v8 only sees files some test
      // imported — so deleting the last test for a module would RAISE the percentage, which is
      // precisely backwards for a gate meant to catch untested code.
      all: true,
      include: ['src/**/*.{ts,tsx}'],

      exclude: [
        // Entry points and generated declarations: no branches, and nothing a test can assert
        // that the build does not already prove.
        'src/main.tsx',
        'src/vite-env.d.ts',

        // Barrel files. A re-export has no behaviour, and counting them inflates the figure with
        // lines that cannot be wrong.
        'src/**/index.ts',

        // Type-only modules compile away to nothing; v8 reports them as 0% of 0 lines and some
        // tools then average that in as a zero.
        'src/**/*.d.ts',
      ],
    },
  },
})
