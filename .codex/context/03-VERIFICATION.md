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
| Containers / operational examples | Compose validation; opt-in [Docker smoke](../../tests/AnketaParser.DockerTests/ComposeStackSmokeTests.cs) when needed | Config validation is structural; skipped smoke is not live acceptance; the smoke test proves recreation, not restart; persistence checks share one server with a separate database per check |
| Python validators / instructions | unittest discovery, Ruff, BasedPyright and [coverage guard](../../.github/scripts/check_python_coverage.py) | Scope follows actual Python files; no hardcoded file count |
| Markdown / navigation / ignores | [Link checker](../../.github/scripts/check_markdown_links.py), its [tests](../../.github/scripts/test_check_markdown_links.py), [skill checker](../../.github/scripts/check_skill_contracts.py), tracked paths and ignore inspection | Local destinations only; independently read language, ownership, examples and Mermaid |
| Runnable behavior: CLI, readiness, ingestion, persistence, queue, recovery, containers | [Runtime acceptance](../../.agents/skills/anketa-live-testing/SKILL.md) through the real entry points, and the opt-in [Docker smoke](../../tests/AnketaParser.DockerTests/ComposeStackSmokeTests.cs) where it applies | Applicability is stated below; what each run proves and how its verdict is worded belong to the acceptance procedure |

## Runtime acceptance

This map owns applicability of proportional runtime acceptance. The
[live-testing skill](../../.agents/skills/anketa-live-testing/SKILL.md) owns how a
selected scenario is executed, observed, judged and reported; the
[CLI skill](../../.agents/skills/anketa-cli/SKILL.md) owns invoking the real
command and identifying its target.

Runtime acceptance is a normal part of finishing a change that can affect
runnable behavior: CLI commands, arguments, machine output, exit codes or
statuses, configuration precedence and readiness, ingestion and filtering
decisions, persistence and migrations, queue and job transitions, repeat
polling, recovery after a failure, or container composition. The agent applies
this itself instead of waiting for the user to request it; which concrete
scenarios a change needs stays with the acceptance procedure.

A change that cannot affect runtime behavior may be reported as `N/A`, and so may
a scenario proved inapplicable to the changed area; state the reason and say
which of the two cases applies. "Not requested", a small diff, passing unit tests
and green hosted CI are not reasons. Runtime acceptance complements unit,
contract and hosted checks and is not replaced by them; the reverse also holds.

Exercise documented safe commands where practical and verify details against
[interface owners](02-INTERFACES.md). Do not run destructive cleanup or touch
production data to validate prose. Test infrastructure uses synthetic data and
isolated names; the operations guide explains volume effects.

Report local checks, live acceptance, skips and current hosted results separately.
A healthy process is not proof of readiness. The
[Git skill](../../.agents/skills/git-workflow/SKILL.md) owns publication evidence
and candidate/trusted activation; the [review skill](../../.agents/skills/coderabbit-review/SKILL.md)
owns explicitly authorized provider execution. Neither procedure belongs here.
