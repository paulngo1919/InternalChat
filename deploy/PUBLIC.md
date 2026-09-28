# Public access — https://chat.benda.io.vn

Audience: whoever runs this machine. How the Docker Compose stack is published on the internet
through the machine's existing Cloudflare Tunnel — the same tunnel (`pr-review`) that serves
`pr.benda.io.vn` (see `E:\ReviewCode\docs\08-cloudflare-tunnel-setup.md`).

## How it fits together

```text
browser ──https──▶ Cloudflare edge ──tunnel──▶ cloudflared (Windows service)
                                                  │  http://localhost:8090
                                                  ▼
                                    nginx (docker-compose.public.yml, nginx/public.conf)
                                      /            → web       (SPA)
                                      /api/ /hubs/ → api       (REST + SignalR WebSockets)
                                      /realms/ /resources/ → keycloak (sign-in, OIDC)
```

One origin for everything, so sign-in, API calls, and the real-time socket are all same-origin on
`https://chat.benda.io.vn`. TLS ends at Cloudflare; the edge listens on loopback only
(`127.0.0.1:8090`), so nothing reaches it except through the tunnel. Keycloak's admin console is not
routed publicly — administer the realm from this machine at `http://localhost:8082`.

## First-time setup

1. `deploy/.env` exists (copy `deploy/.env.example` and fill in secrets).
2. Make the database's application role match `deploy/.env` (once; needed if the database was set
   up before `POSTGRES_APP_PASSWORD` was chosen — symptom: migrations exit with `28P01`):

   ```powershell
   powershell -File deploy/scripts/Sync-AppDbPassword.ps1
   ```

3. Start the stack (any PowerShell):

   ```powershell
   powershell -File deploy/scripts/Start-Public.ps1
   ```

   Builds the images, starts everything, allows `https://chat.benda.io.vn` on the Keycloak web
   client in the live realm, and checks Keycloak advertises the public issuer.
4. Publish the hostname (**elevated** PowerShell, once):

   ```powershell
   powershell -File deploy/scripts/Add-CloudflareRoute.ps1
   ```

   Adds `chat.benda.io.vn → http://localhost:8090` to `%USERPROFILE%\.cloudflared\config.yml`
   (backed up first), creates the DNS record, restarts `cloudflared`. The restart briefly drops every
   hostname on the tunnel (a few seconds).
5. Open `https://chat.benda.io.vn` from outside the office network (e.g. mobile data) and sign in.

## Day to day

| Task | Command |
| --- | --- |
| Rebuild and restart after a code change | `powershell -File deploy/scripts/Start-Public.ps1` |
| Status | `docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.public.yml ps` |
| Logs | `docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.public.yml logs -f nginx api worker` |
| Stop | `docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.public.yml down` |
| Tunnel health | `sc query cloudflared`, `cloudflared tunnel info pr-review` |

## What the public override changes

| Service | Change | Why |
| --- | --- | --- |
| nginx | `nginx/public.conf`, only `127.0.0.1:8090` published | One plain-HTTP origin for the tunnel; Cloudflare does TLS |
| keycloak | `KC_PROXY_HEADERS=xforwarded` | Issuer and redirects say `https://chat.benda.io.vn` for browsers coming through the edge, while `http://localhost:8082` keeps working locally |
| web | Built with `VITE_OIDC_AUTHORITY=https://chat.benda.io.vn/realms/internalchat` | The browser signs in on the public origin |
| api | `Keycloak__Issuer` = public realm URL | Tokens carry the public issuer; the API still fetches keys from `http://keycloak:8080` |

## Known limits

- **Attachments.** Uploads go straight from the browser to MinIO via a presigned URL that names
  `minio:9000`, which a browser cannot reach. Messages work; image/video upload does not through
  this setup (nor through plain Compose). Needs a public, signature-compatible MinIO route.
- **Meetings.** LiveKit media is WebRTC over UDP, which a Cloudflare Tunnel does not carry.
- **Upload size.** Cloudflare's free plan caps request bodies at 100 MB.
- **Shared infrastructure.** This stack and the local "Full Stack" debug launch share the same
  PostgreSQL/Redis/RabbitMQ/Keycloak containers (Compose project `internalchat`). Running both means
  two Workers draining the same queues — harmless, but confusing when debugging.
- **No identity-aware proxy.** Anyone with the URL reaches the Keycloak sign-in page. Consider a
  Cloudflare Access policy in front, as noted for the PR dashboard.
