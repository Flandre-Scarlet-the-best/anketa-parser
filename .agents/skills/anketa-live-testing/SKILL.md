---
name: anketa-live-testing
description: >-
  Prove actual anketa-parser runtime behavior through the real CLI, Host,
  PostgreSQL, Docker stack and configured source instead of trusting unit tests,
  fixtures, static checks or green CI. Use when a change affects runnable
  behavior, CLI or JSON contracts, exit codes, configuration, readiness,
  ingestion, persistence, queue processing, recovery or container composition,
  and for live, smoke, integration, fault or recovery acceptance and runtime
  defect reproduction. Requires independently observable execution, provenance
  of the tested build, isolated data and honest verdicts. Not for prose-only
  changes, design discussion or independent code review.
---

# Live acceptance of anketa-parser

## Ownership

This skill owns **provability**: how to obtain independent evidence that the
real executable and its data path performed what a change requires, and how to
state the resulting verdict honestly.

It does not own neighbouring procedures:

- Command discovery, arguments, target selection and operation safety belong to
  the `anketa-cli` skill. Read the real `--help` and the implementation before
  running a command; this skill adds no command surface of its own.
- Applicability is owned by the verification policy in
  `.codex/context/03-VERIFICATION.md`, which `AGENTS.md` routes to;
  `.github/workflows/ci.yml` owns executable automation.
- Publication belongs to `git-workflow`; separately authorized external review
  belongs to `coderabbit-review`.
- Product contracts, configuration semantics and operating guides belong to
  `docs/` and the implementation, mapped by `.codex/context/02-INTERFACES.md`.

Read only the references the change needs:

- [Acceptance procedure](references/acceptance-procedure.md) for the pre-run
  specification, evidence classes, negative and recovery paths, verdicts,
  reporting and postconditions.
- [Project surfaces](references/project-surfaces.md) for the actual components,
  their owners and the interpretation traps that turn a weak observation into a
  false PASS.
- [Helper execution](references/helper-execution.md) for temporary drivers,
  observability, diagnostic intervals, cancellation, ownership and waiting.

## Activation

Applicability belongs to the verification policy in
`.codex/context/03-VERIFICATION.md`: whether a change requires proportional
runtime acceptance, and when a motivated `N/A` is acceptable, is stated there and
is not repeated here.

This skill is the route for changes that can affect runnable behavior: CLI
commands, arguments, machine output, exit codes or statuses, configuration
precedence and readiness, ingestion and filtering decisions, persistence and
migrations, queue and job transitions, repeat polling, recovery after a failure,
or container composition. Once a scenario is selected, this skill owns how it is
specified, executed, observed, judged and reported.

## Non-negotiable invariants

- **The real implementation is the object of verification.** A temporary helper
  may launch, schedule, isolate, observe, capture, compare and clean up. It MUST
  NOT replace the application's argument parsing, configuration resolution,
  source fetching, HTML extraction, contact or moderation filtering, snapshot
  writing, queue leasing, transaction handling, retry accounting or recovery
  logic with a miniature implementation, and it MUST NOT write a purported
  application result directly into product tables. Success of a wrapper is not
  success of the application.
- **Preserve every safeguard.** Never disable validation, timeouts, retry or
  backoff windows, readiness requirements, TLS policy, resource limits, lease
  protection or cleanup to obtain a green result. A reduced mode, fixture input
  or injected fault proves only that mode.
- **Prove the path that ran.** Before a run, name the changed path that must
  execute and the independent witness of its execution. A zero exit code, a
  started process, accepted Compose syntax, a cached or reused value, a created
  file or an observer's own message cannot by themselves establish the required
  behavior.
- **Identify the tested build.** Correlate the executed artifact with the
  revision, the uncommitted changes, the actual build command, the effective
  configuration and the target database. When provenance is uncertain, build
  normally before accepting, or report the uncertainty instead of implying that
  the current revision ran.
