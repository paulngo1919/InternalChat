import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Test configuration lives in vitest.config.ts, not here. Vitest resolves its own copy of Vite,
// and declaring `test` through Vite's own `defineConfig` makes the two plugin type graphs meet —
// which is a wall of structurally-incompatible `Plugin` errors that says nothing about the actual
// problem. Keeping the two files apart keeps each typed against the Vite it actually uses.

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
})
