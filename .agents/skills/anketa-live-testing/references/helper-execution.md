# Temporary helpers, observability and waiting

This reference governs one-off drivers, wrappers, observers and monitors used
during development and live acceptance, in any suitable language. It does not
authorize a permanent framework, an application change or a replacement for the
product lifecycle, and it does not depend on any particular agent harness.

## 1. Proportionality

Prefer the existing CLI, the project test suites and documented maintenance
commands. Add a temporary driver only for a missing real scenario or an
independent observation that no existing check provides. Keep one-off sources in
an ignored run-owned location such as `.codex/local/scripts/<task>/` and plans,
bounded logs and evidence pointers in `.codex/local/reports/<task>/`; confirm the
ignore rule and the ownership of the path before writing, and use distinct
directories when several agents or worktrees run concurrently.

The driver may use any language and any appropriate library. That gives it no
authority to copy product algorithms, to hold product secrets, or to become a
required build dependency of the repository.

A short local action needs a start line, a terminal outcome and an error reason.
Do not introduce threads, supervisor processes or extra dependencies for
formality. A long, multi-stage or potentially blocking action needs the event
contract below.

## 2. Required events

Report from inside the helper, not only from outside it, and flush important
events immediately instead of buffering them to the end:

- `START`: run identifier, UTC time, purpose, safe target metadata, process
  identifier, expected phases and the log path.
- `PROGRESS`: a completed phase, real counters or another independently
  established sign of forward movement, elapsed time. Do not invent a percentage
  when the total is unknown.
- `WAITING` or `HEARTBEAT`: last known state, the kind of wait (child process,
  network, external dependency), and how long the wait has lasted.
- `SUCCESS`, `ERROR` or `CANCELLED`: terminal outcome, UTC time, duration, the
  observed worker exit code when available, and the location of results and
  diagnostics.

Bound the event rate and the log size; aggregate instead of logging every item.
When output is redirected, consume stdout and stderr concurrently so neither
stream blocks the other. Never record tokens, contacts, source text, raw
external replies, connection strings or exception messages that may contain
them. Keep the console view when one exists.

A heartbeat proves only that its own source is alive. It does not prove that the
process under test advanced, and sustained CPU or disk activity does not prove
that acceptance criteria were reached. Keep monitor liveness, worker liveness
and confirmed progress as three separate facts.

## 3. A diagnostic interval is not a deadline

Before a long run, establish the expected phases and objective signs of normal
work: counters, CLI events, finished artifacts, a legitimate wait for an external
dependency, a changing checkpoint. Distinguish four different times:

1. elapsed since start;
2. elapsed since the last proven progress;
3. elapsed since the last liveness signal;
4. an actual deadline confirmed by the task, a protocol or an agreed budget —
   which may not exist at all.

Choose diagnostic intervals from the nature of the phase and the real
constraints of the operation. Do not impose one universal short timeout on
builds, database recovery, external requests and long ingestion alike. Exceeding
a diagnostic interval means **investigate**, not "terminate": absence of output,
low CPU load or a slow network does not by itself prove a hang.

When a stall is suspected, collect the last worker and monitor records, the child
process tree and status, observable resource use, locks, sockets, open files and
durable progress, using only the diagnostics the current operating system
actually provides. Treat the wait as normal when the phase explains it.

## 4. Ownership and cancellation

One run has one owner for the worker and the monitor lifecycle. Prefer the owner
that spawns the worker and holds a real wait handle or receives its exit, so the
true exit code and the terminal cause are recorded rather than guessed. When
that is impossible, rely on a trustworthy terminal record written by the worker;
an exit code that was never observed is reported as unknown, and a disappeared
process identifier is never interpreted as success.

Prefer the supported shutdown or cancellation path of the product and of the
container runtime. Force termination is justified only by a confirmed failure, an
explicit cancellation or a documented deadline, and it happens after available
evidence is preserved. Stop only processes, containers, volumes and files
belonging to this run; never destroy a user process, an unknown container or
another worktree's evidence. After cancellation, record whether cleanup was
confirmed or only attempted.

Cancellation, retry and cleanup outcomes are distinct: cancellation followed by
a late worker exit does not restart the work and does not produce a second
terminal verdict.

## 5. Waiting without holding the model

Do not spend agent turns sleeping or polling only to wait for a long worker.
When further progress depends solely on that worker and the run may outlive the
current turn, hand the wait to something that does not need the model, or state
plainly that the wait is covered manually.

Event-driven resumption is optional and environment-specific. Use it only when
the current environment actually supports and has verified: surviving the end of
the agent turn, a durable record of the real worker outcome, and a delivery path
that reaches the same session. Any syntax, identifier or session format of a
particular harness is a local convenience, never a requirement of this
procedure, and naming one does not make it available. When resumption cannot be
verified, record the limitation, keep the independently completed evidence for
later inspection, report background continuation as unsupported, and do not
leave an unowned background process behind.

## 6. Significant events and notifications

When a monitor is used, it assigns each significant event a stable key of the
form `<run-id>:<sequence>`, records the event before dispatch, and sends at most
one notification per key. Deduplication is per event key, not per run: an early
notification does not forbid a later one that reports a genuinely new
transition.

- **Actionable anomaly** — a non-terminal transition that may need intervention.
  A new key is created only for a new transition: the anomaly appearing, changing
  kind, or returning after resolution. A persisting identical state, a heartbeat,
  a repeated inspection and the diagnostic interval itself create no new key.
  Bound the number of such notifications per run and record an exhausted limit
  in the log instead of waking indefinitely.
- **Terminal outcome** — exactly one per run: success, error, cancellation or the
  monitor's own failure. It is always notified, even after earlier anomaly
  notifications, and the monitor then records the outcome and stops.
- **Redelivery** of the same key, for example after an unknown delivery status or
  a repeated log read, is a duplicate. The receiver is idempotent: a duplicate
  neither restarts the work nor changes an earlier conclusion.
- **Already handled event** — after handling an event, record its key with the
  evidence so a redelivered notification is only reconciled against the log.
- **Late events after cancellation** — a worker exit or other late signal after
  cancellation is recorded as a cleanup fact; there is no second terminal
  notification and no resumption.

A persisted event is not a delivered notification. Distinguish recorded,
attempted, acknowledged and confirmed-resumption states explicitly; exactly-once
delivery cannot be inferred from a local log or a successful send call.

## 7. Verifying the mechanism itself

When a monitoring or resumption mechanism is introduced, or the environment
changes, test it once on a safe synthetic worker, separately from product
acceptance:

- **Success:** the turn ends before the worker finishes; the monitor records the
  real completion and its true result; one terminal notification is sent; the
  same session actually resumes without a new user message.
- **Error:** the worker returns a non-zero code; the monitor does not report
  success and wakes the agent for analysis.
- **Anomaly then completion:** an anomaly is notified while the worker is still
  running, the agent handles it and ends its turn, and a later terminal event
  with a new key still arrives. Redelivery of either key restarts nothing, and
  the persisting anomaly and heartbeats produce no further notifications.
- **Cancellation or unsupported delivery:** an unambiguous local status exists,
  with no duplicate wake-ups, no orphan processes and no false verdict; a late
  event after cancellation does not wake the agent again.
- **Evidence:** separate timestamps for worker start and end, end of the agent
  turn, each notification and each confirmed resumption, plus event keys, process
  identity, the true exit code or its explicit unknown state, logs and
  postconditions.

This check establishes only that the mechanism works in this environment. A
successful monitor is never product `PASS`; the verdict comes from the
[acceptance procedure](acceptance-procedure.md), and the observer's own success
does not substitute for real runtime behavior.
