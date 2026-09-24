import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import App from './App.tsx'
import { AuthProvider, RequireAuth } from './lib/auth/AuthProvider'
import { readOidcConfig } from './lib/auth/config'
import './index.css'

const rootElement = document.getElementById('root')
if (!rootElement) {
  throw new Error('Root element #root is missing from index.html')
}

// Read once, outside the tree. AuthProvider keys its effect on the config object, so rebuilding it
// per render would restart the sign-in flow on every render.
const oidcConfig = readOidcConfig(import.meta.env)

/**
 * One query client for the tab.
 *
 * Retries are left at the default for reads, which are idempotent. Writes never go through TanStack
 * Query — sends are the offline queue's job, because a retry there has to reuse the same
 * `clientMessageKey` and a generic retry policy knows nothing about that (FR-011).
 */
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // A conversation list refetched on every window focus is a request per tab switch, all day.
      // Real-time events invalidate the cache when it actually changes.
      refetchOnWindowFocus: false,
    },
  },
})

createRoot(rootElement).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthProvider config={oidcConfig}>
        {/*
        Nothing inside renders for an unauthenticated visitor — "protected" is a property of the
        tree rather than a check each screen has to remember (FR-002, T074).
      */}
        <RequireAuth>
          <App />
        </RequireAuth>
      </AuthProvider>
    </QueryClientProvider>
  </StrictMode>,
)
