# UI guidelines

Primary sources only. Claims are linked inline. The goal is a Windows 11-native feel, not a copied web app: Fluent/WinUI is the baseline, Material 3 supplies the rail/card patterns, Nielsen Norman Group supplies the usability rules.

## Sources

- Microsoft: [Navigation basics](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics), [NavigationView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview), [Content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics), [Alignment, margin, padding](https://learn.microsoft.com/en-us/windows/apps/develop/ui/alignment-margin-padding), [Typography](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography), [Color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color), [Contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes), [Iconography](https://learn.microsoft.com/en-us/windows/apps/design/iconography/), [Buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons), [Command bar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/command-bar), [Target size](https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-targeting), [App icon design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design), [App icon construction](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction), [About icons](https://learn.microsoft.com/en-us/windows/win32/menurc/about-icons)
- WinUI Gallery: [github.com/microsoft/WinUI-Gallery](https://github.com/microsoft/WinUI-Gallery) — interactive reference for the patterns above; its TitleBar + NavigationView wiring is the documented Microsoft pattern.
- Material 3: [Navigation rail](https://m3.material.io/components/navigation-rail/overview), [rail guidelines](https://m3.material.io/components/navigation-rail/guidelines), [rail accessibility](https://m3.material.io/components/navigation-rail/accessibility), [Cards](https://m3.material.io/components/cards/overview), [Buttons](https://m3.material.io/components/buttons/guidelines), [button specs](https://m3.material.io/components/buttons/specs), [Shape](https://m3.material.io/styles/shape/overview-principles), [Icons](https://m3.material.io/styles/icons)
- Nielsen Norman Group: [Menu-design checklist](https://www.nngroup.com/articles/menu-design/), [5 visual treatments for accessibility](https://www.nngroup.com/articles/visual-treatments-accessibility/), [Low-contrast text](https://www.nngroup.com/articles/low-contrast/), [5 principles of visual design](https://www.nngroup.com/articles/principles-visual-design/), [Similarity principle](https://www.nngroup.com/articles/gestalt-similarity/)

## Navigation rail

### Structure and grouping

- Navigator principles: consistency, simplicity, clarity. Use familiar controls and standard locations, show only important destinations, keep paths obvious ([Navigation basics](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics)).
- With 5–10 equally important top-level destinations, use an expanded left pane; with more than 5, left navigation beats top navigation ([NavigationView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview), [Navigation basics](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics)). A Material rail holds 3–7 destinations ([M3 rail](https://m3.material.io/components/navigation-rail/overview)) — Ori's five fit.
- Build the pane from menu items + separators + group headers, with Settings in a footer and optional app-level search in the header ([NavigationView anatomy](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)). Pin Settings to the bottom; only navigation items belong in the footer, free-form content goes in `PaneFooter`.
- Keep hierarchy two levels deep at most; two levels is the ideal for comprehension ([NavigationView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)). Do not make users pogo-stick up then down to reach related pages ([Menu-design checklist](https://www.nngroup.com/articles/menu-design/)).
- Prefer a flat structure for peer pages that can be viewed in any order; switch to hierarchical only past ~7 pages ([Navigation basics](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics)).
- Adapt by width: expanded left at ≥1008 px, icon-only `LeftCompact` at 641–1007 px, menu button `LeftMinimal` at ≤640 px ([NavigationView adaptive behavior](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)).
- With a custom title bar, put back/pane-toggle in the title bar and hide the NavigationView copies; this is the pattern used by WinUI Gallery ([NavigationView top whitespace](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview), [WinUI-Gallery](https://github.com/microsoft/WinUI-Gallery)).
- Always communicate current location in the menu ([Menu-design checklist](https://www.nngroup.com/articles/menu-design/)).

### Icons and states

- Icons are visual metaphors; they must symbolize the content of their destination ([Iconography](https://learn.microsoft.com/en-us/windows/apps/design/iconography/), [M3 rail guidelines](https://m3.material.io/components/navigation-rail/guidelines)).
- Filled icon for the active destination, outlined for inactive ([M3 rail accessibility](https://m3.material.io/components/navigation-rail/accessibility)). Keep one icon vocabulary; Material's symbol styles are outlined/rounded/sharp ([M3 icons](https://m3.material.io/styles/icons)).
- A Segoe Fluent Icons glyph at font size 16 occupies a 16×16 epx square, so icon sizing and alignment stay predictable ([Iconography](https://learn.microsoft.com/en-us/windows/apps/design/iconography/), [Segoe Fluent Icons](https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font)).
- Show exactly one selection indicator. In left mode it draws along the item's left edge ([NavigationView selection](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)).
- Hover is a subtle surface tint, not a colour swap of the label. Press and selected are distinct, and selected must win over hover in the trigger order.
- Never rely on colour alone. Add a shape/position cue (left accent bar, filled icon, bolder label) because ~8% of men and 0.5% of women have red-green colour deficiency ([Color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color), [5 visual treatments](https://www.nngroup.com/articles/visual-treatments-accessibility/)).
- Focus must be a clearly visible thick stroke; colour change alone is not enough ([5 visual treatments](https://www.nngroup.com/articles/visual-treatments-accessibility/)). Arrow keys move focus within the pane; Space/Enter invokes ([NavigationView keyboarding](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)).

### Spacing and typography

- All dimensions, margins and padding in increments of 4 epx; use 12 epx gutters below 640 px and 24 epx above ([Alignment, margin, padding](https://learn.microsoft.com/en-us/windows/apps/develop/ui/alignment-margin-padding)).
- Content margins: 12 px in minimal mode, 24 px otherwise ([NavigationView content](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)).
- 8 px between buttons, 8 px control-to-header, 12 px control-to-label, 12 px between content areas, 16 px surface-to-edge text ([Content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics)).
- Target size: 7.5 mm square, roughly 40×40 px at 135 PPI / 100% scaling; make frequently pressed targets larger ([Targeting](https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-targeting)).
- Use one font family throughout: Segoe UI Variable, which has optical-size and weight axes; use Body Strong for a title in confined space and Caption only in very confined space ([Typography](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography), [Content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics)).
- Consistency is a visual-design principle: the same size and the same rules mean the same thing, which makes pages scannable ([5 principles](https://www.nngroup.com/articles/principles-visual-design/), [Similarity](https://www.nngroup.com/articles/gestalt-similarity/)).

### Colour

- Use colour sparingly to mark important elements and to indicate interactivity; one colour signals "interactive" ([Color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color)).
- Text/background contrast ≥4.5:1 (≥3:1 for 18 pt or 14 pt bold) ([5 visual treatments](https://www.nngroup.com/articles/visual-treatments-accessibility/)). Low-contrast text causes eye strain and reduces trust ([Low-contrast text](https://www.nngroup.com/articles/low-contrast/)).
- In dark mode, darker surfaces are lower importance; lighter/brighter surfaces are elevated and important ([Color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color)).
- Contrast themes target ≥7:1. Test in all four built-in themes; do not hard-code colours in HighContrast — use `SystemColor` resources ([Contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)).
- Use a tint layer for selected/hover surfaces rather than a new opaque colour; that keeps the palette small and works in both themes.

## Cards and grids

- A card holds content and actions about one subject. Three types: elevated, filled, outlined ([M3 cards](https://m3.material.io/components/cards/overview)). Pick one per context, not a mix on one page.
- Consistent spacing and gutters semantically group a page into components and should map to the corner-radius scale ([Content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics)). Shape communicates state and attention ([M3 shape](https://m3.material.io/styles/shape/overview-principles)).
- Multiline lists: Body + Caption text, 32 epx icons, Body Strong for section headers. Grid items with icons use centre-aligned Caption; large graphical items use left-aligned Body ([Content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics)).
- 12 px between content areas and 16 px from a surface edge to its text ([Content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics)).
- For image galleries, make the image the subject: one thumbnail per tile, metadata in Caption, hover actions overlaid on the image. Keep text buttons inside cards so emphasis stays on the content ([M3 buttons guidelines](https://m3.material.io/components/buttons/guidelines)).
- Settings pages read as stacked cards under section headers, each card one subject. Keep label/control pairs aligned across cards; one column of controls, not a two-column grid, unless the fields are genuinely paired.
- Virtualize the gallery and keep tiles a consistent aspect ratio so the grid does not reflow on load.

## Actions and hierarchy

- Use a Button for an immediate action; for navigation use a hyperlink-style control, not a Button ([Buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons)).
- Expose one or two commit buttons at a time (e.g. Accept / Cancel). Order commit buttons OK/[Do it]/Yes, then [Don't do it]/No, then Cancel ([Buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons)).
- Button text: concise, specific, usually a single verb; minimum width 120 px for short text, maximum ~26 characters for long text ([Buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons)).
- Placement: right-align a single button in a dialog or container; left-align a single page-level button such as Apply at the bottom of a settings page so it lines up with page content ([Buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons)).
- Keep the highest-emphasis action filled/accented, secondary actions outlined or neutral, low-priority actions text-only. M3 defines five styles — elevated, filled, tonal, outlined, text — and recommends outlined for secondary and text inside cards ([M3 button specs](https://m3.material.io/components/buttons/specs), [M3 guidelines](https://m3.material.io/components/buttons/guidelines)). Exactly one primary button per view.
- A CommandBar puts primary commands on the right and moves the rest into an overflow; common commands such as Copy/Delete belong in primary commands ([Command bar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/command-bar)).
- Every interactive element is focusable and its focus state clearly indicated ([5 visual treatments](https://www.nngroup.com/articles/visual-treatments-accessibility/)). Preserve one global focus ring rather than per-control reinventions.
- Disabled state: reduce opacity, keep the label legible, and never use disabled colour as the only status signal.

## Avoiding the dated WPF look

- Alignment, margins and padding keep UI from looking too cluttered or too sparse ([Alignment, margin, padding](https://learn.microsoft.com/en-us/windows/apps/develop/ui/alignment-margin-padding)).
- Replace heavy 1 px boxes around every surface with elevation: a slightly lighter panel fill plus a subtle divider. Border interactive controls only ([Color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color), [M3 cards](https://m3.material.io/components/cards/overview)).
- Use at most two corner radii: one for cards/surfaces, one for controls. Introducing a third or fourth radius reads as inconsistency ([M3 shape](https://m3.material.io/styles/shape/overview-principles), [Similarity](https://www.nngroup.com/articles/gestalt-similarity/)).
- Do not shrink type below the system scale to fit. Reposition or truncate with a tooltip instead; low-contrast, tiny text is both unreadable and untrusted ([Low-contrast text](https://www.nngroup.com/articles/low-contrast/)).
- Avoid synthetic 3D bevels, gradient fills and drop shadows on flat controls; Fluent uses light, elevation, depth and motion sparingly ([Color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color)).
- Apply the same visual rules to the same element types everywhere; inconsistent size and spacing destroys visual hierarchy ([5 principles](https://www.nngroup.com/articles/principles-visual-design/)).
- Verify under Contrast themes and at 125%/150% DPI, not just the default dark theme ([Contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)).

## App icon

- Iconography in Windows: app icons represent the app in the shell and should express its core function through a metaphor ([Iconography](https://learn.microsoft.com/en-us/windows/apps/design/iconography/), [App icon design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design)).
- For a wallpaper manager, a simple framed landscape — a photo frame with a horizon/peak — reads at 16 px and says "wallpaper" faster than a monitor or a gear. Avoid multiple objects, gradients and text: text is unreadable at 32×32 ([App icon design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design), [Teams icon guidance](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/design/design-teams-app-icon-store-appbar)).
- Silhouette must be distinctive and legible at small sizes; keep detail minimal and avoid tight reflections or dimension effects ([App icon design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design)).
- Colour: use dark, medium and light values; at least half the icon should pass 3.0:1 contrast on both light and dark backgrounds. Yellow only passes on light once darkened; reds struggle on dark ([App icon design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design)).
- High contrast icons are black and white, a direct representation of the colour icon, no gradients. Windows 11 no longer requires separate HC assets ([App icon design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design)).
- ICO must include 16×16, 24×24, 32×32, 48×48 and 256×256 at minimum; supplying 256 means Windows only scales down ([App icon construction](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)).
- System icon sizes: 32×32 at 96 DPI, 48×48 at 150%, 64×64 at 200% for the large icon; small icons come from `SM_CXSMICON` ([About icons](https://learn.microsoft.com/en-us/windows/win32/menurc/about-icons)). An ICO stores separately optimized images per size, avoiding upscaling blur ([About icons](https://learn.microsoft.com/en-us/windows/win32/menurc/about-icons)).
- If shipping MSIX assets too, provide target-size PNGs (16…256) and 100/200/400% scales of the 44×44 and 150×150 logos ([App icon construction](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)). This app currently ships a plain EXE, so the multi-size ICO is the priority.

## Ori-specific recommendations

Use the existing `App.xaml` keys; do not introduce parallel tokens. Values below are in epx/DIP and assume the existing dark palette.

- **Rail geometry:** 208 px expanded, 64 px icon-only compact. Minimum nav row height 40 px to meet the 7.5 mm target. Current `NavButtonStyle` uses `Padding="12,10"` at `NavSize=13` (~33 px tall) — raise vertical padding to 14 or set `MinHeight="40"`.
- **Rail grouping:** main list = Discover, Library, Displays, Automation; Settings goes in the footer slot, matching `FooterMenuItems` guidance. Separator + group header only if a group has 2+ items. Sixteen px (`SpaceM`) above the first group, 8 px (`SpaceS`) between groups, 4 px (`SpaceXS`) between adjacent rows.
- **Rail states:** rest = `MutedBrush`, transparent background; hover = `NavHoverBrush` (#1C2734); pressed/selected = `NavSelectedBrush` (#1B3552) + `TextBrush` label. Add a 3 px `AccentBrush` bar on the selected item's left edge so selection is not colour-only. Keep the existing `DataTrigger` ordering that makes selection beat hover.
- **Focus:** keep the single `FocusVisualStyleKey` dashed `AccentBrush` ring. Do not add per-control focus templates; add `KeyboardNavigation.TabNavigation` only where the visual order differs from tree order.
- **Icons:** `Segoe Fluent Icons` at `GlyphSize=16` for rail and card actions; use 32 px only for the large list-item icons Microsoft calls for. Give every icon-only control a `ToolTip` and an accessible name.
- **Cards:** `CardCornerRadius=12` for gallery tiles and setting cards, `ControlCornerRadius=8` for buttons/inputs. `ChipStyle` is currently 6 — align chips to 8 so there are only two radii. Surface fill `PanelBrush`/`PanelAltBrush`; 1 px `DividerBrush` only where separation is needed, never a full border around every card.
- **Grid:** 12 px gutter between gallery tiles, 16 px card padding (`CardPadding=20` is fine for settings cards), page margin `ContentMargin=32,24,32,28`. At narrow widths switch to a 12 px gutter via an `AdaptiveTrigger`; at ≥1008 px keep 24 px.
- **Type:** keep `TitleSize=28` for page titles, `SectionTitleStyle` (15) for card headers, `BodySize=13` for body, `CaptionSize=11` for metadata. Raise `CaptionSize` and `FieldLabelStyle` from 11 to 12 if they become primary labels; 11 px is acceptable only for secondary metadata that still clears 4.5:1. Keep `EyebrowTextStyle` uppercase for group eyebrows.
- **Buttons:** `PrimaryButtonStyle` once per view for the commit action (Set wallpaper / Save changes); `SecondaryButtonStyle` for alternatives. Use action text inside gallery cards so the image stays dominant. Follow Microsoft's order for two-commit dialogs and left-align a page-level Apply button.
- **Contrast check:** `TextBrush` (#EDF3FB) on `WindowBrush` (#0B1017) and `MutedBrush` (#A9B9CD) on `PanelAltBrush` (#1D2838) look comfortably above 4.5:1 — verify with a contrast checker before claiming compliance. `OnAccentBrush` (#0A1018) on `AccentBrush` (#5B9BFF) is the correct high-contrast pairing; keep it.
- **App icon:** draw a single flat framed-landscape silhouette and export `Ori.ico` with 16/24/32/48/256 images. Check it at 16 px in the tray before shipping; drop any detail that disappears. No text, no gradient, one accent colour.
- **Verification:** review every changed view in the running app with UIAutomation/PrintWindow as `AGENTS.md` requires; a clean build does not prove the layout is right.
