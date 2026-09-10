# Avalonia UI verification (issue #5)

This page records **exactly what has and has not been verified** for the capture/restore
workflow UI. Mock evidence is never presented as live desktop evidence, and untested
scenarios are listed explicitly.

## What was built

A single accessible window (`src/WindowRecall.App`) with six states: profiles home,
capture review, profile editor, restore preview, apply result, and a non-trapping
permission notice. All workflow logic lives in `MainWindowViewModel` against Core
interfaces only (`IProfileStore`, `IWindowSystem`, `IRestorePlanner`); native platform
types are touched in exactly one composition-root file (`AccessibilitySupport.cs`).

## Automated evidence (Linux CI + local, headless)

Commands run on Linux (`ubuntu-latest` equivalent), .NET SDK 8.0.424:

```bash
dotnet restore --locked-mode            # success
dotnet format --verify-no-changes       # success
dotnet build --configuration Release    # success, 0 warnings
dotnet test  --configuration Release    # success (see counts below)
```

`WindowRecall.App.Tests` (23 tests) runs against **in-memory fakes and the Avalonia
headless platform**, proving:

- profile capture review honors per-window exclusion and slug-collision id generation;
- titles are stripped unless opted in, opt-in titles pass through Core redaction at save
  time, and an invalid redaction regex is rejected with an announcement;
- the editor persists launch-policy and exclusion choices;
- preview lists every planned move/skip/ambiguity with source/destination monitor labels
  and never auto-selects an ambiguous match;
- applying multiple windows requires an explicit confirmation step that performs nothing
  before confirmation, and cancel on that step changes nothing;
- an ambiguous row applies only after an explicit candidate pick, with clamped bounds;
- partial failures report per-window (`OK` / `FAILED`) and keep undo available;
- cancel mid-apply reports `CANCELLED` for the not-yet-applied windows and keeps undo for
  the completed ones;
- undo consumes the pre-apply snapshot and clears its own availability;
- an unsupported desktop produces the notice view and Home navigation always escapes it;
- a throwing permission provider cannot break initialization.

Headless UI-tree tests (`MainWindowAccessibilityTests`) additionally assert the real
XAML tree: every interactive control has an automation name or visible text, Home
navigation is declared first, the status region is an Assertive live region, headings
carry heading-level semantics, the undo action is named, and the window is resizable
with a minimum size.

## What automated tests do NOT prove

These tests use injected fake desktops. **No test observed or moved a real window.**
The following were **not tested** in this slice and remain open:

- [ ] Live restore on a real Windows 10/11 desktop (issue #7 integration suite).
- [ ] Live restore on a real macOS desktop with Accessibility permission granted/denied (issue #7).
- [ ] Narrator (Windows) manual pass — **untested**: no Windows desktop available in this environment.
- [ ] VoiceOver (macOS) manual pass — **untested**: no macOS desktop available in this environment.
- [ ] Screenshot evidence — **not recorded**: the test environment is headless.
- [ ] Actual OS-level high-contrast theme, text-scaling, and reduced-motion behavior —
      **untested** on real OS settings (see design notes below for what the UI does support).

## Manual Narrator / VoiceOver checklist (to be executed on real hardware, issue #7)

Execute from the profiles home; announce each result in the run log.

1. Tab order: navigation bar first, then permission banner, then list/toolbar, then status.
2. Every button/checkbox/textbox announces a purposeful name (not "button"/"check box").
3. Capture a desktop: status region announces the capture result without color reliance.
4. Exclude a window and save; focus returns to the profile list on Home.
5. Open restore preview: summary announces actionable/need-review/skip counts.
6. Ambiguous row: combo announces options; selection unlocks the row checkbox.
7. Select 2+ rows and Apply: the confirmation overlay takes focus; Cancel announces "Nothing changed".
8. Confirm multi-apply: progress region announces; Cancel mid-apply reports CANCELLED rows.
9. Result screen: Undo is discoverable via Tab and announces its best-effort effect.
10. Trigger the permission notice (denied accessibility / headless session): Home remains
    reachable and focused controls never disappear under the overlay.
11. Windows: run with Narrator; macOS: run with VoiceOver + keyboard nav; repeat 1–10.
12. Verify no UAC prompt appears (Windows must run as standard user) and no
    Screen Recording permission is requested (macOS).

## Accessibility design notes (implemented, but OS-behavior untested headless)

- **Keyboard:** logical declaration order, no custom focus traps; the confirmation overlay
  only appears with its two buttons reachable; Avalonia's default TabNavigation applies.
- **Semantics:** `AutomationProperties.Name`/`HeadingLevel`/`LiveSetting` are set in XAML;
  the VM's `StatusMessage` is the single assertive live region.
- **Non-color status:** every status cue has a text glyph or word (`[!]`, `[-]`, `[OK]`,
  `Needs review`, `CANCELLED`) — color is redundant, never load-bearing.
- **Text scaling / high contrast:** the window is resizable with a minimum size, uses
  FluentTheme (which follows the system theme variant), no fixed-height clipped text.
- **Reduced motion:** the UI deliberately uses no custom animations or transitions; the
  OS reduced-motion setting therefore cannot be violated by this window's content.
