#!/bin/sh
# Register every namespace this stack needs, once the frontend is healthy.
# temporalio/server does not create namespaces (the deprecated auto-setup image did).
# Two of them, because WorkflowLocalActivity needs history.workflowTaskHeartbeatTimeout
# dropped from its 30m default to 1m and that setting filters by namespace and nothing
# finer. The override lives in ../dynamicconfig/development-sql.yaml.
set -eu

# Space-separated and iterated with a plain `for`: /bin/sh in the admin-tools image may
# be BusyBox ash, so no arrays and no bashisms.
NAMESPACES="${DEFAULT_NAMESPACE:-default} ${LOCAL_ACTIVITY_NAMESPACE:-repro-local-activity}"
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

# Retention applies only at creation. Changing DEFAULT_NAMESPACE_RETENTION and re-running
# `docker compose up` does not update an existing namespace; on a live stack use
#   temporal operator namespace update -n <namespace> --retention 7d
#
# `continue`, not `exit 0`: `default` already exists on every stack made before the
# local-activity case, so an early exit would silently never create the second namespace
# and the failure would surface later as an opaque namespace-not-found at worker connect
# time, with this container having exited 0.
for ns in ${NAMESPACES}; do
  if temporal operator namespace describe -n "${ns}" --address "${ADDRESS}" >/dev/null 2>&1; then
    echo "Namespace '${ns}' already exists (retention unchanged; see note above)."
    continue
  fi

  echo "Creating namespace '${ns}' with ${RETENTION} retention ..."
  temporal operator namespace create -n "${ns}" --retention "${RETENTION}" --address "${ADDRESS}"
  echo "Namespace '${ns}' created."
done
