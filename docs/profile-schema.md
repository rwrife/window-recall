# Profile schema v1

Window Recall profiles are UTF-8, human-readable JSON documents. The root `schemaVersion` is required and is currently `1`. A reader rejects malformed documents, invalid v1 data, older unsupported versions, and unknown future versions without modifying the source file.

Required root fields are `schemaVersion`, `name`, `capturedAtUtc`, `displays`, `windows`, and `privacy`. Coordinates use platform-neutral logical desktop units. Display and window ids are stable hints or opaque session ids; they are never native handles.

Each window contains an application identity, normal bounds, state, optional role/display hints, and a launch policy. Launch defaults to `never`; executable paths are identities only and are not shell command strings. Raw `title` values are removed during serialization unless `privacy.persistWindowTitles` is explicitly `true`.

Example:

```json
{
  "schemaVersion": 1,
  "name": "Desk",
  "capturedAtUtc": "2026-01-02T03:04:05+00:00",
  "displays": [],
  "windows": [],
  "privacy": {
    "persistWindowTitles": false
  }
}
```

Profiles describe intent. Loading one never moves windows. Restore plans are previews, ambiguous matches cannot auto-apply, and an approved restore retains a pre-apply desktop snapshot in its undo receipt.
