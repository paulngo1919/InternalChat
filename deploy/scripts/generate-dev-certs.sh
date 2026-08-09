#!/usr/bin/env bash
# Generates a self-signed TLS certificate for local development.
#
# Constitution, Data Protection: "Certificates are issued free of charge —
# Let's Encrypt via a self-hosted reverse proxy in production, a locally
# generated development CA in Compose. Purchased certificates are neither
# required nor permitted as a dependency."
#
# The output is git-ignored. A private key in the repository would be a real
# secret in source control even if it only protects localhost, and it teaches
# the habit of committing keys.
#
# Usage:  ./deploy/scripts/generate-dev-certs.sh
set -euo pipefail

CERT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../certs" && pwd)"
CRT="${CERT_DIR}/internalchat.crt"
KEY="${CERT_DIR}/internalchat.key"

if [[ -f "${CRT}" && -f "${KEY}" ]]; then
  echo "Development certificate already present at ${CERT_DIR}"
  echo "Delete internalchat.crt and internalchat.key to regenerate."
  exit 0
fi

echo "Generating a self-signed development certificate in ${CERT_DIR}..."

# Runs through Docker so no local OpenSSL install is required — the constitution
# requires onboarding to be clone, cp .env, docker compose up.
#
# MSYS_NO_PATHCONV stops Git Bash on Windows rewriting container-side paths:
# without it `/certs/internalchat.key` becomes `C:/Program Files/Git/certs/...`
# and OpenSSL fails inside the container with a path that never existed there.
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' docker run --rm -v "${CERT_DIR}:/certs" alpine/openssl:latest \
  req -x509 -nodes -newkey rsa:2048 \
  -keyout /certs/internalchat.key \
  -out /certs/internalchat.crt \
  -days 825 \
  -subj "/CN=localhost/O=InternalChat Development" \
  -addext "subjectAltName=DNS:localhost,DNS:internalchat.local,IP:127.0.0.1" \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
  -addext "extendedKeyUsage=serverAuth"

chmod 600 "${KEY}" 2>/dev/null || true

echo
echo "Done. This certificate is self-signed, so browsers will warn on first visit."
echo "That warning is expected locally and is NOT what production looks like:"
echo "production terminates TLS with a Let's Encrypt certificate."
