# AGENTS.md

This file provides guidance to AI coding agents (Claude Code, Codex, GitHub Copilot) when working with code in this repository.

## Overview

Namotion.Interceptor is a .NET library for creating trackable object models through automatic property interception using C# 13 partial properties and source generation. It enables property change tracking, derived property updates, and object graph management with zero runtime reflection.

## Priorities

When tradeoffs conflict, prefer in this order:

1. **Correctness** — documented semantics, thread-safety (no data races, no torn reads/writes), quiescent consistency (state agrees once writes settle), existing invariants hold.
2. **Performance** — minimize allocations and CPU time. Both matter; when they trade, allocations usually win (GC pressure compounds across the host).
3. **Code style / idiom** — non-idiomatic API is fine when 1 or 2 demand it (e.g., `ImmutableArray<T>` in public API instead of `IEnumerable<T>`).

## Development Commands

### Build and Test
- `dotnet build src/Namotion.Interceptor.slnx` - Build entire solution
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` - Run unit tests (default)
- `dotnet test src/Namotion.Interceptor.slnx` - Run all tests including integration
- `dotnet pack src/Namotion.Interceptor.slnx` - Create NuGet packages

Only run integration tests when changing connector implementations (OPC UA, MQTT, WebSocket, etc.) or HomeBlaze UI — run those targeted per project, e.g.:
- `dotnet test src/Namotion.Interceptor.OpcUa.Tests`
- `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`

Risky connector work also needs the [Connector Tester](docs/connector-tester.md), which does not run in CI and takes hours. Agree any long-running verification, that and benchmarks alike, while planning the change rather than once the branch is ready, and confirm it ran before finalizing the pull request.

### Running Samples
- `dotnet run --project src/Namotion.Interceptor.SampleConsole` - Run console sample
- `dotnet run --project src/Extensions/Namotion.Interceptor.SampleBlazor` - Run Blazor sample

### Performance Testing
- `pwsh scripts/benchmark.ps1 -Filter "*RegistryBenchmark*" -LaunchCount 3` - Compare against a base branch (`-Filter` takes several patterns)
- `pwsh scripts/benchmark.ps1 -Filter "*RegistryBenchmark*" -LocalOnly` - Absolute numbers for the current tree

Always read [Benchmarking](docs/benchmarking.md) before running or interpreting a benchmark.

## Architecture

### Core Components
- **Core Library**: `Namotion.Interceptor` - Base interfaces and execution engine (.NET Standard 2.0)
- **Source Generator**: `Namotion.Interceptor.Generator` - Compile-time code generation for `[InterceptorSubject]` classes
- **Extension Libraries**: Tracking, Registry, Validation, Hosting, Sources, Dynamic (.NET 9.0)

### Key Design Patterns
- **Chain of Responsibility**: `IReadInterceptor`/`IWriteInterceptor` middleware chain
- **Service Container**: `IInterceptorSubjectContext` for dependency injection and coordination
- **Source Generation**: Zero runtime reflection through compile-time code generation
- **Observable Streams**: System.Reactive integration for change notifications

### Project Structure
```
src/                                  # Projects are flat here, grouped by name rather than by folder
├── Namotion.Interceptor/             # Core library with base interfaces
├── Namotion.Interceptor.Generator/   # Source generator for [InterceptorSubject]
├── Namotion.Interceptor.{Feature}/   # Libraries: Tracking, Registry, Connectors, OpcUa, AspNetCore, ...
├── Namotion.Interceptor.{X}.Tests/   # Test project per library
├── Namotion.Interceptor.{X}Sample*/  # Example applications
├── Namotion.Interceptor.Benchmark/   # BenchmarkDotNet project
└── HomeBlaze/                        # HomeBlaze application and its device libraries
docs/                                 # Feature and connector documentation
└── design/                           # Internal design documents
```

## Language Requirements

This codebase requires **C# 13 preview features** for partial properties. The `[InterceptorSubject]` attribute triggers source generation that creates interception logic at compile-time.

### Basic Usage Pattern
```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking();

