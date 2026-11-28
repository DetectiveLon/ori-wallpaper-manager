# Ori whole-app rebuild contract (v2-shell, second pass)

Status: **planning only**. This document was produced by static reading of every XAML
view, code-behind, view model, the shell, `App.xaml`, `AGENTS.md`,
`docs/UI-GUIDELINES.md`, `docs/REBUILD-CONTRACT.md`, `docs/BUG-INVENTORY.md` and all four
`scripts/check-*.ps1` guards. No build, test, launch, commit or source edit was made.

The user's complaint is organisation and visual consistency, not missing features.
The previous rebuild added power-user controls; this one makes the app read as **one
product built by one hand**. The contract below is binding: implementers do not invent
a second design language, a new page, or a new data path.

---

## 0. Non-negotiables (read first)

1. Five destinations, flat. No new pages, no removed pages, no Overview.
2. Nav key stays `"Automation"`; its label/access key stays `"_Auto rotation"` (Alt+A).
3. Every binding-only control listed in §5 stays, with the same **file + element +
   attribute + binding path**. The guard keys on all four; moving a familiar path to a
   different attribute still fails.
4. `AutomationView` public surface stays: `ViewModel`, `ViewModel.InitializeAsync()`,
   `StopAsync()`, `RequestStop()`; `MainWindow` calls all of them.
5. All schemas, migrations, stores, services, `RotationLoop.cs`, guards and tests are
   frozen (§6).
6. `AGENTS.md` XAML rules are law (§5.3).
7. Agents do not build, test, launch, commit or touch a file outside their ownership.
   The integrator is the only one who builds and captures the live window.

---

## 1. Audit: concrete symptoms of the "mess"

Evidence is given as `file:line` in the current tree at commit `2550aef`.

### 1.1 Ownership is scattered across pages

| Subject | Where it lives now | Symptom |
|---|---|---|
| Wallhaven API key | `DiscoverView.xaml:278-288` (PasswordBox + Save key) **and** `SettingsView.xaml:55-79` (PasswordBox + Save/Remove) | Two password boxes, two "Save key" buttons, two owners of one secret. Discover also has a third copy of the key hint in the COLOUR section (`DiscoverView.xaml:377-378`). |
| Feeds | `DiscoverView.xaml:176-224` authors a feed; `AutomationView.xaml:79-181` manages it | Conceptually one job split across two pages with no signpost from one to the other. |
| "Apply to" target | `DiscoverView.xaml:415-421` (`SelectedMonitor`), `LibraryView.xaml:224-232` (`SelectedDisplay`), `DisplaysView.xaml` monitor cards | Three differently sized selectors (`172`, `150`, card) with the same purpose and different labels. |
| Refresh monitors | `AutomationView.xaml:65-70` and `DisplaysView.xaml:75-85` | Same action, different style/size (`CardActionButton` MinWidth 132 vs `SecondaryButtonStyle` with glyph). |
| Status | Discover `:403-442`, Library `:283-301`, Displays `:413-423`, Automation `:431-439`, Settings `:247-250` | Status strips sit at the top on some pages, bottom on others; Library has a separate detection-error line underneath. |

### 1.2 Repeated headers

- Shell renders `Title`/`Subtitle` (`MainWindow.xaml:209-217`), then Discover renders its
  own eyebrow + title (`"SEARCH WALLHAVEN"` / `"Find your wallpaper"`,
  `DiscoverView.xaml:95-99`), Settings renders `"A few things worth tuning."`
  (`SettingsView.xaml:34-40`), Displays renders eyebrow + `"Your display layout"`
  (`DisplaysView.xaml:67-73`). The user sees two or three title levels before any content.
- Rail has three header tiers at once: zone eyebrow `"WORKSPACE"`
  (`MainWindow.xaml:155-157`), group headers `"FIND & SAVE"` / `"YOUR DESKTOP"` / `"APP"`
  (`MainViewModel.cs:76-93`), and section labels inside cards.
- Automation's three section labels are bare `TextBlock`s outside cards
  (`AutomationView.xaml:34,82,183`), while every other page puts a section label inside
  a `CardStyle` border.
- Discover uses uppercase `TAGS`, `QUICK TAGS`, `CATEGORIES`... in expanders; Settings
  uses title-case card headers; Library uses none. Same hierarchy, three conventions.

### 1.3 Two type scales and a local-style zoo

`App.xaml:97-126` defines one scale (10/11/13/15/28, `CaptionSize=11`,
`FieldLabelStyle=11`). Each view then redefines it locally:

- Discover: `HintText`, `DiscoverEyebrow`, `DiscoverCaption`, `DiscoverFieldLabel`,
  `FieldTextBox`, `FieldCombo`, `FilterChip`, `ColorChip`, `TagButton`, `SearchButton`,
  `CardSecondaryButton`, `FilterSection` — all raising 11 → 12.
- Settings: `WrappedBody`, `WrappedCaption`, `SettingsFieldLabel` (11 → 12).
- Library: `ToolbarLabel`, `CardTitle`, `CardMeta`, `CardActionButton`, `EditableComboBox`.
- Displays: `DisplayGlyph`, `MonitorChipStyle`, `MonitorCardStyle`.
- Automation: `CardActionButton`, `CardPrimaryActionButton`.

