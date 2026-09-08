---
name: release-notes
description: "Use when writing or rewriting the GitHub release description for a tag of this repository, whether the release is published or still a draft. Produces the house format: bolded theme blocks, then Features, Fixes, Performance, Breaking changes, Migration."
---

# Release notes

Write the body of a GitHub release for one tag, in the format every release from v0.5.0 onward uses.

## Read first

1. **The existing description of the tag**: `gh release view <TAG> --json body -q .body`. Where it already has prose, that prose is your primary source: it carries facts, measured numbers, and wording someone chose deliberately. Restructure and compact it rather than regenerating from scratch, and never drop a breaking change, a migration step, or a measured number it states. Older tags often have no prose at all, only an auto-generated list, in which case you are authoring from the pull requests instead and every claim needs a source.
2. **Two or three recent releases** as the voice reference: `gh release view v0.9.2 --json body -q .body`, and the same for v0.9.1 and v0.9.0. Match their bullet length and section shapes.

## Build the change list from git, not from GitHub

GitHub's generated list is unreliable: it has silently omitted merged pull requests on several tags of this repository. Derive the list yourself.

```
git log --oneline <PREVIOUS_TAG>..<TAG>                       # squash merges carry "Title (#NNN)"
gh pr view <NNN> --json number,title,author                   # title and author, per number found above
```

Look each number up individually. `gh pr list --limit 100` reaches only the hundred most recently merged pull requests, which today stops at #225, so on an older tag it silently returns nothing for the entries you need.

Every entry is `* <pull request title> by @<author> in https://github.com/RicoSuter/Namotion.Interceptor/pull/<NNN>`. A commit in the range with no pull request reference still belongs in the list; give it its subject and short SHA so nothing merged is invisible. Close with `**Full Changelog**: https://github.com/RicoSuter/Namotion.Interceptor/compare/<PREVIOUS_TAG>...<TAG>`, matching the tag prefix style the compare link already uses for that pair.

## Scope: Namotion.Interceptor only

HomeBlaze must not appear in any prose chapter: not in the intro, Features, Fixes, Performance, Breaking changes, Migration, or any tail section. This includes HomeBlaze-only breaking changes and their migration steps.

HomeBlaze appears only inside `## What's Changed`, under a `### HomeBlaze` subheading at the end of that list.

Classify by the paths a pull request touches, not by its title. Anything under `src/` that is not `src/Namotion.Interceptor*` is HomeBlaze: today that is `src/HomeBlaze/`, and on older tags the device libraries that sat at `src/Namotion.Devices.*` before they moved.

```
git show --pretty=format: --name-only <commit> | grep '^src/Namotion\.Interceptor'
```

