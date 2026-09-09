# CLI contract

`window-recall` (the `WindowRecall.Cli` executable) automates the same Core services the GUI uses. It
never talks to native window APIs directly; capture and apply go through the current OS's `IWindowSystem`
adapter (Win32 on Windows, Accessibility/CoreGraphics on macOS, and a capability-less fallback elsewhere).

## Local storage

Precedence for the data root: `--data-root <path>`, then the `WINDOW_RECALL_DATA_ROOT` environment
variable, then the per-OS default:

- Windows: `%LOCALAPPDATA%\window-recall`
- Linux (verified): `$XDG_STATE_HOME/window-recall` falling back to `~/.local/share/window-recall`
- macOS: the per-user local application-data directory resolved by .NET on macOS
  (`Environment.SpecialFolder.LocalApplicationData`); exact location to be confirmed during macOS packaging

Layout: `profiles/<profile-id>.json` for profiles and `undo/latest-undo.json` for the single persisted
undo receipt (archived to `undo/consumed-<receipt-id>.json` when consumed). The data root is a plain
directory path; tilde is not expanded.

## Commands

```text
capture --name <name> [--profile-id <id>] [--persist-titles] [--redact <regex>]... [--force]
profiles list | profiles inspect <profile-id>
plan <profile-id>
apply <profile-id> [--execute] [--select <saved-window-id>]... [--launch-missing]
undo
export <profile-id> [--out <file> | --stdout] [--force]
import <file> [--profile-id <id>] [--on-conflict fail|rename|overwrite]
doctor
```

- `apply` is **dry-run by default**: without `--execute` it only prints the plan. Ambiguous and unmatched
  items are never applied; `--select` can only choose deterministic matched items, and launch items
  additionally require `--launch-missing`.
- A successful `--execute` stores the undo receipt; `undo` consumes it exactly once and archives it.
- `export` writes the same validated JSON schema as the stored profile (titles redacted/removed per the
  profile's privacy settings). It refuses to overwrite without `--force`.
- `import` validates the schema first (rejecting future versions and malformed documents) and never
  overwrites silently: `--on-conflict` is `fail` by default.
- `profiles inspect` reports what a profile actually stores (counts only — never raw titles) and flags
  executable identities that would require shell-string execution.
- `doctor` audits storage paths, profile health, the stored undo receipt, and adapter capabilities.

## `--json` output and exit codes

With `--json`, stdout is a single object `{command, status, exitCode, message, data}`. Exit codes and
status strings are stable machine contract:

| Exit | Status              | Meaning                                                    |
| ---- | ------------------- | ---------------------------------------------------------- |
| 0    | `success`           | Completed as requested (including dry-run `planned` below) |
| 0    | `planned`           | Dry-run preview produced; nothing was executed             |
| 2    | `validationError`   | Bad arguments, unknown/malformed/future schema, unsafe import, missing undo receipt, doctor issues |
| 3    | `noMatch`           | Nothing in the profile can be applied to the current desktop |
| 4    | `permissionMissing` | No desktop capture/apply capability (no desktop session, or macOS Accessibility not granted) |
| 5    | `partial`           | Per-window outcomes include failures; undo receipt is retained |
| 6    | `conflict`          | Capture/export target exists without `--force`, or import conflict with `--on-conflict fail` |

## Offline operation

The CLI performs no network access in any code path. Import, export, capture, plan, apply, undo, and
doctor read and write only the local data root.
