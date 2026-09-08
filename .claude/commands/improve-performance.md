# Improve Performance

Run a measurement-driven performance pass: brainstorm candidates, implement each on its own branch, benchmark each against a shared baseline, and let the user pick winners.

## Usage

`/improve-performance`

Optional argument: a topic slug (e.g. `attach-detach`) or a path to a doc/issue with pre-existing candidates. If omitted, the skill asks.

## Workflow

The skill has three phases: interactive setup (questions up front), automated loop (one subagent per candidate, sequential, hands-off), and interactive picks (user selects winners). All branch management happens in the single working checkout. No worktrees.

The design doc lives only on the parent branch and is updated there as each candidate finishes. Candidate branches contain only the implementation commit.

### Phase 1: Setup (interactive, all questions up front)

Before doing any work, refuse to proceed if the working tree is dirty (`git status --porcelain` non-empty). Tell the user to commit or stash and re-invoke.

Ask the user one question at a time:

1. **Topic slug.** Used as the branch prefix and doc filename. Example: `attach-detach`. Reject anything containing slashes, spaces, or uppercase.
2. **Candidate source.** Offer: (a) brainstorm fresh now, (b) load from an existing doc path, (c) load from a GitHub issue number, (d) user pastes a list. For (a), drive a focused brainstorm in this conversation (no need to spawn a subagent).
3. **Benchmark mapping.** For each candidate, propose a filter pattern covering the benchmarks that can reach it, following the process in [Benchmarking](../../docs/benchmarking.md). Check the filter also catches a row the candidate cannot reach, so each run carries its own noise reference; add a stable unrelated row such as `*ServiceOrderResolverBenchmark.LinearChain*` if it does not. Show the table with a `Coverage` column. For candidates with no clear coverage, ask per candidate: (a) write a new benchmark on the parent branch, (b) skip the candidate, (c) run with the closest existing filter and accept noisy results.
4. **Run config.** `LaunchCount` (default 3) and `Short` toggle (default off).

Test verification is fixed: every candidate runs `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` (matches the project default in `AGENTS.md`). Results are recorded only in the local design doc on the parent branch, not posted to any GitHub issue.

Then print a final plan summary (parent branch name, list of candidates with branch names, benchmark filter, doc path) and ask one explicit `proceed?` confirmation. Warn the user that the working directory will be owned by the skill for the duration, and give a time estimate derived from the chosen filter (see [Benchmarking](../../docs/benchmarking.md) for what a scope costs); a wide filter across several candidates is hours, not minutes.

On confirmation:
- Create the parent branch: `git checkout -b performance/<slug>` from `master`.
- For each new benchmark from step 3a: prompt the user (or implement inline if simple) to add it to `Namotion.Interceptor.Benchmark`. Build to verify. Commit with `perf(bench): add <name> benchmark` on the parent branch.
- Write `performance-<slug>.md` (at repo root) with: motivation, hot spots, candidates list (numbered, each with a `## Results` placeholder section), benchmark filter, decision criteria. Commit on the parent branch.

### Phase 2: Automated loop (sequential, one subagent per candidate)

The agent implements + builds + tests; the orchestrator runs the benchmark, commits, and updates the doc. Agents have been observed hanging on long-running benchmark calls and being sandbox-blocked from invoking `pwsh scripts/benchmark.ps1`, so the benchmark step belongs to the orchestrator.

For each candidate (in the order listed in the doc):