- **Isolate state and hold authority.** Writes go to a disposable, migrated
  database and run-owned Compose resources; inputs are synthetic or the tracked
  anonymized fixture. A request to inspect or diagnose permits read-only
  evidence, not migration, volume deletion, restart of a running Host, external
  live polling or restore. When a required operation is unauthorized or
  impossible in the environment, report the gap; never substitute a simulation
  for the missing run.
- **Separate evidence classes.** Fixture ingestion against an isolated PostgreSQL
  proves the application and database path and proves nothing about a successful
  request to the live source. A real source request over isolated storage adds
  the external boundary but does not by itself prove correctness of long-term
  data, coverage of negative scenarios or completeness of recognition. Cache
  reuse, real retrieval, source errors and unproven boundaries are reported
  apart.
- **Expectations are fixed before execution.** Derive them from the contract and
  the controlled input, not from the output of the component under test, and do
  not revise them after seeing a result.

## Verdict contract

Every mandatory scenario receives its own `PASS`, `FAIL`, `PARTIAL` or
`NOT_RUN` with the actual command, the verified build, the true process exit
code, separated stdout and stderr, independent postconditions and the boundary
that was really exercised.

Overall PASS requires `PASS` for every mandatory scenario and stated coverage of
the materially affected paths. A single `FAIL` forbids an overall PASS; an
unexercised mandatory path leaves the result `PARTIAL` or `NOT_RUN` with its
reason. Unit, contract, integration, database, external-source, container and
hosted-CI evidence are reported separately and never pooled into one claim.

## Long-running work

A short command needs no monitoring beyond start, outcome and error. A run that
may outlive the current turn needs an owner that survives it, real progress
witnesses, a diagnostic interval that is not a deadline, supported cancellation
and cleanup of only run-owned resources. Event-driven waiting is allowed only
through a mechanism actually supported and verified in the current environment,
described without depending on one agent harness: if resumption cannot be
verified, record the limitation and keep the independently completed evidence.
A monitor heartbeat is not application progress, silence is not a hang, and a
queued notification is not confirmed resumption.

## Artifacts and boundaries

Keep one-off drivers, plans, logs and evidence in ignored run-owned locations
such as `.codex/local/scripts/<task>/` and `.codex/local/reports/<task>/`, or in
a verified temporary directory outside tracked files; confirm ignore status
before writing and record cleanup. Do not commit drivers, logs, captures, dumps
or proof artifacts, and do not promote them into a permanent runner, a new CI
gate or a product change. Keep source text, contact values, credentials,
connection strings and raw external replies out of shared evidence. Reusable
regression logic becomes a tracked test only when the task authorizes a
permanent change.

## Procedure

1. Establish the revision, uncommitted changes, the build that will run, the
   effective configuration, the target database and the Compose project. Read
   [project surfaces](references/project-surfaces.md).
2. Map every materially affected behavior, state transition and failure path to
   a mandatory scenario with a predeclared expected result and an independent
   witness; decide which negative and recovery cases apply. Read the
   [acceptance procedure](references/acceptance-procedure.md).
3. Capture the baseline state and the isolation boundary before the first write,
   including any prior scan status, journal, backoff deadline, queue counts and
   schema state.
4. Prefer existing project checks; add the smallest temporary driver only for a
   missing real scenario or independent observation. Follow
   [helper execution](references/helper-execution.md) for observability and
   cancellation.
5. Execute the mandatory scenarios through the real entry points, recording
   real exits, separated streams, statuses and postconditions.
6. Verify postconditions independently of the component under test, and repeat
   runs to establish idempotency, progress preservation and absence of
   duplicates or losses where the change affects them.
7. Compare before and after: tracked files, durable data, processes, containers
   and volumes. Clean up only what belongs to this run, through its normal
   shutdown or the application's own maintenance operation.
8. Report each scenario's verdict, evidence and real boundary; state the overall
   result honestly and list remaining resources, skips and unproven paths.

<!-- output-language: ru -->
