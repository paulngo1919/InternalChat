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
  },
})
