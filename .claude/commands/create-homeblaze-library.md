---
description: Create a new HomeBlaze subject library using the Namotion.Interceptor system
---

# Create HomeBlaze Library

Create a new subject library for HomeBlaze. The subject can be a device, automation, trigger, agent, or any entity that lives in the object graph. The argument is a descriptive term (e.g., `shelly`, `wallbox`, `weather-automation`).

## Usage

`/create-homeblaze-library <subject-term>`

## Prerequisites

First, load and follow the **brainstorming skill** (`superpowers:brainstorming`). This command layers subject-specific context on top of the brainstorming flow. The brainstorming skill drives the conversation structure: understand context, explore approaches, present design incrementally, then implement.

## Phase 1: Research

### Step 1: Understand the subject

Ask the user what they want to build. The argument is a starting point, but clarify:
- What is the subject? (device, automation, trigger, agent, other)
- What does it integrate with? (local API, cloud API, NuGet SDK, MQTT, protocol, nothing external)
- What are the key capabilities? (sensing, control, scheduling, etc.)

If the user provides URLs to API documentation or SDK references, fetch and read them.

### Step 2: Read documentation

Read these directories thoroughly — they define how subjects must be built:

- `docs/*.md` — all interceptor system docs (subject guidelines, registry, lifecycle, tracking, derived properties, etc.)
- `src/HomeBlaze/HomeBlaze/Data/Docs/*.md` — HomeBlaze architecture and general docs
- `src/HomeBlaze/HomeBlaze/Data/Docs/development/*.md` — building subjects guide, conventions
- `src/HomeBlaze/HomeBlaze/Data/Docs/devices/*.md` — existing device documentation as examples

### Step 3: Read reference implementations

Scan `src/Namotion.Devices.*` for existing subject libraries. Read their source to understand the patterns in practice. Compare complexity: hub devices with children (like Philips Hue) vs simpler standalone devices (like GPIO). Pick the closest match to the subject being built as the primary reference.

Also read the corresponding `src/Namotion.Devices.*.HomeBlaze/` UI projects to understand the Blazor component patterns:
- `.csproj` structure (Razor SDK, references to subject project + `HomeBlaze.Components.Abstractions`)
- `_Imports.razor` (standard usings for MudBlazor, subject namespace, component abstractions)
- Widget components (`*Widget.razor`) — compact display with `ISubjectComponent`
- Edit components (`*EditComponent.razor`) — configuration forms with `ISubjectEditComponent`
- Setup components (`*SetupComponent.razor`) — initial subject registration (if applicable)

### Step 4: Read interfaces

Read `src/HomeBlaze/HomeBlaze*.Abstractions*/` to understand all available interfaces. Identify:
- Existing interfaces that fit the subject's capabilities
- Missing interfaces that need to be created
- Interfaces that could be generic (shared across multiple subject types)

### Step 5: Discover external API capabilities

If the subject integrates with an external system:
- Fetch and read API documentation from URLs the user provides
- Query live endpoints if the user provides connection details (read-only/GET endpoints only — never execute write operations)
- Check for NuGet SDKs or third-party libraries and their latest versions
- Compare what the API offers against the user's initial feature list — propose additions for user approval

**Rules:**
- Only read-only/GET endpoints — never execute write operations on live endpoints
- Only query live endpoints when the user explicitly provides connection details

### Step 6: Check for generic interface opportunities

Before creating subject-specific state properties, check whether a measurement or capability could apply to other subjects too. Look at what existing subject libraries expose — if 2+ subjects share a measurement, it likely deserves a generic interface in `HomeBlaze.Abstractions`.

**Naming conventions:**
- Follow existing pattern: `I{Concept}Sensor` for sensors, `I{Concept}Controller` for controllable capabilities, `I{Concept}State` for status interfaces
- Use `Electrical` prefix for electrical measurements to avoid ambiguity (e.g., `ElectricalCurrent` not `Current`)
- Keep property names consistent with the interface name

### Step 7: Present plan

Following the brainstorming pattern, present the plan in incremental sections (200-300 words each), validating each with the user:

1. **Project name** — propose `Namotion.Devices.{Name}` for devices, or `Namotion.{Name}` for non-device subjects (automations, agents, etc.). User confirms.
2. **Purpose & scope** — what this subject does, what it integrates with
3. **Interface mapping** — which existing interfaces to implement, proposed new ones
4. **Subject hierarchy** — which classes, inheritance, child collections (arrays for index-based identity, dictionaries for external IDs)
5. **Property design** — configuration vs state vs derived, internal setters
6. **Background work** — polling, event listening, connectivity patterns
7. **External integration** — API client design, DTOs, authentication
8. **UI components** — which Blazor components to create (Widget, Edit, Setup), what fields/displays each contains, referencing existing `.HomeBlaze` projects as patterns
9. **Feature checklist** — complete list of what the library will support

