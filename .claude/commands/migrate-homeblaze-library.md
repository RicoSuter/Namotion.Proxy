---
description: Migrate a HomeBlaze v1 device library to v2 using the Namotion.Interceptor system
---

# Migrate HomeBlaze Device Library (v1 to v2)

Migrate a device/subject library from HomeBlaze v1 to v2. The argument is a search term to find the v1 library (e.g., `mystrom`, `nuki`, `shelly`).

## Usage

`/migrate-homeblaze-library <search-term>`

## Prerequisites

First, load and follow the **brainstorming skill** (`superpowers:brainstorming`). This migration command layers migration-specific context on top of the brainstorming flow. The brainstorming skill drives the conversation structure: understand context, explore approaches, present design incrementally, then implement.

## Context: v1 vs v2

**HomeBlaze v1** (`../HomeBlaze` relative to this repo) uses:
- `IThing` base interface with string `Id`
- `PollingThing` abstract base class for polling devices
- Manual `DetectChanges(this)` calls to notify state changes
- `[Configuration]`, `[State]`, `[Operation]` attributes on regular (non-partial) properties
- Device libraries named `HomeBlaze.*` or `Namotion.*`
- Blazor UI components co-located in the device project (`.razor` files)

**HomeBlaze v2** (this repo) uses:
- `[InterceptorSubject]` with `partial` classes and `partial` properties
- Source-generated interception (no runtime reflection, no manual `DetectChanges`)
- `[Derived]` properties that auto-update when dependencies change
- Property hooks (`OnPropertyChanging`/`OnPropertyChanged`) for validation and hardware writes
- `BackgroundService` for polling (no `PollingThing` base class)
- Device libraries named `Namotion.Devices.*`
- NO UI code in device projects (UI is separate)

**The goal**: Keep the device's logic and external API integration, but migrate to the Namotion.Interceptor system. Clean up, simplify, fix memory leaks, improve performance, and add tests. Do not just port 1:1 -- improve the code.

## Phase 1: Research

### Step 1: Find the v1 library

Search for projects in `../HomeBlaze/src/` matching the argument. If multiple matches, ask the user to pick. Read ALL source files in the v1 library to understand:
- Device classes, inheritance hierarchy
- Interfaces implemented (capabilities)
- Configuration properties
- State properties and how they're updated
- Operation and query methods
- Polling/connectivity patterns
- External API calls, HTTP clients, models/DTOs
- External NuGet dependencies

### Step 2: Read v2 documentation

Read these directories thoroughly -- they define how v2 subjects must be built:

- `docs/*.md` -- all interceptor system docs (subject guidelines, registry, lifecycle, tracking, derived properties, etc.)
- `src/HomeBlaze/HomeBlaze/Data/Docs/*.md` -- HomeBlaze architecture and general docs
- `src/HomeBlaze/HomeBlaze/Data/Docs/development/*.md` -- building subjects guide, conventions
- `src/HomeBlaze/HomeBlaze/Data/Docs/devices/*.md` -- existing device documentation as examples

### Step 3: Read v2 reference implementations

Scan `src/Namotion.Devices.*` for already-migrated device libraries. Read their source to understand the patterns in practice. Compare complexity: hub devices with children (like Philips Hue) vs simpler standalone devices (like GPIO).

Also read the corresponding `src/Namotion.Devices.*.HomeBlaze/` UI projects to understand the Blazor component patterns:
- `.csproj` structure (Razor SDK, references to device project + `HomeBlaze.Components.Abstractions`)
- `_Imports.razor` (standard usings for MudBlazor, device namespace, component abstractions)
- Widget components (`*Widget.razor`) -- compact display with `ISubjectComponent`
- Edit components (`*EditComponent.razor`) -- configuration forms with `ISubjectEditComponent`
- Setup components (`*SetupComponent.razor`) -- initial device registration (if applicable)

### Step 4: Read v2 interfaces

