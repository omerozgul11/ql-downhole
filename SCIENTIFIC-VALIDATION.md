# Scientific validation

Scientific correctness takes precedence over producing an answer. When a value cannot be computed
reliably, Downhole Studio states why instead of producing a number.

## Method
`dhs validate --out docs/validation` (run by `scripts/build.sh`) produces
[docs/validation/validation-report.md](docs/validation/validation-report.md) and a JSON copy. Every
row lists the input, the expected value, the value Downhole Studio calculated, the difference, the
tolerance and the result. Expected values come from three independent sources:

1. **Hand calculation** for each formula (straight-ray distance, vertical time, interval velocity,
   Vs30, Poisson's ratio, G, E, K, density estimate, unit conversions, trigger correction).
2. **An independent implementation** for ray tracing: the engine solves Snell's law by bisection on
   the ray parameter; the synthetic generator minimises travel time over interface crossing points
   (Fermat's principle) with Newton iterations. They agree to 10⁻⁹ s.
3. **Known synthetic earth models**: five SEG-2 datasets (clean, noisy, missing traces, trigger
   errors, reversed horizontal polarity) are processed exactly as a user would (import, geometry,
   reference picks, trigger correction, automatic picks, layers, refracted-ray velocities) and the
   pick times, layer Vp and Vs and Vs30 are compared with the model.

## Current result
87 of 87 checks pass (version 1.0.0). Highlights: S-pick RMS error 0.09–0.21 ms on clean data and
0.94 ms on noisy data; layer Vs within 4 % (8 % noisy) of the model; Vs30 within 0.2–1.2 % of the
true 367.6 m/s.

## Known limitations
* Straight-ray methods (pseudo- and true interval, vertical-time correction) are biased where rays
  are oblique below slower layers; the true-interval method is only validated where depth/offset ≥ 4.
  Use the refracted-ray method near the surface.
* The refracted-ray model ignores head waves and dipping layers.
* Automatic picks on emergent onsets tend to be slightly late (0.1–0.3 ms in the clean tests);
  zero-phase filtering before picking can make them early. Review picks on lightly processed data.
* Suggested layer boundaries can be misplaced on noisy data; the analyst decides.
* Empirical density relations were calibrated on rocks.
* Validation uses synthetic data. Before production use, QuakeLogic should add comparisons with
  field surveys interpreted independently (e.g. with established software or published case
  histories) under the same reporting format.

## Relation to standards
The workflow follows established downhole practice (ASTM D7400/D7400M: source offset correction,
vertical travel-time correction, interval and layer velocities, P and S identification with
opposite shots). The software does not reproduce the standard and does not certify compliance; the
user remains responsible for professional judgment and applicable requirements.
