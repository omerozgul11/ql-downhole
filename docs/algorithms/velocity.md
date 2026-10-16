# Travel-time geometry and velocity methods

Code: `src/DownholeStudio.Analysis/Geometry`, `src/DownholeStudio.Analysis/Velocity`.

## Geometry
z = receiver depth below ground (measured depth minus collar height when depths are measured from
the collar), z_s = source depth below ground, x = horizontal source offset (separate for P and S).

* Slant distance: R = √((z − z_s)² + x²)
* Corrected (vertical-equivalent) time: t_v = t·(z − z_s)/R
* Average velocity to a receiver: V̄ = R/t

Assumption for t_v: a straight ray in a medium whose velocity does not vary along the ray. Near the
surface below a slow layer, rays bend; use the refracted-ray method there.

## Layer regression (default)
In each interpreted layer, fit t_v = a + z/V to the receivers inside the layer (receivers exactly on
a boundary are used by both layers): V = 1/slope. Ordinary least squares, or Huber M-estimation by
IRLS with the scale re-estimated each iteration as 1.4826·MAD (k = 1.345 by default). Reported: V,
±1σ from the slope standard error (σ_V = σ_slope/slope²), R², n, RMS residual.

## Pseudo-interval
V = (z₂ − z₁)/(t_v,₂ − t_v,₁) between adjacent usable receivers. Sensitive to small pick errors.

## True interval (straight rays)
V = (R₂ − R₁)/(t₂ − t₁) with trigger-corrected times. Assumes straight rays through a uniform medium
between source and receiver; biased below slower layers where rays are oblique (validated only where
z/x ≥ 4).

For the interval methods the layer velocity spans the first and last usable receivers inside the
layer (the same formula over the layer).

## Refracted-ray layer stripping
Rays obey Snell's law through horizontal layers (constant ray parameter p = sin θⱼ/vⱼ). For a
receiver at depth z with offset x the ray parameter solves
X(p) = Σ hⱼ·p·vⱼ/√(1 − p²vⱼ²) = x (monotonic in p; bisection), and the travel time is
t = Σ hⱼ/(vⱼ√(1 − p²vⱼ²)). Layers are solved from the top down: each layer's velocity minimises the
squared misfit between observed (trigger-corrected) times and ray times for the receivers it
contains, holding the layers above fixed (log-spaced grid 10–10 000 m/s refined by golden-section
search). Head waves are not modelled; a layer without receivers blocks the layers below. The
ray-tracer agrees with an independent Fermat-principle minimiser to 10⁻⁹ s (validation report §2).

## Excluded observations
Picks can be excluded (or a depth or record excluded). Excluded observations stay visible (crosses
on the plots), are listed with the reason in the report and audit record, and are not used in any
fit.