var person = new Person(context);
```

## Configuration Extensions

The library uses a fluent configuration API:
- `WithFullPropertyTracking()` - Enable complete change detection
- `WithRegistry()` - Enable object graph navigation
- `WithDataAnnotationValidation()` - Enable automatic validation
- `WithLifecycle()` - Enable attach/detach callbacks

## Build Configuration

- **Global Settings**: `Directory.Build.props` with nullable enabled, warnings as errors
- **Target Frameworks**: .NET Standard 2.0 (core), .NET 9.0 (extensions)
- **Package Version**: released on NuGet, breaking changes are fine when justified but need user approval
- **CI/CD**: GitHub Actions with xUnit testing, coverage reporting, and NuGet publishing
- **Native AOT**: full compatibility where possible is the target (#516). New code prefers static alternatives to runtime code generation and reflection, and existing sites are fixed when a change already touches them.

## Key Dependencies

Versions are pinned in the project files, not here, so read them from there.

- Microsoft.CodeAnalysis.CSharp (source generator)
- System.Reactive (change tracking observables)
- Microsoft.Extensions.DependencyInjection.Abstractions (hosting)
- OPCFoundation.NetStandard.Opc.Ua.* (industrial integration)

## Industrial Integration Focus

The library has specialized support for:
- **OPC UA**: Industrial automation protocol integration
- **MQTT**: IoT messaging patterns
- **ASP.NET Core**: Web API exposure
- **GraphQL**: Real-time subscription support
- **Blazor**: UI data binding components

## Performance Considerations

- All interception logic generated at compile-time (no runtime reflection)
- Dedicated benchmarking with BenchmarkDotNet, see [Benchmarking](docs/benchmarking.md) for how to run a comparison and how to read one
- Recent performance optimizations focused on allocation reduction
- Observable streams for efficient change propagation

## Coding Style

- **Avoid abbreviations** in variable and parameter names unless the name is very long. Use descriptive names (e.g., `attribute` not `attr`).
- **No em dashes** in docs, READMEs, or PR descriptions. Restructure into plain sentences instead.
- **No hard wrapping** in markdown. Keep a paragraph on one line instead of breaking at a column.
- **Inline comments: the why a reader cannot derive.** Length is earned by preventing a plausible wrong edit, such as a lock discipline, a pooled buffer that must not be read after release, or an ordering constraint. It is not earned by defending a decision against alternatives, which belongs in the pull request or `docs/design/`. Never restate the line below.
- **XML docs state the contract**, not the reasoning. `<remarks>` is for a caveat a caller must act on.
- **One canonical location per concept**, cross-referenced. Three copies drift.

## Git Rules

- Never include AI attribution in commit messages, PR descriptions, or GitHub comments. This covers agent names ("Claude", "Codex", "Copilot"), `Co-Authored-By` trailers, and "Generated with" footers.

## Pull Requests

- Fill in [the pull request template](.github/pull_request_template.md). `gh pr create --body-file` bypasses it, so open the file rather than waiting to be shown it.
- Prefix the title with `fix:`, `feat:`, `perf:`, `docs:`, `refactor:`, `test:` or `chore:`.
- Apply an `area:` and a `type:` label at creation with `gh pr create --label`, choosing from `gh label list`.

## Test Conventions

- **Naming**: `When<Condition>_Then<ExpectedBehavior>` (e.g., `WhenDepthIsZero_ThenReturnsNoChildren`)
- **Structure**: Explicit `// Arrange`, `// Act`, `// Assert` comments separating each phase (use `// Act & Assert` for exception tests)
- **No hardcoded waits**: Use `AsyncTestHelpers.WaitUntilAsync(() => condition)` or event-based synchronization (`ManualResetEventSlim`, `CountdownEvent`) instead of `Task.Delay`/`Thread.Sleep`. Hardcoded delays are either too long (slow CI) or too short (flaky CI).

## Public API Tracking

The public API for some libraries is snapshot-tested via `PublicApiGenerator` + `Verify`. Each one's test project has a `VerifyChecksTests.PublicApi` test that compares the generated API against a checked-in `VerifyChecksTests.PublicApi.verified.txt`. When the API changes intentionally, accept the new snapshot by replacing `.verified.txt` with the test's `.received.txt` output.
