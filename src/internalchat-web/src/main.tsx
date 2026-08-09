import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

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

createRoot(rootElement).render(
  <StrictMode>
    <AuthProvider config={oidcConfig}>
      {/*
        Nothing inside renders for an unauthenticated visitor — "protected" is a property of the
        tree rather than a check each screen has to remember (FR-002, T074).
      */}
      <RequireAuth>
        <App />
      </RequireAuth>
    </AuthProvider>
  </StrictMode>,
)
