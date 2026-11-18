# Diagnostic scenarios

This reference covers identification of the executable, its configuration, process
and database, reading of machine documents, and a controlled source diagnosis.
Independent proof of runtime behavior — expected results, postconditions, coverage
and verdicts — belongs to the sibling skill `anketa-live-testing`.

## Identify the executable and its provenance

Launch the build output you intended to test and record what produced it. Prefer
a DLL built for the current task: running the build through the SDK can mix build
output into a captured stream, which corrupts a machine document.

A prebuilt artifact found on the machine is acceptable only when you can state how
it corresponds to the code under test — a build you performed from this worktree,
a build time consistent with the revision, or a hash compared with a fresh build.
If you cannot establish that, say the provenance is unproven instead of implying
that the current revision ran. Then read `--help` and the relevant command group's
help from that exact executable; the current parser, not an older example, defines
which arguments exist and where they are accepted.

## Identify configuration, process and target

The configuration root is the directory of the program, not the repository root,
so a settings file placed in the checkout does not apply to a binary under a build
output directory. Environment-specific JSON and `ANKETAPARSER_*` variables
override it in the order owned by `docs/configuration.md`. For a one-off run,
environment variables are the portable override; one directory's settings never
travel to another process.

Use the machine report as an identity check before trusting any result: the
runtime block names the environment, process identifier, container flag and
instance; the configuration block lists the participating sources and a
credential-free description of the database target with its required flag and
timeouts. Compare those values with the database, role and container you believe
you are using, and report that sanitized description rather than a connection
string.

Keep these processes apart:

- a local CLI process with its own configuration and working directory;
- a CLI executed inside a container, which needs the verified Compose project and
  service and uses that container's environment, not your shell;
- a running Host, with its own configuration, its own schedule and its own
  readiness memory.

Sharing one database is normally the only state they share.

## Read the machine documents

Save the process exit code before parsing anything, and keep stdout and stderr in
separate captures. In machine-output modes stdout carries one JSON document and
human explanation and logs go to stderr; help output stays text even when the
machine flag is present. Documents have different owners and independent schema
versions — a full diagnostic report, a short version document, and the source
group's own envelope are not interchangeable. Field and value meanings belong to
the implementation and the guides; do not infer them from this skill.

Non-ASCII detail text is escaped inside the document, so compare decoded values
rather than raw bytes when you look for a specific message.

The exit code is a shared contract whose meaning still depends on the command. A
nonzero code can classify a refusal, an unusable configuration, an unavailable
dependency or a failed operation, and a zero code can accompany an unsuccessful
stored outcome. Read the reported status, phase and failure category as well, and
never translate a code into a conclusion on its own. If cancellation or an
internal failure produced no document at all, report the absence and the captured
stderr instead of inventing a status.

Argument position is part of the contract: the source group removes its
machine-output flag only from the last argument, so the same flag placed before
the subcommand is rejected as an invalid argument.

## `doctor` is not the Host

`doctor --json` exercises the CLI process that printed it and that process's view
of its dependencies. It performs real probe work — connection, a trivial query and
a schema compatibility check — so a reachable server with an unusable schema is
still an unhealthy result. It cannot report what an already running Host has
observed, and a container healthcheck starts yet another CLI process with its own
result.

To characterize a Host, use evidence that belongs to that process: its structured
log events, its configured database and source, and the container or process state
you verified. Readiness is a per-process observation, not a property of the
machine.

## Controlled source diagnosis

Prerequisites are a thread configuration and a connection string that point at the
same thread and database as the producer under investigation, plus a migrated
schema. The schema status command reports that state and returns success only for
a ready schema; it neither grants migration permission nor applies a migration.
Use a disposable migrated database and a sanitized fixture from the tracked test
fixtures.

1. Read the source group's help, then run a fixture scan against that disposable
   database. Fixture mode still writes the durable journal and product rows under
   a separate fixture source identity, so treat it as a write path, not as a
   read-only rehearsal.
2. Inspect the stored result: the status document for the current state, the
   last success time and any pending snapshot, a single post for its decision,
   addressing, revision count and job state, and the queue for current versus
   historical jobs. Check the fields, not only the exit code — a fresh target
   reports `never_scanned` rather than an error, so a successful read alone says
   nothing about ingestion.
3. Rerun the unchanged fixture only as a control for repetition and identical
   input. Full idempotency proof, with independent checks of identical row sets
   and counters, belongs to live acceptance.
4. For the negative path, prove a successful scan on that same test source first
   and then feed a separate broken copy of the fixture; otherwise the run cannot
   show that a failure preserved the previous success. A stored retry deadline
   may defer the next attempt — do not delete state to bypass it.
5. A fixture conflict means the durable journal belongs to different fixture
   content. Resume with the version that produced it; a changed file is an
   independent run in its own database and must not discard the pending journal.
6. When storage is unavailable, check the intended role, database and schema. The
   report distinguishes between an unavailable schema, an unavailable database and
   an invalid configuration, and those categories call for different fixes.

Keep fixture content, contacts and connection values out of shared reports, and
keep captures in ignored local artifacts or an owned temporary directory together
with the build and target they describe.