Read `src/HomeBlaze/HomeBlaze*.Abstractions*/` to understand all available v2 interfaces. Map each v1 capability to its v2 equivalent. Identify:
- Direct mappings (e.g., v1 `ITemperatureSensor` -> v2 `ITemperatureSensor`)
- Name changes (e.g., v1 `IPowerConsumptionSensor` -> v2 `IPowerSensor`)
- Missing v2 interfaces that need to be created
- v2 interfaces the v1 library didn't use but should (new capabilities)

### Step 4b: Read v1 UI components

Read any `.razor` files in the v1 library to understand:
- What configuration fields are exposed in the setup form
- What state is displayed in widgets
- Any special UI interactions (discovery flows, conditional fields, etc.)

This informs which v2 UI components to create and what fields they need.

### Step 5: Evaluate interceptor fit

For each v1 pattern, assess the best v2 approach:
- Manual `DetectChanges` -> automatic via `[InterceptorSubject]` partial properties
- Computed state -> `[Derived]` properties
- Write-then-poll -> property hooks (`OnPropertyChanging`/`OnPropertyChanged`)
- `PollingThing` -> `BackgroundService` with `ExecuteAsync`
- Nested model `[ScanForState]` -> derived properties reading from internal models

### Step 6: Check external dependencies

For all external NuGet packages used by the v1 library, check for the latest versions. Migrate to latest.

### Step 7: Query live devices (if available)

Ask the user for IP addresses or connection details for real devices, query their read-only status endpoints to discover data the v1 code doesn't use. Many device APIs return significantly more data than v1 implemented. Compare the actual API response fields against v1's property list to find gaps.

**Rules:**
- Only read-only/GET endpoints -- never execute write operations on live devices
- Only when the user explicitly provides connection details
- Compare multiple device variants if available (different models expose different components)

### Step 8: Identify missing features

Check the device's external API/SDK documentation or capabilities for features that v1 didn't implement but v2 could. Propose additions for user approval.

### Step 9: Check for generic interface opportunities

Before creating device-specific state properties, check whether a measurement or capability could apply to other devices too. Look at what other v1 device libraries expose -- if 2+ devices share a measurement, it likely deserves a generic interface in `HomeBlaze.Abstractions`.

Examples of generic interfaces that emerged from migrations:
- `IElectricalVoltageSensor`, `IElectricalCurrentSensor`, `IElectricalFrequencySensor` -- shared by energy meters, EV chargers, solar inverters
- `ISoftwareState` (SoftwareVersion, AvailableSoftwareUpdate) -- shared by any device that reports firmware/software versions

**Naming conventions:**
- Follow existing pattern: `I{Concept}Sensor` for sensors, `I{Concept}Controller` for controllable capabilities, `I{Concept}State` for status interfaces
- Use `Electrical` prefix for electrical measurements to avoid ambiguity (e.g., `ElectricalCurrent` not `Current`)
- Keep property names consistent with the interface name

### Step 10: Present migration plan

Following the brainstorming pattern, present the plan in incremental sections (200-300 words each), validating each with the user:

1. **Project name** -- propose `Namotion.Devices.*` name, user confirms
2. **Interface mapping** -- v1 capabilities to v2 interfaces, proposed new interfaces
3. **Subject hierarchy** -- which classes, inheritance, child collections (arrays for index-based identity, dictionaries for external IDs)
4. **Property design** -- configuration vs state vs derived, internal setters
5. **Polling/connectivity** -- how background work is structured
6. **External API changes** -- latest NuGet versions, API differences
7. **New features** -- capabilities beyond v1 parity (discovered from live device queries and API docs)
8. **UI components** -- which Blazor components to create (Widget, Edit, Setup), what fields/displays each contains, referencing existing v2 `.HomeBlaze` projects as patterns
9. **V1 feature checklist** -- complete list of v1 features with confirmation each is covered

## Phase 2: Implementation

### Design rules

These are critical conventions the implementation MUST follow:

