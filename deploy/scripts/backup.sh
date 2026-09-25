#!/usr/bin/env bash
set -eo pipefail

# T212: Scripted PostgreSQL and MinIO backup

cd "$(dirname "$0")/../.."

if [ ! -f "deploy/.env" ]; then
    echo "Error: deploy/.env is missing. Are you running in the correct environment?"
    exit 1
fi

source deploy/.env

BACKUP_DIR="internalchat-backup-$(date +%Y%m%d%H%M%S)"
mkdir -p "deploy/backups/$BACKUP_DIR"
ARCHIVE_PATH="deploy/backups/$BACKUP_DIR.tar.gz"

echo "Backing up PostgreSQL..."
docker compose -f deploy/docker-compose.yml exec -T postgres pg_dump -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" > "deploy/backups/$BACKUP_DIR/db-backup.sql"

echo "Backing up MinIO..."
# The minio container has `mc` installed. We create a local alias for it.
docker compose -f deploy/docker-compose.yml exec -T minio mc alias set local "http://127.0.0.1:9000" "${MINIO_ROOT_USER}" "${MINIO_ROOT_PASSWORD}" > /dev/null
# Mirror both buckets to a temporary location in the container
docker compose -f deploy/docker-compose.yml exec -T minio mc mirror "local/${MINIO_BUCKET}" "/tmp/${MINIO_BUCKET}" > /dev/null
docker compose -f deploy/docker-compose.yml exec -T minio mc mirror "local/${MINIO_QUARANTINE_BUCKET}" "/tmp/${MINIO_QUARANTINE_BUCKET}" > /dev/null

# Copy them out of the container
docker cp $(docker compose -f deploy/docker-compose.yml ps -q minio):"/tmp/${MINIO_BUCKET}" "deploy/backups/$BACKUP_DIR/${MINIO_BUCKET}"
docker cp $(docker compose -f deploy/docker-compose.yml ps -q minio):"/tmp/${MINIO_QUARANTINE_BUCKET}" "deploy/backups/$BACKUP_DIR/${MINIO_QUARANTINE_BUCKET}"

# Clean up container temp
docker compose -f deploy/docker-compose.yml exec -T minio rm -rf "/tmp/${MINIO_BUCKET}" "/tmp/${MINIO_QUARANTINE_BUCKET}"

echo "Creating archive..."
tar -czf "$ARCHIVE_PATH" -C "deploy/backups" "$BACKUP_DIR"
rm -rf "deploy/backups/$BACKUP_DIR"

echo "Backup complete: $ARCHIVE_PATH"
