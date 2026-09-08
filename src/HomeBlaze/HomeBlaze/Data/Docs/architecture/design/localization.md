---
title: Localization
navTitle: Localization
status: Implemented
---

# Localization Design

How HomeBlaze adapts what it shows to the viewer. Today this covers the display timezone. Language, number, and currency formatting are not yet localized and would be added as further chapters here.

## Display Timezone

### Overview

Every user-facing date and time in HomeBlaze renders in a timezone the viewer controls, chosen per browser. Storage stays UTC end to end; only display formatting and date-picker input parsing convert. The chosen zone defaults to the browser's own timezone and can be overridden to any IANA zone from a toolbar selector. The choice is persisted in a cookie, so it survives reloads and can be read during prerendering.

HomeBlaze runs as Blazor Server, so C# `DateTime.Now` and `TimeZoneInfo.Local` resolve to the server's timezone, not the viewer's. This feature replaces all server-local rendering with a per-circuit display service driven by the browser's zone.

**Status.** Implemented: the display service, IANA resolution and catalog, cookie persistence with a prerender bridge, the toolbar selector, and conversion of the state-value panel, the property-history dialog (display and custom-range input), and file-modified timestamps.

### Architecture

Resolution and formatting are split between a pure, framework-free core in `HomeBlaze.Components.Abstractions` (so any UI package can format a timestamp without referencing the backend) and the web and JavaScript wiring in `HomeBlaze.Host`. Only the DI registration lives in `HomeBlaze.Services`.

#### Components

| Type | Package | Role |
|---|---|---|
| `ITimeZoneDisplay` / `TimeZoneDisplayService` | `HomeBlaze.Components.Abstractions` | Scoped (per circuit) holder of the resolved zone. Formats `DateTimeOffset` and `DateTime`, converts an instant to wall-clock (`ToZoned`), parses picker input back to UTC (`ToUtc`), and raises `Changed`. Returns a placeholder while unresolved. |
| `TimeZonePreference` | `HomeBlaze.Components.Abstractions` | The choice: Automatic (follow browser) or a specific IANA id. Round-trips to a cookie value. |
| `TimeZoneResolver` | `HomeBlaze.Components.Abstractions` | Resolves an IANA (or Windows) id to a `TimeZoneInfo`, never throwing (IANA to Windows fallback, then UTC). |
| `TimeZoneCatalog` | `HomeBlaze.Components.Abstractions` | Builds the selectable IANA zone list from the OS zones, dropping zones that have no IANA mapping. |
| `TimeZoneInterop` (`HomeBlaze.Host`) + `timezone.js` (`HomeBlaze` app `wwwroot`) | `HomeBlaze.Host` | Reads the browser zone (`Intl.DateTimeFormat().resolvedOptions().timeZone`) and writes the preference cookie. |
| `TimeZoneInitializer` | `HomeBlaze.Host` | Resolves the zone on load (see the flow below). Rendered once in `MainLayout`. |
| `TimeZoneSelector` | `HomeBlaze.Host` | Toolbar control: Automatic plus a searchable zone list, marks the active zone, and persists the choice. |

#### Service

```csharp
public interface ITimeZoneDisplay
{
    bool IsResolved { get; }
    TimeZoneInfo? Zone { get; }
    TimeZonePreference Preference { get; }
    string Placeholder { get; }                  // shown while the zone is not yet known
    event Action? Changed;                       // consumers re-render on change

    string Format(DateTimeOffset value);         // absolute instant in the chosen zone, with offset
    string Format(DateTime value);               // Utc kind converted; Local and Unspecified rendered as-is
    DateTime ToZoned(DateTimeOffset value);      // wall-clock in the chosen zone (chart axes, custom formats)
    DateTimeOffset ToUtc(DateTime wallClock);    // parse picker input in the chosen zone, return UTC

    void SetResolved(TimeZonePreference preference, TimeZoneInfo zone);
}
```

The service is registered `AddScoped`, so each Blazor circuit (each viewer and tab) has its own instance. All mutation happens on the circuit's render synchronization context, so no locking is needed.

#### Resolution flow (no flash on first paint)

Blazor Server prerenders before any JavaScript runs, and the prerender pass and the interactive circuit are separate dependency-injection scopes. `TimeZoneInitializer` bridges them:

1. **Prerender (has `HttpContext`, no JS).** Reads the saved cookie. A pinned zone resolves immediately, so the prerendered HTML already shows correct times. Automatic cannot resolve here, because the browser zone is unknown without JavaScript.
2. **`PersistentComponentState`.** The prerender persists the pinned zone id; the interactive circuit restores it in `OnInitialized`, before its first render, so a pinned zone never flashes the placeholder.
3. **After the first interactive render (JS available).** For Automatic, or a first visit with no cookie, JavaScript reports the browser zone; the service resolves it and writes the cookie. This is the only path that briefly shows the placeholder, and only until JavaScript responds.

Automatic is intentionally never persisted as a concrete zone: the circuit re-detects the browser zone via JavaScript on each load, so a user who travels follows their current browser zone.

#### Persistence

The preference is stored in a `homeblaze.timezone` cookie (`SameSite=Lax`, one year). A cookie, rather than local storage, is used because it is readable server-side during prerendering, which is what enables the no-flash behavior for pinned zones. The cookie is written from the interactive circuit via JavaScript (`document.cookie`), since response headers cannot be set after the page has rendered.

#### Zone catalog and resolution

`TimeZoneCatalog.GetZones()` enumerates `TimeZoneInfo.GetSystemTimeZones()` and maps each id to its canonical IANA id (via `TryConvertWindowsIdToIanaId` on Windows; ids are already IANA on Linux and macOS). Zones with no IANA mapping (deprecated Windows-only zones such as `Mid-Atlantic Standard Time`) are dropped. Because Windows collapses many IANA zones into one canonical id, the selector also prepends the browser's own resolved zone when the catalog lacks it, so each viewer can always pick their exact zone.

### Consumers

| Site | Conversion |
|---|---|
| `StateUnitExtensions.GetPropertyDisplayValue` (state panel) | Takes an optional `ITimeZoneDisplay`; the `DateTime` and `DateTimeOffset` branches format through it. Used by `SubjectPropertyPanel`. |
| `PropertyHistoryDialog` | The chart axis, the table, and the range hints use `ToZoned`; the custom-range date pickers use `ToUtc`, so the queried UTC window matches the wall-clock the user picked. |
| `MarkdownFilePageComponent` | The file modified timestamp formats through the service. |

Components subscribe to `ITimeZoneDisplay.Changed` and re-render (the history dialog reloads, since its query window depends on the zone), so changing the zone updates everything live with no page reload.

### Out of scope

- Per-user, server-side accounts or preferences (the choice is per browser).
- Any change to how timestamps are stored or queried (storage stays UTC).
