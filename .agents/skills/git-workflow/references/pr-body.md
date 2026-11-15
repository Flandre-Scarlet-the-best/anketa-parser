# PR body contract

This reference owns body content and the future CodeRabbit disposition format.
[pr-lifecycle](pr-lifecycle.md) owns storage, publication and read-back.

## Structure and language

Write the title and all authored prose in Russian. Keep exact technical names.
Describe the final work item for a reviewer who has not read the conversation:
problem, resulting behavior, actual changes, verification, CI and limitations.
Do not paste prompts, transcripts, command dumps, abandoned plans or stale facts.
Every substantive repository PR, including documentation and skills, must contain
these real visible level-two Markdown headings, exactly once and in this order:

```markdown
## Цель
## Область изменений
## Реализация
## Проверки
## Производительность
## CI
## Готовность
## Ограничения
```

Fill every section with substantive content. Headings hidden in code fences or
HTML comments do not count. Labels followed by a colon are not headings. The hard
limit is **10,000 Unicode characters**, including Markdown, after CRLF/CR to LF
normalization. Keep meaningful facts inside the PR; no Gist is created or needed.
Only truly administrative mutations without a substantive repository diff are
outside this structure. Dependency-bot exemptions require both the exact known
bot account and its matching branch prefix as checked by the validator.

- Goal: 1–3 sentences explaining the problem, need and observable outcome.
- Scope: affected components and behavior owners; repository/base/head and exact
  commits when useful for verification, without a file inventory dump.
- Implementation: important decisions, data/behavior ownership, failure/recovery
  semantics and preserved invariants. Describe results rather than agent steps.
- Checks: commands actually executed, outcomes and what they prove. Distinguish
  unavailable, failed, skipped and planned checks.
- CI: actual hosted jobs/contexts for the current PR HEAD. Include exact HEAD
  when necessary to exclude stale evidence. An absent context is not pending;
  report NOT_RUN or unavailable only as supported by observation.
- Readiness: completed work, remaining requirements, Draft/Ready, required review
  state and the absence of merge without separate permission.
- Limitations: only practical constraints, risks and missing evidence; use one
  sentence if there are no material limitations.

## Performance decision

Always include a short decision, maximum **2,200 characters** for the entire
section including heading and Markdown. Use exactly one status field with one
of these exact Russian values: `Статус: не применимо`, `Статус: не измерено`,
`Статус: измерено`.

For not applicable or unmeasured, the whole section is at most **650 characters**:

- `не применимо`: require `Причина:` with a concrete explanation of no runtime
  impact (at least 40 characters). Do not use this to dismiss executable runtime
  changes affecting performance.
- `не измерено`: require factual `Причина:` and `Диагностика:` (at least 20
  characters each), plus `Доказательство производительности: отсутствует`.
  Never present missing measurements as verified performance.
- `измерено`: each material scenario gets a separate `###` subsection and at
  most six substantive lines, with exact fields `Сценарий:`, `База:`,
  `Кандидат:`, `Результат:`, `Вывод:`, `Регрессии:`. Include workload/profile/n,
  measured base and candidate commits (or first implementation), numbers with
  units, before/after, delta and variation when available, comparability and all
  material regressions or an explicit statement that none were significant.
  For an unmeasured scenario in an otherwise measured section, end its heading
  with `— не измерено` and supply reason, diagnostics and absent-evidence fields.

Keep raw reports and full methodology locally for inspection. Retain enough
numbers, identities and limitations in the PR for a defensible decision. Shorten
duplication elsewhere instead of hiding regressions or copying long tables.

## Conditional sections and validation

Add separate sections when applicable for live acceptance, secrets/security,
migration/rollback or external review. Write their titles and content in Russian,
preserving exact external names. Report live scenario, observations and temporary
data cleanup without conflating runtime acceptance with static tests.

Validate the entire local body before publication and the full read-back after:

```sh
python .github/scripts/check_pr_body.py --body-file <body-file>
```

The validator checks structure, limits and performance fields, not factual truth,
complete Russian language quality or benchmark comparability. Independently
verify those against evidence. Candidate and trusted CI have distinct trust
boundaries described in [pr-lifecycle](pr-lifecycle.md).

## Future single CodeRabbit journal

This is a boundary contract, not authorization to run CodeRabbit or implement its
skill. The future review owner determines invocation, completion, adjudication,
iteration limits and valid dispositions, and delegates all Git publication here.
After an actually completed iteration, keep one cumulative top-level Conversation
issue comment by the PR author, with the exact marker on its own line:

```markdown
<!-- coderabbit-disposition -->

| Итерация / reviewed HEAD | Уровень | Путь | Влияние | Решение | Исправление |
| --- | --- | --- | --- | --- | --- |
```

No iteration means no fabricated comment or mandatory link/table in the body.
The comment contains only the marker and the table, without a separate narrative.
Use one contiguous visible GFM table outside fences/comments, exactly six filled
cells per row. Escape internal pipes. Each data row begins with a positive
iteration number, `·` and the actually reviewed lowercase 7–40 digit hexadecimal
HEAD in a code span. Preserve severity, path, technical impact, disposition and
the actual published fix/commit. Write authored explanations in Russian; keep
machine disposition values exact. Never present false positives or stale/repeated
findings as fixes. A truly clean iteration has one row with `—` for severity and
path, `clean` disposition and its published marker commit if required by the
future review owner. A later fix HEAD is not the reviewed HEAD of that iteration.

Append each iteration without erasing history or repeating headers. Do not paste
raw provider output. If the comment limit would require losing history or a
second journal, stop and explain. Syntax cannot prove truth. Publication,
identity, safe migration and read-back belong to pr-lifecycle.
