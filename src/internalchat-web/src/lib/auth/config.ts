/**
 * Where the SPA finds its authorization server, from build-time environment variables.
 *
 * `VITE_`-prefixed variables are inlined into the bundle, so nothing here may ever be a secret.
 * That is not a limitation being worked around: the SPA is a public client and has no secret to
 * hold, which is exactly why the flow uses PKCE (research.md D5).
 */

import type { OidcConfig } from './oidcClient'

/** Reads a required variable, failing at startup rather than at first sign-in. */
function required(name: string, value: string | undefined): string {
  if (!value) {
    throw new Error(
      `${name} is not set. The SPA cannot sign anyone in without it. See deploy/.env.example.`,
    )
  }

  return value
}

/** Builds the OIDC configuration for this deployment. */
export function readOidcConfig(env: Record<string, string | undefined>): OidcConfig {
  const origin = globalThis.location.origin

  return {
    authority: required('VITE_OIDC_AUTHORITY', env.VITE_OIDC_AUTHORITY),
    clientId: env.VITE_OIDC_CLIENT_ID ?? 'internalchat-web',

    // Derived from the current origin rather than configured. A redirect URI that disagrees with
    // where the app is actually served is rejected by Keycloak with an error that names neither
    // value, and deriving it means it cannot disagree.
    redirectUri: `${origin}/`,
    postLogoutRedirectUri: `${origin}/`,

    // `openid` is mandatory; `profile` and `email` supply the display name and address shown to
    // colleagues. Nothing else is requested — a scope this client does not use is a permission
    // granted for no reason.
    scope: 'openid profile email',
  }
}

/** Base URL of this platform's API. Same origin by default, behind the reverse proxy. */
export function readApiBaseUrl(env: Record<string, string | undefined>): string {
  return env.VITE_API_BASE_URL ?? '/api/v1'
}
