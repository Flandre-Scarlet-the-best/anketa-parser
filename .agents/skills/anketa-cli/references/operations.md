# Operation boundaries

Establish the process, configuration, database role, database name and schema
state before running an operation, and confirm the target with a command that does
not change anything. Report sanitized target metadata — server, database, role,
configured thread identity — instead of a connection string, and keep credentials
out of command lines, shell history and shared logs.

Treat a local CLI, a CLI inside a container and an already running Host as
independent processes until their options, environment and database are verified.
A shared database is the only state they normally share.

## Classify the intent

| Intent | What it requires |
| --- | --- |
| Inspect diagnostics, stored source decisions, queue counts, schema state | A read-only command against the verified configuration. Inspection does not need extra permission beyond running the process, but it does open real connections. |
| Inspect backup metadata or list archives | A read-only backup command whose directory belongs to the intended installation, with the environment of that installation. |
| Reproduce an ingestion or source failure | A disposable migrated database and a sanitized fixture. Fixture polling writes product rows and a durable journal, so it must stay inside that disposable database. |
| Inspect LLM configuration, queue or revision decisions | Use the actual CLI and read `docs/llm.md` for output and configuration. A configured profile is not evidence of model availability. |
| Run or reprocess LLM work | Verify the exact database and revision, active model/profile and external cost boundary. Use a disposable migrated database, a sanitized fixture and a local engine for acceptance unless a remote profile is explicitly selected for the test. |
| Poll the real configured source | Explicit authorization covering that source and that target. Respect a stored retry deadline, and read the shared result afterwards instead of assuming the run changed it. |
| Change schema, rotate or verify backups, restore, alter roles | Authorization that names the operation and the exact target, plus the preconditions of `docs/persistence.md`. |

## Permission follows the request, not the tool

A request to investigate a fault permits read-only inspection of the configuration,
dependency view, stored state and logs. It does not authorize a migration, a
restore, a role change, a volume removal, a restart of a running Host or a live
source poll.

A status that reports a pending schema change is a finding to report, together
with the permission the operator would need. The command that would apply it
existing in the CLI is not authorization, and neither is a green local build.

An operation that writes should also explain what it will leave behind: new rows
in a named database, a new archive file, a changed service or a modified schema.
State that before running it, not after.

## Side effects worth naming

- A live source poll performs an external request and writes durable rows, queue
  entries and scan diagnostics; failures can still change stored state, for
  example by recording a failure and a retry deadline.
- A fixture poll performs no external request but writes the same kinds of rows
  under a separate fixture source identity.
- Storage maintenance commands change the schema, create or verify archives,
  rotate retention or restore into a target database; a backup daemon keeps
  running until cancelled.
- Diagnostic commands open real connections and run read-only queries, so they
  need a reachable target and a compatible schema to report a healthy result.

## Shared, foreign and persistent state

Never delete a volume, database, container, archive or temporary file to make a
symptom disappear. Other people's work may be represented by exactly that state:
verify the resolved name, ownership and creation source before touching anything,
and preserve unknown files and containers that this task did not create.

Do not restart or reconfigure a running Host or a shared stack without
authorization that covers it, and do not change the application's identity — the
Compose project name, the data volume name or the database name — while managing an
existing installation.

Pass secrets through the environment the application already reads, and do not
print resolved configuration. Follow `docs/operations.md`, `docs/persistence.md`
and `docs/security.md` for supported maintenance, target verification and log
safety.

## Cleanup

Remove only resources this run created and can identify by name: its disposable
database, its randomly named Compose project, its temporary settings or
environment file, its captures and helper scripts. Confirm they are gone
afterwards, and report what remains — including anything you deliberately left in
place because it was not yours. A task request never authorizes cleanup of a
working installation.