Result: the same "field label" is 11 px on one page and 12 px on the next; the same
secondary button has four Padding/MinHeight combinations.

### 1.4 Competing button hierarchies

- Discover: one sticky primary (`Search wallpapers`), a **second** primary
  (`Save as rotation feed`, `:216-220`), a primary on **every result card**
  (`Apply to display`, `:505-515`), plus `Apply selected` secondary in the top strip.
- Library: a primary on **every card** (`Set wallpaper`, `:475-485`), plus Create,
  Refresh, Import, Find duplicates all using the implicit neutral button.
- Settings: `Save API key` and `Clear cached images` both use `PrimaryButtonStyle`
  (`:68`, `:95`), plus two inline confirmation buttons.
- Automation: `Add from Discover` and every card's `Start` use primary-derived styles
  (`:20-25`, `:175-179`, `:393-397`).
- Displays is the only page with exactly one accent button. That is the target pattern.

### 1.5 Spacing, radii and control heights drift

- Page content: Discover outer `Margin="18"`; Library toolbar margin 10; Automation
  `Margin="0,0,0,24"` + card `Padding="18"`; Displays `Margin="0,0,0,8"` + card
  `Padding="16"`; Settings `Margin="0,4,0,8"`. `CardPadding` is 20 and nobody uses it
  consistently.
- Card radii: 14 (Discover filter, Library toolbar), 12 (shared), 9 (Library status),
  16 (Library empty state, removal dialog, Settings? no) — four radii where the
  guideline allows two.
- Chips: `ChipStyle` is 8, Library favourite badge overrides to 6, rail status chip is
  10. Controls: ComboBox/TextBox heights 30/32/34/36/40 across pages.
- Result cards are 296 px wide on Discover, 288 px on Library, with 16 px gutters
  instead of the documented 12 px.
- Icon font is `"Segoe Fluent Icons, Segoe MDL2 Assets"` in views but the shared `Glyph`
  style uses `"Segoe Fluent Icons"`; glyph sizes 12/13/14/15/16/20/54.

### 1.6 Empty states and overlays multiply

Discover has a placeholder status, a real empty overlay, a busy overlay and a pager
visibility trigger; Library has its own empty overlay with an extra guidance line plus
three transient status panels. Only one should be visible at a time and both should use
one layout.

---

## 2. Unified information architecture

### 2.1 Pages (exact navigation contract)

Five flat destinations. Keep every key, label string, glyph, access key and TabIndex.

| Order | Nav key | Rail label (`Label`) | `AccessLabel` | Shortcut | Glyph | Section |
|---|---|---|---|---|---|---|
| 1 | `Discover` | `Discover` | `_Discover` | Alt+D | `\uE721` | `FIND & SAVE` |
| 2 | `Library` | `My library` | `My _library` | Alt+L | `\uE8B7` | `FIND & SAVE` |
| 3 | `Displays` | `Displays` | `Dis_plays` | Alt+P | `\uE7F4` | `YOUR DESKTOP` |
| 4 | `Automation` | `Auto rotation` | `_Auto rotation` | Alt+A | `\uE8B3` | `YOUR DESKTOP` |
| 5 | `Settings` | `Settings` | `_Settings` | Alt+S | `\uE713` | footer (no section) |

Alt+P for Displays is retained deliberately — D is already taken by Discover. Do not
"fix" it without also changing Discover, because it is existing muscle memory.

### 2.2 Rail structure (`MainWindow.xaml`, lead-owned)

```
Row 0  brand header (unchanged)
Row 1  REPEATED: main ItemsControl — PrimaryItems only (4 rows, two group headers)
Row 2  footer: Settings button (NavItemTemplate, FooterItem) + LOCAL · ON THIS PC chip
```

- Delete the `"WORKSPACE"` zone eyebrow. It is a third header tier with no information.
- Keep `NavGroupHeaderStyle`, `NavGroupRuleStyle` and `NavButtonStyle` unchanged. Group
  headers appear only for sections with 2+ items (`FIND & SAVE`, `YOUR DESKTOP`). The
  `APP` group disappears because Settings moves to the footer.
- `MainViewModel.Items` stays public and unchanged in content. Add `PrimaryItems` (first
  four) and `FooterItem` (the `Settings` entry) for the two hosts. Selection still comes
  from `DataContext.SelectedItem.Key` through `NavKeyMatchConverter`, so the footer
  button highlights with the same trigger and the accent bar.
- Nothing else in navigation is renamed; `NavigationService` keeps the same five cases.

### 2.3 What moves between pages

