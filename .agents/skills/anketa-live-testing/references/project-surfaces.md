# Project surfaces and evidence owners

This reference maps the **existing** runtime surfaces of the repository to their
owners and to the evidence they can produce. It is deliberately not a command
reference: read the real `--help`, `.codex/context/02-INTERFACES.md`, the
implementation and the product guides before asserting a capability or a status
name. Never encode revisions, counts, post numbers or planning stages of one
change into reusable acceptance logic.

## 1. Components and boundaries

- **CLI** — `src/AnketaParser.Cli/` owns argument forms, help text, the machine
  document, process streams and the exit code. `.codex/context/02-INTERFACES.md`
  links each contract to its implementation and regression evidence. Discover
  commands through `--help`; the `anketa-cli` skill owns discovery, target
  selection and operation safety.
- **Core** — `src/AnketaParser.Core/` owns shared diagnostic contracts, reports,
  machine status values and `ExitCodes`. Exit codes are a machine contract:
  success, unexpected error, invalid arguments, invalid configuration and
  unavailable dependency are distinct, and a command can return a non-success
  code for a legitimate negative outcome.
- **Infrastructure** — `src/AnketaParser.Infrastructure/` owns configuration
  precedence and validation, readiness probing, source fetching and filtering,
  snapshot and journal persistence, queue state, migrations and backups.
  Independently inspected database state is a stronger witness than a wrapper's
  own report.
- **Host** — `src/AnketaParser.Host/` owns startup policy, the scheduled source
  poll and the readiness monitor. Startup policy, the periodic monitor and the
  CLI readiness probe are three different behaviors; losing a dependency does
  not by itself stop the Host, and a finished `doctor` run says nothing about
  what a running Host is polling.
- **PostgreSQL** — `docs/persistence.md`, the migrations and
  `tests/AnketaParser.DockerTests/` own durable state, schema state, roles,
  retention and backups. The CLI probe performs a read-only check and does not
  apply migrations; storage maintenance is an explicit, authorized operation.
- **Docker** — `compose.yaml`, `compose.debug.yaml` and `Dockerfile` own services,
  private network, persistent volume and health checks. Health checks run a
  separate CLI process inside a container, not a query of Host memory.
- **External source** — `docs/dvach.md`,
  `src/AnketaParser.Infrastructure/Sources/` and `tests/fixtures/dvach/` own
  fetching, HTML extraction, contact and moderation filtering, thread identity
  and the controlled anonymized fixture.
- **Verification owners** — `AGENTS.md`, the policy at
  `.codex/context/03-VERIFICATION.md`, `docs/development.md` and
  `.github/workflows/ci.yml` own the baseline command set, the opt-in container
  run and the hosted automation. This skill consumes them; it does not restate
  their command matrix.

## 2. Interpretation traps

These traps are the reason a plausible-looking observation can support a false
verdict. Check them against the current implementation, not against memory.

- **Exit zero is not outcome success.** A command that reads stored state
  succeeds when it read it. Whether an earlier scan actually succeeded is a
  different field of the same document, and a scan command that finishes with a
  non-success code may itself be the correct negative outcome.
- **`doctor --json` describes one process.** It reports the configuration,
  environment and dependency view of the CLI that ran it. It cannot prove what
  another Host, another container or another working directory is doing.
- **Read-oriented names can still write.** A command may initialize, recover or
  normalize internal state when it opens storage, and a scan against a fixture
  writes rows to the same database as a real scan, under a separate
  fixture-scoped source identity. Establish the actual side effects from the
  implementation before treating a command as non-mutating.
- **A fixture run is not a live run.** Fixture ingestion exercises fetching
  alternatives, filtering, snapshot writing, journaling, decisions and queue
  effects on controlled input. It proves nothing about reachability, current
  upstream content or completeness of recognition.
- **A pending or failed journal changes the next run.** A stored backoff
  deadline, an interrupted scan, a conflicting snapshot version or a busy source
  alters what the next command reports. Capture that baseline before the run,
  respect it, or use a separate disposable database and label the experiment
  accordingly; never clear the journal to obtain a green result.
- **Status vocabularies are finite and contractual.** Source status, decision
  outcome, queue job state and schema state have distinct documented values
  owned by the implementation and `docs/`. Assert the value the contract
  defines, and read the human guide for its meaning rather than inferring it.
- **Configuration follows the executable.** Settings are resolved for the
  executable's own content root and environment; a CLI started from a different
  directory, a Host process and a container can each use different settings and
  a different database. Correlate them explicitly instead of comparing their
  reports as if they shared state.
- **Container syntax is not container behavior.** Compose validation proves the
  file parses and resolves. A skipped or opt-in-only integration run is
  `NOT_RUN` for live acceptance, and a healthy container is not a processed
  source.
- **Two processes are not one.** A local CLI, a CLI inside a container and a
  running Host are independent processes with independent configuration,
  database and source. Evidence from one never transfers to another.

## 3. What each surface can witness

| Changed area | Minimum independent witness | Misleading shortcut |
| --- | --- | --- |
| Help, version, argument errors | Real process, separated streams, documented exit code and machine document | Help text proves nothing about configuration or readiness |
| Machine document and streams | One valid document on stdout when the contract guarantees it, diagnostics on stderr, real exit code first | Piping status output into the document parser hides the process outcome |
| Configuration and readiness | Effective sources, sanitized target identity, probe result and its category | A running process or an open port is not readiness |
| Source scan against a fixture | Real CLI path plus independently inspected stored snapshot, decisions and journal in a disposable migrated database | Exit zero, a log line or a file that exists |
| Stored source status and queue | Read against the same database and source as the scan, compared with independently inspected state | A successful read is not a successful poll |
| Repeated scan on one target | Unchanged revisions, decisions and counts, or a documented history change without duplicates | Two zero exits do not prove idempotency |
| Fault, backoff and recovery | Declared failure category, preserved last success, unchanged journal or checkpoint, actual later continuation | A fresh successful run on other input |
| Authorized real source request | Observed request and its processing into isolated storage, respecting retry and backoff rules | Fixture output or a cached result presented as a live fetch |
| Storage schema and maintenance | Read-only schema state plus the authorized operation and its verified effect | A status read is not permission to migrate |
| Backup and restore | Application verification and restoration into an authorized disposable target, then data postconditions | Archive existence or size |
| Host and Compose | The intended process, its effective settings, observed background work, health and run-owned cleanup | A separate healthy CLI or container |

Select only the rows the change requires, then justify the covered scope.

## 4. Isolation and authority

Writes require a disposable, migrated database and run-owned container
resources with unique names. Use the tracked anonymized fixture or synthetic
input; treat its thread identity as test data, not as a production identifier.
Read `docs/configuration.md`, `docs/persistence.md` and `docs/operations.md` for
the supported way to create, migrate, verify and remove those resources.

Read-only diagnosis is not authorization for provisioning, migration, archive
rotation, restore, service restart, deletion of a persistent volume, or a live
external poll. When the required authorization or prerequisite is unavailable,
report the unexercised path as `PARTIAL` or `NOT_RUN` with the blocking
condition, and keep the rest of the evidence.

Keep contact values, source text, credentials, connection strings and raw
external replies out of shared evidence and out of any publication. The security
boundary is owned by `docs/security.md` and the masking implementation.
