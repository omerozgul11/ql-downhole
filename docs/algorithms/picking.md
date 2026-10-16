# First-arrival picking

Code: `src/DownholeStudio.Analysis/Picking`.

All picks are stored in raw record time (seconds after the recorded trigger). Corrections are
applied in the analysis, never written into the pick.

## AIC picker (default)
Maeda (1985) direct AIC on the search window x[0…N−1]:

AIC(k) = k·ln var(x[0…k]) + (N − k − 1)·ln var(x[k+1…N−1])

The onset is the global minimum. Because a window that also contains the decay of the arrival has a
second variance change (signal → quiet), the window is ended shortly after the largest amplitude
(peak + max(8, ¼ of the distance from the window start)). Sharpness = rise of AIC 5 % of the window
either side of the minimum relative to 15 % of the AIC range (0–1).

## STA/LTA
Allen (1978): ratio of the mean energy in a trailing short window ending at the sample to that in a
long window immediately before it. The onset is the start of the rise (last local minimum of the
ratio) before the first crossing of the trigger level.

## Modified energy ratio
Han, Wong & Liu (2009): ER(i) = Σ_{i…i+L} x² / Σ_{i−L…i} x², MER(i) = (ER(i)·|x(i)|)³; onset at the
maximum of MER.

## Search window
From the record start (including pre-trigger samples) or a user time, to a user time or the record
end. With neighbour prediction, the search is restricted to ±2× the prediction half-width around the
time predicted from a straight line of time versus slant distance through the nearest picked depths
(processed top to bottom).

## Signal-to-noise ratio
RMS amplitude in [t, t + signal window) divided by RMS in [t − noise window, t).

## Confidence
Weighted mean of the available factors (weights renormalised when a factor is missing):

| Factor | Weight | Score |
|---|---|---|
| SNR | 0.35 | (log₁₀ SNR − log₁₀ 2)/(log₁₀ 12 − log₁₀ 2), clamped to 0–1 |
| Onset sharpness | 0.20 | picker sharpness |
| Algorithm agreement | 0.20 | 1 if the closest other algorithm is within max(3 samples, 0.3 ms P / 1 ms S), decreasing linearly to 0 at 5× that |
| Neighbour consistency | 0.15 | exp(−(Δ/(2·limit))²), Δ = difference from the neighbour prediction, limit = QC residual limit |
| Opposite-polarity agreement (S) | 0.10 | −r between S⁺ and the aligned S⁻ |

High ≥ 0.75, Medium ≥ 0.45, Low otherwise (thresholds configurable). Each factor carries a sentence
explaining its score; the inspector shows them.

## S-wave traces
S⁺ and S⁻ shots of the chosen horizontal component (X, Y or rotated by the per-depth angle) are
stacked per polarity when repeated, the S⁻ shot is shifted into the S⁺ trigger time base (difference
of trigger corrections) and the half-difference (S⁺ − S⁻)/2 is formed. Energy that reverses with the
source direction (SH waves) is kept; energy common to both shots (P waves, noise) cancels.

## Manual picks
Manual picks are snapped to the nearest sample of the trace (picks are not finer than the data).
Re-running the automatic picker never replaces a manual pick unless requested; the new suggestion is
stored next to it and can be accepted or restored.
