## Window Recall <version> — unsigned release candidate

Local-first window layout capture/restore for Windows 10/11 and macOS. No accounts, no network, no telemetry.

**These artifacts are NOT code-signed or notarized.** Expect SmartScreen/Gatekeeper warnings;
verify `SHA256SUMS` before use. See `docs/release-checklist.md` for exactly what is and is not
verified at this tag.

Portable ZIPs are framework-dependent: install the .NET 8 Desktop Runtime for your platform first.

- `WindowRecall-Cli-<ver>-win-x64.zip` / `-osx-x64.zip` / `-osx-arm64.zip` — headless CLI
- `WindowRecall-App-<ver>-<rid>.zip` — Avalonia desktop app (Windows/macOS)
- `WindowRecall-<ver>-macos-<rid>-app.zip` — unsigned `.app` bundle (macOS)
- `SHA256SUMS` — checksums for every ZIP above

Run `window-recall doctor` after first launch to verify local storage and adapter capabilities.
