# Engineering context

Start with [AGENTS.md](../../AGENTS.md), then open only the map relevant to the
task. This directory is a repository knowledge router for any agent that can
read files; its historical path does not require a particular harness.

| Need | Route |
| --- | --- |
| Locate a project, entry point, adapter or test | [Project map](01-PROJECT-MAP.md) |
| Locate a CLI, JSON, configuration or readiness contract | [Interface map](02-INTERFACES.md) |
| Navigate code semantically or choose diagnostics | [Code intelligence skill](../../.agents/skills/code-intelligence/SKILL.md) |
| Choose checks and interpret their evidence | [Verification map](03-VERIFICATION.md) |
| Understand product usage and operations | [Russian documentation](../../docs/README.md) |
| Publish or maintain a work item | [Git skill](../../.agents/skills/git-workflow/SKILL.md) |
| Execute an explicitly authorized local provider review | [CodeRabbit skill](../../.agents/skills/coderabbit-review/SKILL.md) |

## Ownership

- Root AGENTS owns mandatory agent constraints and source precedence.
- Skills own task procedures; do not reproduce Git lifecycle or review cycles here.
- This directory maps questions to owners, code and tests. It does not redefine
  product contracts or copy complete command/configuration tables.
- Russian docs own human explanations; root README owns introduction and quick start.
- Code, tests, configuration and CI establish actual behavior and executable checks.

Keep all authored context prose in English, including headings and diagram
labels. User-facing documentation, responses and publication output remain
Russian under AGENTS. Preserve identifiers and immutable output verbatim.
Do not store temporary branches, PR identities, SHA values, review journals,
private configuration or session state here. Local artifacts belong outside
tracked context, usually in ignored `.codex/local/`.
