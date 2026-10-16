# Downhole Studio — Architecture

QuakeLogic Downhole Studio is a Windows desktop application for processing, interpreting,
quality-checking and reporting downhole seismic (P/S) surveys, plus a licensing back end
(license server + admin portal). This document is the contract between the layers. Read it before
changing any project boundary.

## 1. Principles

1. **Raw data is immutable.** Imported files are embedded byte-for-byte in the project with their
   SHA-256. Decoded samples are stored once. Everything else (filters, gain, polarity, time shift,
   rotation, trigger correction, picks) is a *description* applied on top of raw samples.
2. **The scientific engine has no UI.** `Domain`, `SignalProcessing`, `Analysis`, `Formats`,
   `Persistence`, `Core`, `Visualization` and `Reporting` never reference Avalonia. Everything the UI
   shows can be computed from a loaded project in a console process
   (`DownholeAnalyzer.Calculate(project)`), which is what the CLI and the validation suite do.
3. **No silent science.** Nothing is extrapolated, interpolated, rotated, resampled, repaired or
   estimated unless the user enabled it, and every such operation is labelled in the UI, the
   project history and the report. When a value cannot be computed reliably the engine returns a
   reason, not a number.
4. **One state, one undo stack.** The whole editable project is a single immutable
   `ProjectState`. Every edit is a pure function `ProjectState → ProjectState` executed through
   `ProjectSession`, which gives application-wide undo/redo and an append-only audit history.
5. **One rendering path.** Plots are drawn by `Visualization` onto a SkiaSharp canvas. The same
   code paints the screen (through an Avalonia custom draw operation), PNG, SVG and the PDF report.
6. **Licensing never touches project data.** A licence problem can switch the application into
   read-only viewer mode; it can never block opening, prevent viewing, or modify a project.

## 2. Repository layout

```
DownholeStudio.sln
Directory.Build.props / Directory.Packages.props / global.json
src/
  DownholeStudio.Domain            immutable records, units, enums; zero dependencies
  DownholeStudio.SignalProcessing  DSP on double[]: FFT, Butterworth SOS, filtfilt, notch, taper,
                                   detrend, AGC, resampling, correlation, rotation, statistics
  DownholeStudio.Analysis          pickers, confidence, trigger correction, polarization, cross-
                                   correlation, travel-time geometry, velocity methods, layer model,
                                   regression, Vs30, engineering properties, density estimation, QC,
                                   DownholeAnalyzer (project → AnalysisResult)
  DownholeStudio.Formats           ISeismicDataReader plug-ins (SEG-2 reader and writer), import
                                   inspection and validation
  DownholeStudio.Persistence       .dhsx container, versioned JSON, SQLite catalogue (recent
                                   projects, instrument profiles, report templates), recovery store
  DownholeStudio.Core              ProjectSession (state + undo/redo + history), commands, import
                                   service, processed-trace cache, workflow status, scenarios
  DownholeStudio.Visualization     SkiaSharp renderers: waveform section, XY plots, travel-time,
                                   profiles, spectrum, hodogram, borehole diagram; export to PNG/SVG
  DownholeStudio.Reporting         PDF report (Skia PDF back end), CSV, XLSX, figure export, audit
  DownholeStudio.Licensing         product-agnostic client: fingerprint, signed tokens, local store,
                                   clock-rollback guard, state machine, server API client; shared
                                   DTO contracts used by the server too
  DownholeStudio.Infrastructure    logging, settings, crash handling, diagnostics package, updater,
                                   file dialogs abstraction, help content
  DownholeStudio.Synthetics        deterministic synthetic downhole surveys with known answers
  DownholeStudio.App               Avalonia 12 MVVM desktop shell
server/
  DownholeStudio.Server.Data       EF Core model + migrations (PostgreSQL), key generation,
                                   token signing, licence domain services, email outbox
  DownholeStudio.LicenseServer     ASP.NET Core API: trials, activation, validation, deactivation,
                                   updates feed, Stripe webhook, email worker
  DownholeStudio.AdminPortal       ASP.NET Core Razor Pages internal admin portal
tools/
  DownholeStudio.Cli               batch analysis, sample-project generation, validation report,
                                   key generation, release-manifest signing
tests/
  *.Tests                          xUnit v3, one per library + IntegrationTests (UI headless,
                                   server against a real PostgreSQL)
packaging/windows                  Inno Setup installer, icons, EULA
docs/                              architecture, algorithms, file formats, validation, licensing,
                                   deployment, user guide
```

