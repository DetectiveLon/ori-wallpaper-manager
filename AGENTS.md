# Ori repository rules for agents

## XAML / WPF gotchas learned the hard way

- `Run.Text` binds **TwoWay by default**. Binding it to a get-only view-model property
  throws `XamlParseException` on every layout pass. Always write
  `Text="{Binding X, Mode=OneWay}"` for `Run` elements.
- Keyed button styles must be declared **after** the implicit `TargetType="Button"`
  style they inherit from. If declared before, `BasedOn="{StaticResource {x:Type Button}}"`
  binds to the Windows theme style, whose `IsEnabled=False` trigger paints buttons
  white. Prefer self-contained styles with an explicit dark `ControlTemplate`.
- A custom `ComboBox` template **must** include `ToggleButton x:Name="PART_DropDownToggle"`
  bound to `IsDropDownOpen`. Without that part, clicking does nothing while the mouse
  wheel still changes the selection, which reads as "not clickable, only scrollable".
- `PasswordBox` has no natural width. Without an explicit `Width`, it collapses to
  its content (~45px) and looks broken.
- `ToggleButton` header templates should use `ClickMode="Press"` and set
  `IsHitTestVisible="False"` on their children so every pixel routes to the button.
- Any failure logged by `App.LogFailure` fires a tray notification. A recurring
  exception therefore becomes a notification storm. Notices are now rate-limited
  to one per minute; keep that guard when touching error handling.

## Verification

- Never ship a UI change on build/guards alone. Capture the live window with
  `PrintWindow` and UIAutomation navigation (no focus steal) and look at it.
- A stale capture looks white/blank when the window is occluded; confirm the
  loaded page from the UIAutomation text tree before trusting a screenshot.
