# Third-party notices

Window Recall binaries are built from the NuGet dependencies pinned in each project's committed
`packages.lock.json`. The authoritative license set for a given build is therefore reproducible:
read the lock file of the exact tagged commit and every referenced `.nupkg` carries its own
license metadata.

Direct dependencies at time of writing:

- **Avalonia 11.3.x** (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent) and its native asset
  packages (SkiaSharp, HarfBuzzSharp, and platform natives) — per their published NuGet license
  metadata.
- **xUnit and Microsoft test SDK packages** — test projects only; never shipped in app or CLI
  artifacts.

Each portable ZIP includes this file plus the repository `LICENSE` (MIT). Before publishing any
signed release, regenerate the full license text bundle from the lock files of the tagged commit
(for example `dotnet project-composite-graph` or the `dotnet-project-licenses` tool) and commit it
here so the archive contents match the tag. This page deliberately does not restate package
licenses inline, because an unpinned paraphrase drifts; the lock files are the contract.