1. Verify state: working tree clean, current branch is `performance/<slug>`. If not, attempt cleanup with `git checkout -- . && git clean -fd`. If still not clean, hard stop and surface the situation to the user.
2. Create the candidate branch: `git checkout -b performance/<slug>-<candidate-slug>` (from the parent's current HEAD, which includes any previous doc updates). Note the flat naming: candidate branches are siblings of the parent under `performance/`, not children, because git refs cannot have both `performance/<slug>` and `performance/<slug>/anything` (filesystem path conflict).
3. Spawn the `performance-optimizer` subagent with a prompt containing:
   - `task_description` (the candidate description verbatim from the doc)
   - `current_branch` = `performance/<slug>-<candidate-slug>`
   - Plus: `candidate_title` so the orchestrator can use it in the eventual commit message.
4. Wait for the subagent to return its structured result. The agent leaves changes uncommitted in the working tree (no benchmark run).
5. Based on the returned status:
   - `success`: orchestrator runs `pwsh scripts/benchmark.ps1 -Filter "<benchmark_filter>" -BaseBranch performance/<slug> -LaunchCount <launch_count> -Stash` against the parent. The script stashes the agent's uncommitted changes, runs benchmark on parent, restores the stash, runs benchmark on candidate, writes `benchmark_*.md`. After the report exists, stage with `git add .` and commit with `git commit -m "perf: <candidate_title>"` on the candidate branch. Capture the resulting commit SHA.
   - `build-failed`, `tests-failed`, `clarification-needed`: clean the working tree with `git checkout -- . && git clean -fd`. Drop any dangling stash matching `benchmark-script-auto-stash` if present. No benchmark is run.
   - `precondition-failed`: hard stop and surface to the user.
6. Switch back to the parent: `git checkout performance/<slug>`.
7. Append the result to the design doc under that candidate's `## Results` section. Include status, commit SHA (or "skipped" with reason), files changed, notes, and the benchmark comparison table (extract just the table, not the full BenchmarkDotNet header).
8. Commit the doc update on the parent branch with message `docs(perf): <candidate-slug> results`.
9. Print a one-line summary to the user (`✓ candidate N/M: <slug> (status, mean Δ, alloc Δ)`).
10. Continue with the next candidate.

### Phase 3: Picks (interactive)

When the loop finishes, print a summary table:

```
N | Candidate slug | Status | Mean Δ | Alloc Δ | Branch
```

Pull the deltas from each candidate's results section in the doc (parse the comparison report). Judge them against what the unreachable rows did in that same run rather than against zero: a delta inside that spread is noise, not a win and not a regression. Flag only what falls outside it.

Ask the user: "Which to keep? (comma-separated indices, `all`, or `none`)."

On selection:
- Cherry-pick each picked candidate's implementation commit onto `performance/<slug>` in the order chosen. Stop on conflict and surface the file list to the user.
- Re-run the benchmark on the combined parent branch: `pwsh scripts/benchmark.ps1 -Filter "<filter>" -BaseBranch master -LaunchCount <n>`. This catches interaction effects between picked candidates.
- Append a `## Combined results` section to the design doc with this final report. Commit on the parent branch.
- Delete every candidate branch (`performance/<slug>-<candidate-slug>`) with `git branch -D`. Picked branches are now redundant (their commit lives on the parent). Unpicked branches are also discarded; their results stay recorded in the doc. The parent branch (`performance/<slug>`) and `master` are never deleted.

Print the parent branch name and a suggested next step (`gh pr create -B master -H performance/<slug>`). Do NOT open the PR. Do NOT push.

## Failure handling

- Dirty tree at any phase boundary: stop, do not attempt destructive cleanup. Show `git status` to the user.
- Subagent returns `build-failed` or `tests-failed`: mark candidate skipped in the doc with the failure detail, clean up the candidate branch state (`git checkout -- . && git clean -fd`, drop any stash named `benchmark-script-auto-stash`), continue with next candidate.
- Subagent returns `clarification-needed`: surface to user mid-loop. Either edit the candidate description in the doc and re-run that candidate, or skip.
- Cherry-pick conflict during final assembly: stop, show conflict files, let user resolve manually.

## Constraints

- Never push branches. Never open PRs.
- Candidate branches (`performance/<slug>-<candidate-slug>`) are deleted automatically only at the end of Phase 3, after the combined benchmark is committed. The parent branch (`performance/<slug>`) and `master` are never deleted automatically.
- Never modify `master` outside this skill. The parent branch holds all in-progress changes.
- Branch names: `performance/<slug>` for the parent, `performance/<slug>-<candidate-slug>` for each candidate (flat, hyphenated; nesting under the parent breaks because git refs cannot have both a file and a directory at the same path). Slugs are lowercase, kebab-case, no slashes.
- Doc path: `performance-<slug>.md` at the repo root (treated as a transient working doc tied to the parent branch). If the file already exists, ask the user whether to append a new dated section or use a different slug.
- Commit messages and doc text: no em dashes, no AI attribution, no "Generated with..." footer.
- Subagents only run in this repository.

## Notes

- The benchmark script (`scripts/benchmark.ps1`) accepts `-BaseBranch` so the comparison runs against the parent perf branch, not master. This matters for candidates that depend on a benchmark added on the parent: comparing against master would show "new benchmark + candidate" with no baseline.
- Sequential by design. Concurrent BenchmarkDotNet runs on the same machine pollute each other's numbers, defeating the purpose. The same applies to anything else running on the box, including builds and test runs.
- [Benchmarking](../../docs/benchmarking.md) covers pinning the CPU before a run and how to read a delta. Both matter here: this skill produces a table of small deltas, which is exactly the shape that gets over-read.
- The user owns the working directory while this runs. Runtime is dominated by the benchmark, which runs two arms per candidate, so it scales with the filter and the candidate count rather than with the test suite. Warn during setup confirmation.
