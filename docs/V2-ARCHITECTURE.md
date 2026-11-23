# V2 shell architecture contract

Goal: replace the code-built WPF pages with XAML views + view models, while reusing the
existing, tested services unchanged. This is a presentation-layer rewrite, not a Core
rewrite.

## Do not modify (shared, owned by the integrator)
- `src/Oriole.Core/**` (all of it)
- `src/Oriole.Desktop/Services/**` (all of it)
- `src/Oriole.Desktop/Pages/AutomationPage.cs`, `LibraryPage.cs`
- `src/Oriole.Desktop/Views/DiscoverPage.cs`
- `src/Oriole.Desktop/MainWindow.xaml`, `MainWindow.xaml.cs`
- `App.xaml`, `App.xaml.cs`, `Oriole.Desktop.csproj`
- `build.ps1`, `.github/**`, `docs/**`, `QA-FINDINGS.md`

## Shared conventions (read before writing code)
- Namespace root: `Oriole.Desktop`. View models: `Oriole.Desktop.ViewModels`.
- Views: `Oriole.Desktop.Views`, XAML UserControls named `<Name>View`.
- Base class: `Oriole.Desktop.Mvvm.ViewModelBase` with `SetProperty` and no external deps.
- Reuse services directly; do not reimplement them:
  - `Oriole.Desktop.Services.DisplayDetection.Read()` -> `DisplayDetectionResult(Monitors, Error)`
  - `Oriole.Desktop.Services.WindowsDesktopWallpaper` (GetMonitors, ApplyWallpaper, SetWallpaperAsync)
  - `Oriole.Desktop.Services.WallpaperImageCache`, `WallhavenImageDownloader`, `ApiKeyStore`
  - `Oriole.Core.Services.WallhavenClient`, `WallpaperLibraryStore`, `ScheduleStore`, `RotationLoop`, `FullscreenWindowMatcher`
  - `Oriole.Core.Models.*`
- Theme resources in `App.xaml`: `PanelBrush`, `PanelAltBrush`, `TextBrush`, `MutedBrush`, `AccentBrush`, `NavButtonStyle`, `PrimaryButtonStyle`, plus implicit dark Button/ComboBox/ScrollBar styles. New XAML may use them via `{StaticResource ...}`.
- Keep the preview honest: do not claim installer, tray, collections, or cross-platform support.

## File ownership (one agent per area — do not touch another area's files)
1. Shell/infrastructure: `src/Oriole.Desktop/Mvvm/**`, `src/Oriole.Desktop/ViewModels/MainViewModel.cs`, `src/Oriole.Desktop/Services/NavigationService.cs`, `src/Oriole.Desktop/Views/HostView.xaml(.cs)`.
2. Library port: `src/Oriole.Desktop/Views/LibraryView.xaml(.cs)`, `src/Oriole.Desktop/ViewModels/LibraryViewModel.cs`.
3. Automation port: `src/Oriole.Desktop/Views/AutomationView.xaml(.cs)`, `src/Oriole.Desktop/ViewModels/AutomationViewModel.cs`.
4. Discover port: `src/Oriole.Desktop/Views/DiscoverView.xaml(.cs)`, `src/Oriole.Desktop/ViewModels/DiscoverViewModel.cs`.
5. Displays + Settings ports: `src/Oriole.Desktop/Views/DisplaysView.xaml(.cs)`, `src/Oriole.Desktop/Views/SettingsView.xaml(.cs)`, `src/Oriole.Desktop/ViewModels/DisplaysViewModel.cs`, `src/Oriole.Desktop/ViewModels/SettingsViewModel.cs`.

## Wave 2 ownership (v2-shell)
- Library virtualization + collections: `Views/LibraryView.xaml(.cs)`, `ViewModels/LibraryViewModel.cs`.
- Tray + startup services: `Services/TrayIconService.cs`, `Services/StartupService.cs` only.
- Integrator owns `App.xaml.cs`, `MainWindow.xaml(.cs)`, project/CI files and all docs.

