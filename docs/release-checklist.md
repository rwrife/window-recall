# Release checklist and verification status

This page is the single honest release gate for Window Recall. Every row is labeled
**automated**, **manual**, **unavailable**, or **untested** on the date given. Mock or
deterministic-Core evidence is never described as live desktop behavior.

Last updated: 2026-09-12 (issue #7 release-prep slice).

## Scenario coverage matrix

Scenario list from issue #7. "Automated (Core)" means a deterministic xUnit test in
`tests/WindowRecall.Core.Tests/RestoreScenarioTests.cs` or the adapter fake-native suites —
real algorithm coverage, not live OS coverage.

| Scenario | Automated (Core/mock) | Live Windows | Live macOS |
|---|---|---|---|
| Laptop-only desktop | `UndockFromExtendedToLaptopOnly…`, `ClampAlwaysLeavesARecoverableRegion…` | unavailable | unavailable |
| External-only desktop (negative origin) | `LaptopOnlyToExternalOnly…` | unavailable | unavailable |
| Extended (two-display) desktop | `DisplayTopologyMapperTests.RelativeTopologyMaps…` | unavailable | unavailable |
| Display removal | `UndockFromExtendedToLaptopOnly…`, `MissingDisplayFallsBackToCurrentPrimaryWithReason` | unavailable | unavailable |
| Negative coordinates | `LaptopOnlyToExternalOnly…`, `NormalizedConversionHandles…` | unavailable | unavailable |
| DPI/scale mismatch | `Capture_MixedDpiCoordinatesScaleRelativeToMonitorOrigin` (mock Win32), `ScaleMismatchAndRotatedTarget…` | unavailable | unavailable |
| Display rotation | `ScaleMismatchAndRotatedTarget…` (Portrait/PortraitFlipped mapping) | unavailable (rotation harness not built) | unavailable |
| Minimized/maximized | `MinimizedAndMaximizedStates…` (Core), `Apply_StateOnlyNormalTarget…`, `Apply_StatefulWindow…` (mock Win32) | unavailable | unavailable |
| Multi-window same app | `SameAppMultipleWindowsStayAmbiguous…` (never auto-applied), both live fixture harnesses open 3 windows | opt-in harness defined, **not yet run** | opt-in harness defined, **not yet run** |
| Rejected move | `RejectedMoveProducesPartialReceipt…` (Core), `Apply_BoundsChange…`/error-mapper suites (mock native) | unavailable | unavailable |
| Partial failure + undo retention | `RejectedMoveProducesPartialReceipt…`, `AdapterFailuresDoNotPreventLaterItems…` (Core) | unavailable | unavailable |
| Machine-readable evidence without titles | macOS evidence JSON schema excludes titles by construction (`Fixture_CaptureMoveRestore_RecordsRedactedMachineReadableEvidence`) | n/a | harness ready, **not yet run** |

## Machine-readable results

- macOS live harness writes redacted JSON (`WINDOW_RECALL_MACOS_EVIDENCE`): opaque ids, requested
  and observed geometry, outcome codes; **no window titles** are serialized.
- Windows live harness (opt-in `WINDOW_RECALL_RUN_WINDOWS_INTEGRATION=1`) asserts re-observed
  geometry; to keep parity with the macOS evidence rule, run it with xUnit XML output
  (`--logger "trx;LogFileName=windows-live.trx"`) which records only test names and results, never
  titles. Archive the `.trx` next to this document's evidence folder when recorded.
- Deterministic CI runs (`ci.yml`, three OSes) execute only mock/fake-native tests, so their logs
  cannot contain window titles by construction — no live capture ever runs in CI. To archive them
  as machine-readable evidence, run `dotnet test --logger "trx;LogFileName=results.trx"` and store
  the `.trx` files with the release tag.

## Packaging status (2026-09-12)

| Item | Status |
|---|---|
| Portable CLI ZIP (win-x64 / osx-x64 / osx-arm64, framework-dependent) | **automated**: `scripts/package.sh` builds all three on the Linux dev host; SHA256SUMS + `artifact-manifest.json` produced (see build evidence) |
| Portable app ZIP (same RIDs) | **automated** as above |
| macOS `.app` bundle wrapper (unsigned) | **automated** structurally on the Linux host; **untested** on real macOS (Launch Services, Gatekeeper, arm64/x64 launch) |
| macOS DMG | **untested / not produced** — `hdiutil` exists only on macOS; CI tag runs can add this once a maintainer validates the flow on a mac |
| Windows MSIX | **not produced** — per issue #7, MSIX only if capabilities and upgrade behavior are validated; neither is validated yet, so it is honestly omitted |
| Authenticode signing | **unavailable** — no certificate; manifests say "not performed" |
| Apple codesign + notarization | **unavailable** — no identity/secrets; optional hook in `scripts/package.sh` stays a no-op and manifests disclose `unsigned` |
| Release workflow | **automated**: `.github/workflows/release.yml` packages per-RID on tag push, uploads artifacts + `SHA256SUMS`, and creates a **draft** GitHub release titled *unsigned release candidate* for manual inspection before publish |
| Clean install / launch / uninstall on Windows | **manual, not yet performed** — needs a real Windows desktop session |
| Clean install / launch / quarantine-dialog / uninstall on macOS | **manual, not yet performed** |
| Local data path verification (`doctor` output paths) | **automated** (Linux, verified) + CLI unit tests; Windows/macOS defaults **untested** on live OSes |
| Upgrade/profile compatibility (read profiles written by older builds) | **automated** for schema v1 fixtures + migration/future-version rejection; binary-upgrade of an installed app **manual, not performed** |
| Import/export round trip | **automated** (CLI tests, conflict modes) |
| Offline operation | **automated** audit: no network APIs exist in `src/` (grep evidence below); runtime no-network soak **manual, not performed** |
| No unexpected Screen Recording / admin permission prompts | **untested** on live OSes (macOS Accessibility prompt path exists deliberately; Screen Recording is never requested in code — `CGRequestScreenCaptureAccess` is not called anywhere) |
| Narrator release checklist | see next page — **not yet executed** on a Windows machine |
| VoiceOver release checklist | see next page — **not yet executed** on a Mac |

## Offline/no-network evidence (automated, 2026-09-12)

`grep -rn "HttpClient\|Sockets\|NetworkInformation\|Dns\|WebClient" src/ --include=*.cs` returns no
product code hits (only `new Uri(path).AbsoluteUri` for a file URL inside the macOS adapter).
No cloud endpoints, telemetry, or account flows exist. This is static evidence; a packet-capture
soak on a firewalled machine remains a manual release-gate item.

## What must happen before this can be called a verified release

1. Run both opt-in live desktop harnesses on real interactive machines and archive the machine-readable
   outputs (see `docs/windows-adapter-verification.md` and `docs/macos-adapter-verification.md`).
2. Execute the manual install/launch/uninstall and permission matrices in
   `docs/accessibility-release-checklists.md` on both OSes.
3. Decide whether signing/notarization secrets will be provisioned; until then every artifact is
   labeled *unsigned release candidate*.
4. Only after 1–2 are complete may README status stop saying live evidence is pending.

## Recorded packaging evidence — 2026-09-12 (issue #7 slice)

Host: Ubuntu arm64 (non-Windows, non-macOS), .NET SDK 8.0.424 from `$HOME/.dotnet`.

- `dotnet restore --locked-mode`: exit 0 (run before and after packaging; committed lock files verified unmodified).
- `dotnet format --verify-no-changes --no-restore`: exit 0, no output.
- `dotnet build --configuration Release --no-restore`: 0 warnings, 0 errors.
- `dotnet test --configuration Release --no-build`: Core 91/91, CLI 27/27, Windows mock 42/42 (+1 skipped live harness), macOS seam 34/34 (+1 skipped live harness), App headless 23/23. The new `RestoreScenarioTests` (6) and `VersionCommand_ReportsAssemblyProductVersion` (1) are included; both opt-in live desktop tests SKIPPED on this host, which is not integration evidence.
- `bash scripts/package.sh 0.1.0` (twice, independent): exit 0 each; produced 8 ZIPs + `SHA256SUMS` + `artifact-manifest.json` + `build-info.json`; all ZIP hashes identical across the two runs (reproducible). Example: `WindowRecall-Cli-0.1.0-win-x64.zip` = `fdb36909…`, `WindowRecall-App-0.1.0-osx-arm64.zip` = `0ef6722a…` (full list in `artifacts/SHA256SUMS`, regenerated per build).
- ZIP contents inspected: CLI/app archives contain `LICENSE`, `README.md`, `THIRD-PARTY-NOTICES.md`, deps.json, and RID apphost; the macOS `.app` archive contains the wrapped bundle with generated `Info.plist` (version-stamped). All are **unsigned** and labeled as such.
- `dotnet WindowRecall.Cli.dll --version` on Linux: `window-recall cli 0.1.0` (from assembly version).
- `window-recall doctor --json` on Linux with a temporary data root: `status=success exitCode=0`, echoes the resolved `dataRoot`.
- One-off note: an early `dotnet publish` run segfaulted (exit 139) under the arm64 SDK before any artifact was written; the rerun completed cleanly. Recorded for honesty; not reproduced.

This host could not execute Win32, AppKit, Launch Services, Gatekeeper, Narrator, or VoiceOver.
No live desktop, install/uninstall, or screen-reader verification was performed here; the matrix
above marks those rows accordingly.
