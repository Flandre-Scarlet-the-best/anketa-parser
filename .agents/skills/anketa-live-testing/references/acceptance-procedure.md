# Live acceptance procedure

This reference specifies how runtime behavior is specified, executed, observed
and judged. It is not a CLI manual, not the repository test matrix and not a
publication procedure. Ownership of those neighbours is described in
[Live acceptance of anketa-parser](../SKILL.md).

## 1. Specification before execution

Map every materially affected behavior, state transition, failure path and
requirement to a scenario that can be verified independently. Before the first
run, record in ignored run evidence:

1. Worktree, revision and uncommitted changes; the executable under test (built
   assembly, container image or published artifact), the build command that
   produced it and its location; runtime version, operating system, effective
   configuration sources and a sanitized target identity.
2. For each mandatory scenario: controlled input and baseline state, expected
   result derived from the product contract and that input, the implementation
   path that must execute, the independent witness of execution, and whether a
   negative or recovery case applies.
3. Isolation boundary and permissions: which database, role, Compose project,
   volume, port and files are used; which operations can write; which external
   calls can occur; who owns spawned processes and containers; the cleanup plan
   and resource limits.
4. Objective PASS and FAIL criteria, what would make evidence incomplete,
   diagnostic intervals distinct from real deadlines, and the observation plan.
5. The witnesses appropriate to the operation: true process exit code, separated
   stdout and stderr, machine document fields, durable rows, revision, decision
   and queue counters, journal and checkpoint state, schema state, build or
   image identity.

Do not derive expected values from the output of the component under test, do
not adjust them after seeing a result, and do not silently drop a scenario that
failed. Select scenarios from the changed area, not from which run is
convenient: an uncovered path stays uncovered in the report.

## 2. Temporary drivers

Prefer existing checks: the unit and contract suites, the CLI itself, the
opt-in Docker integration tests and the documented maintenance commands. Create
a temporary driver only for a missing real scenario or an independent
observation, and keep it minimal.

A driver may schedule the real entry point, isolate input, seed a disposable
database, capture true exits and streams, enforce time and resource limits,
poll status through the application, compare before and after, and clean up. It
MUST NOT:

- reimplement argument parsing, configuration resolution, fetching, extraction,
  filtering, snapshot writing, queue leasing, transaction boundaries, retry
  accounting or recovery;
- write a purported application result directly into product tables;
- replace a claimed live interaction with a mocked or replayed response;
- fabricate a journal, checkpoint or status row;
- disable a safeguard, shorten a retry or backoff window, bypass TLS policy, drop
  a lease constraint or skip verification to reach a green result;
- treat its own orchestration messages as application evidence.

Automate the objectively checkable criteria instead of affirming each output
manually. Human inspection is acceptable only for a property that cannot be
checked mechanically, and it is identified as such in the report.

A helper fault is an observer fault. Diagnose it separately; incomplete
observation is `PARTIAL`, not `PASS` and not automatically a product `FAIL`.

## 3. Evidence classes

Keep these classes apart in planning and in reporting; never pool them into one
claim.

| Evidence | Proves | Does not prove |
| --- | --- | --- |
| Unit and contract suites | The asserted branches and contracts on synthetic inputs | Real database behavior, external source, Host lifecycle, container composition |
| Static checks, formatting, link and skill validators, Python contract suite | Their own scope at an identified revision | Any runtime path |
| Fixture scan against isolated migrated PostgreSQL | The real CLI, filtering, snapshot writing, journal, queue and persistence path on controlled input | A successful request to the live source, current upstream content, completeness of recognition |
| Repeat or resumed run on the same target | The documented idempotency, checkpoint reuse and progress preservation that were actually observed | Behavior of a different source, input version or database |
| Authorized real source request over isolated storage | One real external interaction, its processing and its stored result | Correctness of all durable data, coverage of every negative case, global completeness of the source snapshot |
| Opt-in container integration | The exercised Compose, PostgreSQL and container scenario | Every deployment, restart or failure mode; a skipped run is `NOT_RUN` |
| Host process observation | What the observed Host actually did with its own effective settings | What the CLI in another directory or the container health check reports |

A fixture is a controlled input, not a production default: it validates the
implementation path and expectations derived from its own content. Cache reuse,
repeated retrieval and a genuine network interaction are different outcomes and
are named as such.

## 4. Positive, negative and recovery paths

