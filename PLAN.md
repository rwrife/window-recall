# Window Recall — Delivery Plan

## 1. Scope

Window Recall captures and restores named layouts of ordinary user-owned desktop application windows. The MVP supports Windows 10/11 and current supported macOS releases, works without a network account, and requires users to preview a restore before moving windows.

A layout profile describes intent, not a raw replay script. Saved bounds are normalized against their monitor's usable work area. At restore time the engine maps saved monitors to the current display topology, matches saved entries to current windows, clamps results on-screen, and emits an explicit plan. Platform adapters execute only approved plan items.

## 2. Proposed source structure

```text
src/
  WindowRecall.Core/          # domain, schema, matching, topology, restore planning
  WindowRecall.Platform.Windows/ # Win32 enumeration and placement adapter
  WindowRecall.Platform.MacOS/   # CoreGraphics/Accessibility adapter
  WindowRecall.App/           # Avalonia MVVM desktop shell
  WindowRecall.Cli/           # deterministic headless commands
 tests/
  WindowRecall.Core.Tests/
  WindowRecall.Windows.Tests/
  WindowRecall.MacOS.Tests/
 docs/
  profile-schema.md
  privacy.md
```

## 3. Architecture and boundaries

### Core domain

`WindowRecall.Core` remains UI- and OS-independent. Principal models:

- `DisplaySnapshot`: stable hints, bounds, work area, scale, orientation, and primary flag
- `WindowSnapshot`: app identity, optional redacted title hint, normal bounds, state, display hint, and launch policy
- `LayoutProfile`: version, name, capture metadata, displays, windows, and privacy options
- `CurrentDesktop`: currently observed displays and windows
- `MatchCandidate`: candidate current window plus evidence and confidence
- `RestorePlan`: immutable list of move, resize, state-change, launch, skip, and ambiguity items
- `RestoreReceipt`: actual outcomes and the pre-apply undo snapshot

Core services:

- `IProfileStore` for atomic versioned JSON persistence
- `IWindowMatcher` for deterministic application/window matching
- `IDisplayMapper` for saved-to-current topology mapping
- `IRestorePlanner` for clamping, conflict detection, preview, and selective apply
- `IWindowSystem` for capture, optional launch, and execution

### Platform adapters

**Windows:** P/Invoke wrappers around `EnumWindows`, process metadata, monitor APIs, DPI-aware coordinates, `GetWindowPlacement`, `ShowWindowAsync`, and `SetWindowPos`. Tool windows, cloaked windows, protected processes, and non-user windows are filtered conservatively.

**macOS:** CoreGraphics provides display/window observations; authorized Accessibility (`AXUIElement`) provides state inspection and position/size operations. Bundle identifier and executable URL provide app identity. Permission state is surfaced through capabilities and a UI-facing onboarding contract; capture remains conservatively useful from CoreGraphics metadata without permission. Unsupported/full-screen/system/modal/hidden windows are reported with restore-skip reasons rather than coerced. Native window IDs and AX objects never leave the platform assembly; platform display IDs remain stable display-mapping hints.

### UI and CLI

Avalonia MVVM presents profiles, captured windows, permissions, restore previews, ambiguity resolution, and outcomes. The CLI consumes the same Core services and supports stable JSON output for automation. Neither layer calls native APIs directly.

## 4. Technology choices

- **.NET 8 / C#:** mature interop, shared code across both target operating systems, and straightforward self-contained packaging.
- **Avalonia 11:** one accessible desktop UI while retaining native platform adapters; avoids duplicating WPF and AppKit screens.
- **System.Text.Json:** human-readable, versioned, dependency-light profile format.
- **xUnit + property-based tests:** deterministic unit, schema migration, geometry, and matching coverage.
- **Native OS APIs through narrow wrappers:** avoids screen scraping and heavyweight automation frameworks.
- **GitHub Actions:** Linux for Core, Windows and macOS runners for compilation and OS-specific tests.

No database or network service is needed for the MVP.

## 5. Profile and restore safety

- Writes use temporary-file + atomic-replace semantics.
- Every schema has a version and migration tests; unknown future versions fail read-only with a useful message.
- Raw titles are opt-in. Default matching prioritizes executable path/package identity, process identity, window role, and explicit user choice.
- Restore never places a complete window outside the current usable work area.
- Ambiguous matches are never applied automatically.
- Launching missing apps is disabled by default and constrained to the explicitly stored executable/bundle identity.
- The pre-restore desktop is captured as a one-step undo snapshot before any move.
- Partial failures produce per-window outcomes and retain undo for successful moves.