| Item | New single owner | Other pages |
|---|---|---|
| Saving/removing the Wallhaven API key | **Settings** | Discover removes its PasswordBox and Save-key button. Its filter rail shows one `StatusTextStyle` line ("NSFW needs a key; saved in Settings") and one `GhostButtonStyle` "Open Settings" action bound to a new `OpenSettingsCommand` that calls `MainViewModel.Navigate("Settings")` using the same pattern as `AutomationViewModel.NavigateToDiscover()`. `DiscoverViewModel.ApiKey`, `RefreshApiKey()` and `SaveApiKeyCommand` stay for in-session search; no key is saved from Discover. |
| Creating a named rotation feed | **Discover** (authoring only) | Automation only manages existing feeds. No feed-creation form on Automation. |
| Managing feeds (enable, rename, cadence, filter draft, reorder, remove, fetch) | **Auto rotation** | Discover keeps `FeedName`, `SelectedFeedCadence`, `FeedMaximumItemsPerFetch`, `CustomFeedCadenceText`, `SaveAsFeedCommand`, `ApplyCustomFeedCadenceCommand` and all search filters; it never lists or edits a saved feed. |
| Where a wallpaper lands right now | Discover and Library keep an inline `Apply to` line (same label, same width 172, same styles); **Displays** owns layout/preview/persistence | Every `Apply to` line uses `FieldLabelStyle` + implicit ComboBox; no third variant. |
| Monitor detection / layout / fit / Span | **Displays** | Auto rotation keeps one ghost "Refresh monitors" for schedule adoption only; same label and same `GhostButtonStyle`. |
| Scheduling, cadence, pause, per-display source | **Auto rotation** | Displays never starts a schedule; Automation never stages a fit. |
| Status text | One bottom-of-page `StatusTextStyle` line per page | No status card at the top of a page; transient import/duplicate/confirmation panels are explicitly exempt. |

---

## 3. Unified visual design system

### 3.1 Ground rule

Use `App.xaml` tokens only. Do not add parallel colour brush names, a third radius, or a
new font family. Wrapping and alignment are local layout properties, not new styles.
Every new style is added **by the lead in `App.xaml`**; agents only consume them.

### 3.2 Shared style inventory

| Purpose | Exact style to use | Status |
|---|---|---|
| Page title (28) | `TitleTextStyle` | existing — shell only |
| Section/card heading (15, semi-bold) | `SectionTitleStyle` | existing — every card starts with this |
| Field label (semi-bold, bottom margin 6) | `FieldLabelStyle` | existing; lead raises FontSize 11 → 12 once |
| Body (13) | `BodyTextStyle` | existing |
| Metadata / secondary (11) | `CaptionTextStyle` | existing |
| Uppercase eyebrow (10) | `EyebrowTextStyle` | existing — rail and card eyebrows only |
| Rail group header | `NavGroupHeaderStyle`, `NavGroupRuleStyle` | existing |
| Glyph | `Glyph` (Segoe Fluent Icons, 16) | existing — replace every local `DisplayGlyph`/`Glyph` copy |
| Card surface | `CardStyle` | existing — top-level cards only, `CardPadding` 20, `CardCornerRadius` 12 |
| Inset panel | **new `PanelStyle`** | Border: `PanelAltBrush`, `ControlCornerRadius` 8, `Padding="14"`, no border. Replaces hand-built `PanelAltBrush` borders in Discover/Automation/Displays/Settings. |
| Chip | `ChipStyle` | existing; lead sets CornerRadius to `ControlCornerRadius` (8) everywhere. Remove the 6/10 overrides. |
| Primary button | `PrimaryButtonStyle` | existing — one per page (§3.5) |
| Secondary button | `SecondaryButtonStyle` | existing — alternatives and card commits |
| Ghost button | **new `GhostButtonStyle`** | BasedOn `SecondaryButtonStyle` but transparent background, no border, `TextBrush` label, `Padding="8,6"`, `MinHeight="32"`; self-contained template with hover `ControlHoverBrush` and disabled opacity 0.45 (never white). For refresh, browse, clear, move up/down, remove, "Open Settings", tag presets. |
| Status line | **new `StatusTextStyle`** | BasedOn `CaptionTextStyle`; `TextWrapping=Wrap`, `Foreground=MutedBrush`, `Margin="0,8,0,0"`. One per page, at the bottom. |
| Empty state | **new `EmptyStatePanelStyle`** | Border: centered, `IsHitTestVisible=False`, `PanelBrush`, `CardCornerRadius`, `SubtleBorderBrush` 1 px, `Padding=28`, `MaxWidth=460`. Contents: `Glyph`-family 54 px circle + `TitleTextStyle` headline + `StatusTextStyle` guidance. |
| Collapsible section | `SectionExpanderStyle` + `SectionExpanderHeader` | existing — use for Discover filters and Automation feed filter drafts |
| Editable ComboBox | single local template in Library only | keep `PART_EditableTextBox`, `PART_DropDownToggle`, `ClickMode="Press"`, `IsEditable="True"` |

Delete the local look-alikes named in §1.3. `WrapPanel`/`TextWrapping` stay as attributes.

### 3.3 Spacing scale (DIP; all page-internal gaps use these)

| Value | Meaning | Token / literal |
|---|---|---|
| 4 | adjacent chips, rows, icon-to-label | `SpaceXS` |
| 8 | label→control, button row, control→hint | `SpaceS` |
| 12 | gallery gutter, control group gutter | literal `12` (no token exists; do not add one) |
| 16 | inset panel padding, card-to-card sub-gap | `SpaceM` |
| 24 | between top-level cards / sections | `SpaceL` (= `SectionSpacing`) |
| 32 | page margin top/sides | `SpaceXL` via `ContentMargin` (`32,24,32,28` retained) |

No other margin/padding literals. Card padding is `CardPadding` (20); inset panels use
14; no `Margin="18"`, `"13"`, `"11"`, `"5"` etc.

### 3.4 Type scale

