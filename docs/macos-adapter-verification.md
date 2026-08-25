# macOS adapter verification

## What is implemented

The adapter reads display and on-screen window metadata through CoreGraphics. When macOS Accessibility trust is granted, it enriches observations and changes supported AX window position, size, and minimized state. It never requests Screen Recording, reads pixels, executes shell commands, or sends data over a network.

Application identity is the running application's bundle identifier plus its executable file URL, converted to an absolute path at the Core boundary. Native window IDs and `AXUIElement` references remain internal to `WindowRecall.Platform.MacOS`; Core sees only random opaque session IDs. Apply accepts only IDs from the last complete capture and revalidates native ID, owner PID, bundle identifier, and executable URL before mutation. A cancelled capture does not replace the prior complete session.

Capture reports identified layer-zero application windows even when they are full-screen, system-owned, modal, hidden/off-screen, or missing settable AX position/size attributes. Their platform-neutral `Role` contains an explicit `restore-skip=` reason so later preview work can display the limitation, and apply returns a reasoned per-window `Skipped` outcome without mutation. Apply also validates untrusted plans and approvals, preserves AX permission/stale/rejected/unsupported classifications and partial-apply detail, and performs bounded tolerant re-observation before reporting success.

Without Accessibility permission, `GetCapabilitiesAsync` reports read-only CoreGraphics observation and disables mutation/state capabilities. `MacOSAccessibilityOnboardingService` explains why Accessibility is needed, states that Screen Recording is not needed, and attempts to open the Accessibility System Settings pane through `NSWorkspace`.

## Deterministic evidence

`WindowRecall.MacOS.Tests` uses an injected fake native API for permission, filtering, identity, error mapping, conservative skip, and post-mutation verification. These are static/mock tests, not evidence of macOS desktop interaction.

On the Linux-arm64 implementation host for issue #3, .NET SDK 8.0.424 provided static restore, formatting, compilation, and deterministic test evidence. This does not execute macOS frameworks or verify live Accessibility behavior.

## Opt-in live evidence

Build the fixture on the target architecture and wrap the publish output in the provided app-bundle metadata (example for Apple Silicon):

```bash
dotnet publish fixtures/WindowRecall.MacOSFixture -c Release -r osx-arm64 --self-contained false -o /tmp/window-recall-macos-fixture
APP=/tmp/WindowRecall.MacOSFixture.app
mkdir -p "$APP/Contents/MacOS"
cp fixtures/WindowRecall.MacOSFixture/Info.plist "$APP/Contents/Info.plist"
cp -R /tmp/window-recall-macos-fixture/. "$APP/Contents/MacOS/"
```

Use `osx-x64` on Intel macOS. The bundle identifier lets the harness select only fixture windows. Grant Accessibility permission to the actual `dotnet`/test-host executable that runs the harness, then set:

```text
WINDOW_RECALL_RUN_MACOS_INTEGRATION=1
WINDOW_RECALL_MACOS_FIXTURE=/absolute/path/to/WindowRecall.MacOSFixture.app/Contents/MacOS/WindowRecall.MacOSFixture
WINDOW_RECALL_MACOS_EVIDENCE=/absolute/path/to/macos-live-evidence.json
```

Run the macOS test project interactively. The live test is skipped unless the OS, interactivity, explicit opt-in, paths, and permission are all present. Its JSON records capture count, opaque IDs, requested geometry, observed before/after geometry, state, and move/restore outcome codes. It does not record raw titles.

No live macOS capture, move, or restore was performed on the Linux implementation host. The live AX verification gap must be closed on an interactive macOS machine before this adapter is described as platform-verified.
