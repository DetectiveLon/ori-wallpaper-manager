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

## Build discipline
- Do NOT run `build.ps1`, `dotnet build`, `dotnet test`, or launch Oriole.exe. The integrator builds once after all agents finish, to avoid file locks and conflicting obj/ writes.
- Do NOT commit, push, or switch branches.
- Each agent must report the exact files it created/modified and any integration assumptions.
