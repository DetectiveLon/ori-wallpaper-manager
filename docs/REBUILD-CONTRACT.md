# Power-user rebuild contract (2026-11-28)

One coordinated rebuild of Feed, Auto rotation and Displays. The user's direction:
**more power-user control**, across all three areas. Hardening is done; this is the
feature rebuild.

The integrator (lead) owns the build, all guards, `App.xaml`, `MainWindow.xaml(.cs)`,
`MainViewModel.cs`, `NavigationService.cs`, `csproj`, and every doc except this file.
Agents own only the files listed in their section. Do not run builds or launch Ori.

## Frozen shared surface (source-compatible; additions only)

- `src/Ori.Core/Services/RotationLoop.cs` - do not edit. Its `CalculateNextRun` and
  `RunAsync` signatures are covered by tests.
- `RotationFeed` - keep every current init property and `Validate()`. New properties
  must have safe defaults.
- `WallhavenFeedStore` - class name and file path are fixed by `check-invariants.ps1`.
  Keep every existing public method signature; new methods are additive.
- `DisplayMonitor` - keep `Id`, `DeviceName`, `Left`, `Top`, `Width`, `Height`,
  `IsPrimary`, `CurrentWallpaper`. Additions only.
- `WindowsDesktopWallpaper` - keep existing public method names and signatures,
  including `ApplyWallpaper` and `GetMonitors`.
- `DisplayDetection.Read()` signature stays; additions only.
- `MonitorSelectionService` public surface stays; additions only.
- `AutomationView` public surface stays: `ViewModel` property,
  `ViewModel.InitializeAsync()`, `StopAsync()`, `RequestStop()`. `MainWindow` calls
  these. Nav key stays `"Automation"`; label/access key stays `"_Auto rotation"`
  (Alt+A).
- Guard binding paths stay unless the integrator approves a change:
  - `AutomationView.xaml`: CheckBox `IsChecked` -> `PauseWhenFullscreen`,
    `AutoFetchFromWallhaven`, `IsEnabled`; ComboBox `SelectedItem` ->
    `CadenceMinutes`, `SelectedInterval`.
  - `DiscoverView.xaml`: ComboBox `SelectedItem` -> `SelectedFeedCadence`,
    `SelectedSorting`, `SelectedTopRange`, `SelectedOrder`, `SelectedRatio`,
    `SelectedMonitor`; ToggleButton `IsChecked` -> `General`, `Anime`, `People`,
    `Safe`, `Sketchy`, `Explicit`, `IsSelected`; ListBox `SelectedItem` ->
    `SelectedCard`.
  - `DisplaysView.xaml`: ComboBox `SelectedItem` -> `SelectedPositionOption`.
- `AGENTS.md` XAML rules apply: `Run.Text` bindings use `Mode=OneWay`; keyed button
  styles are self-contained; custom ComboBox templates need `PART_DropDownToggle`;
  `PasswordBox` needs an explicit width; toggle headers use `ClickMode="Press"`;
  logged errors stay rate-limited.

## Cross-area contracts - implement exactly

### Feed agent -> Automation agent

`RotationFeed` gains these init properties (safe defaults):
- `int CurrentPage` = 1
- `int? LastPage` = null
- `string? Seed` = null
- `int MaximumItemsPerFetch` = 3 (clamped 1..10)

`WallhavenFeedStore` gains these public methods:
- `public static (int Page, string? Seed) GetFetchRequest(RotationFeed feed, DateTimeOffset now)`
- `public static void RecordFetchPage(string id, int pageUsed, int? lastPage, string? seed)`

Behaviour: `GetFetchRequest` returns the feed's `CurrentPage` (clamped into 1..LastPage
when known) and its `Seed`. `RecordFetchPage` persists the page actually used and the
reported last page/seed, and advances `CurrentPage` to the next page, wrapping to 1
after `LastPage`. Feeds must walk pages instead of re-fetching page 1 forever.

### Displays agent -> Automation agent

`DisplayDetection` gains `public static event EventHandler? DisplaysChanged`, raised
when Windows signals a display change. Automation subscribes and refreshes sessions
without losing saved schedules. Raise it on the dispatcher thread where possible and
never throw from the event.

### Automation agent -> Feed agent
Automation owns the fetch/apply workflow and the library tagging. Fetched feed
images are tagged with a stable `feed:<feed-id>` source so a display can be bound to
one feed. No changes to feed files are needed for that.

## Area A - Feed agent

Owns:
- `src/Ori.Core/Models/RotationFeed.cs`
- `src/Ori.Core/Services/WallhavenFeedStore.cs`, `WallhavenQueryBuilder.cs`,
  `WallhavenClient.cs`
- `src/Ori.Desktop/ViewModels/FeedViewModel.cs`, `DiscoverViewModel.cs`
- `src/Ori.Desktop/Views/DiscoverView.xaml`, `DiscoverView.xaml.cs`
- `src/Ori.Desktop/Services/WallhavenImageDownloader.cs`
- new test `tests/Ori.Core.Tests/FeedRebuildTests.cs` only (do not edit existing tests)

Deliverables:
1. **Full filter editing, one place.** `FeedViewModel` exposes every `WallhavenFilters`
   field (query, categories, purity, sorting, order, top range, min/exact resolution,
   ratio, colors, page, seed) as an editable draft with one explicit Save command that
   validates and commits. No more query-only edits. Keep `IsEnabled` and
   `CadenceMinutes` binding names and their guard allowlist behaviour.
