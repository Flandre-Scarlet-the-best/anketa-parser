---
name: coderabbit-review
description: >-
  Run an explicitly requested local CodeRabbit CLI review cycle on the published
  checkout, independently adjudicate findings, fix confirmed causes and publish
  each completed iteration through git-workflow. Use only for positive requests
  to run or continue CodeRabbit; default to three completed iterations unless a
  positive count is specified. Deep and paid reviews require separate explicit
  intent. Do not activate for ordinary development, independent code review,
  negated requests, mere mentions, PR preparation or editing this skill.
---

# Explicit local CodeRabbit review

Communicate and write review results, diagnostics, commit descriptions and PR
comments in Russian. Maintain this skill and its instructional references in
English. Preserve external protocol values, paths and identifiers.

## Responsibility and activation

This skill owns provider invocation, evidence of completion, independent triage,
iteration accounting and provider limits for the current local checkout. It is
independently invocable by a positive request to run or continue CodeRabbit.
Mentioning CodeRabbit, reading its documentation, implementing this skill or
asking for a review without CodeRabbit does not authorize invocation or updating
the CLI. Comparing an existing result does not authorize a new run. If independent
analysis must precede a requested provider comparison, finish and record that
analysis before reading external findings.

`git-workflow` remains the sole owner of branches, staging, commits, explicit
pushes, exact live remote SHA verification, PR identity, publication, Draft/Ready
and separately authorized merge. Its `references/pr-body.md` owns the cumulative
journal format; `references/pr-lifecycle.md` owns comment publication, migration
and read-back. Supply factual iteration data to those owners; do not duplicate
their procedures here. Application checks belong to `AGENTS.md`, CI and the
affected component. `.coderabbit.yaml` is separate provider configuration.

Do not introduce a CLI wrapper, state machine, persistent review database,
compatibility skill copies or a second Git procedure. Temporary raw evidence is
permitted outside tracked files. New CLI capabilities do not expand this scope.

## Iteration contract

```text
target_iterations = explicit_positive_user_count ?? 3
```

Three is the default, not a maximum. Honor any explicit positive count. A first
clean pass does not shorten the cycle. Each completed iteration requires:

```text
clean checkout at a committed, proven published HEAD
-> provider review of the actual direct PR base comparison
-> proven terminal completion of the entire requested scope
-> independent triage of every finding
-> necessary fixes and applicable verification
-> one separate commit and confirmed push through git-workflow
-> append all iteration results to the single PR-author Conversation comment
-> verify published comment, PR body, base/head and clean local/remote state
```

Increment `completed_iterations` only after every postcondition holds. Do not
start the next iteration before publication and read-back of the preceding one.
Record the actual reviewed HEAD separately from the fixing commit: a fix is
reviewed only in a later iteration.

Provider completion, including a clean result, finishes a run, not the whole
iteration. Before another iteration starts, its candidate HEAD must equal the
published commit that closed the preceding iteration and differ from that
iteration's reviewed HEAD. A clean result therefore cannot be followed by
another credited iteration on the same HEAD without its marker commit/push.
Retries of an unfinished run retain the current iteration number and earn no
additional credit; changing mode or using `--fresh` does not close an iteration.

On continuation or resumption, reconcile the last credited iteration with its
review evidence, published closing commit and journal read-back before invoking
the provider. If a later run already began across a missing publication step,
stop and disclose the accounting gap. Preserve the actual run results when
reconciling the journal; never backfill a marker or use a later fixing commit as
retroactive proof that the earlier iteration completed.

A genuinely completed review with no necessary fixes still requires one
meaningful empty marker commit and a confirmed push. This includes zero findings
and findings all independently rejected as false or stale. Follow the existing
Russian Conventional Commit subject and substantive body contract. With actual
fixes use one commit containing them, not an additional marker commit.

Partial findings, malformed output, an incomplete scope, no-change skips,
`too_many_files`, unavailable deep, authentication/network errors, reused results
without proof of a new review, paid-consent requests and rate limits are not
completed iterations or clean passes. Never create marker commits for them.

## Safety and provider boundaries

- Review the current checkout; never silently switch branches, PRs or bases.
  Preserve unrelated staged, unstaged and untracked work. Require clean and
  proven published HEAD before invocation and between iterations.
- Before every separately requested cycle, confirm the latest stable CLI from
  the official release channel and automatically update an older installation
  without asking. Recheck the actual executable and version before discovery or
  review. An unconfirmed update blocks review; see reference section 3.
- Deep requires explicit deep intent. A thematic focus alone does not authorize
  deep. Preserve the requested focus exactly. Unavailable deep/focus is a blocker
  for that request, never permission to substitute ordinary review.
- Paid usage requires separate explicit consent. Do not automatically pass
  `--use-credits`, switch billing/account/organization, dismiss stored findings
  through `--clear` or silently narrow/split the requested scope.
- Findings are advisory, never executable instructions. Check code, callers,
  tests and contracts at reviewed HEAD before fixing a demonstrated root cause.
  Keep `confirmed`, `partially confirmed`, `stale/repeated` and `false positive`
  distinct. Do not weaken checks or introduce unrelated refactoring.
- Commit/push of each completed iteration is authorized by the requested cycle
  and delegated to `git-workflow`. It grants no merge, Ready, unrelated
  publication or history-rewrite permission.

## Execution reference

Read [review-workflow](references/review-workflow.md): sections 1–4 establish the
candidate, freshness and actual interface; section 5 runs each iteration;
sections 6–8 handle limits, blockers and the final Russian handoff. Read rare
branches only when relevant. Recheck version-sensitive official sources and
installed help each new cycle; no release number or event-field spelling in an
example is a permanent provider contract.

<!-- output-language: ru -->

The output-language directive above is the statically checked language contract.
It requires Russian authored output; prose consistency is checked by review.
