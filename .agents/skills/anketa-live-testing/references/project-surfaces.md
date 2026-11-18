# Runtime surfaces and acceptance owners

This is a map of **existing** interfaces, not frozen command documentation.
Recheck the actual implementation and tests before using a command or
asserting a capability. Do not encode temporary SHA values, post numbers
or roadmap stages in repeatable verification logic.

## Code and runtime boundaries

- **CLI:** `src/AnketaParser.Cli/` owns argument and JSON semantics,
  source diagnosis and controlled storage operations.
  `.codex/context/02-INTERFACES.md` links commands to their owners
  and tests. Invoke through the sibling `anketa-cli` skill.
- **Core:** `src/AnketaParser.Core/` owns shared diagnostic contracts,
  status and exit codes. Read command-specific status as well as exit
  code: a successful query can report a failed earlier operation.
- **Infrastructure:** `src/AnketaParser.Infrastructure/` owns
  configuration, readiness, fetching/filtering, snapshot persistence,
  migrations and backups. Independent database state and events are
  stronger acceptance witnesses than a wrapper's result.
- **Host:** `src/AnketaParser.Host/` owns startup, scheduled source
  polling, monitoring and shutdown. Host and a diagnostic CLI may use
  different content roots, settings and database roles; independently
  correlate before comparing their results.
- **PostgreSQL:** `docs/persistence.md`, migrations and
  `tests/AnketaParser.DockerTests/` own durable data and integration.
  State transitions, role permissions, retry history and background
  work are integral to verification.
- **Docker:** `compose.yaml`, `compose.debug.yaml` and `Dockerfile`
  own networks, services, volumes and healthchecks. Test with unique
  Compose project names and disposable DBs. `docker compose config`
  proves syntax/configuration only; a skipped Docker test is NOT_RUN
  for live acceptance.
- **External source:** `docs/dvach.md`,
  `src/AnketaParser.Infrastructure/Sources/` and
  `tests/fixtures/dvach/` own source behavior and controlled fixtures.
  A fixture verifies ingestion using saved data; it does not prove
  remote reachability or completeness of a changing upstream snapshot.

## What evidence proves

| Evidence | Supports | Does not prove by itself |
| --- | --- | --- |
| Unit and contract tests | Their tested branches and assertions | Real database, remote source or Host lifecycle |
| Built CLI with isolated DB | Executed command and persisted state | That a separate running Host did the same work |
| Opt-in Docker tests | Actual isolated PostgreSQL/Compose scenarios | Every real deployment failure/restart |
| Authorized real source run | One observed upstream response and its processing | Unobserved edits, account ownership or global recall |
| Static checks and CI | Their reported scope at an identified SHA | Live runtime acceptance |

Use current `AGENTS.md`, `.codex/context/03-VERIFICATION.md`,
CI and product guides as the owners of baseline tests and operations;
do not duplicate their entire command matrix. Preserve security
boundaries in `docs/security.md`. No live polling, production
migration, restart or restore is implicitly authorized by this skill.