## Wave 3 ownership (v2-shell)
- Global dark theme: `App.xaml` only (CheckBox, TextBox, PasswordBox, ListBox, ToolTip, Hyperlink, etc.).
- Library grid: `Views/LibraryView.xaml(.cs)`, `ViewModels/LibraryViewModel.cs`, `Views/Controls/VirtualizingWrapPanel.cs`.
- Release package: `scripts/publish.ps1`, `.github/workflows/release.yml`, `docs/RELEASING.md`.
- Per-view UX polish: `Views/DiscoverView.xaml`, `Views/DisplaysView.xaml`, `Views/AutomationView.xaml`, `Views/SettingsView.xaml` (no view models).
- Theme audit script: `scripts/check-theme.ps1`, `docs/QA-SMOKE.md`.
- Integrator still owns App.xaml.cs, MainWindow, csproj, Core, services and other docs.

## Wave 4 ownership (v2-shell) — visual redesign
- Shell/design system: `App.xaml`, `MainWindow.xaml`, `MainWindow.xaml.cs`.
- Overview: `Views/OverviewView.xaml`.
- Discover: `Views/DiscoverView.xaml`.
- Library + Displays: `Views/LibraryView.xaml`, `Views/DisplaysView.xaml`.
- Automation + Settings: `Views/AutomationView.xaml`, `Views/SettingsView.xaml`.
- Integrator owns all view models, services, Core, csproj and docs. Redesign is XAML-only except MainWindow code-behind for custom window buttons.

## Wave 5 ownership (v2-shell) — hardening pass
- Tray/window semantics: `App.xaml.cs`, `MainWindow.xaml.cs`, `Services/TrayIconService.cs`.
- Navigation cleanup: `ViewModels/MainViewModel.cs`, `Services/NavigationService.cs`, `MainWindow.xaml` (remove Overview + filler, Discover becomes landing page).
- Discover + Library interaction audit: `Views/DiscoverView.xaml(.cs)`, `ViewModels/DiscoverViewModel.cs`, `Views/LibraryView.xaml(.cs)`, `ViewModels/LibraryViewModel.cs`.
- Automation + Displays interaction audit: `Views/AutomationView.xaml(.cs)`, `ViewModels/AutomationViewModel.cs`, `Views/DisplaysView.xaml(.cs)`, `ViewModels/DisplaysViewModel.cs`.
- Inventory + static check: `docs/BUG-INVENTORY.md`, `scripts/check-buttons.ps1`.
- Integrator owns App.xaml, triage, build, live verification and all other docs.

## Wave 6 ownership (v2-shell) — packaging, SDK pin, version identity, guards
- Release packaging: `scripts/publish.ps1` (single source of truth: self-contained,
  single-file, win-x64), `build.ps1` (delegates `-Publish` to publish.ps1).
- Installer: `tools/installer/**` (optional free Inno Setup `.iss`, per-user install
  to `%LOCALAPPDATA%\Oriole`, reuses the app's HKCU Run setting).
- Updater: `tools/Oriole.Updater/**` (separate process; checks GitHub Releases
  manifest, verifies SHA-256, swaps the EXE after exit). Added to `Oriole.sln`.
- Toolchain/version: `global.json` (exact SDK 10.0.404), `Directory.Build.props`
  (pinned LangVersion/AnalysisLevel 10.0 while keeping TreatWarningsAsErrors —
  LangVersion is set to 14.0 because C# 12 collection expressions are in use),
  `Oriole.Core.csproj`, `Oriole.Desktop.csproj` + `app.manifest` (one tag-driven
  version, `AssemblyInformationalVersion` retained for the About page).
- CI/release: `.github/workflows/ci.yml` (adds `v2-shell` push branch, exact SDK,
  full-solution build, guards), `.github/workflows/release.yml` (`verify` job
  gates the release; publishes zip + standalone EXE + update manifest; signing
  noted as deferred).
- Guards: `scripts/check-tray.ps1` (tray Notify), `scripts/check-invariants.ps1`
  (new: SDK pin, no hardcoded 0.1.0, version alignment, feed/import/dedupe,
  editable ComboBox), plus `check-theme.ps1`/`check-buttons.ps1` unchanged.
- Docs: all `docs/*.md`, `README.md`, `QA-FINDINGS.md` (scoped to the older
  feature/library-automation branch).
- NOT wired: the app-side version-check call in `App.xaml.cs` (owned by another
  agent). The exact one-line integration is documented in `docs/RELEASING.md`.

## Build discipline
- Do NOT run `build.ps1`, `dotnet build`, `dotnet test`, or launch Oriole.exe. The integrator builds once after all agents finish, to avoid file locks and conflicting obj/ writes.
- Do NOT commit, push, or switch branches.
- Each agent must report the exact files it created/modified and any integration assumptions.
