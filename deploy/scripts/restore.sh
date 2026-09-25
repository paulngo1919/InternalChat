#!/usr/bin/env bash
set -eo pipefail

if [ -z "$1" ]; then
    echo "Usage: $0 <backup-archive.tar.gz>"
    exit 1
fi

ARCHIVE_PATH="$1"
if [ ! -f "$ARCHIVE_PATH" ]; then
    echo "Archive not found: $ARCHIVE_PATH"
    exit 1
fi

cd "$(dirname "$0")/../.."
if [ ! -f "deploy/.env" ]; then
    echo "Error: deploy/.env is missing."
    exit 1
fi

source deploy/.env

EXTRACT_DIR="deploy/backups/restore-tmp"
mkdir -p "$EXTRACT_DIR"
echo "Extracting archive..."
tar -xzf "$ARCHIVE_PATH" -C "$EXTRACT_DIR"

# The tar extracts a folder named like internalchat-backup-XYZ
BACKUP_NAME=$(ls "$EXTRACT_DIR" | head -n 1)
BACKUP_DIR="$EXTRACT_DIR/$BACKUP_NAME"

echo "Restoring PostgreSQL..."
# Drop and recreate public schema before restoring
docker compose -f deploy/docker-compose.yml exec -T postgres psql -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"
cat "$BACKUP_DIR/db-backup.sql" | docker compose -f deploy/docker-compose.yml exec -T postgres psql -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" > /dev/null

echo "Restoring MinIO..."
# Copy back into container tmp
docker cp "$BACKUP_DIR/${MINIO_BUCKET}" $(docker compose -f deploy/docker-compose.yml ps -q minio):"/tmp/"
docker cp "$BACKUP_DIR/${MINIO_QUARANTINE_BUCKET}" $(docker compose -f deploy/docker-compose.yml ps -q minio):"/tmp/"

docker compose -f deploy/docker-compose.yml exec -T minio mc alias set local "http://127.0.0.1:9000" "${MINIO_ROOT_USER}" "${MINIO_ROOT_PASSWORD}" > /dev/null
docker compose -f deploy/docker-compose.yml exec -T minio mc mirror --overwrite "/tmp/${MINIO_BUCKET}" "local/${MINIO_BUCKET}" > /dev/null
docker compose -f deploy/docker-compose.yml exec -T minio mc mirror --overwrite "/tmp/${MINIO_QUARANTINE_BUCKET}" "local/${MINIO_QUARANTINE_BUCKET}" > /dev/null

# Clean up container tmp
docker compose -f deploy/docker-compose.yml exec -T minio rm -rf "/tmp/${MINIO_BUCKET}" "/tmp/${MINIO_QUARANTINE_BUCKET}"

rm -rf "$EXTRACT_DIR"

echo "Restore complete!"