## 6. Milestones and dependency order

### M1 — Skeleton, schema, and CI

Create the solution, project boundaries, profile v1 schema, atomic store, test fixtures, formatting/analyzer rules, and three-OS CI. This unlocks all later work.

### M2 — Windows adapter vertical slice

Capture ordinary top-level windows and monitor metadata, generate a one-monitor restore plan, apply it, and record outcomes. Verify DPI and minimized/maximized handling on a real Windows runner or documented manual harness.

### M3 — macOS adapter vertical slice

Implement observations and authorized AX moves, plus permission detection/onboarding. Keep native handles and AX objects inside adapter boundaries.

### M4 — Matching and topology engine

Add deterministic identity matching, ambiguity handling, normalized multi-monitor mapping, work-area clamping, missing-monitor fallback, selective apply, cancellation, and undo.

### M5 — Accessible app and CLI

Deliver capture/edit/preview/apply/outcome screens, permission status, keyboard and screen-reader semantics, and CLI commands sharing the same services.

### M6 — Portability and release

Add import/export, schema migration fixtures, privacy controls, Windows/macOS packaging, signed-artifact documentation, release notes, and end-to-end validation checklists.

## 7. Testing strategy

### Deterministic automated tests

- Profile round-trip, atomic-write failure, migration, invalid/future schema, and redaction rules
- Geometry normalization across scale factors, rotations, negative coordinates, missing displays, and changed work areas
- Matcher precedence, multiple windows from one app, title-disabled mode, ambiguity, and stale executable identities
- Planner invariants: no off-screen result, no automatic ambiguous move, stable ordering, cancellation, selective apply, and undo creation
- Adapter wrapper tests using injected native-call seams
- View-model keyboard actions, validation, and accessible status text
- CLI snapshots and documented exit codes

### Platform integration tests

Purpose-built fixture apps expose multiple windows with known roles. On Windows and macOS, tests capture, move, restore, and compare tolerant geometry. OS permissions and restrictions are recorded separately. CI static/mock checks are not presented as real desktop integration evidence.

### Manual release checks

- Laptop-only, external-only, mirrored, extended, dock/undock, DPI mismatch, display rotation, and display removal
- Minimized, maximized, full-screen, hidden, privileged, modal, and multi-window apps
- VoiceOver and Narrator keyboard workflows, text scaling, high contrast, and reduced motion
- Clean install, upgrade, profile backup/restore, uninstall, and offline operation

## 8. Packaging and distribution

- Windows: self-contained `win-x64` portable ZIP first; MSIX after capabilities and upgrade behavior are validated.
- macOS: universal or per-architecture `.app` bundle and notarization-ready DMG workflow. Accessibility permission instructions must identify the exact signed bundle.
- CI publishes checksums and a manifest. Signing and notarization use repository secrets only when configured; unsigned development artifacts are labeled clearly.
- No auto-updater in the MVP. Releases are manually downloaded from GitHub.

## 9. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Window identity is unstable across launches | Layered deterministic matching, explicit ambiguity, user overrides, and fixtures for multi-window apps |
| Apps reject or immediately override moves | Per-window result reporting; never retry indefinitely or claim success without observed geometry |
| DPI/coordinate differences place windows incorrectly | DPI-aware native calls, normalized work-area coordinates, clamping invariants, and real multi-display tests |
| macOS Accessibility permission is confusing | Permission status, OS settings deep-link where supported, clear explanation, and graceful read-only behavior |
| Display IDs change after docking | Combine platform hints with bounds/scale/orientation and score mappings rather than relying on one identifier |
| Titles can expose sensitive information | Disable title persistence by default, pattern redaction, inspectable JSON, and no telemetry/network |
| Launch paths become unsafe or stale | Launch off by default, validate identity, no shell command strings, and require preview |
| OS APIs evolve | Narrow platform seams, capability reporting, platform CI, and documented minimum versions |

## 10. Explicit non-goals

- Restoring application-internal documents, browser tabs, tabs in terminals, or unsaved state
- Creating or assigning Windows virtual desktops or macOS Spaces in MVP
- A continuously running tiling policy engine
- Pixel capture, OCR, content understanding, AI matching, or usage analytics
- Enterprise policy, remote administration, team sharing, or cloud sync
- Bypassing OS permissions or manipulating privileged/system windows

## 11. Definition of an MVP release

The first release requires green Core, Windows, and macOS build/test jobs; recorded platform integration results; a keyboard-complete restore workflow; profile import/export; accurate privacy and permission documentation; reproducible packages; checksums; and a release checklist that distinguishes automated, manual, and untested scenarios.
