# Downhole seismic field guide

## 1. Before you start

This guide covers a downhole seismic survey from start to finish, for first-time users: preparing the borehole, setting out the sources, recording each depth, and analysing the records in Downhole Studio. Read it once in the office, then take the checklists and the field log sheet (section 9) to the site.

### What a downhole test measures

A downhole test measures how fast seismic waves travel through the ground at a borehole:

1. A source at the surface, a short distance from the borehole, sends seismic waves into the ground.
2. A geophone clamped inside the borehole records their arrival, one depth at a time.
3. Two kinds of waves are recorded:
   - **P (compression) waves** come from a vertical hammer blow on a plate.
   - **S (shear) waves** come from horizontal blows on the two ends of a loaded beam.
4. The travel time to each depth gives the P-wave velocity (Vp) and the S-wave velocity (Vs) of each soil layer.

Vs is the key parameter for engineers. It is used for seismic site class (Vs30), for the small-strain shear modulus in foundation and liquefaction work, and for ground-response analysis.

> **Three things must be right in the field:**
> - a properly grouted borehole with depths measured accurately;
> - a clean, repeatable trigger, ideally checked by a reference geophone at the source;
> - S waves recorded in **both** directions (S+ and S−) at every depth.
>
> Downhole Studio can correct filtering, trigger jitter and picking afterwards. It cannot correct a bad hole or wrong offsets.

This guide follows the established downhole practice described in ASTM D7400/D7400M. It does not reproduce that standard, and following it does not by itself certify compliance. Always follow the specification for your project.

### The equipment list

| Item | What to bring | Notes |
|---|---|---|
| Seismograph | At least 4 channels; sample interval of 0.125 ms or finer; SEG-2 output | Most engineering seismographs qualify. A laptop runs the recording software. |
| Borehole geophone | 3-component (one vertical, two horizontal) sensor with a clamp: a mechanical arm or an inflatable packer | A pump or gas bottle if the clamp is a packer. The cable is marked in depth. |
| Reference (spy) geophone | One vertical geophone planted next to the source | Records the true moment of each hit and checks the trigger. |
| Trigger | Trigger switch on the hammer, or a trigger geophone | It must start each recording at the same instant every time. |
| P-wave source | Sledgehammer, 5–10 kg (10–20 lb), and a steel or aluminium strike plate | The hammer is struck vertically. |
| S-wave source | Timber beam (for example 15 × 15 cm × 2.4 m, 6 × 6 in × 8 ft) with steel end caps; a vehicle to load it | The vehicle's wheels sit on the beam so it does not slide when struck. |
| Depth control | Depth-marked cable, or a measuring tape fixed to the cable; a reference mark at the collar | Measure to the **centre of the sensor**. |
| Measuring tape | 10 m (30 ft) tape for the source offsets | Offsets are measured horizontally from the centre of the borehole. |
| Borehole tools | Weighted tape or plumb line, clean water for flushing, casing cap key | To check depth and that the hole is clear. |
| Power | Charged batteries for the seismograph, laptop and clamp | Plus spares. |
| Paperwork | Printed field log sheet (section 9), boring log, pencil | Write down every file. |
| Safety | Ear defenders, safety glasses, gloves, safety boots, high-visibility vest | The hammer operator needs all of them. |

### Check everything in the office

- Connect the borehole geophone to the seismograph. Tap each component and check that the correct channel responds: vertical on channel 1, horizontal X on channel 2, horizontal Y on channel 3.
- Connect the reference geophone (channel 4) and the trigger. Check that a tap on the trigger starts a recording.
- Clamp and release the sensor several times, and check that the clamp holds firmly.
- Install Downhole Studio on the office computer (section 8). Open the sample project once, so you know what a finished survey looks like.

## 2. The borehole

A downhole test is only as good as the borehole. The seismic waves have to pass from the soil, through the grout and casing, into the sensor.

### What the hole needs

