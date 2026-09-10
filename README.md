# Window Recall

**Window Recall is a local-first desktop utility for Windows 10/11 and macOS that saves named window layouts and restores apps after monitor, dock, or workspace changes.**

> Status: deterministic matching, display-topology mapping, safe immutable previews, selective coordination, and one-step best-effort undo are implemented in Core. A headless CLI (capture, profiles, plan, apply, undo, export, import, doctor) with documented exit codes, stable `--json` output, conflict-safe import/export, persisted undo receipts, and privacy controls is implemented and tested against an injected fake desktop on Linux, Windows, and macOS runners. Windows and macOS adapter vertical slices have deterministic fake-native coverage and opt-in live fixture harnesses; Windows consumes topology-planned multi-monitor bounds and state changes, while macOS remains read-only without Accessibility permission and re-observes authorized AX mutations. Live Windows and macOS evidence has not yet been recorded, and packaging remains future work.

## Overview

Switching between a laptop screen, a desk dock, a projector, and remote work often scatters windows, hides them off-screen, or forces the same manual rearrangement every day. Window Recall captures the windows in a workspace, shows exactly what will move or launch, and restores the layout on demand.

Window Recall is intended to stay understandable and safe: profiles are ordinary local JSON, restore starts with a preview, unmatched windows are reported rather than guessed, and the app does not upload titles, application identities, or layout history.

## Target users

- Laptop users who repeatedly dock and undock
- Developers who arrange terminals, editors, browsers, and documentation per project
- Creators and streamers who switch between editing, recording, and presentation layouts
- Accessibility users who benefit from predictable window placement and keyboard-driven restoration
- Shared-desk and multi-monitor users whose display topology changes often

## Concrete use cases

1. Capture a **Desk — Coding** profile with an IDE on the main display, browser on the right, and terminal below it.
2. Undock, work on the laptop, then reconnect and preview a restore before applying it.
3. Create a **Presentation** profile that places slides on the projector and notes on the laptop.
4. Restore only the applications that are already open, or explicitly opt in to launching missing applications.
5. Export a profile for backup, inspect it as JSON, and import it on another machine without an account.

## Intended workflow

1. Install and launch Window Recall.
2. On macOS, grant Accessibility permission when the OS prompts; Windows requires no administrator access.
3. Arrange application windows and select **Capture layout**.
4. Name the profile and review included windows. Sensitive windows can be excluded before saving.
5. Later, select a profile. Window Recall compares saved and current monitors, matches windows, and displays a dry-run plan.
6. Apply all safe matches or choose individual windows. Missing, ambiguous, minimized, and off-screen cases remain visible in the result.
7. Export or back up profiles as versioned JSON whenever desired.

## MVP features

- Capture visible top-level application windows, placement state, and monitor identity
- Named, editable profiles with per-window include/exclude controls
- Deterministic app/window matching with clear confidence and ambiguity reporting
- Monitor-topology mapping using normalized coordinates and safe on-screen bounds
- Restore preview, selective apply, cancellation, and an undo snapshot for the most recent restore
- Optional, explicit launch of missing applications from a stored executable or bundle identity
- Keyboard-accessible Avalonia UI plus a headless CLI for capture, plan, apply, list, import, and export
- Local versioned JSON storage and portable profile import/export
- Windows 10/11 and macOS support behind platform-specific adapters

## Non-goals

- Tiling-window-manager replacement or continuous enforcement of window positions
- Controlling virtual desktop/Space assignment in the first release
- Restoring document contents, browser tabs, application state, or unsaved work
- Screen recording, screenshots, keystroke logging, or content inspection
- Cloud synchronization, user accounts, team administration, or telemetry by default
- Moving privileged, system-owned, full-screen, or otherwise OS-restricted windows by force

## Privacy, permissions, and storage

Window Recall is offline by default and has no required service or account.

- **Stored data:** profile name, application identity, optional window-matching hints, geometry/state, monitor metadata, and user preferences.
- **Not stored by default:** screenshots, window contents, keyboard input, clipboard data, or document contents.
- **Window titles:** may be used as an optional local matching hint; users can disable title persistence or redact title patterns per profile.
- **Windows permissions:** standard desktop APIs; no administrator privilege is planned.
- **macOS permissions:** Accessibility is required to inspect and reposition other applications' windows. Screen Recording is not required because Window Recall does not capture pixels.
- **Location:** `%LOCALAPPDATA%/WindowRecall` on Windows and `~/Library/Application Support/WindowRecall` on macOS.
- **Export/backup:** versioned, human-readable JSON selected explicitly by the user.
- **Network:** none required. The MVP contains no AI or cloud integration.

## Accessibility expectations

- Complete keyboard navigation and visible focus indicators
- Screen-reader names, roles, state, and restore-result announcements
- No color-only status communication
- Support for OS text scaling and high-contrast themes
- Reduced-motion behavior and no timing-dependent interaction
- Confirmation and undo for actions that move multiple windows

## Architecture at a glance

```text
Avalonia UI / window-recall CLI
              |
        WindowRecall.Core
 profiles | matching | topology mapping | restore plans | undo
              |
      IWindowSystem adapter
       /                     \
Windows Win32             macOS AX/CG
```

See [PLAN.md](PLAN.md) for boundaries, milestones, testing, packaging, and risks.

## Milestones

1. Core domain model, profile schema, and CI
2. Windows capture/restore adapter
3. macOS capture/restore adapter and permission onboarding
4. Safe matching, topology mapping, preview, and undo
5. Accessible UI and CLI
6. Import/export, packaging, and release validation

Progress is tracked in [GitHub Issues](https://github.com/rwrife/window-recall/issues).

## Development quickstart

Install the .NET 8 SDK pinned by `global.json`, then:

```bash
git clone https://github.com/rwrife/window-recall.git
cd window-recall
dotnet restore
dotnet format --verify-no-changes
dotnet build --configuration Release
dotnet test --configuration Release
```

Run the development shell with `dotnet run --project src/WindowRecall.App` or the headless CLI with `dotnet run --project src/WindowRecall.Cli -- doctor`. Profiles use the documented [schema v1](docs/profile-schema.md), remain local JSON, and omit raw window titles unless explicitly opted in; see [Privacy](docs/privacy.md) for exactly what is stored where and [CLI contract](docs/cli.md) for commands, exit codes, and `--json` output.

Platform adapter tests use injected fake native APIs and are not proof that real windows were observed or moved. See [Windows adapter verification](docs/windows-adapter-verification.md) and [macOS adapter verification](docs/macos-adapter-verification.md) for filtering rules, permission behavior, live harness instructions, and clearly separated mock/build/live evidence.

See [Restore engine guarantees](docs/restore-engine.md) for the matching evidence tiers, topology score inputs, normalized clamping invariant, preview selection rules, and undo limitations.

## License

Window Recall is licensed under the MIT License; see [LICENSE](LICENSE).
