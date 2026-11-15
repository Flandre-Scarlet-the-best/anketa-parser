---
name: git-workflow
description: >-
  Publish and maintain a repository work item through a task branch, staging,
  commit, explicit push, remote SHA verification and one Draft PR; handle PR
  updates, verified stack synchronization, Ready, separately authorized merge
  and cleanup. Use for Git/GitHub lifecycle actions, including API-only work.
  Do not use for Git explanations, read-only investigation or provider-specific
  review execution.
---

# Git and GitHub lifecycle

This skill is the sole owner of the general Git/GitHub procedure. It supports
standard Git/gh and suitable GitHub APIs/connectors with the same postconditions.
It does not own application scope, repository verification requirements or
provider-specific review invocation, adjudication, iteration and rate limits.
Those remain with the task, root agent contract and applicable review owner.
Do not create a wrapper CLI, publication state machine, persistent journal or
duplicate procedure in another document.

Communicate and write commit descriptions, PR titles/bodies, issue comments,
review comments and reports in Russian. Keep technical identifiers exact.
Maintain this skill and its instructional references in English; exact Russian
validator literals are the only necessary language exceptions.

## Routing

Read the relevant owner before its operation:

- [publication](references/publication.md): repository/base/upstream discovery,
  branch, verification, staging, commit, push, exact remote postcondition,
  uncertain network outcome recovery and verified stack synchronization.
- [pr-body](references/pr-body.md): substantive PR structure, Unicode limits,
  performance decision and the future single CodeRabbit disposition table.
- [pr-lifecycle](references/pr-lifecycle.md): PR identity, local body source,
  create/update/read-back, Draft/Ready, CI/review freshness, conflicts, separately
  authorized merge, cleanup and trusted-check activation.

Intent activates this skill without requiring its literal name. Read-only
history/PR inspection, code review and explaining Git do not activate mutation
procedures. Editing this skill does not authorize unrelated GitHub actions.

## Completion contract

For a publishable repository task, unless the user explicitly limits the task
to local work or commit-only, continue autonomously through:

```text
change -> applicable verification -> inspected staging -> commit
-> explicit task-ref push -> exact live remote SHA comparison
-> create/update one Draft PR -> full published read-back -> handoff
```

Commit alone, push exit status and cached tracking refs do not prove publication.
Merge is excluded and always requires a separate current explicit instruction
for the particular PR and state. Ready also requires explicit instruction or an
explicit task contract. Successful checks do not grant either permission.

## Invariants

- One independent work item, one semantic branch, one PR. Continue an existing
  task on its branch; do not create a new PR for each phase.
- Identify repository, remote, upstream and actual base/head, including fork
  ownership, from live refs. Never silently substitute the default branch for
  an explicitly requested dependent base.
- Preserve unknown work and stage exact task paths. Never absorb user staging.
- No bare force, blind push, unknown branch deletion or history rewrite for an
  ordinary published PR. Only the verified stack exception in publication
  permits necessary rewriting of the task's own affected branches.
- Never turn synchronization into stack registration, reordering, deletion,
  ungrounded retargeting, rewriting someone else's branch or permission to merge.
- Resolve unknown network outcomes by reading live state before retrying.
- Verify content, CI and review against the actual current HEAD. Preserve all
  required checks and report unavailable evidence honestly.
- Keep transient PR numbers, SHA values, roadmap phases, absolute workstation
  paths and author names out of permanent instructions and branch conventions.
