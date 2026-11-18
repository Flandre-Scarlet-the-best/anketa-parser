# Temporary helpers, observability and event waiting

This adapts the strict observable-helper rules from the source
`anki-live-testing` skill. It applies to one-off development drivers,
wrappers and monitors in any suitable language. It does not authorize
a new permanent framework or replace the real application lifecycle.

## Small, isolated and truthful

Prefer the existing CLI or project test runner; create a temporary
driver only to orchestrate a missing real scenario or independent
observation. Use ignored, run-owned paths such as
`.codex/local/scripts/<task>/` for source and
`.codex/local/reports/<task>/` for plans and bounded evidence, or a
verified temporary directory outside tracked files. Confirm ignore
status and ownership. Different concurrent worktrees/agents require
unique run IDs and distinct files. The driver may use C#, Rust,
Python, PowerShell, Bash or any appropriate language; that does not
license it to copy product algorithms.

Every executable helper MUST report START, terminal outcome and
an error reason. For a long, multi-stage or potentially blocking
helper, emit flushed, non-sensitive events:

- `START`: run ID, UTC time, safe target metadata, PID, phases and log path.
- `PROGRESS`: completed phase, actual counters or independently proven
  forward movement, elapsed time; no invented completion percentages.
- `WAITING`/`HEARTBEAT`: last known work state, wait category,
  signal source and elapsed wait.
- `SUCCESS`/`ERROR`/`CANCELLED`: actual terminal cause, UTC
  time, observed worker exit code when available and evidence location.

For short work START, outcome and error suffice; do not introduce
threads or a monitor for formality. When redirecting output, consume
stdout and stderr concurrently to avoid deadlocks. Bound event rates
and log size. Do not expose tokens, source text, contacts, full
provider responses, connection strings or sensitive exception
messages. A monitor heartbeat proves **only** its monitor is alive.
CPU or I/O activity does not prove required application progress.

## A diagnostic interval is not a deadline

Separate elapsed runtime, time since proven progress, time since the
last heartbeat and an actual requirement/protocol deadline. A
diagnostic interval triggers investigation, not immediate termination.
Choose it based on the operation; do not impose one arbitrary short
timeout on builds, providers and database recovery. Inspect actual
worker/child state, captured events, resource use, locks, sockets and
durable progress before deciding a hang occurred. Windows, Linux and
WSL expose different diagnostics.

Prefer supported cancellation and shutdown. Force termination only
for a justified confirmed failure, explicit cancellation or a
documented deadline. Preserve evidence and touch only run-owned
processes. Do not weaken product timeouts to wait longer.

## Event-based waiting outside the model

Do not repeatedly spend agent turns sleeping or polling solely to
wait for a long helper. When a worker may outlive the current turn,
use a **verified** independent monitor or a specialized repository
waiting skill if actually present, compatible and authorized.
This is conditional: naming a tool or finding it on another machine
does not prove it can resume the current session.

A legitimate out-of-model monitor:

1. Establishes actual OS, worktree, worker identity, owning session
   and the supported process wait/exit mechanism. Prefer spawning and
   directly waiting on the worker; otherwise require a trustworthy
   worker-written terminal record. Disappeared PID is not exit 0.
2. Persists run ID, safe events and real worker outcome. Before the
   agent turn ends, verify the monitor is alive, survives that turn,
   and has a valid delivery channel; only then claim it will monitor.
3. Assigns stable `<run-id>:<sequence>` keys to significant events
   and records each before notification. Send at most one notification
   per meaningful new actionable anomaly, with a bounded count; send
   a separate **single terminal** notification even after an anomaly.
   No model wakeups for repeated alarms, normal progress or heartbeat.
4. On delivery, reconciles the event key with the actual worker
   outcome and durable postconditions. Retries/duplicate notifications
   MUST NOT restart completed or ongoing work. Late events after
   cancellation only document cleanup, not another terminal wake-up.
5. Terminates the monitor after a terminal event; records failed
   launch, worker error, monitor failure, cancellation and delivery
   failure as different outcomes.

Only use harness-specific queue/resume syntax after verifying actual
tool capabilities and the correct session ID. Do not assume a
`codex queue` command, background `&`, `nohup` or a notification
implies the session actually woke. If survival/delivery/resumption
cannot be verified, mark it UNSUPPORTED and retain independently
completed evidence for later inspection. Do not promise unattended
continuation or leave an unowned background process.

## Verify the monitoring mechanism separately

When adding or changing a monitor, test it with a safe synthetic
worker, separate from product acceptance: actual successful exit,
nonzero exit, anomaly followed by terminal completion, event
deduplication, cancellation and unsupported delivery. Record times
for worker start, end of agent turn, notifications and confirmed
resumption, plus true exit, event IDs and cleanup. An absence of
LLM events is not proof of zero token cost.

A successfully delivered monitor message is **not** product PASS.
Use the main acceptance procedure for product evidence and the
PASS/FAIL/PARTIAL/NOT_RUN classification; the observer's success
never substitutes for genuine runtime behavior.
