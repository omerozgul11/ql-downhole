namespace DownholeStudio.App.ViewModels;

/// <summary>The integrated user guide. Keys match each page's help topic.</summary>
public static class HelpContent
{
    public static IReadOnlyList<HelpTopic> Topics { get; } =
    [
        new("start", "Getting started",
            """
            Downhole Studio takes you from raw downhole seismic records to a reviewed velocity model and a professional report.

            The workflow strip at the top shows twelve steps. Each step shows its status — done, needs attention or not started — but you can move between pages at any time. Nothing is locked.

            Example workflow (try it with Explore Sample Project on the start screen):
            1. Project — name the survey, client job number and date.
            2. Import — drag the SEG-2 files (or a folder) onto the Import page, review the preview and warnings, then import.
            3. Geometry — enter the P and S source offsets and assign each record to a depth and shot type (P, S+, S−). Check the channel map (vertical, X, Y, reference).
            4. Waveforms — look at every trace; check polarity, dead or clipped traces.
            5. Processing — keep processing light for picking; compare processed and raw onsets. Pick the reference geophone onsets and choose a trigger correction if needed.
            6. P Picks and S Picks — run the automatic picker, then review every depth. Fix, drag or delete picks; accept the ones you agree with.
            7. Results — read the quality-control findings and fix what is flagged.
            8–9. Velocity Model — add layer boundaries (or use Suggest), drag them on the travel-time graph, choose the velocity method.
            10. Engineering — enter densities to obtain moduli; read Vs30.
            11. Results — review the quality verdict and compare scenarios.
            12. Report — fill in client and site details and generate the PDF.

            Guided mode explains each page and recommends defaults. Expert mode shows every parameter. Switch with the Guided/Expert button in the top bar; the project is identical in both.
            """),
        new("project", "Projects, snapshots and scenarios",
            """
            A project (.dhsx) holds the original records byte-for-byte (with SHA-256 checksums), the decoded samples, geometry, processing, picks, layers, report settings and an append-only history of every change.

            Saving is atomic: the file is written to a temporary copy, verified, and only then replaces the previous file, which is kept as name.dhsx.bak. A recovery copy is written every few minutes to your local application data; after a crash Downhole Studio offers to recover it. Your own file is never overwritten by auto-save.

            Snapshots store the complete interpretation at a moment in time inside the project; restoring one is an undoable edit.

            Scenarios are alternative interpretations of the same data — for example manual versus automatic picks, or two filter settings. They share the raw data. Compare them on the Results page.

            Duplicate writes an independent copy with a new project identity. Archive creates a ZIP with the saved project, a machine-readable audit record and the result tables.
            """),
        new("import", "Importing SEG-2 data",
            """
            Downhole Studio reads SEG-2 (revision 1) in both byte orders with 16-bit, 32-bit and floating-point samples. The 20-bit packed format (code 3) is reported as unsupported rather than decoded by guesswork; export such files as 32-bit from the acquisition software.

            Before importing, every file is inspected: sample count and interval, trace count, channel numbers, labels, recording date and any depth or shot type that can be read from keywords or the file name. Problems are listed as Errors, Warnings or Information, for example different sample rates, missing channels, duplicate files, duplicate depths or an unexpected acquisition order.

            Questionable data is never repaired silently. Corrections such as skipping duplicate files, ordering records by recording time or using suggested depths are only applied when you tick them.

            Tip: drag files or folders anywhere onto the Import page.
            """),
        new("geometry", "Survey geometry and channels",
            """
            Depth reference: choose whether measured depths are below the ground surface or below the top of casing (collar). With a collar reference, depths are converted using the collar height.

            Sources: P and S sources have independent horizontal offsets and depths. The offset is the horizontal distance from the borehole axis.

            Depth assignment: records can be assigned by acquisition order (first depth, increment, top-down or bottom-up, and the shot pattern per depth), from the suggestions read from the files, or edited one by one in the table. Mirror depths fixes a survey assigned in the wrong direction.

            Channels: map each channel to Vertical, Horizontal X, Horizontal Y or Reference. Built-in profiles (for example "QuakeLogic 3C Borehole") apply a known layout; save your own instruments as profiles. Reverse polarity corrects a sensor wired backwards.

            Every geometry change updates the analysis immediately.
            """),
        new("waveforms", "Waveform viewer",
            """
            Mouse wheel: zoom time around the cursor. Ctrl + wheel: amplitude gain. Shift + wheel: scroll through depths. Alt + wheel: show more or fewer traces. Right-drag or Shift-drag: pan. Double-click: fit everything.

            Display: wiggle, variable area or both; each trace normalised to its own peak or all traces to a common peak; stacked or overlaid; evenly spaced or on a true depth scale.

            Raw / Processed / Processed over raw shows exactly which data you are looking at. The spectrum below shows the dominant frequency and −3 dB band of the selected trace, raw versus processed.

            Hover over a trace to see depth, channel, component, sample interval, amplitude, time, pick, SNR and QC status. Click to select it and edit its properties (gain, polarity, static shift, exclude) in the inspector.
            """),
        new("processing", "Signal processing",
            """
            Each component group (vertical, horizontal, reference) has its own processing chain. Steps are applied to a copy of the raw samples in order; each can be switched off, edited in the inspector, moved or removed, and every change is recorded in the history and can be undone.

            Filters are Butterworth designs with selectable order, causal or zero-phase. Zero-phase filtering does not shift the waveform but is acausal: energy can appear slightly before a sharp onset. Causal filters delay the waveform. For first-break picking keep processing light and compare with the raw trace.

            The suggested band-pass is computed from the average spectrum; it is only a suggestion and is added only when you click it.

            Reference / trigger: pick the onset on the reference (spy) geophone for every record, manually or automatically, then choose a correction. Subtract reference onset measures times from the moment the source was struck. Align to median removes shot-to-shot trigger jitter only. Raw times are always kept next to corrected times.
            """),
        new("p-picks", "Picking P arrivals",
            """
            Run the automatic picker, then review every depth.

            Mouse: click to place a pick, drag a pick to move it, Alt + click snaps to the automatic suggestion, Ctrl + click selects without picking.
            Keyboard: ← / → move the selected pick by one sample, Shift + ← / → by ten samples, ↑ / ↓ select the previous or next depth, Enter accepts the suggestion, Delete removes the pick, Space includes or excludes it.

            Algorithms: AIC (Maeda 1985) finds the point that best splits the window into noise and signal; STA/LTA detects an increase of short-term over long-term energy; the modified energy ratio (Han et al. 2009) sharpens the energy increase. Each automatic pick has a confidence score from SNR, onset sharpness, agreement between algorithms and consistency with neighbouring depths. The inspector explains why a pick scores low.

            Solid marks are manual or reviewed picks; dashed marks are automatic picks not yet reviewed; a hollow violet mark is an automatic suggestion that differs from your manual pick. Manual picks are never overwritten by re-running the picker unless you ask for it.
            """),
        new("s-picks", "Picking S arrivals",
            """
            S waves are recorded on the horizontal components with the source struck in opposite directions (S+ and S−). A shear wave reverses polarity with the source direction; P energy and noise common to both shots do not. The half-difference (S+ − S−)/2 keeps the shear wave and cancels the rest, which makes the S arrival much clearer.

            The depth detail shows S+ and the inverted S− together: where they line up, the energy is shear. Correlate opposite shots confirms this numerically (a strongly negative correlation is expected).

            Rotation: the borehole probe can rotate between depths. Analyse finds the horizontal direction with the most energy in a window at the S arrival (covariance polarization; the hodogram shows the particle motion). Rotation is applied only when you click Apply, and the angle is recorded and reported.

            Correlate adjacent depth proposes an arrival from the waveform shift relative to the nearest picked depth. Use it as a check or to fill a weak depth; the pick is recorded as coming from cross-correlation.
            """),
        new("velocity-model", "Velocity model and layers",
            """
            Methods:
            • Layer regression fits t_v = a + z/V to the corrected travel times inside each layer (t_v = t·(z − z_s)/R, R = √((z − z_s)² + x²)). Standard and robust.
            • Refracted-ray layer stripping traces Snell's-law rays through the layers and solves each layer's velocity from the top down. Most accurate near the surface where rays are oblique.
            • Pseudo-interval Δz/Δt_v and true interval ΔR/Δt between adjacent receivers. Both assume straight rays; true interval is biased below slower layers where rays bend.

            Regression can be ordinary least squares or robust (Huber), which down-weights outliers without deleting them.

            Layers: add a boundary by depth, Ctrl-click on the travel-time graph, or Suggest (piecewise-linear segmentation with a BIC penalty). Drag a dashed boundary to move it; the regression updates immediately. Merge, split and delete from the layer inspector. Click a point to select its depth; Shift-click excludes or includes it — excluded points remain visible as crosses and are listed in the report.
            """),
        new("engineering", "Engineering parameters and Vs30",
            """
            ν = (Vp² − 2Vs²) / (2(Vp² − Vs²)); G = ρVs²; E = 2G(1 + ν); K = ρ(Vp² − 4Vs²/3). Values that are physically inconsistent (for example Vs ≥ Vp or a negative modulus) are withheld and flagged, never silently corrected.

            Density is never assumed. Enter measured values per layer, one constant, or explicitly choose an empirical estimate from Vp (Gardner 1974 or Brocher 2005). Estimated densities and the moduli derived from them are labelled ESTIMATED; both relations were developed for rocks.

            Vs30 = 30 / Σ(hᵢ/Vsᵢ) over the top 30 m. If the S measurements do not reach 30 m Downhole Studio reports "Insufficient measured depth for direct Vs30 calculation". You may allow extrapolation (the deepest layer is extended); the value is then labelled EXTRAPOLATED everywhere, including the report. Add custom intervals for other time-averaged velocities.
            """),
        new("results", "Quality control and results",
            """
            Automatic checks flag low SNR, emergent onsets, large residuals, unusual velocity jumps, arrivals that get earlier with depth, poor or missing opposite-polarity confirmation, energy on an unexpected component, trigger timing problems, poor regressions, and invalid elastic properties.

            Green ✓ means acceptable, yellow ! review, red × likely problem. Click a finding to go to the depth, record or layer concerned.

            The analysis quality — Good, Review recommended or Incomplete — summarises the findings and explains why. It is advisory and does not certify compliance with ASTM D7400/D7400M or any other standard: results depend on survey quality and professional judgment.
            """),
        new("report", "Reports and exports",
            """
            The PDF report includes the cover page, project, client and site information, survey configuration and instruments, geometry, processing methodology, quality control, P and S analysis, travel-time curves, velocity profiles, layer model, engineering parameters, Vs30, notes, warnings, conclusions and appendices with the pick table and the processing history. Choose which sections to include.

            Every report records the Downhole Studio version, analysis date, velocity method, processing settings, data coverage, warnings and the user, so it can be reproduced.

            Branding: company name, logo, contact details, footer, engineer and registration. Save them as a template for future projects.

            Exports: all tables to Excel (full precision values; nothing rounded), individual tables to CSV, and figures to PNG (any pixel size), SVG or PDF with a white or transparent background, or to the clipboard.
            """),
        new("keyboard", "Keyboard shortcuts",
            """
            Ctrl+N New project · Ctrl+O Open · Ctrl+S Save · Ctrl+Shift+S Save as
            Ctrl+Z Undo · Ctrl+Y Redo · Ctrl+I Import · Ctrl+R Generate report
            Ctrl+K Command palette (search every command and page) · F1 Help

            Picking pages: ← → nudge pick (Shift: ×10) · ↑ ↓ previous/next depth · Enter accept suggestion · Delete remove pick · Space include/exclude
            Waveforms: wheel zoom · Ctrl+wheel gain · Shift+wheel scroll · double-click fit
            """),
        new("licensing", "Activation, licensing and privacy",
            """
            Downhole Studio is licensed per computer and is activated offline; no internet connection is needed, ever.

            To activate: the activation window (and Settings → License) shows this computer's code, five groups of five characters. Copy it, or select E-mail to QuakeLogic, and send it with your company name and order number. QuakeLogic replies with an activation key for this computer. Paste the whole key (it starts with dhs1.) and select Activate, or open the activation file attached to the e-mail.

            The key works only on the computer whose code it was issued for. The code is derived from the computer's hardware identifiers and stays the same when Downhole Studio is reinstalled or updated; after replacing the computer or reinstalling Windows, request a new key.

            When the computer is not activated, projects can still be opened and viewed (read-only). Licence problems never delete or modify project files.

            To move the licence to another computer, use Settings → License → Remove Activation from This Computer, then send the new computer's code to QuakeLogic.

            Privacy: survey data never leaves your computer. The computer code contains no personal or project data, and Downhole Studio does not collect usage telemetry.
            """),
        new("standards", "Standards and professional responsibility",
            """
            The downhole workflow follows the established practice described in ASTM D7400/D7400M (downhole seismic testing). Downhole Studio does not reproduce the standard and does not certify compliance with it or any other standard.

            The software does not replace professional judgment. Results depend on survey quality, field procedures and interpretation; the user is responsible for assessing the applicable standards and regulations.
            """),
    ];
}