- For a positive path, prove the required work happened: an observed fetch, an
  applied snapshot, a written row, a leased and completed job, a served response
  — not merely a file that exists or a process that stopped.
- For a negative path, declare the expected refusal before the run and verify
  both the reported category or status and the unchanged state that must
  accompany it. A deliberate refusal passes only as a refusal; it never proves
  the successful path. For example, a malformed or conflicting input is expected
  to fail with a category and a preserved journal instead of destroying the
  previous successful state.
- For repeat behavior, compare before and after: snapshot versions, stored
  posts, decision counts, job states and timestamps. Two successful exits do not
  demonstrate idempotency; unexplained duplicates and silently dropped records
  are failures of the acceptance even when every exit code was zero.
- For recovery, identify the last confirmed checkpoint, the preserved journal or
  lease, the fact of interruption, the later continuation and the absence of
  duplicates and losses. A separate successful run on different input is not
  proof of resumption.
- For storage operations, verify the real restoration target, the source
  archive and the resulting data through the application's own verification
  path. Archive size alone is not restorability.
- For long background work, distinguish progress, process liveness and the
  observer's own heartbeat; treat a missing deadline as missing rather than
  inventing one.

## 5. Verdict classification

| Verdict | Meaning |
| --- | --- |
| `PASS` | The required real path executed; the predeclared outcome and postconditions were proved independently |
| `FAIL` | An adequately observed product path violated its predeclared contract |
| `PARTIAL` | Execution happened, but meaningful evidence, coverage or attribution is missing |
| `NOT_RUN` | A mandatory scenario never executed, including a missing prerequisite before launch |

An observer failure during a witnessed but unconfirmed run is `PARTIAL`, not an
assumed product `FAIL` or `PASS`. A prerequisite missing before execution — an
unavailable SDK, an unauthorized external request, an unreachable service — is
`NOT_RUN`, and the blocking prerequisite is stated exactly. An external failure
requires independently supported attribution; do not blame the source without
showing the phase that failed.

If any mandatory scenario fails, an overall PASS is impossible. If any mandatory
scenario is unverified, the overall result is `PARTIAL` or `NOT_RUN` with its
reason. Do not hide a gap behind a neighbouring scenario's PASS, behind unit
results, or behind a green hosted run.

An unproven provenance is its own limitation: when the executed artifact cannot
be tied to the tested revision, provenance-dependent scenarios are `PARTIAL`
even if the artifact behaved as desired.

## 6. Report per scenario

For each mandatory scenario report its verdict, reason and actual coverage.
Choose the remaining evidence according to what happened:

- **Not run (`NOT_RUN`):** State the blocking condition, why it prevented the
  scenario from running, and which boundary remains untested. If useful, label
  any planned command as planned and keep preflight results separate. There is
  no scenario execution command, process exit code or stream capture to report.
- **Executed or observed:** Record the actual command if you ran one, the
  executable or image identity and whether provenance was verified, the sanitized
  target, effective configuration and isolated resources used. Record observed
  machine status and independent postconditions, and say how you checked them.
  Keep captured stdout and stderr separate, quoting only what is needed and
  excluding secrets, contacts and raw source text. Report unavailable captures
  as unavailable, not empty.
- **Process outcome:** For a terminated process report the observed exit code,
  or mark it unknown if it was not captured as described in
  [Helper execution](helper-execution.md). For a process still running at the end
  of observation (for example, a Host), report the observation window, observed
  state or transitions, and that no exit was observed yet. Do not infer shutdown
  or an exit code. Use `PARTIAL` if a required outcome remains unobserved; an
  observation-only scenario can pass while the process is still running if all
  predeclared criteria were proved and none requires termination.

Report cleanup performed and resources intentionally left in place, including
any owned resources allocated before a scenario was blocked.

Separate unit, contract, integration, real-database, external-source, container
and hosted-CI evidence explicitly.

## 7. Postconditions and cleanup

Verify that drivers and reports remain ignored, that tracked files differ only by
intended task changes, and that durable data, processes, containers and volumes
match the recorded baseline or a justified intentional change. Clean up only
resources confirmed as belonging to this run, through their normal shutdown or
the application's documented maintenance operation. Never delete an unknown
volume, directory, process or another worktree's resources to make a failure
disappear; report what remains and why.

When a regression is reproduced, record the reproduction and the desired
permanent test. Add or change a tracked test only when the task authorizes a
permanent change; verification-only work does not modify the repository.
