# Agent contract

## Language and evidence

Communicate with the user in Russian. Write plans, reports, review results, PR
titles and bodies, issue and review comments, commit descriptions, project
documentation, source comments, application-owned logs and human-readable
diagnostics in Russian. For Conventional Commits keep the type and optional
scope in English and write the description after the colon in Russian.

This file and all instructional files in the canonical Git skill are maintained
in English. Their language does not change the required Russian behavior.
Preserve exact Russian literals required by validators even inside English
instructions. Keep API names, identifiers, paths, commands, JSON keys, exit
codes, protocol values and immutable external messages unchanged. Independent
output from dependencies, SDKs and CI is external evidence, not authored prose.
Apply the language rule to new or substantively changed material; do not
translate unrelated existing files or silently change public contracts.

Inspect applicable `AGENTS.md` and `AGENTS.override.md` before changing a scoped
area. Follow higher-priority instructions and explicit task requirements.
Current code, configuration, tests and observed runtime behavior take precedence
over historical descriptions. Distinguish verified facts, hypotheses and future
capabilities; resolve contradictions against the actual behavior owner.

## Scope and architecture

This repository is a C#/.NET infrastructure foundation for anketa-parser. The
implemented surface is a long-running Generic Host, a diagnostic CLI, shared
configuration/readiness logic and Docker/PostgreSQL infrastructure. Parsing,
profiles, Telegram, LLM integrations, domain persistence and migrations are not
implemented. Do not introduce fictitious domain entities or future frameworks.

| Owner | Responsibility |
| --- | --- |
| [Core](src/AnketaParser.Core) | Package-free diagnostic contracts, `DiagnosticsRunner`, reports, status and exit codes. |
| [Infrastructure](src/AnketaParser.Infrastructure) | Configuration pipeline and validators, Npgsql readiness probe, runtime snapshot and readiness state. |
| [Host](src/AnketaParser.Host) | Executable composition, startup diagnostics, readiness monitor and shutdown. |
| [Cli](src/AnketaParser.Cli) | Executable composition, command parsing and diagnostic JSON presentation. |
| [Unit tests](tests/AnketaParser.Tests) | Configuration, diagnostics, CLI, host and DI contracts without external services. |
| [Docker smoke](tests/AnketaParser.DockerTests) | Opt-in isolated Docker/PostgreSQL integration. |

Dependencies flow from Host/Cli to Infrastructure to Core; executables may also
reference Core directly. Keep composition in executables; Infrastructure
registers adapters through `AddAnketaParserInfrastructure`. Reuse
`DiagnosticsRunner` for both entry points. Core must not depend on CLI, hosting,
database or network implementation packages. Keep adapter logic separate from
application contracts. The current topology is one application process and one
database, not a distributed domain architecture.

## Configuration, diagnostics and data

[README.md](README.md) owns operational usage, configuration keys and diagnostic
examples. [AnketaParserConfiguration.cs](src/AnketaParser.Infrastructure/Configuration/AnketaParserConfiguration.cs)
owns configuration precedence: optional `appsettings.json`, environment-specific
JSON selected by `DOTNET_ENVIRONMENT`, ignored `appsettings.Local.json`, then
`ANKETAPARSER_` environment variables with `__` for nested keys. Host and CLI
must use this same pipeline and typed validators. Defaults permit a missing
content root and an unconfigured database; do not invent mandatory credentials.

Do not commit `.env`, local overrides, connection strings, tokens or private
working artifacts. Use the existing secret-free connection descriptor; never
dump raw configuration or dependency exception messages containing credentials.
Preserve structured `ILogger` fields and stable `EventId` values rather than
adding another file log or ad hoc stdout diagnostics.

`doctor --json` must emit exactly one JSON document on stdout; logs and human
diagnostics go to stderr. Preserve schema keys, machine status values and the
exit-code contract in [ExitCodes.cs](src/AnketaParser.Core/ExitCodes.cs): success,
internal error, invalid arguments, invalid configuration and unavailable required
dependency have distinct codes. A running process does not prove readiness.
The PostgreSQL probe performs `SELECT 1`; it does not create a domain schema.
An optional unconfigured dependency is skipped/degraded; an unavailable required
dependency must not be reported healthy.

[compose.yaml](compose.yaml) owns the persistent PostgreSQL volume and private
container network. [compose.debug.yaml](compose.debug.yaml) explicitly enables
loopback debugging. Preserve the default absence of published ports. Source
edits do not authorize production restarts, migrations or volume deletion.
Use synthetic data and isolated Compose project names for tests. Inspect exact
resolved paths and ownership before file writes or cleanup; preserve unknown
files, user edits and persistent data. Never use cleanup as a substitute for
understanding a failure.

## Verification owners

[global.json](global.json) owns SDK selection;
[Directory.Build.props](Directory.Build.props) owns build policy;
[Directory.Packages.props](Directory.Packages.props) and project lockfiles own
dependencies. Preserve locked restore and the strictness of existing checks.
[ci.yml](.github/workflows/ci.yml) owns automated verification and event routing:

```sh
dotnet restore --locked-mode
dotnet format anketa-parser.slnx --verify-no-changes --no-restore
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
docker compose config --quiet
python -m unittest discover -s .github/scripts -p "test_*.py"
python .github/scripts/check_skill_contracts.py
```

Choose checks proportionate to the change and run required repository checks.
Before running the Python contract suite, install the pinned checker dependency
with `python -m pip install -r .github/scripts/requirements.txt`, preferably in a
local virtual environment. PyYAML is used only for safe skill frontmatter parsing;
the PR body validator still uses the standard library. A structurally valid document is
not proof of factual accuracy or language quality: read the resulting prose.

Docker smoke is skipped unless `ANKETAPARSER_DOCKER_TESTS=1` is explicitly set;
it builds and runs an isolated stack and tests connectivity and data survival.
`docker compose config` only validates configuration. Unit success or a skipped
smoke is not live container acceptance. Report actual execution, failures,
skips and unavailable tools separately. Do not weaken checks to obtain green CI.

## Git and skill routing

One independent work item has one task branch and one PR. Preserve unrelated
staged, unstaged and untracked work. Do not work directly on the default branch.
A local commit does not complete authorized publication; verify the remote
result. Merge always needs separate, current explicit authorization.

The sole procedure owner is [git-workflow](.agents/skills/git-workflow/SKILL.md).
Read it before branch/staging/commit/push/PR lifecycle work, then load its
reference for the operation. It owns publication, PR structure, Draft/Ready,
conflicts, the narrowly verified stack exception and post-merge cleanup. Do not
duplicate these procedures in README, another skill or a context index.

Discover repository skills under `.agents/skills/<name>/SKILL.md`, check their
frontmatter and applicability, and read only relevant references. There is one
canonical copy, independent of the agent harness. Do not create compatibility
copies, indexes or new skills without a task requiring them. CodeRabbit runs
require explicit positive authorization; mentioning it or editing workflow
instructions does not authorize a run. Its future review procedure will own
provider-specific behavior and delegate Git publication to this skill.

The PR body validator and candidate workflow own deterministic structure checks;
the trusted workflow uses only base code. Their trust and activation boundaries
are documented by the Git skill. Do not claim an absent hosted context is
pending or successful, or alter protection rules to bypass its activation.
