# Restore engine guarantees

The restore engine lives in `WindowRecall.Core`. It consumes platform-neutral snapshots and emits immutable previews; native handles remain private to platform adapters. Planning never changes the desktop.

## Deterministic matching

Candidates require an exact, case-insensitive application id. Within that application they are ranked by these documented evidence tiers:

1. **Exact session identity** — the opaque session id agrees during the current adapter session, and the application identity also agrees.
2. **Stable application identity** — application id, with executable/bundle/package path as an additional stable hint.
3. **Application and structure** — application identity plus window role.
4. **Title hint** — a normalized title adds evidence only after application identity matches. A title never creates a candidate by itself.

Executable identity, role, and optional title add fixed scores. Candidate ordering is score descending, then opaque current id ordinal; saved windows are resolved in saved-id ordinal order. Current windows are assigned at most once. Equal best scores are returned as ranked candidates with explicit `Ambiguous` status, no selected current id, and no automatic move.

## Display mapping and bounds

Mapping first rewards stable platform id and display name. It then compares work-area size/aspect, scale, orientation, primary status, and the display center's position relative to the primary display. Pair selection is deterministic and one-to-one. When saved displays outnumber attached displays, each unpaired display maps to the current primary with an explicit `PrimaryFallback` reason.

Window bounds are stored in logical desktop coordinates. Planning converts each edge proportionally from the saved work area to the mapped current work area, which handles negative origins, rotations, DPI/scale changes, and taskbar/dock work-area reductions. Width and height cannot exceed the target work area. Clamping leaves at least 64 logical horizontal units and a 32-unit top recovery region reachable on-screen (or the whole window/work area when smaller).

## Preview, apply, and undo

Preview items are sorted by saved window id and carry a reason, action, target, state, and inclusion flag. Deterministic existing-window operations start included. Ambiguous and invalid items are excluded. Missing applications are reasoned skips unless the profile permits an explicit launch; launch items are still excluded by default and `WithSelection(..., includeLaunches: true)` is required to select them. Selection returns a new plan and never mutates the original.

The coordinator captures the desktop immediately before any mutation and calls the adapter once per selected item. Cancellation is observed between items; already-finished results are retained and later items receive `Cancelled`. Adapter failures are recorded without discarding other outcomes. The receipt contains the actual pre-apply snapshot plus every per-window result.

Only the newest receipt is eligible for undo, and attempting it consumes that one step. Undo restores the pre-apply normal bounds and state for every item that may have mutated, continues after individual failures, and returns per-window best-effort outcomes. It cannot recreate vanished windows or guarantee that an application accepts a requested placement.
