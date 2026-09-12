# Accessibility release checklists (Narrator & VoiceOver)

These checklists are **to be executed on real hardware** by a sighted operator driving the
screen readers. The UI workflow itself is automated headless (automation names, live-region
status, keyboard order — see `docs/avalonia-ui-verification.md`), but screen-reader speech is a
manual gate. Nothing here has been executed yet; do not claim it has.

Environment: Windows 11 (latest) with Narrator (Win+Ctrl+Enter), macOS with VoiceOver (Cmd+F5),
each at 100%/150%/200% scaling and with high contrast / increased-contrast modes enabled.

## Shared steps (both platforms)

| # | Step | Pass criteria | Result |
|---|---|---|---|
| A1 | Launch Window Recall from the installed location | App announces itself; no unexpected permission prompt (Windows: none; macOS: Accessibility only, never Screen Recording) | ☐ not run |
| A2 | Tab through the profiles home | Every control reachable, focus always visible, announcement includes control role + purpose | ☐ not run |
| A3 | Capture a profile | Focus moves to capture review; window list rows announce app name, state, and whether a title is stored | ☐ not run |
| A4 | Preview a restore | Status region announces plan counts; ambiguous items announce "needs your choice" and are not selected | ☐ not run |
| A5 | Apply a restore | Live-region outcome announced per window; partial failure announces which window failed | ☐ not run |
| A6 | Undo | Undo announces whether it succeeded and is then consumed | ☐ not run |
| A7 | Cancel mid-restore | Escape stops later items; announced cancelled state matches the receipt | ☐ not run |
| A8 | Color independence | With high/increased contrast on, no information is conveyed by color alone | ☐ not run |
| A9 | Reduced motion | With the OS setting on, no animated transitions | ☐ not run |
| A10 | DPI/scaling | UI remains readable and hit-targets usable at 150% and 200% | ☐ not run |

## Narrator-specific

| # | Step | Pass criteria | Result |
|---|---|---|---|
| N1 | Scan mode over the profile list | Rows read as list items with position (n of m) | ☐ not run |
| N2 | Narrator + Edge navigation into the editor | Text fields announce labels + value; validation errors announced on commit | ☐ not run |
| N3 | Ghost/inactive windows after display removal | Reason text read aloud per skipped window | ☐ not run |

## VoiceOver-specific

| # | Step | Pass criteria | Result |
|---|---|---|---|
| V1 | VO cursor over the profile list | Same list semantics as N1 | ☐ not run |
| V2 | Rotor navigation to the permission notice | Notice readable without disturbing the modal-free workflow | ☐ not run |
| V3 | Permission onboarding | Notice explains Accessibility need and states Screen Recording is not required; the settings shortcut button works | ☐ not run |

Archive filled checklists (photo or text export) beside this document with the release tag and OS
build numbers before the release is marked verified.

# Troubleshooting guide

## macOS: Window Recall cannot move windows

1. Grant **Accessibility** to Window Recall in System Settings → Privacy & Security → Accessibility,
   then fully quit and relaunch Window Recall. Window Recall never asks for Screen Recording —
   if some other prompt appears, that is a bug: capture it and file an issue.
2. Apps in full-screen spaces, system windows, and modal sheets are deliberately **skipped** with a
   reason in the preview; they are never forced. Move them manually if needed.
3. After a macOS update, re-granted permissions may be reset; rerun `window-recall doctor`
   (the `capabilities` block shows what the adapter can currently do).

## Windows: some windows were not restored

1. Protected/process-isolated windows, tool windows, cloaked windows, and owned dialogs are
   conservatively excluded from capture; they will not appear in a profile at all.
2. Some applications reject programmatic moves (documented in the preview as a per-window failed
   outcome). Window Recall reports the failure per window and keeps everything else recoverable —
   run `window-recall undo` to roll back the successful moves if the result is unwanted.
3. After a display removal, restored windows are clamped so a recoverable region is always on
   screen. If a window still seems lost, check the other display's edge and use Win+Shift+←/→ to
   snap it between monitors.

## Stale profile identity (app moved or renamed)

A profile stores executable paths as identity, never shell commands. If you moved the executable,
`doctor` flags `unsafe/missing identities`; re-capture the profile from a running desktop rather
than hand-editing paths — import validates and rejects malformed or future-version documents.

## Off-screen recovery

Any restore clamps results into the current usable work area, and every apply retains a one-step
undo receipt (`window-recall undo`, available once). If a third party tool moved a window off
screen, `capture` a new profile on the current desktop; profiles always reflect observed geometry.

## Offline behavior

Window Recall works fully offline and contains no update or telemetry pings; check for new
versions manually on the repository releases page. Unsigned archives will show SmartScreen
(Windows) or Gatekeeper (macOS) warnings until signing lands — see `docs/release-checklist.md`
for the current signing status.
