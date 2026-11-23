# Manual QA smoke checklist (v2 shell)

Run this after the integrator produces a build: launch Oriole from the build output and click through the six areas below in order. This is a visual smoke test, not an automated test — do not run `build.ps1`, `dotnet build`, `dotnet test`, or launch from source.

**Global checks — repeat on every screen:**

- [ ] Dark theme throughout: near-black window (`#10141B`), dark sidebar (`#151B24`), dark panels (`#1A222E`). No light WPF default surface anywhere.
- [ ] **No white boxes.** Buttons, ComboBoxes, TextBoxes, PasswordBoxes, CheckBoxes, ListBox cards, ScrollBars, tooltips and dropdown popups stay dark in normal, hover, focus, selected **and disabled** states. Disabled controls are dimmed, never repainted white. A white rectangle on a control is a regression — stop and note the screen.
- [ ] Text is legible: primary/muted/accent brushes, no white-on-white or dark-on-dark text.
- [ ] Failures appear as inline status text with a refresh/retry action, never as a blocking modal dialog.
- [ ] **Error log:** `%LOCALAPPDATA%\Oriole\logs\errors.log`. The same path is printed under **Settings → DATA & ABOUT → Error log**. The log rotates (bounded size/history) and is redacted so API keys and other sensitive values are not written in plaintext. If anything looks wrong, note the last timestamped entry there.
- [ ] Optional static guard (not CI-wired): `pwsh -NoProfile -File scripts/check-theme.ps1` — exit 0 = clean, exit 1 = findings to review. It flags literal white/SystemColors brushes and disabled controls with no explicit Style.

## Landing / navigation (Discover is the landing page)

- [ ] On launch, **Discover** is shown (the old Overview screen was removed in `v2-shell`).
- [ ] The sidebar nav items (Discover, Library, Displays, Automation, Settings) navigate when clicked; selected-state styling is visible.
- [ ] Status/preview copy is current (no stale "coming soon" text); no orphaned Overview route remains.
- [ ] Vertical ScrollBar, if present, is dark; scroll to the bottom and back with no white flash.

## Discover

- [ ] Filter sidebar and results pane render side by side; sidebar search button stays visible at the bottom after scrolling the filter list.
- [ ] Tags, minimum-resolution, exact-resolution and custom-colour **TextBoxes** are dark with light text; clicking into them shows a dark caret/focus state, not a white field.
- [ ] API-key **PasswordBox** is dark; typed characters mask and remain legible.
- [ ] Category/content **CheckBoxes** (General, Anime, People, SFW, Sketchy, NSFW) have dark glyphs and labels, including hover and checked states; NSFW remains off by default.
- [ ] Sorting, Toplist period, Order and Aspect-ratio **ComboBoxes** are dark; open each dropdown and verify the popup list and selected item are dark, not the default white menu.
- [ ] Colour swatch strip and its inner ScrollBar are dark.
- [ ] "Apply to" monitor ComboBox lists **both** connected monitors by device name; selecting either keeps the dropdown dark.
- [ ] Result cards are dark; card buttons (Open page, Preview, Save to library, Apply, Previous, Next) are dark; hover/selected borders use the accent brush, not a white outline.
- [ ] Any inline status/error text is muted-colour and readable; check `errors.log` if search or preview fails.

## Library

- [ ] "Search library" TextBox, Collection ComboBox, "Favorites only" CheckBox, Refresh button and "Apply target" ComboBox are all dark; typing filters the list after a short pause.
- [ ] Disabled Refresh (when nothing to refresh) is visibly dimmed, not a white button.
- [ ] Card grid: dark card surfaces, dark thumbnail placeholders, legible title/detail text. No white tile while images decode.
- [ ] Per-card buttons (Set, Favorite, Open, Remove, Move) and the per-card collection ComboBox are dark in normal, hover and disabled states.
- [ ] Scroll the virtualized list to the end and back: recycled rows do not flash white or show white gaps.
- [ ] Empty-library message panel is dark; "Apply target" still lists both monitors.
- [ ] Inline status text is readable; check `errors.log` after any failed open/apply.

## Displays

- [ ] One card per connected monitor — **both** monitors on the test rig — with correct device names, resolution and primary flag; card positions match the physical layout.
- [ ] Each card's fit-mode **ComboBox** is dark and its dropdown list is dark; selecting a fit mode keeps the selection readable.
- [ ] "Choose image" button has the accent-coloured surface with legible foreground — no white block behind or around it.
- [ ] Preview reflects the chosen crop/fill on each monitor; applying changes the wallpaper on both displays, and the prior wallpaper is preserved if an apply fails.
- [ ] Page ScrollBar is dark; no white tooltips when hovering icons or labelled controls.
- [ ] Display detection failure, if any, shows inline status plus Refresh — no modal; check `errors.log` for a COM/HRESULT entry.

## Automation

- [ ] One schedule card per monitor — **both** monitors on the test rig — with distinct headings and independent controls.
- [ ] "NEXT CHANGE" **countdown is visible and ticks down**; selecting a different interval updates it; navigating away and back does not reset it to zero.
- [ ] Interval ComboBox is dark, opens to a dark list, and the selected value is readable; when disabled it is dimmed, not white.
- [ ] Start / Pause / Rotate now / Stop **Buttons** are dark; enabled and disabled states are distinguishable without any white surface.
- [ ] "Pause a display's schedule while a full-screen app is active" CheckBox is dark and toggles; label wraps without overflow.
- [ ] "Refresh displays" works inline; global status text is muted-colour and readable.
- [ ] Check `errors.log` after Start/Rotate if a schedule reports an error — the current wallpaper must remain unchanged.

## Settings

- [ ] Wallhaven API-key **PasswordBox** is dark with masked, legible characters; Save API key / Remove key buttons are dark; key status message is readable.
- [ ] "Clear cached images" button and cache status are dark; clearing the cache does not remove saved library wallpapers.
- [ ] "Start Oriole when I sign in" toggle button is dark with a readable Off/On label; toggling updates the status text.
- [ ] DATA & ABOUT path text is legible, including **Error log: `%LOCALAPPDATA%\Oriole\logs\errors.log`**; copy that path for any issue found above.
- [ ] Page ScrollBar and any tooltips are dark; no white panels after scrolling top to bottom.
- [ ] Confirm the About/version text shows the runtime-derived version (not a hard-coded preview string).
- [ ] Final pass: revisit every screen and confirm the dark theme survived all navigation; check `errors.log` one last time for entries timestamped during this run.

## Tray, import/auto-rotation and release (v2-shell additions)

- [ ] Tray icon notifications (rotation/failure balloon tips) appear and are readable; a tray right-click shows Open/Exit without restoring the window.
- [ ] Library folder import adds images and duplicate detection skips already-saved files; collections and the editable collection ComboBox work.
- [ ] Tag presets / auto-rotation feed settings are present and the feed persists across a restart.
- [ ] Portable release: download the self-contained zip, extract and run `Oriole.exe` on a PC **without** the .NET Desktop Runtime — it must start.
- [ ] (Optional, if Inno Setup available) the per-user installer installs to `%LOCALAPPDATA%\Oriole`, adds Start menu/uninstall entries, and uninstall preserves library/logs.
