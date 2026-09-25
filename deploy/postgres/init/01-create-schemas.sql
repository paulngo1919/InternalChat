-- Create the schema Keycloak uses (KC_DB_SCHEMA=keycloak in docker-compose).
-- This runs once when the postgres volume is first initialised.
CREATE SCHEMA IF NOT EXISTS keycloak;

-- The unaccent extension is needed for accent-insensitive search (research.md D9).
CREATE EXTENSION IF NOT EXISTS unaccent;

-- pg_stat_statements is preloaded via shared_preload_libraries; create the
-- extension so the views are available for performance investigation.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
