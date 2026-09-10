<!-- Start with the summary: what changed and why it matters to a consumer, in a few sentences or
     bullets. No heading; the description opens on it.

     Where issues are involved, close the summary with a list of them. The first form has a
     mechanical effect on merge and the others do not:

     - Closes #123        closes the issue on merge. Only GitHub's keywords do this:
                          close/closes/closed, fix/fixes/fixed, resolve/resolves/resolved.
     - Related to #123    touched but not finished. Avoid the keywords above or it closes anyway,
                          which is what "addresses" and "supersedes" are for.
     - Follow-up: #123    deliberately left for later, so a reviewer can see where the scope line is.

     Delete each comment as you fill its section in, so the description reads as prose rather than
     half boilerplate. Anything left here also reaches the tools that read this body later.
     Drop any heading that does not apply, except Breaking changes. -->

## Why

<!-- The problem this solves, when the summary does not already make it obvious. Drop this heading if it does. -->

## Contract

<!-- Optional. What a caller can now rely on: the invariants this change establishes or alters.
     One guarantee per bullet, stating the limit as plainly as the promise, for example "serialization
     is per subscription, an observer shared by several subscriptions may still be invoked
     concurrently" or "disposal drops queued work, a delivery already in flight may still complete".
     Drop this heading when the change establishes no new guarantee. -->

## Breaking changes

<!-- What a consumer must act on, each with the migration step that follows from it. Cover all three
     kinds, since only the first announces itself:

     - API:      a signature, rename or removal. The compiler catches it, or a failed recompile does.
     - Behavior: compiles and runs, and does something different.
     - Contract: compiles and runs, and the caller is now wrong unless it changes. State the new
                 obligation, for example an interface implementer that must now stamp a timestamp.

     Write "None" rather than deleting this heading, so an empty section is a decision rather than
     an oversight. This is the section the release notes most often need and most often lack.

     Check the public API snapshot diff before answering: a change in
     VerifyChecksTests.PublicApi.verified.txt that is not described here reaches consumers unannounced. -->

## Performance

<!-- Start with a paragraph in plain terms: what the change costs or saves, and where. State a known
     regression as readily as an improvement, and say when a cost was accepted on purpose.

     Then condense the numbers into the table, one row per benchmark the change can reach. Do not paste
     raw BenchmarkDotNet output for both arms; a reader cannot diff two 15-column tables by eye.

     Keep at least one row the change cannot reach, marked as the noise reference. A delta means nothing
     until it clears what that row did in the same run, and picking a reference is subtler than it looks,
     because [GlobalSetup] is per class. See docs/benchmarking.md.

     Drop this heading when nothing was measured; an unmeasured claim is worse than silence. -->

| Benchmark | Before | After | Delta | Allocated |
|---|---:|---:|---:|---|
|  |  |  |  |  |
| _noise reference_ |  |  |  |  |

## Diff composition

<!-- Paste the output of: pwsh scripts/diff-composition.ps1
     It prints the whole table, header included. Add -PerProject to break production code down per
     project on a change that spans several. -->

## Verification

<!-- Tick what you did, and give the count where a suite reports one. Strike an entry through with
     ~~two tildes~~ and a short reason when it does not apply, so an unticked box always means
     "not done yet" rather than "not relevant". Say plainly what you did not run and why.

     Documentation:     the docs/ pages describing the behavior or API this changes. A stale sample
                        outlives a rename by months, and the release notes link these pages.
     Unit tests:        dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
     Integration tests: per project, for connector or HomeBlaze UI changes
     Benchmarks:        docs/benchmarking.md, every benchmark the change can reach plus a noise reference
     Connector Tester:  docs/connector-tester.md, load and chaos profiles for risky connector work,
                        agreed while planning because they take hours -->

- [ ] Documentation updated
- [ ] Unit tests
- [ ] Integration tests
- [ ] Benchmarks
- [ ] Load tests
- [ ] Chaos tests