A pull request that changes `src/Namotion.Interceptor*` source as well is a core entry and stays in the main list; write up only its core half. Judge on source files and ignore incidental ones, since almost every HomeBlaze change also touches the solution file, `Directory.Build.props`, a workflow or a doc. Titles mislead in both directions: "HomeBlaze: Philips Hue" (#243) shipped seven source files in `Namotion.Interceptor.Mcp` and is a core entry, while "HomeBlaze: Add myStrom WiFi Switch integration" (#245) touched nothing outside HomeBlaze but the solution file.

If removing HomeBlaze content empties a section, drop the section. A short body is the correct outcome for a release that was mostly HomeBlaze; do not pad it.

## Structure

The intro is **only** bolded theme blocks. No framing or scene-setting sentence before them, no lead-in like "Two threads run through this release". Start straight at the first block.

```
**Theme name (#PR).** Two or three sentences: what changed and why it matters to a consumer.

**Second theme (#PR).** Same.
```

Three to five blocks on a normal release, one or two on a small one. Nothing else above the first `##` heading.

Then, omitting any section with nothing to say:

1. `## Features`
2. `## Fixes`
3. `## Performance`
4. `## Breaking changes`
5. `## Migration`
6. A release-specific tail section when one is warranted (`## Dependencies and cleanup`, `## Known limitation`)
7. `## What's Changed`
8. The `**Full Changelog**` line

Headings are sentence case exactly as spelled above.

Keep it compact: one sentence per bullet, two at most, each ending with its pull request reference like `(#420)`. Detail belongs in the pull request, which the reader can open.

`## Breaking changes` is the exception, because a reader acts on it. Group by area with `### Area (#PR)` subsections when there is more than one, and allow two sentences per bullet.

Badge every bullet there with its kind, matching the labels a pull request description uses, so the note can carry the author's own classification through instead of re-deriving it:

- `API:` a signature, rename or removal. The compiler catches it, or a failed recompile does.
- `Behavior:` compiles and runs, and does something different.
- `Contract:` compiles and runs, and the caller is now wrong unless it changes. State the new obligation.

Badge all three consistently rather than only the surprising ones, so a reader can scan for `Contract:` and trust that its absence means there is none. Two kinds of bullet take no badge, because neither is a change a caller acts on in code: a scope note such as "no public API is added or removed", and a packaging change such as a new transitive dependency. That kind is the rare and dangerous one: nothing in a build or an API snapshot reveals it, and both instances in the last ten releases were of it, `LastSynchronizedAt` becoming a correctness obligation for direct `ISubjectSource` implementers and `WriteChangesAsync` no longer permitted to read the model.

`## Migration` is a numbered list, one instruction per line, harvested from the migration step each breaking bullet in the pull request carries. Keep measured numbers (percentages, byte sizes, allocation counts) wherever the source has them, and never invent one.

A pull request description has one section with no counterpart here. Its `## Contract` states the invariants the change establishes, which split two ways: a guarantee that is new becomes a `## Features` bullet, and a guarantee that changed becomes a `Contract:` bullet under `## Breaking changes`. A limitation stated there, such as a queue being unbounded, belongs in a `## Known limitation` tail section when a consumer would otherwise be surprised by it.

## Doc links

Link terms inline in the prose. Never add a list-of-links section.

Use absolute URLs pinned to the release's own tag, so they show the docs as of that version:
`https://github.com/RicoSuter/Namotion.Interceptor/blob/<TAG>/docs/<file>.md`

Verify before linking, because the docs of an old tag are not today's docs:

```
git ls-tree --name-only <TAG> docs/                    # the file exists at that tag
git show <TAG>:docs/<file>.md | grep -nE '^#{2,3} '    # the heading exists; lowercase it and hyphenate spaces for the anchor
```

A handful of links is right. Link the doc that covers a theme, not every noun.

## Verify claims against the tree, and report what is wrong

Pull request bodies describe intent at the time of writing and drift from what shipped. Several claims in this repository's history did not survive checking: a rename to a member that never existed at the base tag, a removal of helpers that were private all along, diagnostics counters described as new that already existed. All three were true when written and false by merge, which is a drift problem no amount of care at authoring time prevents.

So check anything load-bearing before it reaches the notes. The tree wins; a claim the tree contradicts comes out.

```
git show <TAG>:<path>                                        # what actually shipped under that name
git grep -n '<MemberName>' <PREVIOUS_TAG> -- src/            # did it exist at the base at all
git diff <PREVIOUS_TAG> <TAG> -- '*PublicApi.verified.txt'   # exact public API breaks, where snapshots exist
```

Public API snapshots only exist from v0.6.0 onward. Below that, breaking changes rest on source diffs, so say less rather than more.

Run the check in both directions:

- **Wrong**: a claim in a pull request body that the tree contradicts at this tag. Note what the body says and what shipped.
- **Incomplete**: a change in the tree that no pull request body mentions. The public API snapshot diff is the sharp tool here, since every entry in it should be traceable to some description. An unexplained entry is a real break nobody wrote up, which is how the `sourceName` to `connectorName` rename across the OPC UA registrations was found.

### Report it

Finish the run with a short report to the user, separate from the release body and never inside it:

```
Pull request descriptions worth correcting:

- #323 says `GetPath` was renamed to `TryGetPath`. `GetPath` does not exist at v0.6.1; only
  `TryGetPath` did, with the same signature. The rename predates the base tag.
- #321 lists `PollingItemCount` and `TotalReconnectionAttempts` as new diagnostics. Both already
  existed on `OpcUaClientDiagnostics` at v0.6.1. Only `PendingWriteCount` is new.
- #262 says it removed `OpcUaClientRegistration` and `OpcUaServerRegistration`. Neither type exists
  at either tag, so it describes an intermediate branch state.

Not mentioned by any description, found in the API snapshot diff:

- `sourceName` renamed to `connectorName` on `AddOpcUaSubjectClientSource`, `AddOpcUaSubjectServer`
  and their keyed overloads. Breaks named-argument call sites.
```

State the claim, state what the tree shows, and stop there. Whether a description gets corrected is the user's call, not something to fix on their behalf, and the release notes ship correct either way because the wrong claims never entered them.

## Style

AGENTS.md applies: no em dashes, no abbreviations in prose, and no AI attribution anywhere. Also keep markdown paragraphs on one line rather than wrapping at a column, and use exact API names in backticks.

## Apply and check

```
gh release edit <TAG> --notes-file <path>
gh release view <TAG> --json body -q .body | grep -E '^## '     # section order
```

Confirm the body opens on a `**` block, contains no em dash, mentions HomeBlaze nowhere before `## What's Changed`, and links only to its own tag:

```
gh release view <TAG> --json body -q .body | grep -oE '/blob/v[0-9.]+/' | sort -u
```

That last check is also how you catch a body written for the wrong tag.

A draft release keeps an `untagged-...` URL and its tag does not exist yet, so its tag-pinned doc links only resolve once it is published. That is expected; do not switch them to `master`.

## Doing several tags at once

One agent per tag works well. Give each a private scratchpad path or tag-scoped filenames (`v0.6.1-body.md`, not `body.md`); agents sharing a directory have overwritten each other's working files, and the failure mode is publishing one release's text under another tag. The `/blob/` check above catches it.
