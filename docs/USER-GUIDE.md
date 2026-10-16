# Downhole Studio — User Guide

This guide follows a survey from raw SEG-2 files to a finished report. The same text, organised by page, is available inside the application: press **F1** or click **?** on any page.

- [1. Installing and starting](#1-installing-and-starting)
- [2. The workspace](#2-the-workspace)
- [3. Projects, snapshots and scenarios](#3-projects-snapshots-and-scenarios)
- [4. Import](#4-import)
- [5. Geometry and channels](#5-geometry-and-channels)
- [6. Waveforms](#6-waveforms)
- [7. Processing and trigger correction](#7-processing-and-trigger-correction)
- [8. P picks](#8-p-picks)
- [9. S picks](#9-s-picks)
- [10. Velocity model](#10-velocity-model)
- [11. Engineering parameters and Vs30](#11-engineering-parameters-and-vs30)
- [12. Results and quality control](#12-results-and-quality-control)
- [13. Reports and exports](#13-reports-and-exports)
- [14. Settings, updates and diagnostics](#14-settings-updates-and-diagnostics)
- [15. Licensing and privacy](#15-licensing-and-privacy)
- [16. Keyboard shortcuts](#16-keyboard-shortcuts)
- [17. Troubleshooting](#17-troubleshooting)

---

## 1. Installing and starting

**Requirements:** 64-bit Windows 10 (21H2 or later; 22H2 recommended) or Windows 11, 8 GB RAM recommended, 1 GB of free disk space for the program plus room for projects.

1. Run `DownholeStudio-Setup.exe`. By default the program is installed for the current user only, so no administrator rights are needed. An administrator can choose *Install for all users* instead.
2. If Windows SmartScreen shows *Windows protected your PC*, click **More info → Run anyway**. This happens with builds that are not Authenticode-signed.
3. Optional setup tasks are a desktop shortcut and opening `.dhsx` project files with Downhole Studio.
4. Start **Downhole Studio** from the Start menu. The start screen offers **Start 14-Day Free Trial**, **Activate License**, **Explore Sample Project** and recent projects.

The sample project is a complete worked survey. It contains 32 depths, P and opposite-direction S shots and a reference geophone. All of its data is synthetic and its true answer is known. It is the quickest way to see every page with real content.

**Uninstalling** (Settings → Apps) removes the program. It never removes your projects or your settings and licence, which are in `%LOCALAPPDATA%\QuakeLogic\Downhole Studio`.

## 2. The workspace

- The **workflow strip** at the top lists the twelve steps: Project, Import, Geometry, Waveforms, Processing, P Picks, S Picks, Velocity Model, Engineering, Results, Report. Each step shows its status: done, needs attention, or not started. Nothing is locked, so you can move between pages in any order.
- **Guided / Expert** (top bar): Guided mode explains each page and recommends defaults. Expert mode shows every parameter. The project is identical in both modes.
- The **inspector** on the right edits whatever is selected: a trace, a processing step, a pick, a layer.
- **Ctrl+K** opens the command palette, which searches every command and page.
- **Undo/redo** (Ctrl+Z / Ctrl+Y) covers every change to the project, including geometry, processing, picks, layers and report settings.
- Notifications appear briefly at the bottom right.

The analysis recalculates automatically after every change. You never need to "run" the velocity model or the QC.

## 3. Projects, snapshots and scenarios

A project file (`.dhsx`) holds:

- the original records byte-for-byte, with SHA-256 checksums, and the decoded samples;
- geometry, processing, picks, layers and report settings;
- an append-only history of every change.

**Saving is atomic.** The file is first written to a temporary copy and verified. Only then does it replace the previous file, which is kept as `name.dhsx.bak`. Every few minutes a recovery copy is written to your local application data, and after an unexpected shutdown Downhole Studio offers to restore it. Recovery copies never overwrite your project file.

- **Snapshots** store the complete interpretation at a moment in time inside the project. Restoring a snapshot is itself an undoable edit.
- **Scenarios** are alternative interpretations of the same raw data, for example manual versus automatic picks, or two filter settings. Compare them on the Results page.
- **Duplicate** writes an independent copy with a new project identity.
- **Archive** creates a ZIP containing the saved project, a machine-readable audit record and the result tables.

Projects are saved where you choose. The default folder is `Documents\Downhole Studio`. Projects never leave your computer.

## 4. Import

Drag SEG-2 files or a whole folder onto the Import page, or use **Add files / Add folder** (Ctrl+I).

**Supported formats:** SEG-2 revision 1 in either byte order, with 16-bit or 32-bit integer or 32/64-bit floating-point samples. The 20-bit packed format (data code 3) is reported as unsupported rather than decoded by guesswork. Export such files as 32-bit from the acquisition software.

**Every file is inspected before anything is imported.** The preview lists:

- sample count and interval;
- trace count and channel numbers;
- recording date and time;
- any depth or shot type that can be read from the file's keywords or file name.

Problems are listed as **Errors**, **Warnings** or **Information**. Examples are mixed sample rates, missing channels, unreadable files, duplicate files, duplicate depths and an acquisition order that does not match the recording times.

**Nothing is repaired silently.** Corrections such as skipping duplicate files, ordering records by recording time or using the suggested depths are applied only when you tick them. Files with errors are not imported.

## 5. Geometry and channels

- **Depth reference:** depths can be measured below the ground surface or below the top of casing (collar). With a collar reference, depths are converted using the collar height.
- **Sources:** P and S sources have independent horizontal offsets (distance from the borehole axis) and depths.
- **Depth assignment:** depths can be assigned in three ways:
  - by acquisition order: first depth, increment, top-down or bottom-up, and the shot pattern per depth (for example P, S+, S−);
  - from the suggestions read from the files;
  - one record at a time in the table.

  **Mirror depths** fixes a survey that was assigned in the wrong direction.
- **Channels:** map each channel to Vertical, Horizontal X, Horizontal Y or Reference. Built-in profiles, such as *QuakeLogic 3C Borehole*, apply a known layout, and you can save your own instruments as profiles. **Reverse polarity** corrects a sensor that was wired backwards.
- The **borehole diagram** shows the receivers, sources and layers to scale and updates as you type.

## 6. Waveforms

| Action | Mouse / key |
|---|---|
| Zoom time around the cursor | Mouse wheel |
| Amplitude gain | Ctrl + wheel |
| Scroll through depths | Shift + wheel |
| Show more / fewer traces | Alt + wheel |
| Pan | Right-drag or Shift-drag |
| Fit everything | Double-click |

**Display options:**

- wiggle, variable area, or both;
- each trace normalised to its own peak, or all traces to a common peak;
- stacked or overlaid traces;
- traces evenly spaced, or placed on a true depth scale.

The **Raw / Processed / Processed over raw** switch shows exactly which data you are looking at. The spectrum below the traces shows the dominant frequency and the −3 dB band of the selected trace, raw versus processed.

Hover over a trace to see its depth, channel, component, sample interval, amplitude, time, pick, SNR and QC status. Click a trace to select it. The inspector then edits its gain, polarity and static shift, or excludes it. Excluded traces stay in the project and are listed in the report.

## 7. Processing and trigger correction

The vertical, horizontal and reference components each have their own processing chain. Steps are applied in order to a copy of the raw samples. Each step can be switched off, edited in the inspector, moved or removed. Every change is recorded in the history and can be undone. **The raw data is never modified.**

Available steps:

- mean or trend removal;
- taper;
- Butterworth high-pass, low-pass and band-pass, of any order, causal or zero-phase;
- notch;
- normalisation, trace balance and AGC;
- polarity reversal;
- time shift;
- anti-aliased resampling.

> **Picking advice.** Zero-phase filters do not shift the waveform, but they are acausal: energy can appear slightly *before* a sharp onset. Causal filters delay the waveform. For first-break picking keep processing light and compare with the raw trace.

The **suggested band-pass** is computed from the average spectrum. It is only a suggestion and is added to a chain only when you click it.

**Trigger correction.** If the survey has a reference (spy) geophone:

1. Pick its onset for every record, automatically or by hand.
2. Choose a correction:
   - **Subtract reference onset:** times are measured from the moment the source was struck. This removes both a constant trigger delay and shot-to-shot jitter.
   - **Align to median:** removes shot-to-shot jitter only.

Raw times are always kept next to corrected times. QC flags records whose reference onset deviates from the expected time.

## 8. P picks

Run the **automatic picker**, then review every depth.

| Action | Mouse / key |
|---|---|
| Place a pick | Click |
| Move a pick | Drag, or ← / → (one sample), Shift + ← / → (ten samples) |
| Snap to the automatic suggestion | Alt + click, or Enter |
| Select without picking | Ctrl + click |
| Previous / next depth | ↑ / ↓ |
| Remove pick | Delete |
| Include / exclude pick | Space |

**Algorithms:**

- **AIC** (Maeda, 1985) finds the point that best splits the window into noise and signal.
- **STA/LTA** detects a rise of short-term over long-term energy.
- **Modified energy ratio** (Han et al., 2009) sharpens that rise.

Each automatic pick has a **confidence score** built from five factors, and the inspector explains why a pick scored low:

- signal-to-noise ratio;
- onset sharpness;
- agreement between algorithms;
- consistency with neighbouring depths;
- for S, agreement between opposite shots.

**Pick markers:**

- Solid marks are manual or reviewed picks.
- Dashed marks are automatic picks not yet reviewed.
- A hollow violet mark is an automatic suggestion that differs from your manual pick.

Re-running the picker never overwrites manual picks unless you ask it to. The pick table shows every time at full precision. Typing in it changes a pick only when you actually edit the value.

## 9. S picks

S waves are recorded on the horizontal components, with the source struck in opposite directions (S+ and S−). A shear wave reverses polarity with the source direction; P energy and noise common to both shots do not. The half-difference **(S+ − S−)/2** therefore keeps the shear wave and cancels the rest.

- The **depth detail** overlays S+ and the inverted S−. Where they line up, the energy is shear.
- **Correlate opposite shots** checks this numerically: a strongly negative correlation is expected.
- **Rotation.** The probe can rotate between depths. **Analyse** finds the horizontal direction with the most energy in a window around the S arrival, using covariance polarization; the hodogram shows the particle motion. The rotation is applied only when you click **Apply**, and the angle is recorded and reported.
- **Correlate adjacent depth** proposes an arrival from the waveform shift relative to the nearest picked depth. Picks made this way are recorded as coming from cross-correlation.
- **Stack repeated shots** averages repeated records at the same depth and shot direction before picking.

## 10. Velocity model

**Methods:**

- **Layer regression** fits t_v = a + z/V to the corrected travel times inside each layer, with t_v = t·(z − z_s)/R and R = √((z − z_s)² + x²). It can use ordinary least squares or robust **Huber** regression, which down-weights outliers without deleting them.
- **Refracted-ray layer stripping** traces Snell's-law rays through the layers and solves each layer's velocity from the top down. It is the most accurate method near the surface, where rays are oblique.
- **Pseudo-interval** (Δz/Δt_v) and **true interval** (ΔR/Δt) velocities between adjacent receivers. Both assume straight rays. True-interval velocities are biased below slower layers, where rays bend.

**Layers:**

- **Add a boundary** by typing a depth, by Ctrl-clicking on the travel-time graph, or with **Suggest**. Suggest uses a piecewise-linear segmentation with a BIC penalty and requires at least a 15% velocity contrast between layers. Treat its result as a starting point, and prefer boundaries from the boring log where one exists.
- **Drag** a dashed boundary to move it; the regression updates immediately.
- **Merge, split or delete** layers from the layer inspector.
- **Points:** click a point to select its depth. Shift-click excludes or includes it. Excluded points remain visible as crosses and are listed in the report.

The residual plot shows how well each layer's line fits its points. Residual QC runs only after layers have been interpreted.

## 11. Engineering parameters and Vs30

| Parameter | Equation |
|---|---|
| Poisson's ratio | ν = (Vp² − 2Vs²) / (2(Vp² − Vs²)) |
| Shear modulus | G = ρVs² |
| Young's modulus | E = 2G(1 + ν) |
| Bulk modulus | K = ρ(Vp² − 4Vs²/3) |

Values that are physically inconsistent, for example Vs ≥ Vp or a negative modulus, are **withheld and flagged**, never silently corrected.

**Density is never assumed.** You can:

- enter measured values per layer;
- enter one constant value; or
- explicitly choose an empirical estimate from Vp (Gardner, 1974, or Brocher, 2005).

Estimated densities, and the moduli derived from them, are labelled **ESTIMATED**. Both empirical relations were developed for rocks.

**Vs30** = 30 / Σ(hᵢ/Vsᵢ) over the top 30 m. If the S measurements do not reach 30 m, Downhole Studio reports *Insufficient measured depth for direct Vs30 calculation*. You may allow extrapolation, which extends the deepest layer. The value is then labelled **EXTRAPOLATED** everywhere, including in the report. Add custom intervals to obtain other time-averaged velocities.

## 12. Results and quality control

The automatic checks flag:

- low SNR and emergent onsets;
- large residuals and unusual velocity jumps;
- arrivals that get earlier with depth;
- poor or missing opposite-polarity confirmation;
- energy on an unexpected component;
- trigger timing problems;
- poor regressions;
- invalid elastic properties.

Each finding has a plain-language explanation. Click it to go to the depth, record or layer concerned.

The **analysis quality** is one of *Good*, *Review recommended* or *Incomplete*, and the page explains why. It is advisory. It does **not** certify compliance with ASTM D7400/D7400M or any other standard. Results depend on survey quality and professional judgment.

**Scenario comparison** puts the layer velocities and Vs30 of two scenarios side by side.

## 13. Reports and exports

The **PDF report** can contain the following sections; choose which to include:

- cover page;
- project, client and site information;
- survey configuration and instruments;
- geometry;
- processing methodology;
- quality control;
- P and S analysis and travel-time curves;
- velocity profiles and the layer model;
- engineering parameters and Vs30;
- notes, warnings and conclusions;
- appendices with the pick table and the full processing history.

Every report records the software version, analysis date, velocity method, processing settings, data coverage, warnings and the user, so it can be reproduced.

**Branding** covers your company name, logo, contact details, footer, engineer and registration. Save it as a template to reuse it in other projects. Choose Letter or A4 paper in Settings.

**Exports:**

- all tables to **Excel**, at full precision with nothing rounded;
- individual tables to **CSV**;
- figures to **PNG** (any pixel size), **SVG** or **PDF**, with a white or transparent background, or to the **clipboard**;
- an **audit record** (JSON) listing the imported files with checksums, the processing, the picks and their origin, the exclusions, the methods and the software version.

## 14. Settings, updates and diagnostics

- **General:**
  - your name, which is recorded in the project history and reports;
  - the recovery-copy interval;
  - theme (light, dark or system);
  - display units. Calculations always use SI units at full precision; units change only how values are shown and exported.
- **Defaults:**
  - default P and S pickers;
  - new projects start with mean removal only. Filters are never applied automatically.
- **Updates:**
  - choose whether to check automatically, and the stable or beta channel;
  - **Check for updates now**.

  Updates are signed by QuakeLogic. Each installer is verified against its signed checksum before it is run, and a failed check deletes the download. Updates never change your projects.
- **Diagnostics:** **Export diagnostic package…** creates a ZIP for QuakeLogic support. It contains system information, settings, logs with personal details redacted, and crash reports. Projects are included only if you choose them.

## 15. Licensing and privacy

- **Per computer:** Downhole Studio is licensed per computer.
- **Trial:** every computer can use a 14-day trial. It starts when you click **Start trial** and does not restart after reinstalling.
- **Activation:**
  - activate with the e-mail address and licence key (`DHST-XXXX-XXXX-XXXX-XXXX`) you received;
  - paid licences work offline for 30 days between online checks;
  - if the system clock moves back significantly, you are asked to connect once to confirm the licence.
- **Moving to another computer:** use **Settings → License → Deactivate this computer**, then activate on the new computer.
- **Without an active licence**, projects can still be opened and viewed read-only. Licence problems never delete or modify project files.
- **Privacy:** survey data never leaves your computer. Licensing sends only:
  - a salted hash of hardware identifiers;
  - the application and Windows versions;
  - the computer name, if you allow it.

  Downhole Studio does not collect usage telemetry.

## 16. Keyboard shortcuts

| Shortcut | Command |
|---|---|
| Ctrl+N / Ctrl+O | New / open project |
| Ctrl+S / Ctrl+Shift+S | Save / save as |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+I | Import |
| Ctrl+R | Generate report |
| Ctrl+K | Command palette |
| F1 | Help |

The shortcuts for the waveform and picking pages are in sections 6 and 8.

## 17. Troubleshooting

| Symptom | What to do |
|---|---|
| *Windows protected your PC* when installing | The installer is not Authenticode-signed. Click **More info → Run anyway**, or ask QuakeLogic for a signed build. |
| A file is listed as *unsupported data format code 3* | Re-export it from the acquisition software as 32-bit integer or float SEG-2. |
| Picks look early after filtering | Zero-phase filters are acausal. Pick on lightly processed data, or use a causal filter and check against the raw trace. |
| Vs30 shows *Insufficient measured depth* | The S picks do not reach 30 m. Either report Vs to the deepest depth, or allow extrapolation (labelled EXTRAPOLATED). |
| Moduli are blank | No density has been chosen, or the velocities are physically inconsistent at that layer. See the QC finding. |
| *Network validation required* | Connect to the internet once so the licence can be confirmed. Projects stay viewable. |
| *Activation limit reached* | Deactivate the licence on a computer you no longer use, or contact QuakeLogic. |
| The program closed unexpectedly | Restart it and accept the recovery offer. Then send the diagnostic package (Settings → Diagnostics) to QuakeLogic support. |

For support, contact [quakelogic.net/contact](https://quakelogic.net/contact).
