#!/usr/bin/env bash
# Reproducible portable-package builder for Window Recall release candidates.
#
# What this script can honestly produce:
#   * Portable ZIPs for the CLI and the Avalonia app on win-x64, osx-x64, and
#     osx-arm64 (framework-dependent .NET 8), plus LICENSE/README/THIRD-PARTY-NOTICES.
#   * SHA256SUMS for every artifact and a machine-readable artifact manifest
#     (build-info.json + artifact-manifest.json) that record the SDK, runtime,
#     commit, and per-file hashes.
#   * Unsigned macOS .app bundles wrapped around the app publish output.
#
# What this script never claims:
#   * It does not sign or notarize anything. If APPLE_SIGNING_IDENTITY is set,
#     a codesign step is attempted; any failure (or an unset identity) leaves the
#     bundle unsigned and every manifest states that explicitly. Windows signing
#     and MSIX are not implemented here because their upgrade behavior is
#     unvalidated (see docs/release-checklist.md).
#
# Usage:  scripts/package.sh [version]     (version defaults to 0.1.0)
# Output: artifacts/ under the repository root (git-ignored).
set -euo pipefail

VERSION="${1:-0.1.0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
fi
command -v zip >/dev/null 2>&1 || { echo "zip is required" >&2; exit 1; }

OUT="$ROOT/artifacts"
PUB="$OUT/publish"
STAGE="$OUT/stage"
rm -rf "$OUT"
mkdir -p "$PUB" "$STAGE"

SDK_VERSION="$(dotnet --version)"
COMMIT="$(git rev-parse HEAD)"
COMMIT_SHORT="$(git rev-parse --short HEAD)"
SOURCE_DATE_EPOCH="$(git show -s --format=%ct HEAD)"
# Zip entries are dated from SOURCE_DATE_EPOCH so identical inputs give byte-identical archives.

restore_locked() {
  dotnet restore --locked-mode
}

# RID-scoped publish rewrites packages.lock.json with RID-specific graphs. Snapshot them
# and restore on exit so packaging never leaves the working tree dirty.
LOCK_SNAPSHOT="$OUT/.lock-snapshot"
mkdir -p "$LOCK_SNAPSHOT"
( cd "$ROOT" && git ls-files '*packages.lock.json' | while read -r f; do
    mkdir -p "$LOCK_SNAPSHOT/$(dirname "$f")"; cp "$f" "$LOCK_SNAPSHOT/$f"; done )
trap 'cd "$ROOT" && git ls-files "*packages.lock.json" | while read -r f; do cp "$LOCK_SNAPSHOT/$f" "$f" 2>/dev/null || true; done' EXIT

publish() { # project rid target
  dotnet publish "$1" -c Release -r "$2" --self-contained false -o "$3" /p:Version="$VERSION"
}

copy_docs_into() { # publish-dir
  cp LICENSE README.md "$1/"
  if [ -f "$ROOT/docs/third-party-notices.md" ]; then
    cp "$ROOT/docs/third-party-notices.md" "$1/THIRD-PARTY-NOTICES.md"
  fi
}

restore_locked

for RID in win-x64 osx-x64 osx-arm64; do
  publish src/WindowRecall.Cli "$RID" "$PUB/cli-$RID"
  copy_docs_into "$PUB/cli-$RID"
  publish src/WindowRecall.App "$RID" "$PUB/app-$RID"
  copy_docs_into "$PUB/app-$RID"
done

