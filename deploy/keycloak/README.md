# Keycloak realm

`realm-export.json` is imported on first boot by `docker compose up` (the `keycloak` service runs
`start-dev --import-realm`). It is the development realm only. A production realm is provisioned by
the platform team and must **not** import this file — see [Not for production](#not-for-production).

## Development credentials

Every user's password equals their username. Deliberately weak and obviously fake: safe on a laptop,
unsafe anywhere else.

| Username | Display name | Realm roles |
| --- | --- | --- |
| `an.nguyen` | Nguyễn Thị Vân An | `employee`, `chat-admin` |
| `binh.tran` | Trần Quốc Bình | `employee` |
| `chi.le` | Lê Bảo Chi | `employee` |
| `dung.pham` | Phạm Tiến Dũng | `employee` |
| `giang.hoang` | Hoàng Thu Giang | `employee` |
| `hai.vo` | Võ Minh Hải | `employee` |
| `khanh.dang` | Đặng Gia Khánh | `employee` |
| `lan.bui` | Bùi Ngọc Lan | `employee` |
| `minh.do` | Đỗ Nhật Minh | `employee` |
| `nga.ngo` | Ngô Thúy Nga | `employee` |
| `phuc.duong` | Dương Hồng Phúc | `employee` |
| `quyen.ly` | Lý Thanh Quyên | `employee` |
| `son.truong` | Trương Hoài Sơn | `employee` |
| `thao.dinh` | Đinh Phương Thảo | `employee` |
| `tuan.mai` | Mai Anh Tuấn | `employee` |
| `uyen.cao` | Cao Diệu Uyên | `employee` |
| `viet.ha` | Hà Đức Việt | `employee` |
| `xuan.luong` | Lương Thị Xuân | `employee` |
| `yen.trinh` | Trịnh Hải Yến | `employee` |
| `duc.nguyen` | Nguyễn Trung Đức | `employee` |

## The user id is not a UUID, on purpose

Each user's Keycloak id is `dev-{username}`, so the `sub` claim is `dev-an.nguyen` rather than a
random UUID. `employee.external_subject` is the join between a token and a database row, and
`tools/Seeder` derives it as `dev-{username}`. A random id here would produce a realm whose users
sign in perfectly and match no employee — an empty conversation list with nothing obviously wrong.

`tools/Seeder/DevelopmentSeeder.Roster` is the single definition of the roster. If you change it,
regenerate this file rather than editing both.

## Clients

| Client | Type | Flows | Purpose |
| --- | --- | --- | --- |
| `internalchat-web` | public | authorization code + PKCE (S256) | The SPA. A public client cannot hold a secret, so PKCE is what stops an intercepted code being redeemed by someone else. |
| `internalchat-test` | public | direct access grants | Integration and end-to-end tests, which need a token without driving a browser. |

Both carry an audience mapper putting `internalchat-web` in `aud`, because the API validates
audience and Keycloak otherwise emits only `azp`.

`internalchat-web` declares a back-channel logout URL of
`http://api:8080/api/v1/auth/backchannel-logout`. That endpoint feeds the Redis revocation set
(research.md D5), which is half of how FR-003's five-minute deadline is met — the other half is the
300-second access-token lifetime set on the realm.

## Not for production

- `sslRequired: none` — TLS terminates at nginx in every other environment.
- `internalchat-test` exists at all. A password grant bypasses the browser flow and therefore PKCE.
- Every password equals its username.

## Regenerating

The realm is generated from the seeder roster so the two cannot drift. The generator lives with the
task that produced it (T061); to change the roster, edit
`tools/Seeder/DevelopmentSeeder.Roster`, regenerate, and re-run `docker compose down -v` so Keycloak
imports afresh — **import runs only against an empty realm**, so editing this file and restarting
changes nothing on its own.