`TitleTextStyle` 28 (shell only) · `SectionTitleStyle` 15 · `BodyTextStyle` 13 ·
`FieldLabelStyle` 12 · `CaptionTextStyle` 11 · `EyebrowTextStyle` 10 · `Glyph` 16.
No page-level local `FontSize="12"` text styles; use `FieldLabelStyle` or
`CaptionTextStyle` with `TextWrapping="Wrap"` where needed.

### 3.5 Button hierarchy — one primary action per page

1. Exactly one `PrimaryButtonStyle` control per page, used for the page's commit:
   - Discover → `Search wallpapers` (sticky bottom of filter rail)
   - Library → page-level `Set wallpaper` in the toolbar, `Command="{Binding ApplyCommand}"`,
     `CommandParameter="{Binding SelectedEntry}"`; status line explains "select an entry"
     when empty. Per-card `Set wallpaper` becomes `SecondaryButtonStyle`.
   - Displays → `Apply` (bottom-left of the target card)
   - Auto rotation → page-level `Fetch now` in the global card, bound to a **new**
     `FetchNowCommand` on `AutomationViewModel` (targets `SelectedDisplay`, or all running
     when `FetchApplyToAll`). Every per-display Fetch now / Start becomes secondary.
   - Settings → `Save API key` only.
2. All other actions are `SecondaryButtonStyle` (real alternatives, including card
   commits) or `GhostButtonStyle` (low-priority, navigation-like and destructive).
3. A repeating card template never contains a `PrimaryButtonStyle` control.
4. Modal/inline confirmation panels are a separate surface: one `PrimaryButtonStyle`
   "Confirm" plus one ghost "Cancel" is allowed there (Library removal, Settings orphan
   cleanup).
5. Ordering: commit first, then alternatives, then ghost actions; right-align a single
   button in a dialog, left-align a page-level Apply.

### 3.6 Alignment and empty states

- Every view fills the shell content area; no view-level outer `Margin`. First card
  starts flush with the shell content edge. Card headers align at `CardPadding` (20).
- One label/control column per card; ComboBox/TextBox `MinHeight="32"`, consistent
  `ControlCornerRadius` 8. Only the Library editable ComboBox keeps its custom template.
- One status line per page at the bottom, with a single accent dot glyph; colour never
  carries meaning alone — the text does.
- Empty state and busy state are mutually exclusive: when the collection is empty,
  show `EmptyStatePanelStyle`; when `IsBusy` is true, show a ProgressBar overlay instead;
  never both. `IsHitTestVisible=False` on overlays.
- Cards use `PanelBrush` + `SubtleBorderBrush` only; nested groups use `PanelStyle`.
  No full border around every nested surface, no shadows, no gradients.

---

## 4. Per-page layout specification

Section order is top → bottom. "Preserve" lists bindings/controls that must survive with
the same path; they are not optional.

### 4.1 Discover (`Views/DiscoverView.xaml`, `.xaml.cs`; `ViewModels/DiscoverViewModel.cs`)

**Layout.** Two columns: filter rail `276` px, results `*`. No outer `Margin="18"`;
rail card uses `CardStyle` with `Margin="0,0,24,0"`.

**Rail sections, in order:**
1. Card header: one `SectionTitleStyle` "Filters". Delete the eyebrow + "Find your
   wallpaper" stack.
2. `TAGS` — `SectionExpanderStyle`, open: `Tags` TextBox + hint.
3. `CATEGORIES` — open: `General`/`Anime`/`People` toggle chips.
4. `CONTENT` — collapsed: `Safe`/`Sketchy`/`Explicit` toggle chips + NSFW hint.
5. `QUICK TAGS` — collapsed: `TagPresets`, `RecentTags`.
6. `SORTING` — collapsed: `SelectedSorting`, `SelectedTopRange` (`IsTopRangeEnabled`,
   `TopRangeToolTip`), `SelectedOrder`. **API key PasswordBox and Save key button are
   removed**; a single `StatusTextStyle` NSFW/key hint + ghost `OpenSettingsCommand`
   button takes their place.
7. `RESOLUTION` — collapsed: `MinimumResolution`, `ExactResolution`, `SelectedRatio`.
8. `COLOUR` — collapsed: `ColorChoices[].IsSelected`, `CustomColors`. Remove the
   duplicated API-key hint at the bottom of this section.
9. `ROTATION FEED` — collapsed: `FeedName`, `SelectedFeedCadence`,
   `CustomFeedCadenceText` + `ApplyCustomFeedCadenceCommand`, `FeedMaximumItemsPerFetch`,
   `SaveAsFeedCommand` (now `GhostButtonStyle`, not a second primary), one hint pointing
   to Auto rotation for management.
10. Sticky footer: `Search wallpapers` — the page's only `PrimaryButtonStyle`.

**Results column, in order:** (1) target line: `Apply to` + `SelectedMonitor` ComboBox
(width 172) and ghost `ApplySelectedCommand`; (2) results `ListBox` wrap grid (296 px
cards, 12 px gutter, `SelectedCard`, per-card thumbnails); (3) status line bound to
`Status`; (4) pager `PreviousCommand`/`NextCommand`/`PageText`. Empty/busy overlays live
inside (2); `EmptyStateTitle`/`EmptyStateMessage`/`HasSearched` drive them.

