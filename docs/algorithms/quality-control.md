# Quality control

Code: `src/DownholeStudio.Analysis/Qc/QualityAnalyzer.cs`. Checks flag; they never change data.

| Check | Severity | Rule (defaults, configurable) |
|---|---|---|
| Low SNR | red < 3, yellow < 6 | pick SNR |
| Weak onset | yellow | sharpness < 0.35 |
| Low confidence | yellow | automatic pick scored Low (with the reasons) |
| Unreviewed automatic pick | note | automatic pick not accepted |
| Large residual | yellow > limit, red > 2×limit | P 0.5 ms, S 1.5 ms, once layers are interpreted |
| Arrival earlier at a deeper receiver | red | corrected time decreases with slant distance |
| Unusual velocity jump | yellow | adjacent pseudo-interval velocities differ by > 2.5× |
| Invalid interval | yellow | Δt ≤ 0 or Δz ≤ 0 |
| No opposite-polarity confirmation | yellow | S⁺ or S⁻ missing |
| Same polarity in S⁺ and S⁻ | red | correlation > 0 around the S pick |
| Poor opposite-shot correlation | yellow | |r| < 0.6 |
| Possible incorrect component | yellow | horizontal energy > 3× vertical around a P pick, or the reverse for S |
| Trigger timing | red/yellow/note | reference onset deviates from the median by > 0.5 ms (see trigger-correction.md) |
| Missing reference onset | red | correction enabled but no onset |
| Poor regression | yellow | R² < 0.95 with ≥ 3 points |
| Layer without velocity | yellow | reason given |
| Elastic consistency | red/yellow | see engineering.md |
| Vs30 unavailable / extrapolated | yellow | |

**Verdict.** *Incomplete* when no data, no depths, fewer than two usable P or S arrivals, fewer than
half the depths with S, or no layer velocities. *Review recommended* when any red flag, more than
20 % of picks with yellow pick flags, Vs30 unavailable or extrapolated, or unreviewed automatic
picks. Otherwise *Good*. The verdict is advisory and does not certify compliance with any standard.
