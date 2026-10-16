# Reference geophone and trigger correction

Code: `src/DownholeStudio.Analysis/Trigger/TriggerCorrector.cs`.

A reference (spy) geophone at the source records the moment of impact in every record. Its onset
t_ref is picked automatically (AIC over the first 50 ms, including pre-trigger samples) or by hand.

| Mode | Correction added to raw times | Use |
|---|---|---|
| None | 0 | trigger is reliable |
| Subtract reference onset | −t_ref + t_expected | times measured from the impact; t_expected = travel time from source to reference geophone (normally ≈ 0) |
| Align to median | −(t_ref − median(t_ref)) | remove shot-to-shot jitter but keep the common trigger offset |

Raw time, reference onset, correction and corrected time are kept and reported for every depth.
A record without a reference onset cannot be corrected; its arrivals are excluded from the
velocity analysis with the reason shown (never silently used uncorrected). When repeated shots are
stacked, or S⁻ is combined with S⁺, the traces are first aligned by the difference of their trigger
corrections.

QC flags reference onsets that differ from the median by more than the limit (default 0.5 ms):
as a likely problem when no correction is applied, as a note when the correction removes it, and
for review when the deviation exceeds four times the limit (possible reference mispick).
