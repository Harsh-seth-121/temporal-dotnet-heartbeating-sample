# observability/

Everything the metrics stack needs, in one directory. Nothing here is installed
globally. You need Docker Desktop, plus python3 if you regenerate or probe the
dashboards.

```
compose.yml                     eight services + volumes + network
.env                            image pins and Postgres credentials
dynamicconfig/                  MANDATORY server config
scripts/                        idempotent schema init + namespace creation
prometheus/prometheus.yml       six scrape jobs
grafana/provisioning/           datasource + dashboard provider
grafana/dashboards/             dashboard JSON, one Grafana folder per directory
grafana/build-dashboards.py     generator for the four authored boards
grafana/probe-dashboards.py     proves every panel returns data
```

## Up and down

```bash
docker compose up -d
docker compose down       # keep all data
docker compose down -v    # full reset (REQUIRED if you change NUM_HISTORY_SHARDS)
```

The same three commands work unchanged from the repo root. `../compose.yml` `include`s
this file and pins the project name to `temporal-dotnet-sandbox`, so both directories
drive one stack.

`../scripts/demo-up.sh` and `../scripts/demo-down.sh` wrap these three and also handle
the host processes, which compose has never heard of. See [../docs/DEMO.md](../docs/DEMO.md).

That project name is deliberately **not** `temporal-sandbox`, which is what the Go
project this repo was ported from uses. Sharing it means `docker compose up` here
silently adopts that stack's containers and volumes, and `down -v` from either repo
wipes the other's data. The `container_name` and network `name` values are prefixed for
the same reason: those are global to the Docker daemon, not scoped to the compose
project.

| URL | What |
|---|---|
| <http://localhost:8080> | Temporal Web UI |
| <http://localhost:3000> | Grafana (anonymous Admin, no login) |
| <http://localhost:9090/targets> | Prometheus target health |
| <http://localhost:9091> | Pushgateway |
| <http://localhost:8000/metrics> | Temporal server metrics, not SDK |

## What each process emits

| Process | Transport | Port | Emits |
|---|---|---|---|
| Temporal server (container) | scraped | 8000 | 233 metric families, 27 of them `temporal_*` from its own embedded Go SDK workers |
| `Repro.Worker` (host) | scraped | 8077 | SDK metrics + `repro_*` custom metrics |
| `Repro.LoadGen` (host) | scraped | 8078 | same, under continuous traffic from THREE start loops |
| `Repro.Starter` (host) | pushes on exit | 9091 | SDK **client** metrics only |
| `Repro.Replay` (host) | opt-in `--metrics` | 8079 | **nothing**, 200 with an empty body |

`demo-up.sh` starts the first three. `Repro.Replay` is always manual.

`Repro.Starter` pushes client metrics only. `repro_workflow_*` and `repro_activity_*`
come from the worker, because workflow and activity code does not execute in the
starter.

`--metrics <addr>` overrides the configured port on worker, loadgen and replay.
`--metrics off` starts no exporter and binds no port at all, which is how you run a
second worker on this host without fighting the first one for :8077. See
[../docs/CONFIG.md](../docs/CONFIG.md).

## Before you debug this stack

- [../docs/GOTCHAS.md](../docs/GOTCHAS.md) lists 29 behaviors that look exactly like
  bugs, worst first. Absent counters, integer-millisecond histograms, label-value
  asymmetry, the mandatory dynamicconfig mount.
- [../docs/DASHBOARDS.md](../docs/DASHBOARDS.md) covers `probe-dashboards.py`, the
  every-target-renders result, and which imported panels are known-empty and why.
- [../docs/WORKFLOWS.md](../docs/WORKFLOWS.md) lists all 16 `repro_*` metrics with their
  kinds, tags and tag values, and which of the three workflows emits each one. Check a
  selector there before assuming a panel is broken.