**Preserve exactly:** `Tags`, `General`, `Anime`, `People`, `Safe`, `Sketchy`, `Explicit`,
`SelectedSorting`, `SelectedTopRange`, `SelectedOrder`, `SelectedRatio`, `SelectedMonitor`,
`SelectedCard`, `FeedName`, `SelectedFeedCadence`, `FeedMaximumItemsPerFetch`,
`CustomFeedCadenceText`, `ColorChoice.IsSelected`, `TagPresets`, `RecentTags`, `Status`,
`PageText`, `HasSearched`, `EmptyStateTitle`, `EmptyStateMessage`, `IsBusy`, and all
existing commands (`SearchCommand`, `PreviousCommand`, `NextCommand`, `PreviewCommand`,
`SaveCommand`, `ApplyCommand`, `ApplySelectedCommand`, `OpenPageCommand`,
`ApplyPresetCommand`, `ApplyTagCommand`, `SaveAsFeedCommand`,
`ApplyCustomFeedCadenceCommand`). New: `OpenSettingsCommand` only. In card templates,
`Apply to display` → `SecondaryButtonStyle`; Open page / Preview / Save → ghost.

**Code-behind:** keep `ApiKeyBox.PasswordChanged` bridge only if the key field remains in
XAML — it does not; remove the bridge and keep `OnLoaded` calling `RefreshApiKey()`,
`LoadDisplays()`, `OwnerWindow`, and `OnUnloaded` calling `CancelSearch()`. Keep
`OnSectionExpanded` only if expanders still use it.

### 4.2 Library (`Views/LibraryView.xaml`, `.xaml.cs`; `ViewModels/LibraryViewModel.cs`)

**Sections, in order:**
1. Toolbar `CardStyle`, two rows. Row 1: `SearchText`, `TagFilterText` +
   `ClearTagFilterCommand`, `SelectedCollectionFilter`, `SelectedSort`, `FavoritesOnly`,
   `SelectedDisplay` (width 172), then the page's single primary `Set wallpaper`
   (`ApplyCommand` + `SelectedEntry`). Row 2 (ghost): `Import folder…` (Click handler),
   `NewCollectionName` + `CreateCollectionCommand`, `FindDuplicatesCommand`,
   `RefreshCommand` (`IsRefreshEnabled`).
2. Transient panels (only when active): import progress (`IsImporting`/`ImportProgress`),
   duplicates (`HasDuplicates`/`DuplicateStatus`/`Duplicates` + `RemoveDuplicateCommand`).
3. Gallery `ListBox`: `Entries`/`SelectedEntry`, `OnCardLoaded` EventSetter and
   `DataContextChanged` lazy thumbnails, 288 px cards, 12 px gutter. Per-card order:
   thumbnail + favourite badge, `Title`, `DetailText`, chip strip (collection, favourite,
   `TagChips`, `ColorSwatches`), `Set wallpaper` (secondary), ghost actions
   (favourite/open/remove), editable collection move row (`SelectedCollection`,
   `MoveToCollectionCommand`, `MoveTargets`), tag row (`TagText`, `SetTagsCommand`,
   `TagStatus`).
4. Status line: `Status` then `DisplayDetectionError` (warning brush, text-led).
5. Pager: `PreviousPageCommand`/`CanGoPrevious`, `PageLabel`, `NextPageCommand`/`CanGoNext`.
6. Removal overlay: `IsRemoveConfirmationOpen`, `RemoveConfirmationText`,
   `DeleteFileOnRemove`, one primary Confirm + ghost Cancel.

**Preserve exactly:** all bindings in the guard allowlist for this view (§5.1) plus
`SearchText`, `TagFilterText`, `NewCollectionName`, `SelectedEntry`, `Entries`,
`Displays`, `CollectionFilters`, `SortOptions`, `MoveTargets`, `Duplicates`, `Status`,
`DisplayDetectionError`, `HasDisplayDetectionError`, `IsImporting`, `ImportProgress`,
`HasDuplicates`, `DuplicateStatus`, `IsRemoveConfirmationOpen`, `PendingRemoveEntry`,
`RemoveConfirmationText`, `DeleteFileOnRemove`, `CanGoPrevious`, `CanGoNext`, `PageLabel`,
`EmptyStateTitle`, `EmptyMessage` and every existing command. Keep `IsEditable="True"`
and the editable ComboBox template with both PART names.

### 4.3 Discover → feed authoring → Auto rotation (`Views/AutomationView.xaml`, `.xaml.cs`; `ViewModels/AutomationViewModel.cs`)

**Sections, in order:**
1. Global controls card: `SectionTitleStyle` "Automation", one caption line, three
   checkboxes (`PauseWhenFullscreen`, `AutoFetchFromWallhaven`, `FetchApplyToAll`), ghost
   "Refresh monitors" (`RefreshDisplaysCommand`), and the page's single primary
   "Fetch now" (`FetchNowCommand` — new). Remove the two long duplicated explanatory
   captions; one short line is enough.
