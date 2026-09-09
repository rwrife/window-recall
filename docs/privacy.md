# Privacy

Window Recall is local-first. There is no account, no telemetry, no cloud sync, and no network code in
the MVP. The GUI and CLI share this behavior.

## What is stored and where

- Profiles are human-readable JSON (schema v1, see `profile-schema.md`) under the local data root:
  - Windows: `%LOCALAPPDATA%\window-recall\profiles`
  - Linux (verified): `~/.local/share/window-recall/profiles` via `Environment.SpecialFolder.LocalApplicationData` (XDG state home)
  - macOS: the directory resolved by `Environment.SpecialFolder.LocalApplicationData` on that host;
    not yet verified on real macOS hardware (see open verification gaps)
  - See `docs/cli.md` for the CLI override precedence and the exact layout.
- A single persisted undo receipt (`undo/latest-undo.json`) exists only after an explicit
  `apply --execute`. It stores pre-apply geometry and per-window outcome codes. **Raw window titles are
  always stripped from the receipt before it is written.** It is replaced by the next apply and archived
  immediately when consumed by `undo`.

Nothing else is persisted. No screen pixels are captured. There is no keyboard logging, no history of
window titles over time, and no analytics.

## Raw window titles are opt-in

- By default `privacy.persistWindowTitles` is `false`, and the serializer removes every `title` value
  before a profile is written, regardless of what capture observed.
- Matching works without titles: application identity, executable/bundle identity, and window role are
  the primary evidence; titles are only an optional weak hint.
- When a profile opts in (`"persistWindowTitles": true`), every stored title is still passed through the
  profile's `redactionPatterns` first. Patterns are validated regular expressions (bounded count,
  length, and match timeout). A pattern that behaves pathologically fails closed: the whole title is
  replaced with `[redacted]` rather than persisted raw.
- Inspect exactly what a profile stores with `window-recall profiles inspect <id> --json`; it reports
  counts and unsafe executable identities and never echoes stored titles.

## Application identities are identities, not commands

Stored `executablePath` values are used only for identity matching. Window Recall never launches through
a shell string: `import` and `capture` reject paths containing shell control syntax, home expansion, or
relative/command-line shapes. Explicit launch (when a profile sets `allowExplicitLaunch`) targets the
stored absolute executable/bundle identity and is previewed before it can be applied.

## Permissions

- Windows: no administrator rights are required or requested.
- macOS: window capture/restore requires Accessibility permission. The CLI surfaces this as a
  `permissionMissing` result (exit 4); capture never happens without it and nothing is partially moved.

## Deletion and backup

- Delete a profile: `rm` the single JSON file, or `IProfileStore.DeleteAsync` / GUI delete. Uninstalling
  leaves the data root untouched so deletion is always a deliberate user choice.
- Backup: copy the `profiles` directory, or use `export`/`import` for validated, conflict-safe moves.
- No profile, receipt, or log is uploaded anywhere; `doctor` reports `networkUsage: "none"`.
