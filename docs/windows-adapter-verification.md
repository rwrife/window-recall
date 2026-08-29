# Windows adapter and fixture verification

## Adapter behavior

`WindowsWindowSystem` keeps native handles in a per-capture session map and exposes only opaque random window IDs to Core. All Win32 access is behind `IWindowsNativeApi`; deterministic tests inject a fake implementation. The production `Win32NativeApi` is safe to load on every supported CI OS and returns an unsupported capability outside Windows.

Capture includes a top-level window only when all of these conservative rules hold:

- `IsWindowVisible` is true;
- `DWMWA_CLOAKED` is false;
- `WS_EX_TOOLWINDOW` is absent;
- `GetWindow(..., GW_OWNER)` reports no owner;
- it is not the `GetShellWindow` handle;
- the process ID, executable identity, monitor, DPI, and positive normal placement can be read.

An inaccessible/protected process is therefore omitted from capture rather than partially trusted. Monitor DPI is obtained with the Windows 10/11 `GetDpiForWindow` API from a visible per-monitor-aware window assigned to that monitor while the adapter uses a scoped per-monitor-aware thread context. It does not call `GetDpiForMonitor` from that context and never substitutes 96 DPI after a query, DLL, or context failure. A monitor with no window from which reliable DPI can be obtained is explicitly omitted, and apply reports `Unsupported` when the required monitor DPI is unavailable. `GetWindowPlacement.rcNormalPosition` is workspace-relative, so the adapter first converts it to screen coordinates using the owning monitor's work-area/bounds offset; placement state remains separate from `showCmd`. Physical Win32 rectangles are converted to 96-DPI logical coordinates by preserving each monitor's global origin and scaling offsets and sizes relative to that origin. This avoids independently scaling global origins on mixed-DPI desktops.

Apply accepts only explicitly approved, unambiguous move/state plan items whose opaque current ID still resolves in the latest complete capture session. Capture maps are immutable and atomically replaced. Immediately before mutation, the adapter revalidates the captured process ID and application identity so a reused HWND cannot affect an unrelated window. Missing approvals are skipped; duplicate approvals and duplicate saved-window IDs in a malformed plan fail without a native call.

Topology planning supplies logical target bounds. On a multi-monitor desktop the adapter deterministically selects the valid monitor containing the target center (then the nearest monitor and stable native id as fallbacks), converts with that monitor's origin and DPI, and verifies in the same logical coordinate space. Opaque window ids are reused across captures only while the HWND, process id, and application identity all agree; this lets the coordinator take its required immediate pre-apply snapshot without making an approved plan stale.

A minimized/maximized window is restored before changing normal bounds, then its approved or preserved state is applied. Generic full-screen transitions are unsupported. Each requested mutation step is attempted at most once and later mutation steps stop after a native failure; no automatic recovery mutation is performed. If an earlier step succeeded, the failure outcome is explicitly marked `Partial` and identifies the state or bounds change already made. Verification performs at most three asynchronous observations, with a two-logical-pixel geometry tolerance, so delayed `ShowWindowAsync` effects may converge without an unbounded retry.

Cancellation is honored at item boundaries and checked again immediately before the first mutating call. Once mutation for an item starts, its bounded best-effort mutation and verification sequence finishes; cancellation short-circuits verification waits and prevents subsequent items from starting. Native access-denied, missing-handle, rejected, unsupported, and verification failures produce per-window outcomes.

## Opt-in live desktop harness

The Windows Forms fixture opens three ordinary named windows. The integration test launches it, captures all three, moves them, restores their original logical bounds/state, and requires successful re-observation.

On an interactive Windows 10/11 desktop with .NET 8 and one or more valid attached monitors:

```powershell
dotnet build fixtures/WindowRecall.WindowsFixture/WindowRecall.WindowsFixture.csproj -c Release
$env:WINDOW_RECALL_RUN_WINDOWS_INTEGRATION = "1"
$env:WINDOW_RECALL_FIXTURE_EXE = (Resolve-Path "fixtures/WindowRecall.WindowsFixture/bin/Release/net8.0-windows/WindowRecall.WindowsFixture.exe")
dotnet test tests/WindowRecall.Windows.Tests/WindowRecall.Windows.Tests.csproj -c Release --filter Category=WindowsDesktopIntegration
```

The test skips unless the OS is Windows, the process is interactive, and the opt-in variable is set. A skip is not integration evidence.

## Recorded evidence — 2026-08-24

Host: Ubuntu 24.04 arm64, non-Windows. The .NET SDK under `$HOME/.dotnet` was placed first on `PATH`; no global packages changed.

- `dotnet restore WindowRecall.sln --locked-mode`: all projects up to date.
- `dotnet format WindowRecall.sln --verify-no-changes --no-restore`: exited 0 with no output.
- `dotnet build WindowRecall.sln --configuration Release --no-restore`: succeeded with 0 warnings and 0 errors.
- `dotnet test WindowRecall.sln --configuration Release --no-build`: passed Core 31/31, macOS seam 1/1, and Windows 40/40; Windows desktop integration 1 skipped because this is not Windows.
- `dotnet restore fixtures/WindowRecall.WindowsFixture/WindowRecall.WindowsFixture.csproj --locked-mode` followed by `dotnet build fixtures/WindowRecall.WindowsFixture/WindowRecall.WindowsFixture.csproj --configuration Release --no-restore`: restore succeeded and the cross-target fixture build succeeded with 0 warnings and 0 errors.
- `git diff --check` and `git diff --cached --check`: exited 0 with no output.

The Windows unit tests are deterministic mock tests. This Linux run did not execute Win32, open fixture windows, or provide live Windows 10/11 integration evidence. Live desktop verification remains pending until the opt-in command above is run on an interactive Windows host.

## Recorded restore-engine evidence — 2026-08-29

Host: Linux arm64, non-Windows, .NET SDK 8.0.424 from `$HOME/.dotnet`.

- Locked restore: all projects up to date.
- Format verification: exited 0 with no changes.
- Release build: succeeded with 0 warnings and 0 errors.
- Release tests: Core 57/57, macOS seam 1/1, Windows mock tests 42/42; the opt-in Windows desktop integration test was skipped on this non-Windows host.
- `git diff --check`: exited 0 with no output.

These results verify Core algorithms and the injected Windows adapter contract, not live Win32 behavior.
