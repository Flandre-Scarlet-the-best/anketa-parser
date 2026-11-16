# Verification selection

[Context index](INDEX.md) · [Human verification guide](../../docs/development.md)

[AGENTS](../../AGENTS.md) owns mandatory constraints and baseline commands;
[CI](../../.github/workflows/ci.yml) owns executable automation and event routing.
Use their current commands rather than keeping a second full command list here.

| Changed area | Select evidence | Meaning / limit |
| --- | --- | --- |
| CLI, schema, streams, exits | [CLI tests](../../tests/AnketaParser.Tests/CliApplicationTests.cs), relevant direct invocation | Verify JSON and separate stdout/stderr; help is textual |
| Configuration, redaction | [Configuration tests](../../tests/AnketaParser.Tests/ConfigurationTests.cs), [descriptor tests](../../tests/AnketaParser.Tests/ConnectionStringDescriptorTests.cs) | Precedence, validation and secret-free descriptors; not all logging paths |
| Runner, cancellation, Host | [Runner tests](../../tests/AnketaParser.Tests/DiagnosticsRunnerTests.cs), [Host tests](../../tests/AnketaParser.Tests/HostApplicationTests.cs) | Unit/DI/startup/stop without services; do not infer all timer transitions |
| Dependencies / build policy | Locked restore, formatting, Release build and tests from CI | Preserve strictness and lockfiles |
| Containers / operational examples | Compose validation; opt-in [Docker smoke](../../tests/AnketaParser.DockerTests/ComposeStackSmokeTests.cs) when needed | Config validation is structural; skipped smoke is not live acceptance; smoke tests restart, not recreation |
| Python validators / instructions | unittest discovery, Ruff, BasedPyright and [coverage guard](../../.github/scripts/check_python_coverage.py) | Scope follows actual Python files; no hardcoded file count |
| Markdown / navigation / ignores | [Link checker](../../.github/scripts/check_markdown_links.py), its [tests](../../.github/scripts/test_check_markdown_links.py), [skill checker](../../.github/scripts/check_skill_contracts.py), tracked paths and ignore inspection | Local destinations only; independently read language, ownership, examples and Mermaid |

Exercise documented safe commands where practical and verify details against
[interface owners](02-INTERFACES.md). Do not run destructive cleanup or touch
production data to validate prose. Test infrastructure uses synthetic data and
isolated names; the operations guide explains volume effects.

Report local checks, live acceptance, skips and current hosted results separately.
A healthy process is not proof of readiness. The
[Git skill](../../.agents/skills/git-workflow/SKILL.md) owns publication evidence
and candidate/trusted activation; the [review skill](../../.agents/skills/coderabbit-review/SKILL.md)
owns explicitly authorized provider execution. Neither procedure belongs here.
