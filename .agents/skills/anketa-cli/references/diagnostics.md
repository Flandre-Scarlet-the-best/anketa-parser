# Diagnostic scenarios

## Identify the process

Run `--help` from the built CLI, or forward it with
`dotnet run --project src/AnketaParser.Cli -- --help` from the repository.
For structured output prefer a built DLL so build messages cannot enter the
stdout capture. Read the command group's help before choosing arguments.
Capture stdout into one file and stderr into another over normal pipes. Parse
stdout only after saving the process exit code. Check both the reported state
and the numeric exit code. Keep evidence in ignored local artifacts or a
temporary directory and record which build and target were used.

`doctor --json` diagnoses the CLI process and its dependency view. Its output
cannot establish what an existing Host is polling. To inspect a running
container, first verify the actual Compose project, service and configuration.
Run a read-only command in that container when its connection and environment
are required. Read source status from the same database and thread as Host.

## Reproduce source selection

Use an isolated migrated PostgreSQL and an anonymized fixture. Follow the
database guide `docs/persistence.md` to resolve the database owner and the
source guide `docs/dvach.md` to resolve the fixture identity. The tracked sample
`tests/fixtures/dvach/thread.json` uses thread
`100000`; avoid assuming fixture numbers are production IDs.

1. With the test configuration, run `source help`, then `source scan --fixture
   <path>`. Confirm successful completion, counts and application progress.
2. Inspect `source status --fixture`, `source post 100017 --fixture` and
   `source queue --fixture`. The post is a quote-only contact case and should
   return `no_own_contact` with no job. Queue counts describe current and
   historical jobs independently.
3. Rerun the same scan and compare repeated posts and queue counts. To reproduce
   malformed input, write a separate broken fixture and use a fresh test thread
   or database if a prior failure is in backoff. The new target has no previous
   successful scan, so run a successful scan there before breaking the fixture;
   otherwise the comparison does not prove that the failing run kept the earlier
   success. If the target is unchanged, wait out the backoff instead. Compare the
   failure phase and last successful scan. Keep fixture contents out of reports.
   If `source scan --fixture` reports `fixture_conflict`, the stored journal
   belongs to another file of the same thread: rerun with that file instead of
   switching fixtures mid-import.

A zero exit code from `source status` proves that CLI read the stored status.
Inspect the outcome and last success time to tell whether a scan succeeded.
If storage is unreachable, verify the connection role, database and schema on
the intended test target. Use `storage schema` for schema status.