- **Casing.** PVC casing, typically 50–100 mm (2–4 in) inside diameter, capped at the bottom. Metal casing is not recommended: it carries waves of its own along the pipe.
- **Grout.** The space between the casing and the soil must be completely filled with grout, from the bottom up.
  - Use a cement–bentonite grout that sets to roughly the stiffness of the surrounding soil.
  - Let the grout cure before testing, as your project specification requires.
  - **Never test in an ungrouted hole or one with air gaps.** Gaps delay the waves, weaken them and change their shape.
- **Depth.** The hole should be a few metres deeper than the deepest test depth. For Vs30, aim to measure to at least 30 m (100 ft) below the ground surface.
- **Verticality.** Keep the hole as vertical as possible. If a deviation survey exists, keep it with the survey record.

### On the day

1. Remove the cap. Lower a weighted tape to check the depth, and to confirm that the hole is clear all the way to the bottom.
2. If the hole is dirty, flush it with clean water and check the depth again.
3. Choose the depth reference: the ground surface, or the top of the casing (the collar). If you use the collar, measure the height of the collar above the ground ("stick-up"). Downhole Studio converts collar depths to depths below ground.
4. Note the ground elevation and coordinates of the borehole if they are known.

## 3. Lay out the sources

Place the P and S sources near the borehole, as close as is practical without disturbing the casing. Record each offset separately and accurately. Measure the **horizontal** distance from the centre of the borehole to the centre of each source.

{{diagram:borehole}}

### Source offsets

- A horizontal offset of **1–3 m (3–10 ft)** is typical.
- Closer than that, waves travelling down the casing and grout, and disturbed ground around the hole, can obscure the shallow arrivals.
- Further away, rays near the surface travel at a steep angle and bend at layer boundaries. The shallow velocities then depend more on the correction method.
- Keep each offset the same for the whole survey. If a source has to move, write down the new offset and from which depth it applies.
- If a source sits higher or lower than the ground at the collar (for example on a slope), note the height difference as the source depth.

> Downhole Studio uses the P and S offsets to correct the travel times for the slanted ray path. An offset that is wrong by 0.5 m (1.5 ft) produces clearly wrong velocities in the top few metres. Measure it twice.

### The P-wave source

Place the strike plate on firm, levelled ground at the P offset. A vertical hammer blow on the plate produces a strong P wave. Plant the reference geophone firmly beside the plate.

### The S-wave source

{{diagram:beam}}

Shear waves are made by striking a heavy beam horizontally:

1. Lay the beam on the ground at the S offset, with its long axis at right angles to the line from the borehole to the beam. This line is the **radial** direction.
2. Park a vehicle so that its wheels rest on the beam and press it firmly onto the ground. The beam must not slide when struck.
3. Strike one end horizontally, along the beam's axis. This is the **S+** (left) shot.
4. Then strike the opposite end. This is the **S−** (right) shot.

The shear wave reverses polarity between the two shots, while the P energy and most of the noise do not. Downhole Studio uses this to confirm and enhance the S arrival with the difference trace, (S+ − S−)/2.

> Hit the beam squarely on its end cap with the same force each time. If the beam moves or rocks, re-seat it before the next shot.

## 4. Connect and set up the seismograph

### Channels

Connect the channels in this order, which matches the **QuakeLogic 3C Borehole** channel profile in Downhole Studio:

| Channel | Sensor | Used for |
|---|---|---|
| 1 | Borehole geophone, vertical (Z) | P-wave arrivals |
| 2 | Borehole geophone, horizontal X | S-wave arrivals |
| 3 | Borehole geophone, horizontal Y | S-wave arrivals; rotation if the probe turns |
| 4 | Reference (spy) geophone at the source | The true moment of each hit (trigger check) |

If your instrument uses a different order, you can map the channels in Downhole Studio, and save the layout as a profile for future surveys.

### Recording settings