# --- macOS .app wrappers (unsigned unless APPLE_SIGNING_IDENTITY is provided) ---
for RID in osx-x64 osx-arm64; do
  APP="$STAGE/Window Recall.app"
  rm -rf "$APP"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  sed -e "s/__VERSION__/$VERSION/g" "$ROOT/packaging/macos/Info.plist.in" > "$APP/Contents/Info.plist"
  cp -R "$PUB/app-$RID/." "$APP/Contents/MacOS/"
  SIGNING="unsigned"
  if [ -n "${APPLE_SIGNING_IDENTITY:-}" ]; then
    # Optional hook: only meaningful on macOS with a matching identity in the keychain.
    # Any failure here is non-fatal and stays disclosed as unsigned.
    if codesign --force --deep --options runtime --sign "$APPLE_SIGNING_IDENTITY" "$APP" 2>"$OUT/codesign-$RID.log"; then
      SIGNING="codesigned:$APPLE_SIGNING_IDENTITY"
    else
      echo "warning: codesign failed for $RID; the .app remains unsigned (see artifacts/codesign-$RID.log)" >&2
    fi
  fi
  printf '%s\n' "$SIGNING" > "$OUT/signing-$RID.txt"
  # Normalize entry timestamps for reproducibility, keep the apphost executable.
  find "$APP" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
  chmod +x "$APP/Contents/MacOS/WindowRecall.App" 2>/dev/null || true
  (cd "$STAGE" && find "Window Recall.app" -depth -print | LC_ALL=C sort | TZ=UTC zip -X -q "$OUT/WindowRecall-$VERSION-macos-$RID-app.zip" -@)
  rm -rf "$APP"
done

# --- Portable ZIPs (deterministic: sorted entries, fixed timestamps) ---
zip_dir() { # source-dir output-zip
  find "$1" -type f -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
  (cd "$1" && find . -type f -print | LC_ALL=C sort | TZ=UTC zip -X -q "$2" -@)
}

for RID in win-x64 osx-x64 osx-arm64; do
  zip_dir "$PUB/cli-$RID" "$OUT/WindowRecall-Cli-$VERSION-$RID.zip"
  zip_dir "$PUB/app-$RID" "$OUT/WindowRecall-App-$VERSION-$RID.zip"
done

# --- Checksums and manifests ---
(cd "$OUT" && sha256sum ./*.zip > SHA256SUMS)

python3 - "$VERSION" "$SDK_VERSION" "$COMMIT" "$COMMIT_SHORT" "$OUT" <<'PY'
import hashlib, json, os, platform, sys, datetime

version, sdk, commit, short, out = sys.argv[1:6]
zips = sorted(f for f in os.listdir(out) if f.endswith(".zip"))
manifest = {
    "schemaVersion": 1,
    "product": "window-recall",
    "version": version,
    "producedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
    "toolchain": {
        "dotnetSdk": sdk,
        "buildHost": platform.platform(),
        "commit": commit,
        "packaging": "scripts/package.sh",
    },
    "frameworkDependent": True,
    "prerequisite": ".NET 8 Desktop Runtime for the matching RID (portable ZIPs); MSIX, signing, and notarization are NOT included",
    "signing": {
        "windowsAuthenticode": "not performed",
        "macosCodesign": "not performed (optional APPLE_SIGNING_IDENTITY hook; see signing-*.txt)",
        "notarization": "not performed",
    },
    "artifacts": [],
}
for name in zips:
    path = os.path.join(out, name)
    with open(path, "rb") as handle:
        digest = hashlib.sha256(handle.read()).hexdigest()
    manifest["artifacts"].append({
        "file": name,
        "sha256": digest,
        "bytes": os.path.getsize(path),
    })
with open(os.path.join(out, "artifact-manifest.json"), "w", encoding="utf-8") as handle:
    json.dump(manifest, handle, indent=2)
    handle.write("\n")
with open(os.path.join(out, "build-info.json"), "w", encoding="utf-8") as handle:
    json.dump({k: manifest[k] for k in ("version", "toolchain", "frameworkDependent", "prerequisite", "signing")}, handle, indent=2)
    handle.write("\n")
print(f"manifest: {len(zips)} artifacts hashed")
PY

rm -rf "$STAGE"
echo "Packaging complete. Artifacts in artifacts/:"
ls -1 "$OUT"
