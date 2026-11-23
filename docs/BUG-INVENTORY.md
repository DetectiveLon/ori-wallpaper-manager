# Oriole UI interaction inventory / bug inventory

Scope: `v2-shell` branch, Wave 5 hardening pass. Inventory owner per
`docs/V2-ARCHITECTURE.md` (Wave 5, "Inventory + static check"). This document is
**static analysis only**: every view XAML, its code-behind and its view model were
read, plus `MainWindow`, `App.xaml.cs`, `NavigationService`, `TrayIconService` and
`StartupService`. Nothing here was run in a live WPF session (build/launch is the
integrator's job), and this pass did not edit source, App.xaml, MainWindow,
csproj, CI or any other doc.

The working tree was changing concurrently (other Wave 5 agents editing their
owned files). This inventory reflects the **final state read at the end of this
pass**: Overview navigation was removed, Discover became the landing page,
X-close/minimize semantics were reworked, and tray icon click/double-click was
wired up. Earlier states are described in "User-reported issues" so the fixes
remain traceable.

Status meanings:

- **works** — the binding or handler resolves to an existing member in the code as written.
- **suspect** — resolves statically, but runtime behaviour or user intent cannot be
  confirmed without running the app. Treat as "needs live verification", not as "fine".
- **broken** — statically has no target: handler not wired, key resolves to `null`,
  or the screen is unreachable.

A machine-checkable companion lives at `scripts/check-buttons.ps1` (attribute-level
guard: flags `Button`/`ComboBox`/`CheckBox`/`ListBox` with no `Command` and no
`Click`/`SelectionChanged`/`Checked` handler, plus literal `IsEnabled="False"`).
It intentionally cannot detect the semantic failures marked **broken** below —
those need this document. On the current tree it reports 17 findings, all of them
binding-only `SelectedItem` / `IsChecked` / `ItemsSource` controls that this
inventory confirms are genuinely wired. A newer `scripts/check-invariants.ps1`
also guards the packaging/SDK/version invariants added in `v2-shell`.

## Inventory

| Screen | Control | Expected action | Wiring (command or handler) | Status | Notes |
|---|---|---|---|---|---|
| Shell (MainWindow) | Title bar drag region (Border) | Drag the window | `MouseLeftButtonDown="OnTitleBarMouseLeftButtonDown"` → `DragMove()` | works | Handler ignores presses on caption buttons via `FindAncestor<Button>`. |
| Shell (MainWindow) | Title bar double-click | Maximize / restore | `OnTitleBarMouseDoubleClick` exists in `MainWindow.xaml.cs` but **no `MouseDoubleClick` attribute in XAML** | broken | Dead handler. The title-bar Border wires only `MouseLeftButtonDown`; this is the remaining "dead interaction" in the shell. |
| Shell (MainWindow) | Minimize button | Ordinary taskbar minimize | `Click="OnMinimizeClick"` → `SystemCommands.MinimizeWindow` | works | `OnStateChanged` was removed; minimizing no longer hides to tray. Resolves the user-reported minimize-to-tray issue. |
| Shell (MainWindow) | Maximize button | Maximize / restore | `Click="OnMaximizeClick"` → `SystemCommands.MaximizeWindow` / `RestoreWindow` | works | Glyph switches on `WindowState` via `DataTrigger`; WM_GETMINMAXINFO clamping keeps a maximized window inside the work area. |
| Shell (MainWindow) | Close (X) button | Hide the window to tray, keep rotation running | `Click="OnCloseClick"` → `SystemCommands.CloseWindow`; `Closing` → `OnClosing`; when `!App.ExplicitExit`: `e.Cancel=true`, `ShowInTaskbar=false`, `Hide()` | works | Reworked mid-pass: X no longer exits; only the tray "Exit" menu item sets `App.ExplicitExit=true` and shuts down. `StopRotationForShutdown` handles both paths. Needs a live smoke test that hidden windows stop rotation on Exit. |
| Shell (MainWindow) | Sidebar "Settings" nav | Open Settings | `Click="Navigate"` `Tag="Settings"` → `MainViewModel.Navigate` | works | Selected-state styling applied in `MainWindow.Navigate`. |
| Shell (MainWindow) | Sidebar "Discover" nav | Open Discover (landing page) | `Click="Navigate"` `Tag="Discover"` | works | `MainViewModel.Items[0]` is Discover and the constructor calls `Navigate("Discover")`. |
| Shell (MainWindow) | Sidebar "My library" nav | Open Library | `Click="Navigate"` `Tag="Library"` | works | — |
| Shell (MainWindow) | Sidebar "Displays" nav | Open Displays | `Click="Navigate"` `Tag="Displays"` | works | — |
| Shell (MainWindow) | Sidebar "Automation" nav | Open Automation | `Click="Navigate"` `Tag="Automation"` | works | Navigation also caches the view for shutdown handling. |
| Shell (MainWindow) | Sidebar "Overview" nav | — | Removed: no `Tag="Overview"` button remains in `MainWindow.xaml` | works | Resolved: Overview was removed from the shell as planned for Wave 5. |
| Shell (MainWindow) | `PageBody` ContentControl | Host the current view | `Content="{Binding CurrentView}"` | works | Startup now hosts Discover, never a null Overview view. |
| Overview (OverviewView) | Whole screen — hero CTA + six cards | Navigate to sections | `NavigateCommand` with `CommandParameter` on all seven buttons | broken | `OverviewView.xaml(.cs)` still exists but is **orphaned**: no navigation key and no `new OverviewView()` in source. Dead files, never shown at runtime. |
| Overview (OverviewView) | Hero CTA "Explore wallpapers" | Navigate to Discover | `Command="{Binding NavigateCommand}" CommandParameter="Discover"` | broken | Unreachable screen; the command could only resolve if a DataContext were supplied, which never happens. |
| Overview (OverviewView) | Card "Overview" | Navigate to Overview | `Command="{Binding NavigateCommand}" CommandParameter="Overview"` | broken | Unreachable, and the key no longer has a view. |
| Overview (OverviewView) | Cards "Discover"/"Library"/"Displays"/"Automation"/"Settings" | Navigate to each screen | `NavigateCommand` with matching `CommandParameter` | broken | Valid keys, but the screen is unreachable. Safe to delete these files. |
| Discover | Tags textbox | Enter Wallhaven query | `{Binding Tags, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}` → plain auto-property | works | Free-text only. Backlog P2 ("smarter tag discovery": suggestions, presets, learned preferences) is not implemented — see Backlog section. |
| Discover | Category chips (General/Anime/People) | Toggle Wallhaven categories | `IsChecked` two-way to bool auto-properties | works | Read by `DiscoverViewModel.BuildFilters`. |
| Discover | Purity chips (SFW/Sketchy/NSFW) | Toggle purity filters | `IsChecked` two-way to bool auto-properties | works | `BuildFilters` rejects NSFW without an API key; an `ApiKeyBox` exists on this page. |
| Discover | API key PasswordBox | Enter Wallhaven key | `PasswordChanged` → `vm.ApiKey` (code-behind bridge) | works | PasswordBox cannot bind; bridged explicitly. |
| Discover | Sorting / Toplist / Order / Ratio combos | Select filter values | `SelectedItem` two-way to `FilterChoice` properties | works | Values consumed in `BuildFilters`. |
| Discover | Min resolution / Exact resolution / Custom hex textboxes | Enter filter values | two-way to auto-properties | works | — |
| Discover | Palette colour chips (ItemsControl/ToggleButton) | Select colours | `IsChecked` two-way to `ColorChoice.IsSelected` | works | — |
| Discover | "Search wallpapers" button | Run a Wallhaven search | `Command="{Binding SearchCommand}"` | works | **Manual** search only. Automatic Wallhaven rotation without manual search/pick/download is backlog P1 — see Backlog section. |
| Discover | "Apply to" monitors combo | Choose target display | `SelectedItem` two-way → `SelectedMonitor` | works | `LoadDisplays` selects the primary display on load. |
| Discover | Results ListBox | Select a result card | `SelectedItem` two-way → `SelectedCard` | works | — |
| Discover | Card "Open page" | Open Wallhaven page in browser | `OpenPageCommand` + `CommandParameter="{Binding}"` via ancestor ListBox | works | Guards the URI with `WallhavenImageDownloader.IsTrustedUri`. |
| Discover | Card "Preview" | Show full-size preview dialog | `PreviewCommand` + `CommandParameter` | works | — |
| Discover | Card "Save to library" | Download and save locally | `SaveCommand` + `CommandParameter` | works | — |
| Discover | Card "Apply" | Set wallpaper on selected display | `ApplyCommand` + `CommandParameter` | works | `CanExecute` requires `SelectedMonitor != null`. |
| Discover | Pager Previous / Next | Change results page | `PreviousCommand` / `NextCommand` | works | Disabled while busy or out of range. |
| Library | Search textbox | Filter library | `{Binding SearchText, UpdateSourceTrigger=PropertyChanged}` + 180 ms debounce | works | — |
| Library | Collection combo | Filter by collection | `SelectedItem` → `SelectedCollectionFilter` | works | Rebuilt after reload/move. |
| Library | "Favourites only" CheckBox | Filter favourites | `IsChecked` → `FavoritesOnly` | works | — |
| Library | Refresh button | Reload library | `Command="{Binding RefreshCommand}"`, `IsEnabled="{Binding IsRefreshEnabled}"` | works | — |
| Library | "Apply to" displays combo | Choose target display | `SelectedItem` → `SelectedDisplay` | works | — |
| Library | Entries ListBox (+ `EventSetter Loaded`) | Render paged cards and lazy thumbnails | `ItemsSource="{Binding Entries}"` + `Handler="OnCardLoaded"` | works | `OnCardLoaded` attaches `DataContextChanged` for recycled rows. |
| Library | Card "Set wallpaper" | Apply local image | `ApplyCommand` + `CommandParameter` | works | No `CanExecute` guard; reports an inline status error if no display/file. |
| Library | Card "Toggle favourite" | Toggle favourite | `FavoriteCommand` + `CommandParameter` | works | — |
| Library | Card "Open" | Open image file | `OpenCommand` + `CommandParameter` | works | — |
| Library | Card "Remove" | Remove library entry | `RemoveCommand` + `CommandParameter` | works | Shows a Yes/No `MessageBox` confirmation — a modal, but a deliberate action gate, not a dead control. |
| Library | Card collection combo + "Move" button | Move entry to a collection | `SelectedCollection` two-way + `MoveToCollectionCommand` | works | — |
| Library | Pager Previous / Next | Change library page | `PreviousPageCommand` / `NextPageCommand` with `CanGoPrevious` / `CanGoNext` | works | Page size 48. |
| Automation | "Pause … full-screen app" CheckBox | Set global pause policy | `IsChecked` two-way → `PauseWhenFullscreen` | works | Read by rotation loops off-thread. |
| Automation | "Refresh displays" button | Re-detect monitors | `Command="{Binding RefreshDisplaysCommand}"` | works | — |
| Automation | Schedules ItemsControl | One card per display | `ItemsSource="{Binding Schedules}"` | works | — |
| Automation | Interval ComboBox | Choose rotation interval | `SelectedItem` two-way → `SelectedInterval`, `IsEnabled="{Binding IntervalEnabled}"` | works | — |
| Automation | "Start" button | Start rotation | `StartCommand` + `CanStart` | works | Disabled when the library has no images. |
| Automation | Pause/Resume button | Toggle manual pause | `PauseCommand` + `CanPause` | works | Label bound to `PauseButtonText`. |
| Automation | "Rotate now" button | Manual rotation | `RotateNowCommand` + `CanRotateNow` | works | Disabled when the library has no images. |
| Automation | "Stop" button | Stop rotation | `StopCommand` + `CanStop` | works | — |
| Displays | Refresh button | Re-detect monitors | `Command="{Binding RefreshCommand}"` | works | — |
| Displays | Per-monitor "Choose image" button | Open file picker and apply | `ChooseImageCommand` bound via `AncestorType=UserControl` + `CommandParameter="{Binding}"`; VM event → code-behind `OpenFileDialog` → `ApplyWallpaper` | works | Command/event handshake is the only view-service bridge in this screen. |
| Settings | API key PasswordBox | Enter Wallhaven key | `PasswordChanged` / `ApiKeyCleared` bridges in code-behind | works | — |
| Settings | "Save API key" button | Persist DPAPI-protected key | `Command="{Binding SaveApiKeyCommand}"` | works | — |
| Settings | "Remove key" button | Clear stored key | `Command="{Binding RemoveApiKeyCommand}"` | works | Raises `ApiKeyCleared` to blank the PasswordBox. |
| Settings | "Clear cached images" button | Delete image cache | `Command="{Binding ClearCacheCommand}"` | works | — |
| Settings | Launch-at-login toggle button | Toggle HKCU Run entry | `Command="{Binding ToggleStartupCommand}"` | works | `StartupService` never throws; status reported inline. |
| Settings | GitHub hyperlink | Open source URL | `RequestNavigate="OnRequestNavigate"` (code-behind) | works | — |
| Settings | "Open data folder" button | Open local data directory | `Command="{Binding OpenDataFolderCommand}"` | works | — |
| Settings | About / version area | Installer and auto-update controls | **No control exists** | broken | The app still has no in-app update button. `tools/Oriole.Updater` and the optional installer exist, but the version-check call is documented only (`docs/RELEASING.md`); `App.xaml.cs` is owned by another agent. |
| Tray (App + TrayIconService) | Tray icon single-click / double-click | Show and restore the window | `NotifyIcon.MouseClick` / `MouseDoubleClick` gated on `MouseButtons.Left` | works | Plain `Click`/`DoubleClick` are intentionally NOT used: WinForms raises them for right/middle clicks too, which would make the window impossible to keep hidden. `App.OpenFromTray` is idempotent. |
| Tray (App + TrayIconService) | Rotation / failure notifications | Show a tray balloon tip | `Notify(string, string, bool)` → `NotifyIcon.ShowBalloonTip` | works | Exposed by `TrayIconService` for `v2-shell` tray notifications. |
| Tray (App + TrayIconService) | "Open Oriole" menu item | Restore window | `openItem.Click` → `App.OpenFromTray` | works | Sets `ShowInTaskbar=true`, restores state, `Show()` + `Activate()`. |
| Tray (App + TrayIconService) | "Exit" menu item | Shut down | `exitItem.Click` → `App.ExitFromTray` → `App.ExplicitExit=true`, stop rotation, `Current.Shutdown()` | works | This is now the only explicit quit path. |

## User-reported issues (explicit cross-check)

| Reported issue | Static evidence / final state | Status |
|---|---|---|
| Minimize goes to tray instead of taskbar | Earlier: `OnStateChanged` set `ShowInTaskbar=false` + `Hide()`. Final: no `OnStateChanged`; `OnMinimizeClick` only calls `MinimizeWindow` | works (resolved) |
| X-close behaviour | Earlier: X exited via `SystemCommands.CloseWindow`. Final: `OnClosing` cancels the close and hides to tray unless `App.ExplicitExit`; only tray "Exit" shuts down | works (changed; needs live smoke) |
| Tray icon click / double-click | Earlier: only `ContextMenuStrip`. Final: `MouseClick` / `MouseDoubleClick` gated to `MouseButtons.Left` (plain `Click` is deliberately avoided) | works (resolved) |
| Dead menus / lists | Tray click and the Overview nav button are now wired/removed. Remaining dead items: title-bar double-click handler defined but not wired; `OverviewView` unreachable | broken (partially resolved) |
| Overview filler pages | Overview nav removed from `MainWindow.xaml`, startup navigates to Discover, `NavigationService` has no overview case. `OverviewView.xaml(.cs)` files remain but are orphaned | works (shell resolved; dead files remain) |

## Feature backlog (updated for v2-shell)

Source: `docs/BACKLOG.md` (captured 2026-11-23, updated for `v2-shell`). These
items are features rather than wiring defects; several are now implemented in
`v2-shell` and are listed so the inventory does not mistake "missing feature"
for "working".

| Item | Priority | Current state in code | Status |
|---|---|---|---|
| Automatic Wallhaven rotation (fetch/rotate/cache without manual search, pick or download) | P1 | Implemented in `v2-shell`: persisted tag feeds (`WallhavenFeedStore`) drive fetch/rotate/cache | done in v2-shell |
| Installer and auto-update | P1 | Self-contained single-file release + `tools/installer/` (Inno Setup) + `tools/Oriole.Updater`; the in-app check is documented but not wired (`App.xaml.cs` owned by another agent) | partly done (wiring pending) |
| Smarter tag discovery (suggestions, presets, learned preferences) | P2 | Implemented in `v2-shell`: tag presets + editable ComboBox; learned preference model not implemented | partly done |
| Clearer status and notifications; real image import and organization | P2 | Implemented in `v2-shell`: tray `Notify` balloon tips, folder import + SHA-256 dedupe + collections; rename not implemented | done in v2-shell (rename pending) |

## Assumptions / limitations

- Status is assigned from source only; no build, launch or live click was performed.
- Binding paths were resolved by reading the DataContext each view sets in its
  constructor (or the shell DataContext for the now-orphaned `OverviewView`).
  Theme resources in `App.xaml` were treated as visual only.
- "User-reported" items are those supplied with the Wave 5 task; `docs/BACKLOG.md`
  and `docs/IMPLEMENTATION_STATUS.md` were used to cross-check intent.
- Items marked **suspect** need a live smoke test by the integrator before being
  closed; items marked **broken** are statically provable and need a source change
  owned by the integrator (this pass owns only this document and the checker).
- The checker was executed on the final tree with PowerShell 7: 17 findings,
  exit 0 by default and exit 1 with `-Strict`. The companion
  `scripts/check-invariants.ps1` was also run and passed. No build, test, commit
  or push of the application was performed by this inventory pass.