- **Don't recreate subjects in polling loops** -- update existing instances (like Hue's `Update()` pattern). Subjects are stateful and expensive to recreate.
- **Internal setters** -- if properties are only set by device logic (polling, events), use `internal set`. Public setters only for properties that external code should write to.
- **All `[InterceptorSubject]` properties must be `partial`** -- initialized in constructors, NOT field initializers.
- **Collections replaced entirely, never mutated** -- `Items = newArray`, not `Items[0] = x`.
- **Clean up and simplify** -- don't port v1 patterns that the interceptor system makes unnecessary.
- **Fix memory leaks** -- dispose resources properly, cache HTTP clients, dispose children on replacement.
- **Performance** -- avoid allocations in hot paths, parallelize independent API calls.
- **No regressions from v1** -- every v1 capability must be present in v2.
- **No UI code in the device project** -- subject libraries must be headless-capable.
- **Use System.Text.Json** -- migrate any Newtonsoft.Json usage to `System.Text.Json`. Use `[JsonPropertyName]` attributes on DTOs only (not on subject properties).
- **Consider separate DTOs from subjects** -- don't put `[JsonPropertyName]` on subject properties. Deserialize into internal DTOs, then map to subject properties explicitly. This keeps subjects clean and avoids mixing serialization concerns into the domain model.
- **Consider hybrid JSON parsing** -- for APIs with dynamic keys (e.g., `"switch:0"`, `"cover:1"`), parse the top level with `JsonElement.EnumerateObject()` and deserialize per-component into typed DTOs.
- **Child collections: arrays vs dictionaries** -- use arrays when index is the identity (relay 0, pin 3, phase A). Use `Dictionary<string, T>` when identity comes from external system (API IDs, serial numbers). See v2 docs for details.
- **Inject and use `ILogger<T>`** -- constructor-inject `ILogger<T>` for logging errors, warnings, and key lifecycle events. Don't silently swallow exceptions.

### Deliverables

1. **V2 subject project** -- `src/HomeBlaze/Namotion.Devices.{Name}/` with all device classes and `.csproj`
2. **Service extension** -- `{Name}ServiceCollectionExtensions.cs` using `AddSubject` pattern
3. **V2 UI project** -- `src/HomeBlaze/Namotion.Devices.{Name}.HomeBlaze/` with Blazor components:
   - `.csproj` (Razor SDK, references device project + `HomeBlaze.Components.Abstractions`, MudBlazor)
   - `_Imports.razor` (standard usings: `Microsoft.AspNetCore.Components`, `MudBlazor`, device namespace, `HomeBlaze.Components.Abstractions`, `HomeBlaze.Components.Abstractions.Attributes`)
   - Widget component (`{SubjectName}Widget.razor`) -- implements `ISubjectComponent`, compact display
   - Edit component (`{SubjectName}EditComponent.razor`) -- implements `ISubjectEditComponent`, configuration form with local state, dirty tracking, `OnInitialized` (NOT `OnParametersSet`)
   - Setup component (`{SubjectName}SetupComponent.razor`) -- only if initial discovery/registration is needed (e.g., API key pairing). Not needed for simple IP-based devices.
4. **Device documentation** -- `src/HomeBlaze/HomeBlaze/Data/Docs/devices/{Name}.md`
5. **JSON config** -- `src/HomeBlaze/HomeBlaze/Data/Devices/{Name}.json`
6. **Solution integration** -- add all projects to `src/Namotion.Interceptor.slnx`
7. **HomeBlaze registration** -- add project references to `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj` and register assemblies in `src/HomeBlaze/HomeBlaze/Program.cs` (both the device assembly and the `.HomeBlaze` UI assembly via `TypeProvider.AddAssembly`)
8. **Tests** -- `src/HomeBlaze/Namotion.Devices.{Name}.Tests/` where applicable
9. **New interfaces** (if needed) -- added to `src/HomeBlaze/HomeBlaze.Abstractions/`

### Verification

- `dotnet build src/Namotion.Interceptor.slnx` -- must succeed with 0 errors, 0 warnings
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` -- must pass
- Review v1 feature checklist -- confirm no regressions