Dependency direction (arrows point at dependencies):

```
App ─► Core, Visualization, Reporting, Licensing, Infrastructure
Reporting ─► Core, Visualization          Infrastructure ─► Core, Licensing, Persistence
Core ─► Analysis, Formats, Persistence    Visualization ─► Domain (+ SkiaSharp)
Analysis ─► SignalProcessing, Domain      Formats, Persistence ─► Domain
Synthetics ─► Analysis, Formats           LicenseServer, AdminPortal ─► Server.Data ─► Licensing
```

`Licensing`, `Infrastructure`, `Visualization` and `Reporting`'s layout engine contain nothing
downhole-specific in their public contracts so a future MASW/HVSR/Refraction/Crosshole studio
can reuse them.

## 3. Domain model (SI units internally)

Internal units: metre, second, m/s, kg/m³, pascal, hertz, radian. Display conversion happens
only in `UnitFormatter` (SI or US customary). Values are always `double`; rounding is display-only.

* `ProjectState` — `Metadata`, `Survey`, `Scenarios`, `ActiveScenarioId`, `Report`, `Snapshots`
  (headers only). Immutable, value-equal, serialised as `project.json`.
* `Survey` — `Records` (one per imported shot file: source file identity, traces' metadata,
  assigned station and shot kind), `Stations` (measurement levels: receiver depth, P / S+ / S−
  record links), `Geometry` (borehole, independent P and S source geometry, acquisition
  direction, sensor orientation), `ChannelMap` (channel → component role and polarity).
* `AnalysisScenario` — everything interpretive: processing pipelines per component group, per-trace
  adjustments, trigger settings and reference picks, P and S picks, S-wave settings (rotation,
  stacking), layer model, density model, analysis settings. Scenarios share raw data.
* `ArrivalPick` — time (raw record time), origin (manual / automatic / accepted suggestion /
  correlation), algorithm, confidence (score + level + per-factor breakdown + human-readable
  reasons), SNR, included flag, comment, and the automatic suggestion kept alongside manual picks.

Raw samples live outside the state in an `ITraceStore` keyed by trace id, so undo snapshots never
copy seismic data.

## 4. Editing model, undo and history

`ProjectSession.Execute(ProjectEdit)` where `ProjectEdit` = description + history entry +
`Func<ProjectState, ProjectState>`. The session pushes the previous state on the undo stack,
raises `StateChanged`, marks the project dirty, and appends to the audit history (undo and redo are
themselves audited). Continuous gestures (dragging a pick or a layer boundary) use
`BeginGesture/EndGesture` so a drag is one undo step. Because states are immutable records with
structural sharing, an undo entry costs a few object references.

## 5. Processing

A `ProcessingPipeline` is an ordered list of `ProcessingStep` (polymorphic JSON with a `kind`
discriminator): remove mean, linear detrend, taper, high/low/band-pass Butterworth (order, causal or
zero-phase), notch, normalise, balance, AGC, polarity, time shift, resample (anti-aliased). Each step
can be enabled, edited, reordered, removed. `ProcessedTraceCache` keys results by
(trace id, pipeline fingerprint, adjustment fingerprint) in a bounded LRU cache, so raw arrays are
read once and processed arrays are produced lazily.

## 6. Analysis engine

