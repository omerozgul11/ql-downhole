# Reports and exports

Code: `src/DownholeStudio.Reporting`.

## PDF report
`ReportGenerator` lays out pages with a small flow engine (`Pdf/PdfLayout.cs`) on the Skia PDF back
end: headings, wrapped paragraphs, key/value lists, call-outs, tables with repeated headers across
page breaks, and figures drawn by the same renderers the application uses on screen (so figures are
vector graphics and identical to the screen). Generation runs twice: a counting pass for "Page n of
N" and the rendering pass. Letter or A4.

Sections (each can be switched off): cover (branding, logo, client, dates, sign-off, a
reproducibility block with software version, analysis date, method, scenario, data coverage,
quality verdict and user, and the professional-responsibility disclaimer), project, client, site,
survey configuration, instruments, geometry (with diagram), processing methodology (every enabled
step with parameters, picking statistics manual/automatic, S-wave method, rotation angles, trigger
correction, velocity method and equations, density source), quality control (verdict, reasons and
summary), P and S analysis (trace sections with picks and status glyphs, excluded observations),
travel-time curves and residuals, velocity profiles, layer model and interval tables, engineering
parameters (with ESTIMATED labels) and profiles, Vs30 (with EXTRAPOLATED or the reason it is not
available), processing notes, warnings, conclusions, Appendix A pick table (raw, trigger
correction, corrected, vertical, origin, confidence, inclusion, comments) and Appendix B processing
history. Branding (company, logo, contact, footer, engineer, registration) can be saved as reusable
templates.

## Tables
`ReportTables` builds picks, layers, intervals, engineering, time-averaged Vs, QC and history tables
in SI and converts to display units only when written. XLSX (`XlsxWriter`, Office Open XML without
third-party libraries) stores full-precision doubles with display formats — rounded values are
never written as data. CSV is RFC 4180, UTF-8 with BOM, invariant decimal point, with text cells
protected against formula injection.

## Figures
Every figure (travel time, residuals, layer and interval profiles, engineering profiles, P and S
sections, geometry) exports to PNG at any pixel size, SVG or PDF, with white or transparent
background, or to the clipboard.

## Audit record
`AuditExporter` writes JSON with the imported files (name, SHA-256, size, import time), channel map,
geometry, processing steps and trace adjustments, rotation angles, trigger settings and reference
picks, every pick with origin, algorithm, confidence and inclusion, analysis method and settings,
layer results, Vs30, QC flags and the full history, plus the software version. Project → Create
archive bundles the saved project, the audit record and an Excel workbook.
