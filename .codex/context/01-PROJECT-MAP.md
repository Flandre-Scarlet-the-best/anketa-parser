# Project map

[Context index](INDEX.md) · [Human architecture guide](../../docs/architecture.md)

The implemented surface is infrastructure: one long-running process and one
database. Parsing, profiles, Telegram, LLM, domain persistence, migrations and
HTTP APIs are absent. Inspect current owners before describing new behavior.

| Area | Owner and entry points | Tests / explanation |
| --- | --- | --- |
| Package-free contracts | [Core](../../src/AnketaParser.Core/), especially `DiagnosticsRunner` and `ExitCodes` | [Runner tests](../../tests/AnketaParser.Tests/DiagnosticsRunnerTests.cs) |
| Configuration and adapters | [Infrastructure](../../src/AnketaParser.Infrastructure/); `AddAnketaParserInfrastructure` registers adapters | [Configuration tests](../../tests/AnketaParser.Tests/ConfigurationTests.cs) |
| Host composition and lifetime | [Program](../../src/AnketaParser.Host/Program.cs), [HostApplication](../../src/AnketaParser.Host/HostApplication.cs), startup and monitor services | [Host tests](../../tests/AnketaParser.Tests/HostApplicationTests.cs) |
| CLI composition and presentation | [Program](../../src/AnketaParser.Cli/Program.cs), [CliApplication](../../src/AnketaParser.Cli/CliApplication.cs) | [CLI tests](../../tests/AnketaParser.Tests/CliApplicationTests.cs) |
| Container runtime | [Dockerfile](../../Dockerfile), [Compose](../../compose.yaml), [debug override](../../compose.debug.yaml) | [Docker smoke](../../tests/AnketaParser.DockerTests/ComposeStackSmokeTests.cs), [operations](../../docs/operations.md) |
| Build and dependency policy | [Solution](../../anketa-parser.slnx), [SDK](../../global.json), [build props](../../Directory.Build.props), [package props](../../Directory.Packages.props), project lockfiles | [Development](../../docs/development.md) |
| Automation | [CI](../../.github/workflows/ci.yml), [Python scripts](../../.github/scripts/), [type-check scope](../../.github/pyrightconfig.json), [Ruff](../../ruff.toml) | [Verification map](03-VERIFICATION.md) |

Dependencies flow Host/Cli → Infrastructure → Core; executables also reference
Core directly. Composition stays in executables. Both entry points reuse the
same runner. Keep implementation packages outside Core and adapter behavior
outside presentation. The architecture guide explains this boundary for humans.

## Knowledge surfaces

[README](../../README.md) is the short entry, [docs](../../docs/README.md) the
canonical human guide, [AGENTS](../../AGENTS.md) the mandatory contract, and
[skills](../../.agents/skills/) the procedure owners. This context is tracked;
other tool state and private files under `.codex/`, plus `.serena/`, remain
ignored by [.gitignore](../../.gitignore). Do not create alternate harness copies.