`DownholeAnalyzer.Calculate(ProjectState, ITraceStore)` returns an immutable `AnalysisResult`:
per-station times (raw, trigger correction, corrected, geometry-corrected), slant distances,
per-method velocities (pseudo-interval, true interval, average, layer regression, refracted-ray layer
stripping), layer results with regression statistics and residuals, Vs30 / VsZ (or the reason it
cannot be computed), engineering properties with validity flags, QC flags and an overall quality
verdict. Each algorithm sits behind an interface (`IPWavePicker`, `ISWavePicker`,
`ITriggerCorrector`, `IPolarizationAnalyzer`, `ICrossCorrelationAnalyzer`, `IVelocityCalculator`,
`ILayerModeler`, `IEngineeringPropertyCalculator`, `IVs30Calculator`). Formulas and assumptions:
`docs/algorithms/`.

## 7. Persistence

`.dhsx` is a ZIP container:

```
manifest.json            format id, schema version, app version, timestamps, content hashes
project.json             ProjectState (versioned; migrations in ProjectMigrations)
history.json             append-only audit history
snapshots/<id>.json      named project snapshots (state only)
raw/<sha256>/<name>      original imported files, byte-identical
traces/<traceId>.f64     decoded samples, little-endian float64
```

Saving writes to a temporary file in the same folder, verifies it, then atomically replaces the
target and keeps the previous version as `<name>.dhsx.bak`. Auto-save writes recovery copies to
`%LocalAppData%\QuakeLogic\Downhole Studio\Recovery` (never next to the user's file). User
preferences live in `settings.json`; recent projects, instrument/channel profiles and report
templates in `catalog.db` (SQLite).

## 8. Desktop application

Avalonia 12, MVVM (CommunityToolkit.Mvvm), Microsoft.Extensions.DependencyInjection. The shell has
a toolbar (file, edit, import/export, report, settings, help, project name, save state, licence
chip, version), a workflow strip (12 steps with done / needs-attention / pending status), left
navigation, a centre workspace, a context inspector and a status bar. Pages are view models
resolved from DI; each page is a thin adapter between `ProjectSession`/`AnalysisResult` and
controls. Plots are `SkiaPlotControl`s hosting `Visualization` renderers. Guided and Expert modes
are a view-model flag that shows explanations/defaults or exposes all parameters; the project is
identical in both.

## 9. Licensing

Per-computer seats. The client computes a device hash locally
(SHA-256 of normalised Windows `MachineGuid` + SMBIOS system UUID + application salt) and a
hardware hash (SMBIOS UUID + baseboard serial + salt) used only to stop trial resets; raw
identifiers are never transmitted. The server (ASP.NET Core + PostgreSQL) is authoritative and
returns ECDSA P-256 signed tokens; the client embeds only public keys. Paid tokens allow 30 days
offline, trial tokens 3 days (never beyond trial end). A protected local "last seen" stamp detects
clock rollback and requires online validation instead of accusing the user. See
`docs/licensing/` and `LICENSE-SERVER.md`.

## 10. Updates, crash handling, logging

The updater reads a release manifest (version, channel, notes, installer URL, SHA-256) signed with
a separate update key, verifies signature and hash, then hands over to the Inno Setup installer.
Global exception handlers write a crash report and a recovery copy; on next start the user chooses
Recover or Discard. Logs are structured JSON lines in `%LocalAppData%\QuakeLogic\Downhole
Studio\Logs`, with licence keys and personal data redacted. Help → Export Diagnostic Package
zips logs, version and system information; project files are added only if the user selects them.

## 11. Build and release

`scripts/build.sh` (Linux) / `scripts/build.ps1` (Windows) restore, build, test and publish a
self-contained win-x64 application, then compile the Inno Setup installer (on Linux through a
Docker image running Inno Setup). Releases ship a single installer
`DownholeStudio-Setup-<version>.exe` with a SHA-256 sidecar. See `BUILD.md`, `DEPLOYMENT.md`.
