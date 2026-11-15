# PR lifecycle

This reference owns PR identity, source/publication/read-back, review state,
conflicts, authorized merge and cleanup. Read [publication](publication.md) for
remote commit proof and [pr-body](pr-body.md) for substantive content rules.

## Identity and publication

1. Establish repository, actual base/head, head repository and owner. Fully list
   matching open PRs through GitHub API pagination or a sufficient verified
   `gh pr list --base <base> --head <head> --json ...` result. Branch-name equality
   across forks is not identity. `gh pr view` alone does not prove uniqueness.
2. Exactly one match permits updating that PR after rereading it by number.
   Zero matches permits preparing a new PR; ambiguity, incomplete listing or an
   unreadable state blocks mutation. Do not create another PR for a new phase.
3. With a checkout, keep the canonical local body in ignored
   `.codex/local/pr/<task>/body.md`. Confirm it is ignored and untracked. The path
   is merely a working artifact convention, not a dependency on an agent harness.
   API-only work may use the full structured body argument as its local source.
   If an existing PR has no verified matching local source, first read its full
   published body and restore that source without losing sections or tables.
   Reconcile unknown local/remote differences instead of overwriting either.
   In API-only mode retrieve and run the validator from the exact candidate SHA;
   if changed, also validate against the trusted base version where available.
4. Prepare a Russian title/body for the final diff; read the complete source and
   run the body validator. Inspect repository PR templates before creating a PR.
   Verify expected remote HEAD again. Use `gh pr create --draft --base <base>
   --head <head> --title <title> --body-file <file>` or equivalent structured API.
   For updates use the existing verified PR number and `--body-file`. Avoid inline
   shell escaping that changes Markdown/newlines or executes body content.
5. Reread the full published object after every write: repository, base/head,
   exact head SHA, title, full body, state and Draft/Ready. Compare the full body
   with its source after newline normalization, validate it again and reconcile
   differences. A successful mutation response alone is insufficient. Unknown
   outcomes require reading first, never blind duplicate creation.
6. Keep the source and published body current after fixes, verification, CI or
   review changes. Recheck uniqueness, expected remote ref and latest PR state
   before handoff. Report the actual terminal state and remaining limitations.

## CI and review freshness

Inspect actual checks, rulesets/branch protection and review threads for the
current HEAD and base. Do not reuse green checks from another commit. After HEAD
changes, reassess affected tests and review evidence, including each rewritten
stack layer. Native GitHub can retain approvals for a content-preserving rebase;
verify content equivalence rather than assuming all old evidence is current.
Resolve review conversations based on the actual change and applicable review
owner. Never claim a review ran or a thread was resolved without reading state.

Draft is the default. Ready requires an explicit user instruction or an explicit
current task contract. Green CI, a push or a Draft's existence grants no Ready
or merge permission.

## Conflicts and base movement

Inspect the common ancestor and each side's diff. Resolve by intended semantics;
never blanket-select ours/theirs or delete unknown changes. Regenerate derived
files from their source owner. Ordinary published branches merge the updated
base without history rewriting; unpublished history may be rebased. Only the
verified stack exception in publication permits necessary cascading rewrites.
Verify no unmerged paths or incomplete merge/rebase remain, inspect the new diff
and rerun affected checks. Refresh descriptions and CI/review evidence for new
HEADs. Stop when preservation of others' work cannot be proved.

## Merge and cleanup

Merge always requires a **separate current explicit user instruction for the
particular PR and state**. Recheck exact HEAD, base, required CI, review and
policy immediately before merging. No admin bypass or forced merge is allowed.
Auto-merge requires the same permission and waiting auto-merge is not a completed
merge. In a native stack, determine every PR that the operation would merge;
permission for one PR does not implicitly authorize merging additional layers.

Use a merge method supported by the actual repository policy; do not hardcode
squash. Verify installed interfaces and bind the operation to the expected HEAD
when supported (for example `--match-head-commit`). Read actual merged status and
merge SHA afterward. Only confirmed merge permits cleanup of this task's own
branch and temporary artifacts. Prove the result is preserved, inspect downstream
dependencies and ownership before deleting a remote branch. Use `git branch -d`
locally. If squash prevents safe deletion, leave the branch and report it; never
escalate automatically to `-D`. Preserve unrelated files and worktrees.

## Future disposition comment publication

No review iteration means no comment is needed. If a future authorized review
produces one, follow the format in pr-body. Establish PR author from API data,
the acting account's ability to write as that author, PR identity and complete
paginated top-level Conversation comments. Inline review threads and bot messages
are not the journal. Locate the exact marker and verify author and comment ID.
Zero matches permits creation only for an actual completed iteration; exactly
one permits updating that verified ID. Multiple markers, wrong authors, incomplete
listing or API failure block mutation; never delete someone else's comments.

Recover the complete published text/history, reconcile unknown changes with the
working copy and reread immediately before writing. This reduces races but is
not atomic concurrent-edit protection. Publish the Russian table, reread by ID
and enumerate comments again to verify identity, uniqueness, complete content and
current PR base/head/Draft. On unknown outcomes read before retrying. Do not start
the next iteration until the preceding publication is confirmed.

For legacy tables inside a current PR body, first recover and reconcile both
histories without loss, publish and confirm the single comment, and only then
remove the old section from the body. Reread both objects. Never migrate
historical PRs automatically or discard body history before comment confirmation.
Respect the actual trusted validator version during migration; do not bypass old
required checks or treat head validation as independent base validation.

## Trusted checks and staged activation

`pr-body.yml` on `pull_request` runs candidate code; it is useful regression
evidence, not independent enforcement when the PR edits its validator.
`pr-body-trusted.yml` on `pull_request_target` checks out the exact event base
SHA with credentials persistence disabled. It must never check out or execute
head code. PR text/event metadata are untrusted data, not shell source. Use only
minimal read permissions, no additional secrets and no write token.

The event payload is a snapshot, not a guarantee that the description is unchanged
later. Reread the current body independently at handoff. A comment-only change
does not automatically refresh a PR HEAD required check; `issue_comment` runs in
default-branch context. Do not represent a default-branch run as PR HEAD evidence.
If needed, rerun an existing PR workflow only after verifying its event HEAD,
base, body and trusted version still match current state, then reread results/content.
A rerun reuses the original event payload: it cannot validate a changed body.
Changed descriptions require a fresh applicable PR event and a new workflow run.
Otherwise wait for an applicable PR event; do not invent bypass contexts.

Ordinary dependent PRs target their direct base; native stacks route Actions as
if targeting the stack trunk. CI has no pull-request base filter so both are
covered without hardcoding temporary feature branches. Merge-group verification
is handled separately by CI, not by pretending it has a pull_request body.

New workflows/validators in head do not establish trusted enforcement. The base
may not contain them yet; inspect real hosted runs and state this limitation.
Activation order: prepare in a PR, verify candidate/local behavior, obtain
separate merge permission, confirm the files in the trusted base, observe a
successful trusted-base run on an applicable PR, then configure a required
context only with appropriate user authorization. Preserve existing protection
and bypass rules. Never impose a cyclic required context first introduced by
the PR, weaken protections, or label an absent check pending/successful. Job-name
collisions from head workflows also need review; matching a name is not proof of
trusted execution. No CodeRabbit automation or mandatory external review is
introduced by these checks.
