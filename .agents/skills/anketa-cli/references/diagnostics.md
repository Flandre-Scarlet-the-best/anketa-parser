# Diagnostic scenarios

This reference explains **safe command execution and result interpretation**.
For independent runtime acceptance, see the sibling
`.agents/skills/anketa-live-testing/SKILL.md`; its CLI acceptance reference
owns multi-scenario proof and durable postconditions.

## Identify the exact executable

Run `--help` from the known built CLI, or from the repo via
`dotnet run --project src/AnketaParser.Cli -- --help`, and consult the
command group's help. A built DLL is preferable when capturing machine
stdout: a build may otherwise introduce unrelated output. Preserve real
exit status; capture stdout and stderr through separate pipes, parse
the documented JSON document and check its `status`, `exitCode` and
required fields. Evidence belongs in ignored local artifacts or an owned
temporary directory. Record executed build/worktree and sanitized target.

`doctor --json` reports the CLI's configuration and dependency view,
not what another Host process has fetched. A healthcheck running an
independent CLI also does not reveal Host in-memory state. To diagnose
the Host, verify the actual Compose project, selected source, database
and role, then use an authorized read-only command against the same
configuration. A query exit 0 does not prove the prior operation succeeded.

## Controlled source diagnosis

Use a disposable migrated PostgreSQL and anonymized fixture; consult
`docs/persistence.md` and `docs/dvach.md` for target setup and
actual fixture identities. The tracked sample
`tests/fixtures/dvach/thread.json` is test data; its IDs must not
become production defaults.

Start with `source help`. Use `source scan --fixture <path>` only
against isolated storage: it performs actual database writes despite
using a fixture. Then inspect `source status --fixture`,
`source post <number> --fixture` and `source queue --fixture`.
Check the decision and state, not only exit 0. A second controlled scan
can help diagnose repeated observations, but full idempotency proof
requires independently checked before/after records under
`anketa-live-testing`.

For failures preserve the original fixture and the last-success state.
Backoff is stored and may defer retry even when the next fixture is
malformed. To show preservation of a **previous success**, first prove
a real success on that same test source, then exercise the bad input;
a fresh empty source cannot support that claim.

If a changed fixture yields `fixture_conflict`, a pending durable
snapshot belongs to the earlier content: resume it with the original
fixture until complete. If that content is unavailable, use an
independent disposable source/database and label the run accordingly.
Do not delete or overwrite the pending journal just to remove conflict.
When storage is unavailable, inspect the intended role, database and
schema; `storage schema` reports schema state but grants no migration
permission. Redact fixture content, contact data and connection values
from shared reports.
