#!/bin/sh
# Register EVERY namespace this stack needs, once the frontend is healthy.
# temporalio/server does not create namespaces (the deprecated auto-setup image did).
#
# TWO namespaces, not one, and the second is not organisational. WorkflowLocalActivity needs
# `history.workflowTaskHeartbeatTimeout` dropped from its 30m default to 1m, and that setting
# is declared server-side as NewNamespaceDurationSetting: it filters by NAMESPACE and by
# nothing finer -- not task queue, not workflow type. A dedicated namespace is therefore the
# only way to lower it for that workflow while the other three keep the stock default. The
# override itself lives in ../dynamicconfig/development-sql.yaml.
set -eu

# Space-separated, iterated with a plain `for`. This runs under /bin/sh in the admin-tools
# image, which may be BusyBox ash: no arrays, no bashisms.
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

# NOTE: retention is only applied at CREATION. Changing DEFAULT_NAMESPACE_RETENTION
# and re-running `docker compose up` does NOT update an existing namespace -- the
# describe branch below skips it. To change it on a live stack:
#   temporal operator namespace update -n <namespace> --retention 7d
#
# `continue`, NOT `exit 0`, and that one word is the whole reason this loop exists rather
# than a second copy of the block. The original exited on the first namespace that already
# existed. On every stack created before the local-activity case -- which is every stack that
# has not been torn down with --volumes -- `default` exists, so an early exit would silently
# never create the second namespace. The failure then surfaces minutes later and somewhere
# else entirely, as an opaque namespace-not-found at worker connect time, with this container
# having exited 0.
for ns in ${NAMESPACES}; do
  if temporal operator namespace describe -n "${ns}" --address "${ADDRESS}" >/dev/null 2>&1; then
    echo "Namespace '${ns}' already exists (retention unchanged; see note above)."
    continue
  fi

  echo "Creating namespace '${ns}' with ${RETENTION} retention ..."
  temporal operator namespace create -n "${ns}" --retention "${RETENTION}" --address "${ADDRESS}"
  echo "Namespace '${ns}' created."
done
