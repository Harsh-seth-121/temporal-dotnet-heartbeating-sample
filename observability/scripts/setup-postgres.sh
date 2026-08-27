#!/bin/sh
# Create and migrate the Temporal PostgreSQL schemas. Runs inside
# temporalio/admin-tools, the only official image shipping temporal-sql-tool and
# /etc/temporal/schema. temporalio/server cannot do this itself.
#
# Idempotent: probes with update-schema first; only creates if that fails.
set -eu

: "${POSTGRES_SEEDS:?POSTGRES_SEEDS is required}"
: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${SQL_PASSWORD:?SQL_PASSWORD is required (temporal-sql-tool reads it from the env)}"

PORT="${DB_PORT:-5432}"
SQLTOOL="temporal-sql-tool --plugin postgres12 --ep ${POSTGRES_SEEDS} -p ${PORT} -u ${POSTGRES_USER}"
SCHEMA_ROOT=/etc/temporal/schema/postgresql/v12

echo "Waiting for PostgreSQL at ${POSTGRES_SEEDS}:${PORT} ..."
attempt=1
until nc -z -w 2 "${POSTGRES_SEEDS}" "${PORT}"; do
  if [ "${attempt}" -ge 60 ]; then
    echo "ERROR: PostgreSQL never became reachable after ${attempt} attempts" >&2
    exit 1
  fi
  attempt=$((attempt + 1))
  sleep 2
done
echo "PostgreSQL is reachable."

setup_db() {
  db="$1"
  versioned="$2"
  if ${SQLTOOL} --db "${db}" update-schema -d "${versioned}" >/dev/null 2>&1; then
    echo "Database '${db}' already initialized and up to date."
    return 0
  fi
  echo "Initializing database '${db}' ..."
  ${SQLTOOL} --db "${db}" create || true
  ${SQLTOOL} --db "${db}" setup-schema -v 0.0
  ${SQLTOOL} --db "${db}" update-schema -d "${versioned}"
  echo "Database '${db}' initialized."
}

setup_db temporal            "${SCHEMA_ROOT}/temporal/versioned"
setup_db temporal_visibility "${SCHEMA_ROOT}/visibility/versioned"

echo "PostgreSQL schema setup complete."