2. Rotation feeds card: `SectionTitleStyle` "Rotation feeds", one compact row per feed
   in `Feeds`: name (`Name`), cadence (`CadenceOptions`/`CadenceMinutes` +
   `CustomCadenceText`/`ApplyCustomCadenceCommand`), state (`IsEnabled`), `StatusText`,
   actions `FetchNowCommand`, `SaveFiltersCommand`, `MoveUpCommand`, `MoveDownCommand`,
   `RemoveCommand` (all secondary/ghost). Put the **full filter draft** behind a
   `SectionExpanderStyle` "Filters": `FilterQuery`, `IncludeGeneral`/`IncludeAnime`/
   `IncludePeople`, `IncludeSafe`/`IncludeSketchy`/`IncludeExplicit`, `SortingChoices`,
   `OrderChoices`, `TopRangeChoices`, `RatioChoices`, `ColorChoices`, `MinimumResolution`,
   `ExactResolution`, `Seed`, `Page`, `MaximumItemsPerFetch`, `SaveCommand`, `ResetCommand`,
   `SaveError`, `IsDirty`, `CanSave`. The VM already exposes all of these; the current
   XAML only shows `FilterQuery`, which is the inconsistency to fix.
3. `SectionTitleStyle` "Display schedules" (inside a `PanelStyle`, not bare).
4. One `CardStyle` per item in `Schedules`, order: heading/subheading + state chip; NEXT
   CHANGE countdown; `StatusText`; per-display `PauseWhenFullscreen`; source pickers
   (`SourceKind`/`SourceKinds`, `SourceId`/`SourceIdOptions`); interval block
   (`Intervals`/`SelectedInterval`/`IntervalEnabled`, `CustomMinutesInput` +
   `CommitCustomIntervalCommand` + `CustomMinutesStatus`); action row led by Start/Pause
   (secondary), then Previous / Rotate now / Fetch now / Stop (ghost).
5. "Add feed from Discover" as a ghost link-style action (navigation, not a button
   hierarchy competitor). `AddFeedFromDiscoverCommand` preserved.
6. Bottom status line bound to `GlobalStatus`.

**Preserve exactly:** `PauseWhenFullscreen` (global **and** per-display),
`AutoFetchFromWallhaven`, `FetchApplyToAll`, `CadenceMinutes`, `IsEnabled`,
`SourceKind`, `SourceId`, `SelectedInterval`, `IntervalEnabled`, `Schedules`, `Feeds`,
`GlobalStatus`, `SelectedDisplay` and every command on both view models listed above.
Do **not** edit `RotationSession.cs`, `ScheduleStore.cs`, `WallhavenFeedStore.cs` or any
migration. The new `FetchNowCommand` must reuse the existing serialized fetch/apply path
and `FetchApplyToAll`; it must not create a second fetch pipeline.

### 4.4 Displays (`Views/DisplaysView.xaml`, `.xaml.cs`; `ViewModels/DisplaysViewModel.cs`)

**Sections, in order:**
1. Connected displays `CardStyle`: one `SectionTitleStyle` "Your display layout" + one
   caption; ghost "Refresh monitors" (`RefreshCommand`); scaled diagram with real image
   previews, PRIMARY/TARGET chips, origin marker, no-monitors text. Delete the separate
   eyebrow line.
2. Target display `CardStyle`: `SectionTitleStyle` "Choose source and fit" + one caption;
   source kind (`SourceKindOptions`, `OnSourceKindChanged`), file path (`DraftFilePath`
   + ghost Browse via `ChooseImageCommand`/`ChooseImageRequested`), library/feed source
   (`AvailableSources`, `OnSourceChanged`), fit (`PositionOptions`,
   `SelectedPositionOption`, `CanChooseFit`, `PositionDescription`), Span toggle
   (`IsSpanActive`, `ClickMode="Press"`, `OnSpanChecked`/`OnSpanUnchecked`), Span notice
   (`IsSpanActive`, `SpanImageName`, `SetSpanWallpaperCommand`).
3. One primary `Apply` (`ApplyCommand`), left-aligned, with its one-line failure note.
4. Bottom status line bound to `Status`.

**Preserve exactly:** `SelectedPositionOption`, `Monitors`, `RefreshCommand`,
`ApplyCommand`, `SelectMonitorCommand`, `ChooseImageCommand`, `SetSpanWallpaperCommand`,
`IsSpanActive`, `DraftFilePath`, `SelectedSourceKindName`, `SourceKindOptions`,
`AvailableSources`, `CanApply`, `CanChooseFit`, `PositionOptions`, `PositionDescription`,
`LayoutWidth`, `LayoutHeight`, `DiagramScale`, `OriginLeft`, `OriginTop`, `OriginVisible`,
`NoMonitors`, `SpanImageName`, `Status`, both `ChooseImageRequested` /
`ChooseSpanImageRequested` events and every code-behind handler. Keep all `Run.Text`
bindings `Mode=OneWay`, the `DisplayFitPreviewConverter` MultiBindings, `InputBindings`
with `Command`, and the editable-comboBox-free design. Keep the existing store path and
atomic write; no schema change.

### 4.5 Settings (`Views/SettingsView.xaml`, `.xaml.cs`; `ViewModels/SettingsViewModel.cs`)

