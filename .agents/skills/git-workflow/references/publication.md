# Publication

This reference owns branch, staging, commit and push through the confirmed remote
postcondition. [pr-lifecycle](pr-lifecycle.md) owns the subsequent PR operations.

## Establish checkout and base

Before mutation inspect repository root, remotes, branch, upstream, HEAD, status
and staged/unstaged diff. Fetch the actual remote; tracking refs are caches.
Use `git ls-remote --symref <remote> HEAD` to discover the remote default branch
when needed. Prefer the branch's real upstream remote; do not assume `origin`.
An explicit task base overrides default-branch discovery. Establish its live SHA
and create a semantic branch from that exact commit. Do not hardcode historical
PR numbers, SHA values or phase identifiers. Announce a branch change.

For continued work establish the existing branch/PR identity rather than creating
another branch. Do not mutate the default branch. A mismatched repository,
ambiguous upstream/base or unknown work is a precondition problem to resolve,
not an invitation to guess. Isolate task work if necessary; never reset, clean,
stash or discard someone else's changes to make the checkout convenient.

## Verification and staging

Run applicable checks from the root agent contract and the actual area owner.
Inspect the entire final diff and stage only explicit task paths with
`git add -- <path> ...`. Read `git diff --cached`, its stat and status before
commit. Do not include ignored local artifacts, secrets or unrelated staging.
If ownership cannot be established, stop the dependent operation and explain.

### Commit message

Use Conventional Commits for every commit subject: keep the type and optional
scope in English and write the description after the colon in Russian. Example:
`feat(memory): автоматическая предметная память языковых решений`.
Choose the type and scope from the actual change; the example does not introduce
a project feature. PR titles follow the same subject format (see
[pr-body](pr-body.md)).

Every commit must include a substantive Russian body after a blank line. Explain
what changed and why, using concise paragraphs or bullets for distinct changes.
Include relevant verification or limitations when useful. Do not merely repeat
the subject, paste the task prompt or include a command transcript. For example:

```text
docs(git): единый формат сообщений коммитов и заголовков PR

- Для коммитов и заголовков PR закреплён Conventional Commits:
  тип и scope на английском, описание после двоеточия на русском.
- Тело коммита объясняет изменения и их причины, чтобы историю
  можно было понять без переписки с агентом.
```

Do not add `Co-Authored-By` trailers, AI model names, agent signatures or other
automatic AI attribution to authored commit messages or PR text. Preserve
existing history and unrelated authorship metadata; this rule does not authorize
rewriting old commits or changing Git author/committer identity.

With a local checkout, prefer a UTF-8 message file and `git commit --file <file>`
to preserve the subject, blank line and body without shell interpolation. Inspect
the complete resulting message as well as the staged content. Commit on the task
branch. Empty commits require a separately defined workflow reason; they are not
a substitute for an implementation or check.

## Explicit push and remote proof

Read `git ls-remote --refs <remote> refs/heads/<branch>` immediately before push.
Compare with known ancestry and expected remote state. Fetch and inspect any
unknown commits and differences from the common ancestor before proceeding.
Ordinary divergence does not authorize rewriting history. Use a normal merge
where appropriate or report a blocker if preserving the work cannot be proved.

Publish only the task ref, never a bare push governed by `push.default`:

```sh
git push --set-upstream <remote> refs/heads/<branch>:refs/heads/<branch>
git rev-parse HEAD
git ls-remote --refs <remote> refs/heads/<branch>
```

For later pushes use the same explicit remote/refspec without `--set-upstream`.
Record the expected commit before mutation and compare the exact SHA with a new
live remote read afterward. Exit code zero and a matching cached tracking ref
are insufficient. A mismatch means publication is incomplete.

On timeout, disconnect or uncertain push/API response, read actual remote state
first. Retry only after proving the intended operation did not happen and the
preconditions still hold. Preserve newly arrived commits. Apply the same rule to
PR creation/edits and comment creation; never create duplicates after a timeout.
If state cannot be read, report the last confirmed state and stop mutation.

## Narrow exception for a verified stack

For ordinary published PRs, rebase/amend and force-push remain prohibited without
separate explicit authorization. A verified stack permits necessary restack,
cascading rebase, synchronization and publication of the task's own affected
branches without requesting permission for each technical step. Before using it:

1. Verify live PR metadata proves membership in one repository and native stack,
   or the user explicitly established the dependent branch chain. Similar names,
   an assumed ancestry or a dependent base alone do not prove registration.
2. Record exact repository, trunk, ordered base/head pairs, affected branches,
   branch ownership, local SHA values and live expected remote SHA values.
   Determine the full downstream impact before starting.
3. Fetch and inspect all affected ancestry and commits. Prove there are no
   unknown commits and no unsaved user changes in affected worktrees/indexes.
   Preserve known tips as recovery refs before rewriting; do not overwrite an
   existing ref. If ownership or preservation cannot be proved, stop.
4. Synchronize from the changed lower layer upward, replaying only each layer's
   own changes onto its correct parent. Resolve conflicts semantically using
   both sides and their common ancestor, not whole-file ours/theirs selection.
   Review range-diffs and final diffs to prove no unrelated work disappeared.
5. Rerun affected verification, publish only the recorded own branches and
   reread every resulting remote ref. For manual rewritten pushes use an
   explicit lease bound to the previously recorded live SHA:

   ```sh
   git push --force-with-lease=refs/heads/<branch>:<expected-sha> <remote> refs/heads/<branch>:refs/heads/<branch>
   ```

   Never use bare `--force`, an implicit tracking-ref lease, or a blind push.
   Background fetches must not change the recorded expected SHA. A lease failure
   requires inspection of new remote work, not replacing the expected value
   mechanically. A stack manager is acceptable only after verifying equivalent
   ownership, expected-state protection and exact read-back guarantees.
6. Treat partial multi-branch publication as incomplete. Read all live tips after
   an unknown outcome; resume only after reconciling the actual chain. Do not
   blindly roll back remote refs. Refresh PR descriptions, CI and review evidence
   for every new HEAD; retained GitHub approvals alone are not proof that new
   content was reviewed.

This exception does not authorize deleting/reordering PRs, changing stack
membership, rewriting others' branches, ungrounded base changes, merging or
altering protection rules. Keep recovery refs until publication and preservation
are confirmed. Do not introduce a mandatory stack CLI.

Official behavior: [GitHub stack rules](https://docs.github.com/en/pull-requests/reference/stacked-pull-requests),
[stack maintenance](https://docs.github.com/en/pull-requests/how-tos/create-pull-requests/managing-stacked-pull-requests),
[Git explicit leases](https://git-scm.com/docs/git-push). Recheck version-sensitive
interfaces before use. Native stacks evaluate Actions against the trunk;
ordinary dependent PRs use their direct base. Do not assume one implies the other.

## API-only execution

When no local Git/gh checkout is available, use suitable GitHub APIs/connectors.
Read actual refs, create the task branch from the exact base commit, prepare a
Git tree containing only listed task changes, commit with the expected parent,
then update only the task ref by fast-forward. Read back its exact SHA and inspect
the actual diff. Preserve all verification and PR postconditions. An API without
safe expected-state protection cannot be used to rewrite a stack. Tool absence
does not permit weaker guarantees or an unverified claim of completion.
