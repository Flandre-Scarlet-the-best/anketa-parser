# CLI-first live acceptance

This reference supplies independent **runtime proof obligations**, not a
second CLI manual. The sibling `anketa-cli` skill owns command discovery,
argument contracts, target selection and operation permissions. Inspect
`src/AnketaParser.Cli/`, `.codex/context/02-INTERFACES.md`,
`docs/diagnostics.md`, `docs/dvach.md`, `docs/persistence.md` and
`docs/operations.md` for the current behavior owners.

## Verify the actual target and process

Distinguish the local CLI process, CLI in Compose, independently running
Host, PostgreSQL database and external source. Confirm the executing
worktree/build, effective configuration, selected database/role, Compose
project and source thread; log only sanitized descriptors. A local
`doctor --json` describes the **CLI's** view, not Host memory or the
last periodic scan by another process. Container health by itself does
not prove a particular source was processed.

For machine output prefer a known built DLL: `dotnet run` can introduce
build output. Capture the real process stdout and stderr separately and
record its exit code first; then parse JSON, `status`, `exitCode` and
required fields according to that command's actual contract. A zero
exit for reading a status does not prove the earlier scan succeeded.

## Applicable scenarios

| Behavior | Minimum independent witness | Misleading shortcut |
| --- | --- | --- |
| Help, version and bad arguments | Real command, streams, exit and documented machine output | Help cannot prove configuration or readiness |
| `doctor --json` | A single valid stdout document where guaranteed, stderr separation, real configuration and dependency outcome | Another Host's health is not measured |
| `storage schema` | Safe read of actual intended schema, matching a separate migration check | Reading status grants no permission to migrate |
| `source scan --fixture` | Real CLI filtering, journaling and write path in disposable migrated PostgreSQL | Fixture is not live HTTP |
| `source status/post/queue` | Same target database and source; report agrees with independently inspected durable state | Query exit 0 is not scan success |
| Repeated scan | Before/after revisions, observations and jobs, no unexplained duplicates, preserved history | Two success exits do not prove idempotency |
| Fault and resume | Expected failure phase, unchanged last success, durable checkpoint and actual later continuation | Successful new-source scan is not resumption |
| Network source | Explicitly authorized request, evidence of fetch and applied result, respected retries | Cached results and fixtures cannot prove network access |
| Backup and restore | Application verify/restore, isolated empty target and data postconditions | Dump existence is not restorability |
| Host / Compose | Same effective settings, observed background actions, health and owned cleanup | CLI from another environment cannot prove Host execution |

Select only scenarios actually required by the changed scope. Use inputs and
commands from the tested version rather than hardcoding fixture IDs, present
counts or transient feature names. The tracked anonymized source fixture
is a controlled input, not a production default.

`source scan --fixture` **writes** to PostgreSQL even though it does not
fetch over the network. A command with a read-oriented name may initialize
or alter internal state: inspect the actual owner before treating it as
non-mutating. A stored backoff or pending snapshot can affect malformed
fixture tests. Preserve the pending journal, use the original fixture to
resume it, or use a separate independent database and label the test
accordingly. Never delete the journal just to obtain green results.

## Authorization and reporting

A request to diagnose production permits read-only evidence collection,
not provisioning, migration, archive rotation, restore, service restart
or manual live source polling. Verify the exact operation and target
authorization before persistent changes or external requests. Prefer
isolated PostgreSQL, synthetic/anonymized data, unique Compose resources
and the application's own cleanup. Keep contacts, raw source text,
credentials and connection strings out of shared logs and PRs.

For complete verdict and evidence rules use
[Acceptance procedure](acceptance-procedure.md). For owned long-running
helpers use [Helper execution](helper-execution.md).
