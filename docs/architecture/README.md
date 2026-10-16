# Architecture
The architecture is described in [ARCHITECTURE.md](../../ARCHITECTURE.md). Algorithms are in
[docs/algorithms](../algorithms/README.md), the licensing protocol in [docs/licensing](../licensing/README.md).

Module dependencies (arrows point at dependencies):

```
App ─► Core, Visualization, Reporting, Licensing, Infrastructure
Reporting ─► Core, Visualization          Infrastructure ─► Core, Licensing, Persistence
Core ─► Analysis, Formats, Persistence    Visualization ─► Domain (+ SkiaSharp)
Analysis ─► SignalProcessing, Domain      Formats, Persistence ─► Domain
Synthetics ─► Analysis, Formats           LicenseServer, AdminPortal ─► Server.Data ─► Licensing
Cli ─► Core, Reporting, Synthetics, Licensing
```

Extension points for a future product suite (MASW, HVSR, Refraction, Crosshole Studio): licensing,
update service, plotting, report layout, settings, crash handling and the project-shell patterns
contain nothing downhole-specific. A future AI-assisted QC module would implement the same picker
and QC interfaces (`IPWavePicker`, `ISWavePicker`, QC flags) and must label its suggestions as
suggestions; core calculations never depend on network services.
