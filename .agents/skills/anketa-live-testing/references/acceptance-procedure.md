# Live acceptance procedure

This reference specifies proof of **actual product execution**, not CLI syntax,
the general test matrix, publication steps or provider review. See
[Live and smoke acceptance](../SKILL.md).

## Specification before execution

Map every materially affected behavior, failure path, state transition and
requirement to an independently verifiable scenario. Before each run record
in ignored task evidence:

1. Actual worktree and revision, uncommitted modifications, tested DLL/image,
   build provenance, runtime, configuration and sanitized target identity.
2. For each mandatory scenario, the input and baseline state, expected result
   **derived from the contract and controlled input**, affected implementation
   path, independent execution witness, and applicability of a negative case.
3. Isolation and permission boundaries, possible writes, external calls,
   ownership of processes and volumes, cleanup and resource constraints.
4. Objective PASS/FAIL criteria, what makes evidence incomplete, diagnostic
   intervals distinct from real deadlines, and observation plan.
5. Witnesses appropriate to the operation: actual process exit and separate
   stdout/stderr, JSON fields, UTC events, database rows, revision/queue
   counts, source responses, checkpoints, image/build identity or files.

Do not determine expected values from the same implementation's output,
update them after seeing a result, or silently drop a failing scenario.
A fixture, replay or cached response proves only that route. Real network
retrieval requires an authorized request and evidence that it happened.

## Genuine execution, not a second implementation

Prefer the existing CLI, Host, Docker tests and project-owned helpers. A
temporary driver may schedule the actual executable, isolate input, capture
true exits and logs, enforce safe limits, and independently compare results.
It MUST NOT reimplement the application's domain algorithm, write the
supposed output directly into product tables, substitute a mocked provider
for a claimed live interaction, fabricate checkpoints or weaken safety
checks. Fault injection in an authorized mode proves that mode; if the
requirement is normal behavior, test the normal mode too.

Tie the tested executable to actual source and working changes. Record the
build and file/image location, or perform a fresh normal build if provenance
is uncertain. The CLI invoked in one directory, another running Host, and
a prebuilt container may be different builds and configurations. A HEAD
included in a report is not evidence that that specific binary ran.

## Independent observations

- Capture true product process exit, stdout and stderr separately, and
  inspect status, nested fields and semantics according to the actual
  command contract. Success of a wrapper is not success of the product.
- Verify durable postconditions independently through an authorized
  read-only database operation or another behavior owner; compare with
  state captured before execution.
- Prove the intended path: an actual fetch versus a reused cache, applied
  snapshot versus simply fetched JSON, readiness query versus a live Host,
  a completed backup/verification versus file existence.
- For repeats, confirm documented idempotency or revision/history changes
  without duplicates. For failures, inspect category, phase, retry deadline,
  unchanged last-success state and durable progress.
- For cancellation and recovery, identify the last committed checkpoint,
  preserved journal, resumed work and absence of duplicates or losses.
  A fresh successful run with different data does not prove interrupted
  work resumed.
- For backup/restore, verify an authorized disposable target, source archive,
  actual restoration and resulting data; archive length alone is insufficient.
- For Host/Compose, observe the intended process, effective database/source,
  actual health and background work; a separate healthy CLI or container
  cannot prove what the Host processed.

A deliberate negative scenario is PASS only if the *actual owner* rejects
the controlled input as predicted and postconditions hold. It does not
establish the positive scenario. Distinguish failure of the observer from
failure of the product; a disappeared PID does not imply success.

## Status classification

| Status | Meaning |
| --- | --- |
| `PASS` | The required real path was executed; expected outcome and postconditions independently proved |
| `FAIL` | An adequately observed product path violated its predeclared contract |
| `PARTIAL` | Some execution occurred, but meaningful evidence, coverage or attribution is missing |
| `NOT_RUN` | A required scenario never executed, including missing prerequisites before launch |

An observer crash during a witnessed but unconfirmed run produces PARTIAL,
not assumed product FAIL or PASS. Unavailable prerequisites before execution
are NOT_RUN. An external failure requires independently supported attribution;
do not blame the provider without showing the failed phase. If any mandatory
scenario FAILs, overall PASS is impossible. Any unverified mandatory scenario
makes the overall result incomplete, with its reason explicit.

For every scenario report sanitized inputs/target, actual commands, verified
binary, true exit and status, independent evidence, conclusion and limits.
Separate unit/contract evidence, fixture integration, real database,
external-provider and hosted-CI evidence. Overall PASS is allowed only when
every mandatory scenario PASSes and materially affected paths are covered.

## Temporary artifacts and postconditions

Temporary scripts live in an ignored per-task area such as
`.codex/local/scripts/<task>/`; bounded logs, plan and evidence pointers
in `.codex/local/reports/<task>/`. Verify ignores before writing; keep unique
run/worktree ownership. Helpers can use any appropriate language, but must
not gain authority to duplicate the implementation. Automate objectively
checkable assertions rather than manually declaring each output correct.
Human inspection is acceptable only when it cannot be automated and is
separately identified.

Compare state before/after: Git changes, persisted data, processes,
containers and file ownership. Clean up only resources confirmed as
belonging to this run, by their normal shutdown protocol. Never delete a
persistent volume or unknown directory to hide failure; report remaining
resources and uncertainty. Do not publish source messages, contact values,
credentials, connection strings or raw external replies. Promote reusable
regression logic into tracked tests only when the task authorizes a
permanent change; do not commit one-off drivers, logs or proof artifacts.