| Setting | Use | Why |
|---|---|---|
| Sample interval | 0.0625–0.125 ms (16,000–8,000 samples per second) | Arrival times are needed to a fraction of a millisecond at shallow depth |
| Record length | 0.25–0.5 s | Long enough for the slowest S wave at the deepest depth, plus margin |
| Pre-trigger (delay) | A few milliseconds, for example 2–10 ms | Shows the quiet signal just before the onset. Downhole Studio reads the delay from the file. |
| Acquisition filters | Off: no low-cut, high-cut or notch filters | Filters change the shape and timing of the onset. Processing is done later, without changing the data. |
| Gain | The same on all three borehole channels; avoid clipping | Clipped traces lose the onset shape |
| Stacking | 1 hit per record; 3–5 hits if the site is noisy | Stack repeated hits of the same type only. Never stack S+ with S−. |
| File format | SEG-2, one file per shot | Downhole Studio reads 16-bit and 32-bit integer and 32-bit and 64-bit floating-point SEG-2 files in either byte order. The packed 20-bit format is not supported: export as 32-bit instead. |

### File names

Downhole Studio suggests each file's depth and shot type from the file name, or from the `DEPTH` and `NOTE` keywords in the SEG-2 header. Use a consistent name for every shot, for example:

| Shot | File name |
|---|---|
| P at 12.0 m | `DH_012.0m_P.sg2` |
| S+ (left end) at 12.0 m | `DH_012.0m_SL.sg2` |
| S− (right end) at 12.0 m | `DH_012.0m_SR.sg2` |

- The depth is read from a number followed by `m` (for example `012.0m`).
- The shot type is read from `P`, `SL`, `S+` or `LEFT` (for S+), and `SR`, `S-` or `RIGHT` (for S−).

If your seismograph only numbers its files, that is fine. Record the shots in the same order at every depth, and write the file numbers on the field log sheet. Downhole Studio can then assign depths and shot types by acquisition order.

## 5. Record each depth

{{diagram:sequence}}

Work through the depths at a constant interval. **1 m (or 0.5 m, 2.5 ft or 5 ft)** is typical; a finer interval near the surface helps resolve thin layers. Testing from the bottom upward or from the top downward both work. Use one direction for the whole survey, and note it.

At each depth:

1. **Set the depth.** Lower or raise the sensor until its centre is at the test depth, read against the reference mark at the collar.
2. **Clamp.** Engage the clamp or inflate the packer. Wait a few seconds for the cable and sensor to settle.
3. **Record P.** Strike the plate. Check that the vertical channel shows a clear first arrival and that the reference channel shows the hit.
4. **Record S+.** Strike the left end of the beam.
5. **Record S−.** Strike the right end of the beam. On the horizontal channels, the S arrival should be reversed compared with S+.
6. **Check and repeat.** If a record is noisy, clipped or mistimed, record it again before moving. Note which file is the good one.
7. **Write it down.** Enter the depth, file names and any remarks (noise, a passing truck, a slipping beam) on the field log sheet.
8. **Release** the clamp and move to the next depth.

> At the end of the survey, re-record two or three depths. Repeated measurements that agree are the best evidence of a good survey.

### What a good record looks like

- **P record:** a sharp first arrival on the vertical channel, with little energy before it. The reference channel shows a sharp onset at almost the same time on every shot.
- **S+ and S− records:**
  - A later, lower-frequency arrival with larger amplitude on the horizontal channels.
  - Its polarity is clearly reversed between S+ and S−.
  - The early P energy looks the same in both records.
- **Arrivals move later with depth.** Arrivals that get earlier with depth, or jump suddenly, point to a wrong depth, a moved source or a trigger problem. Check before you move on.

## 6. Field QC and safety

### Field QC checklist

- The grout was cured, the hole was checked to the bottom, and the depth reference and collar stick-up are written down.
- The P and S offsets were measured from the borehole centre and written down. Neither source moved during the survey, or every move is written down.
- At every depth there is one good P record, one good S+ record and one good S− record.
- No record is clipped. The reference onset is sharp and consistent.
- S+ and S− show opposite polarity on the horizontal channels.
- Arrival times increase steadily with depth.
- Two or three depths were repeated at the end and agree with the first measurements.
- All files are copied to a second drive or the office before leaving site.

