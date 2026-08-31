# Capture and replay a history

```bash
temporal workflow show --workflow-id repro-workflow --output json > history/heartbeat-job.json
dotnet run --project src/Repro.Replay -- --history history/heartbeat-job.json

# All THREE committed fixtures at once. `history/` holds heartbeat-job.json,
# simple-no-activity.json and workflow-simple-activity.json. The second carries
# WORKFLOW_EXECUTION_UPDATE_ACCEPTED and _UPDATE_COMPLETED events, and MEASURED,
# HistoryJsonFixer handles those enum shorthands from `workflow show --output json` with
# no help.
dotnet run --project src/Repro.Replay -- --history history/
```

`Repro.Replay` must be given every workflow type it might meet. It FAILS on a history
whose type it was not registered with. The failure is loud and specific, not a disguised
nondeterminism error. MEASURED, with `WorkflowSimpleActivity` unregistered:

```
replay OK: history/heartbeat-job.json
replay OK: history/simple-no-activity.json
replay FAILED: history/workflow-simple-activity.json
  Temporalio.Exceptions.InvalidWorkflowOperationException: LangFail: ...
  "Workflow type WorkflowSimpleActivity is not registered on this worker,
   available workflows: HeartbeatWorkflow, SimpleNoActivity"
  ApplicationFailureInfo type "NotFoundError"
```

Note what that shows: the registered fixtures still pass, only the unregistered one fails,
and the message names both the missing type and the available ones. There is no
`WorkflowNondeterminismException` and no `TMPRL1100`, which are the two markers of a REAL
nondeterminism error, the same discriminator this file gives further down.

## Capturing the simple-activity fixture

`workflow-simple-activity.json` is 11 events with one
`ACTIVITY_TASK_SCHEDULED` / `_STARTED` / `_COMPLETED` triple at eventIds 5/6/7, and the gap
between `_STARTED` and `_COMPLETED` is the activity's 5s sleep. `heartbeat-job.json` has the
same 11-event shape and the same triple, so that is not what makes this one worth keeping.

What is new is what the triple CARRIES: a `ScheduleActivityTask` with no `heartbeatTimeout`
and no `scheduleToCloseTimeout` -- the minimal options object, where the heartbeat fixture
has 5s and 3600s, and an `ActivityTaskCompleted` payload holding a `WeatherReading`, whose
name-keyed fields are the replay contract described in `SimpleActivityJob.cs`.

Every `SimpleActivityInput` parameter has a default, so a fixed ID needs no input JSON
beyond `{}` and there is nothing to grep out of `workflow list`:

```bash
temporal workflow execute \
  --task-queue repro-task-queue \
  --type WorkflowSimpleActivity \
  --workflow-id repro-weather-fixture \
  --input '{}'

temporal workflow show --workflow-id repro-weather-fixture --output json \
  > history/workflow-simple-activity.json
```

Capture the LIVE path, not the synthetic fallback. The two produce structurally identical
histories, because the fallback is inside the activity so only the `ActivityTaskCompleted`
payload differs. A fixture carrying a real temperature is self-documenting, though. Payload
property names are PascalCase; the .NET converter does not camel-case them.

`--history` also accepts a directory, and replays every `*.json` in it.

There is **no `--fields` flag anywhere** in CLI 1.8.1, not on `show`, not on `list`, not
on `describe`. All three answer `Error: unknown flag: --fields`, so a guide that tells
you to reach for it is written against a different CLI. Plain `--output json` is what
the replayer consumes, and `WorkflowHistory.FromJson` handles the CLI's enum shorthands
itself.

History JSON format is tied to server version. Recapture rather than reuse a history
across a server upgrade.

## Catching a nondeterminism error

Change `HeartbeatWorkflow.workflow.cs`, replay the old history, and a replay error tells
you the change is not backward compatible. Measured, after inserting a
`Workflow.DelayAsync` before the activity call:

```
Temporalio.Exceptions.WorkflowNondeterminismException: Nondeterminism:
  [TMPRL1100] Nondeterminism error: Timer machine does not handle this event:
  HistoryEvent(id: 5, ActivityTaskScheduled)
```

Exit code is 1. Match on the **type** (`WorkflowNondeterminismException`, a subclass of
`InvalidWorkflowOperationException`), not on the message. The `TMPRL1100` code does
appear, but it comes from the Rust Core, not the managed SDK. You will not find that
string anywhere in `sdk-dotnet`.

## The replayer emits no metrics, and .NET hides that better than Go

Go's replayer hard-codes a no-op metrics handler, so the attempt fails loudly.
`WorkflowReplayerOptions` *does* accept a real `TemporalRuntime`, which looks like an
improvement and is a trap: Core starts a real HTTP listener and `/metrics` answers
**200 with a zero-byte body**. Point a Prometheus job at it and the target reads UP
forever while every panel stays blank.

Measured, and reproducible:

```bash
# :8079 is not scraped by Prometheus, so nothing else can contaminate the result.
# --metrics holds the endpoint open for 30s, since a replay itself takes ms.
dotnet run --project src/Repro.Replay -- --history history/ --metrics 0.0.0.0:8079
curl -sS -o /dev/null -w '%{http_code} %{size_download}\n' localhost:8079/metrics
# 200 0
```

Do not spend time instrumenting replay.
