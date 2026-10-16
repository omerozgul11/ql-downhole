# The .dhsx project container

A ZIP archive (code: `src/DownholeStudio.Persistence/ProjectPackage.cs`).

| Entry | Content |
|---|---|
| `manifest.json` | format id `quakelogic.downhole-studio.project`, schema version, application version, save time, project id and name, SHA-256 of every other entry |
| `project.json` | the complete project state (metadata, survey, geometry, channel map, scenarios with processing, picks, layers, density and analysis settings, report settings, snapshot list) — camelCase JSON, enums as strings |
| `history.json` | append-only audit history (time, user, version, scenario, operation, description, parameters) |
| `snapshots/<id>.json` | project states stored as snapshots |
| `raw/<sha256>/<file name>` | the original imported files, byte-identical |
| `traces/<trace id>.f64` | decoded samples, little-endian float64 |
| `assets/…` | other assets such as the report logo |

**Versioning.** `schemaVersion` is checked on load; older schemas are migrated in
`ProjectMigrations`; newer ones are refused with a clear message.

**Integrity.** JSON entries are verified against the manifest on load; every entry is verified
after writing. Saving writes a temporary file in the same folder, verifies it, then atomically
replaces the target, keeping the previous file as `<name>.dhsx.bak`. A failure leaves the existing
file unchanged.

**Lazy loading.** Trace data is read from the package on first use and cached; before the same file
is overwritten, all referenced entries are loaded into memory.

Raw data is never modified. All processing, picks and interpretation are descriptions stored in
`project.json` and applied to copies of the samples.