**Sections, in order:**
1. Wallhaven card: `SectionTitleStyle` "Wallhaven", description, `ApiKeyBox` (explicit
   `Width="360"`), `Save API key` (the page's only primary), ghost "Remove key",
   `KeyStatus`. This is now the only place a key is saved or removed.
2. Startup card: `SectionTitleStyle` "Startup", `StartupEnabled`/`StartupAutomationName`
   chip, `StartupToggleLabel` + `ToggleStartupCommand`, ghost, `StartupStatus`,
   `StartMinimized` + `ToggleStartMinimizedCommand`, `StartMinimizedStatus`.
3. Local files card: `SectionTitleStyle` "Local files", ghost "Clear cached images",
   ghost "Repair library paths", ghost "Clear orphan wallpaper files", inline
   confirmation (`IsClearOrphansConfirmationOpen`, `ClearOrphansConfirmationText`,
   `ConfirmClearOrphansCommand`, `CancelClearOrphansCommand`), `CacheStatus`.
4. About & data card: `SectionTitleStyle` "About & data", `LibraryPathText`,
   `SchedulesPathText`, `LogPathText`, `VersionInfo`, `UpdateStatus`, GitHub `Hyperlink`
   with `RequestNavigate`, ghost Open data folder / View error log / Copy diagnostics.
5. Bottom status line bound to `Status` — remove the extra `CardStyle` wrapper.

**Preserve exactly:** every command and property currently bound in
`SettingsView.xaml`, the `ApiKeyCleared` event, `RefreshApiKey()`, `RefreshCache()`,
`RefreshUpdateStatus()` and the code-behind `PasswordChanged`/`RequestNavigate` bridges.
No version literal may be introduced (`check-invariants.ps1` guards this).

---

## 5. Hard constraints the implementation must not break

### 5.1 Binding-only allowlist (`scripts/check-buttons.ps1`, `-Strict` in CI)

The guard key is `relative XAML path | element | attribute | Binding Path`. Keep these
exact tuples. `PauseWhenFullscreen` appears twice; both copies count.

| View | Element.Attribute → binding path |
|---|---|
| `AutomationView.xaml` | `CheckBox.IsChecked` → `PauseWhenFullscreen` (global), `PauseWhenFullscreen` (per display), `AutoFetchFromWallhaven`, `IsEnabled` (feed), `FetchApplyToAll`; `ComboBox.SelectedItem` → `CadenceMinutes`, `SelectedInterval`, `SourceKind`, `SourceId` |
| `DiscoverView.xaml` | `ComboBox.SelectedItem` → `SelectedFeedCadence`, `SelectedSorting`, `SelectedTopRange`, `SelectedOrder`, `SelectedRatio`, `SelectedMonitor`; `ListBox.SelectedItem` → `SelectedCard`; `ToggleButton.IsChecked` → `General`, `Anime`, `People`, `Safe`, `Sketchy`, `Explicit`, `IsSelected` |
| `DisplaysView.xaml` | `ComboBox.SelectedItem` → `SelectedPositionOption` |
| `LibraryView.xaml` | `ComboBox.SelectedItem` → `SelectedCollectionFilter`, `SelectedSort`, `SelectedDisplay`; `ComboBox.Text` → `SelectedCollection`; `CheckBox.IsChecked` → `FavoritesOnly`, `DeleteFileOnRemove`; `ListBox.SelectedItem` → `SelectedEntry` |

Any **new** binding-only interactive control needs a reviewed entry in the script and in
`docs/BUG-INVENTORY.md`; only the lead may add one. Do not work around the guard by adding
a dead `Command`.

### 5.2 Other static guards

- `check-theme.ps1`: no literal white (`#FFF`, `#FFFFFF`, `#FFFFFFFF`, `White`) and no
  `SystemColors` brush anywhere in XAML; no `IsEnabled="False"` without an explicit
  `Style`. Use a binding to a VM state (`CanStart`, `CanGoNext`, ...) instead.
- `check-invariants.ps1`: editable ComboBox with `IsEditable="True"` must remain in
  `LibraryView.xaml`; no hardcoded version in `SettingsViewModel.cs` or
  `WallhavenClient.cs`; SDK pin, version alignment, tray handler bodies untouched.
- `check-tray.ps1`: `TrayIconService.cs` stays frozen; MouseClick/MouseDoubleClick stay
  gated on `MouseButtons.Left` and call `_onOpen`; `Notify` keeps `ShowBalloonTip`.

### 5.3 `AGENTS.md` XAML rules (exact)

- `Run.Text` bindings always `Mode="OneWay"` (`DisplaysView.xaml:172-233` is the
  existing correct pattern; do not regress it when restructuring cards).
- Keyed button styles are declared **after** the implicit `TargetType="Button"` style,
  or are fully self-contained with an explicit dark `ControlTemplate`. Never rely on
  `BasedOn="{StaticResource {x:Type Button}}"` resolving to app style from a keyed style
  declared earlier, and never let a theme disabled trigger paint a button white.
- Every custom `ComboBox` template includes
  `ToggleButton x:Name="PART_DropDownToggle"` with `ClickMode="Press"` bound to
  `IsDropDownOpen`, plus `PART_Popup`. The editable variant also needs
  `PART_EditableTextBox`.
- ToggleButton header/expander templates use `ClickMode="Press"` and
  `IsHitTestVisible="False"` on their children.
- `PasswordBox` always has an explicit `Width`.
- `App.LogFailure` tray notices stay rate-limited to one per minute. Do not add a
  per-layout-pass logged exception.
- No literal `IsEnabled="False"`, no hardcoded white brushes.

### 5.4 Public and data surface

- `AutomationView.ViewModel`, `ViewModel.InitializeAsync()`, `StopAsync()`,
  `RequestStop()` and nav key `"Automation"` / `"_Auto rotation"` (Alt+A) are frozen.
  `MainWindow` startup bootstrap and shutdown cleanup depend on them.
- `MainViewModel.Items`, `Navigate(string?)`, `CurrentView`, `AutomationView`,
  `EnsureAutomationView()`, `RefreshSettingsUpdateStatus()` keep their signatures.
- `NavigationService.CreateView`, `TryGetView`, all five case-insensitive keys stay.
- Data schemas, migrations, store file locations and every test are frozen. A view-model
  change that requires a schema change is out of scope for this rebuild.

---

## 6. File ownership map (6 agents, non-overlapping)

| # | Agent | Owns (and nothing else) |
|---|---|---|
| 1 | **Lead — shell, navigation, App.xaml** | `src/Ori.Desktop/App.xaml` (add `PanelStyle`, `GhostButtonStyle`, `StatusTextStyle`, `EmptyStatePanelStyle`; raise `FieldLabelStyle` to 12; normalise `ChipStyle` radius 8), `src/Ori.Desktop/MainWindow.xaml`, `MainWindow.xaml.cs`, `src/Ori.Desktop/ViewModels/MainViewModel.cs`, `src/Ori.Desktop/Services/NavigationService.cs`. Lead also owns the single build, all guards runs and live verification. |
| 2 | **Library** | `src/Ori.Desktop/Views/LibraryView.xaml`, `LibraryView.xaml.cs`, `src/Ori.Desktop/ViewModels/LibraryViewModel.cs`. |
| 3 | **Discover / feed authoring** | `src/Ori.Desktop/Views/DiscoverView.xaml`, `DiscoverView.xaml.cs`, `src/Ori.Desktop/ViewModels/DiscoverViewModel.cs`, `src/Ori.Desktop/ViewModels/FeedViewModel.cs`. Automation may bind to `FeedViewModel`'s public surface but may not edit this file. |
| 4 | **Auto rotation** | `src/Ori.Desktop/Views/AutomationView.xaml`, `AutomationView.xaml.cs`, `src/Ori.Desktop/ViewModels/AutomationViewModel.cs` (including the new page-level `FetchNowCommand`). |
| 5 | **Displays** | `src/Ori.Desktop/Views/DisplaysView.xaml`, `DisplaysView.xaml.cs`, `src/Ori.Desktop/ViewModels/DisplaysViewModel.cs`. |
| 6 | **Settings** | `src/Ori.Desktop/Views/SettingsView.xaml`, `SettingsView.xaml.cs`, `src/Ori.Desktop/ViewModels/SettingsViewModel.cs`. |

### Frozen — no implementation agent may touch

- `src/Ori.Core/**` — every model and service, explicitly including
  `src/Ori.Core/Services/RotationLoop.cs`, `ScheduleStore.cs`, `WallhavenFeedStore.cs`,
  `WallhavenClient.cs`, `WallhavenQueryBuilder.cs`, `WallpaperLibraryStore.cs` and all
  migrations.
- `src/Ori.Desktop/Services/**` — all services, explicitly including
  `RotationSession.cs`, `DisplayDetection.cs`, `MonitorSelectionService.cs`,
  `WindowsDesktopWallpaper.cs`, `ApiKeyStore.cs`, `TrayIconService.cs`, `App.xaml.cs`
  behaviour. The **only** exception is `NavigationService.cs`, owned by the lead.
- `scripts/check-buttons.ps1`, `check-theme.ps1`, `check-invariants.ps1`, `check-tray.ps1`,
  `build.ps1`, `scripts/publish.ps1`, `.github/**`, `global.json`, `Directory.Build.props`,
  `Ori.sln`, all `*.csproj`, `app.manifest`.
- `tests/**` — all existing tests, new or modified. No agent adds or edits a test.
- `AGENTS.md`, `docs/**` except this contract, `README.md`, `QA-FINDINGS.md`.
- `src/Ori.Desktop/App.xaml.cs`, `Mvvm/**`, `GlobalUsings.cs`.

If an agent needs a change in a frozen file, it stops and reports the request to the
integrator instead of editing it.

---

## 7. Integration checklist (lead only)

1. Apply the four new App.xaml styles before agents consume them, or tell agents to use
   the existing closest style until the lead lands them. Never let an agent add a resource
   to a view's `Resources` that duplicates one of the four new names.
2. Run all four guards: `pwsh -NoProfile -File scripts/check-buttons.ps1 -Strict`,
   `check-theme.ps1`, `check-invariants.ps1`, `check-tray.ps1 -Strict`. Expect zero findings;
   if the binding count drops, a preserved binding was lost.
3. Build once; then verify each of the five pages live with UIAutomation text tree +
   `PrintWindow` as `AGENTS.md` requires. A clean build does not prove layout.
4. Check specifically: Alt+D/L/P/A/S navigation and selected-state accent bar; the
   Settings footer button; one filled accent button per page; no white disabled buttons;
   ComboBox drop-downs open on click; empty state never blocks the list behind it;
   API key exists only in Settings; rotation still starts at launch and stops on tray Exit.