2. **Cadence unification.** One shared choice list driven by Core bounds
   (15..10080 minutes) plus a custom numeric entry. Discover, FeedViewModel and Core
   must agree. Do not keep three different dropdowns with three different rules.
3. **Paging** via the contract above; `WallhavenQueryBuilder` must emit Page/Seed.
4. **Discover becomes authoring only.** Keep `FeedName`, `SelectedFeedCadence`,
   `SaveAsFeedCommand`, the full filter controls and every guard binding path. Remove
   the duplicate feed-management list/rows and point the user to Auto rotation for
   management. NSFW still requires a saved key; no key in feed settings.
5. **Store schema v3** with migration from v2, tolerant of unknown fields, corrupt-file
   quarantine retained. Existing 27 store tests and selection tests must still pass.

## Area B - Automation agent

Owns:
- `src/Ori.Core/Services/ScheduleStore.cs` (including `DisplayScheduleState`)
- `src/Ori.Desktop/ViewModels/AutomationViewModel.cs`
- `src/Ori.Desktop/Views/AutomationView.xaml`, `AutomationView.xaml.cs`
- new files `src/Ori.Desktop/Services/RotationSession.cs` and, if useful,
  `src/Ori.Desktop/Services/RotationCoordinator.cs`
- new test `tests/Ori.Core.Tests/ScheduleRebuildTests.cs` only

Deliverables:
1. **Real structure.** Introduce one `RotationSession` per display that owns its
   `RotationLoop`, cancellation, explicit state (`Idle/Preparing/Running/Paused/
   Applying`), next-run timestamp, source selection and pause flags. The view model
   becomes a binding surface over sessions instead of ~2500 lines of ad-hoc gates.
   Leave `RotationLoop.cs` untouched.
2. **Custom intervals.** Accept any whole-minute value 1..10080, persisted in
   `IntervalName` (legacy names still parse). UI keeps the `SelectedInterval`
   ComboBox binding and adds a custom-minutes input with an explicit commit command.
3. **Per-display source binding.** Add `SourceKind` (`Library`, `Collection`, `Feed`)
   and `SourceId` (string?) to `DisplayScheduleState` with defaults. Rotation picks
   candidates from that source; `Feed` means library entries tagged `feed:<id>`.
   Migrate `schedules.json` to schema v2 while reading v1 files.
4. **Hotplug.** Subscribe to `DisplayDetection.DisplaysChanged`; adopt saved schedules
   by fingerprint, add new displays, stop removed ones with `keepSavedSchedule` and
   never clobber a second display.
5. **Feed integration.** Use `WallhavenFeedStore.GetFetchRequest`/`RecordFetchPage`
   and `feed.MaximumItemsPerFetch` instead of the hard-coded 3-item/page-1 behaviour.
   Keep app-wide serialized fetching and 429 backoff.
6. **Fetch now** targets the selected display by default with an explicit "apply to
   all running" option; preserve the prior wallpaper on failure.
7. Keep nav label/key, guard bindings and the `AutomationView` public contract above.
   Keep one card per monitor and a visible NEXT CHANGE countdown.

## Area C - Displays agent

Owns:
- `src/Ori.Desktop/ViewModels/DisplaysViewModel.cs`
- `src/Ori.Desktop/Views/DisplaysView.xaml`, `DisplaysView.xaml.cs`
- `src/Ori.Desktop/Services/WindowsDesktopWallpaper.cs`, `DisplayDetection.cs`,
  `MonitorSelectionService.cs`
- new `src/Ori.Desktop/Services/DisplayPreferencesStore.cs`
- new test `tests/Ori.Desktop.Tests/DisplayRebuildTests.cs` only

Deliverables:
1. **Real preview.** Show the actual image in each monitor card with the real
   crop/fill for the chosen fit mode, plus file name and resolution. Remove the fake
   vector illustration and "Illustration only" label.
2. **Fit and span are different things.** Fill/Fit/Stretch/Center/Tile are per-display
   choices persisted in `DisplayPreferencesStore`; Span is a separate explicit control
   that does not fire a dialog from a ComboBox setter. Restoring from Span returns the
   display's previous fit. Keep the `SelectedPositionOption` binding path on the fit
   ComboBox for the guard.
3. **Per-display source picker**: file, library item or feed, with one explicit Apply
   (one primary button per view), transactional failure handling and a readable
   status. No immediate apply on selection change.
4. **Correct display identity.** Number cards with Windows' own display numbers, not
   COM index. Keep dedupe of the same panel but do not collapse genuinely distinct
   panels. Clear stale selected-monitor ids in `MonitorSelectionService.Load`.
5. **Hotplug** via `DisplayDetection.DisplaysChanged`; refresh preserves selection and
   preferences by fingerprint. Fix the diagram overflow and replace six fit booleans
   with one enum plus a converter.
6. `DisplayPreferencesStore` writes `%LOCALAPPDATA%\Ori\settings\display-preferences.json`
   atomically with corrupt-file quarantine, following existing store conventions.
   Keep all existing `WindowsDesktopWallpaper` signatures.

## Build discipline
- Do not run `build.ps1`, `dotnet build`, `dotnet test`, or launch Ori. The integrator
  builds once after all agents finish.
- Do not commit, push, or switch branches.
- Report exact files created/modified, any guard allowlist additions needed, and any
  integration assumption the integrator must check.
