#!/bin/sh
# Register the 'default' namespace once the frontend is healthy.
# temporalio/server does not create namespaces (the deprecated auto-setup image did).
set -eu

NAMESPACE="${DEFAULT_NAMESPACE:-default}"
RETENTION="${DEFAULT_NAMESPACE_RETENTION:-1d}"
ADDRESS="${TEMPORAL_ADDRESS:-temporal:7233}"
MAX_ATTEMPTS="${TEMPORAL_HEALTH_CHECK_MAX_ATTEMPTS:-30}"
SLEEP_SECONDS="${TEMPORAL_HEALTH_CHECK_SLEEP_SECONDS:-5}"

echo "Waiting for Temporal frontend at ${ADDRESS} to be healthy ..."
attempt=1
until temporal operator cluster health --address "${ADDRESS}" >/dev/null 2>&1; do
  if [ "${attempt}" -ge "${MAX_ATTEMPTS}" ]; then
    echo "ERROR: Temporal did not become healthy after ${MAX_ATTEMPTS} attempts" >&2
    exit 1
  fi
  echo "  not ready yet (attempt ${attempt}/${MAX_ATTEMPTS})"
  attempt=$((attempt + 1))
  sleep "${SLEEP_SECONDS}"
done
echo "Temporal is healthy."

# NOTE: retention is only applied at CREATION. Changing DEFAULT_NAMESPACE_RETENTION
# and re-running `docker compose up` does NOT update an existing namespace -- this
# branch exits first. To change it on a live stack:
#   temporal operator namespace update -n default --retention 7d
if temporal operator namespace describe -n "${NAMESPACE}" --address "${ADDRESS}" >/dev/null 2>&1; then
  echo "Namespace '${NAMESPACE}' already exists (retention unchanged; see note above)."
  exit 0
fi

echo "Creating namespace '${NAMESPACE}' with ${RETENTION} retention ..."
temporal operator namespace create -n "${NAMESPACE}" --retention "${RETENTION}" --address "${ADDRESS}"
echo "Namespace '${NAMESPACE}' created."
