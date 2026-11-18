---
name: anketa-live-testing
description: >-
  Prove real anketa-parser behavior through live, smoke, integration, recovery
  and fault-acceptance runs using its actual CLI, Host, PostgreSQL and Docker.
  Use for runtime acceptance or reproducing a real-world failure; not for routine
  unit tests, simple CLI help, design-only work or independent code review.
  Require independently observable execution and isolated data.
---

# Live and smoke acceptance

## Responsibility

Verify the **actual implementation** via its real executable, environment,
dependencies and persistence contracts. Apply when a task requests or materially
requires live/smoke/integration acceptance, runtime reproduction, failure and
recovery verification, or a behavior comparison. Unit tests, static checks,
green CI, and the success of a test wrapper do not constitute live acceptance.

The sibling `anketa-cli` skill owns discovery, execution and safe operation of
CLI commands. This skill owns independent behavioral evidence, scenario coverage,
runtime witnesses and acceptance verdicts. `code-intelligence` handles semantic
development tooling; `git-workflow` owns publication; `coderabbit-review`
owns separately authorized external review. Do not duplicate their procedures.

Read only applicable references:
- [Acceptance procedure](references/acceptance-procedure.md) for scenario mapping,
  proof, negative cases, verdicts and postconditions.
- [CLI acceptance](references/cli-acceptance.md) for CLI-first verification
  across process, source and database boundaries.
- [Project surfaces](references/project-surfaces.md) for actual component owners
  and evidence levels.
- [Helper execution](references/helper-execution.md) for temporary scripts,
  extended runs, observation, cancellation and supported event delivery.

## Non-negotiable invariants

- **Exercise the real behavior owner.** A helper may launch, observe, capture
  and compare; it MUST NOT replace application parsing, source fetching,
  filtering, persistence, retries, backup or recovery logic with a miniature
  implementation. It MUST NOT directly write a purported application result
  or manufacture a passing report.
- **Identify the tested build.** Correlate DLL/image, actual build command,
  revision, uncommitted changes, current worktree and target configuration.
  A printed HEAD or old binary alone cannot prove changed code executed.
- **Prove the required path.** Before each run identify the changed behavior,
  independently expected outcome and witness of execution. Exit 0, a healthy
  process, a cached entry or wrapper success alone cannot prove that path ran.
- **Preserve all safeguards.** Never disable validation, TLS, resource limits,
  retry windows, data protection or cleanup to force PASS. A fixture,
  deliberately injected fault or test mode proves only its own mode, not
  a successful external-provider interaction.
- **Isolate state and obtain authority.** Use anonymized/synthetic inputs,
  disposable migrated PostgreSQL and run-owned Compose resources for writes.
  Verify operation, target, permission and side effects before touching an
  existing Host, database, external source or backup. Never delete unknown
  state or other worktrees' resources.
- **Do not rewrite expectations after execution.** Map all materially changed
  paths to obligatory scenarios before running them. Explain any uncovered
  path; do not reinterpret an easier path as the required one.

## Outcome contract

Inspect the genuine process exit code, separate stdout/stderr, structured
status, durable state and independent postconditions. For each mandatory
scenario report `PASS`, `FAIL`, `PARTIAL` or `NOT_RUN` with evidence and
limitations. An intentionally rejected invalid operation passes only if the
expected refusal and state invariants were proved; it does not prove the
positive path. Overall PASS requires independently proved PASS for **every**
mandatory scenario, including the affected code paths. Keep one-off drivers
and raw evidence ignored, with recorded cleanup and any remaining resources.

Do not confuse a monitor heartbeat with application progress, silence with
a deadline, or a queued notification with confirmed agent resumption.
Use only a verified mechanism available in the current harness and environment;
otherwise report the limitation rather than claiming background continuation.

<!-- output-language: ru -->
