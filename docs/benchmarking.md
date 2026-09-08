# Benchmarking

The suite lives in `src/Namotion.Interceptor.Benchmark` and runs on BenchmarkDotNet. Running it is easy; reading it is not, because the interception paths are short enough that noise is the same size as the deltas people look for.

## The usual process

1. Find the benchmarks your change can reach: `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release -- --filter "*" --list flat`. That lists methods; `[Params]` turns some of them into several report rows.
2. Compare those in one filtered run, including at least one row the change cannot reach as a noise reference.
3. Use the whole suite only when step 1 cannot bound the change. It costs hours, so agree it first.

```
pwsh scripts/benchmark.ps1 -Filter "*RegistryBenchmark*","*ServiceOrderResolverBenchmark.LinearChain*" -LaunchCount 3
```

The script header lists every flag. The one with a trap is `-Filter`: several patterns are matched as OR, but they must be a PowerShell array rather than a comma joined string, and the array form needs a PowerShell caller, since from bash `"a","b"` collapses into one argument that matches nothing. Output is `benchmark_<timestamp>.md` in the working directory.

### Where it goes wrong

The script checks the base branch out **in your working tree**, so:

- Run from a worktree, placed **outside** the repository. BenchmarkDotNet resolves the project by searching up from the working directory, and an inner worktree gives it a second candidate.
- Git refuses a branch another worktree holds (`fatal: '<branch>' is already used by worktree at ...`), so the default `-BaseBranch master` dies at the first checkout when your main checkout sits on `master`. `-BaseBranch origin/master` avoids it, being detached.
- The base ref resolves at that checkout, not at startup, so a fetch, pull, reset or another worktree's commit in between changes what you measured. Check `$(git rev-parse --git-dir)/logs/HEAD` afterwards, and quote both hashes when reporting.

## Pin the CPU first

A CPU that changes frequency mid-run makes the arms incomparable by more than anything you are measuring. Pin it, and keep the machine quiet: no builds, no test runs, no leftover MSBuild nodes. BenchmarkDotNet's header cannot confirm the pin, having reported two different maxima for the two arms of one pinned run, so check the operating system instead (`scaling_governor`, `scaling_min_freq`, `scaling_max_freq`, `intel_pstate/no_turbo`).

Allocation columns survive all of this far better than timings and are usually the whole answer for allocation work. Not immune, though: accounting is process-wide, so a benchmark with a background thread such as `PropertyChangeSubscriptionsBenchmark` or `SubjectSourceBenchmark` absorbs that thread's allocations, and `Gen0`/`Gen1`/`Gen2` shift with heap randomization.

## Reading the results

**BenchmarkDotNet's statistics do not span the two arms.** `Ratio` against a `[Benchmark(Baseline = true)]` method, and the Mann-Whitney test behind `--statisticalTest`, work within one run. A branch comparison is two independent runs that the script concatenates, with nothing computed across them, so both arms can look precise and still disagree.

**Read the noise off a row the change cannot reach.** What that row does in the run is what the harness does to unchanged code, and a real delta has to clear it. Take it from your own run, and note that shorter benchmarks are more sensitive to code placement, so a slow reference understates the floor for fast rows.

Choose the reference carefully: `[GlobalSetup]` is per class and heap randomization re-runs it after every iteration, so a row is only insulated when its **class** setup also avoids your change. `RegistryBenchmark.GenerateSubjectId` touches no subject itself, but its class setup builds a thousand tracked `Car`s. `ServiceOrderResolverBenchmark` is the safe fallback, the only class producing rows that avoids subjects in both benchmarks and setup; one row such as `LinearChain` is enough, the whole class gives a spread.

**The Error column is not a significance test.** It says how precisely one run measured itself, not whether tomorrow's run agrees, and a reference row can move many times its error bar with provably unchanged code. `-LaunchCount N` does not fix that: each arm keeps its own binary, so a placement difference reproduces on every launch instead of averaging out.

**Repeat before believing a small delta.** Two runs of the same commits measure the noise directly. Expect sign flips: a `-Short` run against an identical tree gave a +2.2% median, eight of thirteen rows above +2%, and single rows at +32% and +45%.

**`-Short` decides nothing.** Every delta in that run sat inside its own error band. Use it to check that a filter selects what you expect, never to judge a change.

**Watch the timer floor.** Sub-nanosecond rows swing wildly because of the measurement, not the code. Benchmarks built for such paths amortize with `OperationsPerInvoke`, as `RegistryBenchmark.ReadParents` does over 256 calls, and those are meaningful; distrust an unamortized one.

## When a benchmark cannot answer

A benchmark shows "within noise", never "free". For whether a guard, a cast or an extra call costs anything, diff the machine code instead: minutes, and no noise floor. It settles whether a helper stayed inlined after gaining a branch, or whether a per-instantiation constant folded away.

Drive the members in a loop from a small console application, on both sides:

```
DOTNET_TieredCompilation=0 DOTNET_TieredPGO=0 DOTNET_ReadyToRun=0 \
DOTNET_JitDisasmDiffable=1 DOTNET_JitDisasm='SetPropertyValue WriteProperty' \
dotnet app.dll > jit.txt
```

`DOTNET_JitDisasmDiffable=1` is what makes two dumps comparable; without it addresses differ every run. Patterns are space separated, and a bare name matches every type declaring that method, which is what you want for a chain. Explicit interface implementations need the fully qualified metadata name, `Namotion.Interceptor.IInterceptorSubject.get_Properties`, or nothing matches and it looks like the method was never compiled. Diff the instruction lines only: the header's `; N single block inlinees` count changes when a new inlinable helper appears, identical instructions notwithstanding.

For a generator change nothing in the runtime moves. If `git diff --name-only <base>..<branch> -- src/ ':!*Tests*' ':!*Benchmark*' ':!*Generator*'` is empty, only generated code can differ, so diff that first. Force a rebuild, or an up-to-date build emits nothing and two empty directories look exactly like "no change":

```
dotnet build <project> -c Debug -t:Rebuild -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=<dir>
```

Output lands under `<dir>/<generator-assembly>/<generator-type>/*.g.cs`.

## What a run costs

Both arms, full job, `-LaunchCount 3`, from one machine, for scoping only:

| Scope | Both arms |
|---|---|
| `*RegistryBenchmark*` | about 30 minutes |
| Whole suite | about 6 hours |

`SubjectSourceBenchmark` measures in milliseconds per operation, and `ConstructThreeLevel` earns a `MultimodalDistribution` warning and so runs to its hundred iteration ceiling rather than the default fifteen, every launch. Between them they dominate a full run. Judge `ConstructThreeLevel` by allocations, its timing carrying a standard deviation around a quarter of its mean.

A class present on only one branch has nothing to compare against, so exclude it unless you are deliberately measuring it. To compare a new benchmark properly, put it on the base branch first.