## Phase 2: Implementation

### Design rules

These are critical conventions the implementation MUST follow:

- **Don't recreate subjects in polling loops** — update existing instances. Subjects are stateful and expensive to recreate.
- **Internal setters** — if properties are only set by subject logic (polling, events), use `internal set`. Public setters only for properties that external code should write to.
- **All `[InterceptorSubject]` properties must be `partial`** — initialized in constructors, NOT field initializers.
- **Collections replaced entirely, never mutated** — `Items = newArray`, not `Items[0] = x`.
- **Clean up and simplify** — favor clarity over cleverness.
- **Fix memory leaks** — dispose resources properly, cache HTTP clients, dispose children on replacement.
- **Performance** — avoid allocations in hot paths, parallelize independent API calls.
- **No UI code in the subject project** — subject libraries must be headless-capable.
- **Use System.Text.Json** — use `[JsonPropertyName]` attributes on DTOs only (not on subject properties).
- **Consider separate DTOs from subjects** — deserialize into internal DTOs, then map to subject properties explicitly. This keeps subjects clean and avoids mixing serialization concerns into the domain model.
- **Consider hybrid JSON parsing** — for APIs with dynamic keys (e.g., `"switch:0"`, `"cover:1"`), parse the top level with `JsonElement.EnumerateObject()` and deserialize per-component into typed DTOs.
- **Child collections: arrays vs dictionaries** — use arrays when index is the identity (relay 0, pin 3, phase A). Use `Dictionary<string, T>` when identity comes from external system (API IDs, serial numbers). See docs for details.
- **Inject and use `ILogger<T>`** — constructor-inject `ILogger<T>` for logging errors, warnings, and key lifecycle events. Don't silently swallow exceptions.

### Deliverables

1. **Subject project** — `src/HomeBlaze/Namotion.Devices.{Name}/` (or `src/HomeBlaze/Namotion.{Name}/` for non-device subjects) with all subject classes and `.csproj`
2. **Service extension** — `{Name}ServiceCollectionExtensions.cs` using `AddSubject` pattern
3. **UI project** — `src/HomeBlaze/Namotion.Devices.{Name}.HomeBlaze/` with Blazor components:
   - `.csproj` (Razor SDK, references subject project + `HomeBlaze.Components.Abstractions`, MudBlazor)
   - `_Imports.razor` (standard usings: `Microsoft.AspNetCore.Components`, `MudBlazor`, subject namespace, `HomeBlaze.Components.Abstractions`, `HomeBlaze.Components.Abstractions.Attributes`)
   - Widget component (`{SubjectName}Widget.razor`) — implements `ISubjectComponent`, compact display
   - Edit component (`{SubjectName}EditComponent.razor`) — implements `ISubjectEditComponent`, configuration form with local state, dirty tracking, `OnInitialized` (NOT `OnParametersSet`)
   - Setup component (`{SubjectName}SetupComponent.razor`) — only if initial discovery/registration is needed (e.g., API key pairing). Not needed for simple IP-based subjects.
4. **Documentation** — `src/HomeBlaze/HomeBlaze/Data/Docs/devices/{Name}.md` for devices, or the appropriate `Docs/` subfolder for other subject types
5. **JSON config** — `src/HomeBlaze/HomeBlaze/Data/Devices/{Name}.json` for devices, or the appropriate `Data/` subfolder for other subject types
6. **Solution integration** — add all projects to `src/Namotion.Interceptor.slnx`
7. **HomeBlaze registration** — add project references to `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj` and register assemblies in `src/HomeBlaze/HomeBlaze/Program.cs` (both the subject assembly and the `.HomeBlaze` UI assembly via `TypeProvider.AddAssembly`)
8. **Tests** — `src/HomeBlaze/Namotion.Devices.{Name}.Tests/` where applicable
9. **New interfaces** (if needed) — added to `src/HomeBlaze/HomeBlaze.Abstractions/`

### Verification

- `dotnet build src/Namotion.Interceptor.slnx` — must succeed with 0 errors, 0 warnings
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` — must pass
- Review feature checklist — confirm all planned features are implemented