### Safety

- The hammer operator wears ear defenders, safety glasses, gloves and safety boots. Keep everyone clear of the swing.
- Never strike the beam while anyone is near the vehicle wheels. Set the vehicle's parking brake and chock its wheels.
- Keep the borehole capped when unattended, and keep hands and loose clothing clear of the cable reel.
- Follow the site's traffic, utility and permit rules, and work with a lookout near traffic.

## 7. Back at the office: from records to a report

Downhole Studio takes the records through twelve steps. The workflow strip at the top of the window shows where you are. The orange **Next** button always leads to the next step.

1. **Project.** Create a project. Enter the survey name, job number and date.
2. **Import.** Drag the folder of SEG-2 files onto the Import page. Review the inspection results: sample rates, missing channels, duplicate files and depth order. Nothing is repaired silently.
3. **Geometry.** Enter the P and S offsets, and the depth reference (ground surface or collar with stick-up). Assign depths and shot types from the file names or by acquisition order. Apply the *QuakeLogic 3C Borehole* channel profile.
4. **Waveforms.** Look at every trace. Check polarity, and look for dead or clipped traces.
5. **Processing and trigger.**
   - Keep processing light for picking.
   - Pick the reference onsets. Choose *Subtract reference onset* to remove the trigger delay and jitter.
6. **P and S picks.**
   - Run the automatic picker, then review every depth.
   - For S waves, use the S+ / S− overlay and the difference trace.
7. **Velocity model.** Add layer boundaries, or use *Suggest*. Prefer boundaries from the boring log where they exist, and choose the velocity method.
8. **Engineering.** Enter measured densities, or deliberately choose an estimate, to obtain the moduli. Read Vs30.
9. **Results.** Review the quality-control findings and the overall verdict.
10. **Report.** Fill in the client and site details, then generate the PDF report and export the tables.

The *Downhole Studio Brochure and User Guide* explains every page in detail. The *Training Video* shows the whole workflow once.

## 8. Install Downhole Studio

1. Run `DownholeStudio-Setup.exe` (in folder 1 of the client package). No administrator rights are needed.
2. If Windows shows *Windows protected your PC*, choose **More info → Run anyway**.
3. Start Downhole Studio. Click **Start 14-Day Free Trial**, or **Activate License** with the e-mail address and licence key you received.
4. Click **Explore Sample Project** to see a complete worked survey before you analyse your own.

## 9. Checklists and field log sheet

### Before you leave the office

- The seismograph, laptop and clamp batteries are charged, and the spares are packed.
- The borehole geophone has been checked channel by channel, and the clamp works.
- The reference geophone, trigger, hammer, plate, beam and end caps are packed.
- The depth-marked cable and the measuring tape are packed.
- The borehole details are known: depth, casing size, grout date, and the boring log.
- Printed field log sheets, a pencil and the safety equipment are packed.

### On site, before the first depth

- The hole has been checked to the bottom, and the depth reference and collar stick-up are written down.
- The P plate and the S beam are placed, and the offsets are measured and written down.
- The vehicle is parked on the beam, braked and chocked.
- The reference geophone is planted beside the source.
- Test shots have been recorded. All four channels and the trigger are working. Gain is set and nothing is clipped.

<!-- pagebreak -->

### Field log sheet

Site: ____________________ Borehole: __________ Date: __________ Operator: ____________________

P offset: ______ m   S offset: ______ m   Depth reference: ground / collar (stick-up ______ m)

Sample interval: ______ ms   Record length: ______ s   Depth interval: ______ m   Direction: top-down / bottom-up

| Depth (m) | P file | S+ file | S− file | Remarks (noise, repeats, source moves) |
|---|---|---|---|---|
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |

