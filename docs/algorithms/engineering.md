# Vs30, engineering parameters and density

Code: `src/DownholeStudio.Analysis/Engineering/Engineering.cs`.

## Time-averaged velocity and Vs30
V(z_t, z_b) = (z_b − z_t) / Σ hᵢ/Vᵢ over the layer portions inside [z_t, z_b]; Vs30 = V(0, 30 m).
If the deepest usable S measurement is shallower than z_b, the value is **not calculated** and the
reason is shown ("Insufficient measured depth for direct Vs30 calculation"). Only if the user
explicitly allows extrapolation is the deepest layer's Vs extended to z_b; the value is then labelled
EXTRAPOLATED in the application, tables, plots and the report. The first layer's velocity is assumed
to apply from the ground surface; a note is raised when the shallowest measurement is deeper than
2 m.

## Elastic properties (isotropic, small strain)
* Poisson's ratio ν = (Vp² − 2Vs²)/(2(Vp² − Vs²))
* Shear modulus G = ρVs²
* Young's modulus E = 2G(1 + ν)
* Bulk modulus K = ρ(Vp² − 4Vs²/3)
* Constrained modulus M = ρVp²

The equivalent forms E = ρVs²(3Vp² − 4Vs²)/(Vp² − Vs²), K = E/(3(1 − 2ν)), G = E/(2(1 + ν)) and
K + 4G/3 = M are checked numerically in the tests.

Safety rules (values withheld and flagged, never corrected): Vs ≥ Vp; Vp ≈ Vs (ν undefined);
K ≤ 0 (Vp/Vs ≤ 2/√3); E ≤ 0; missing density (moduli not calculated). ν < 0 is reported as
unusual. When Vp > 1400 m/s and Vp/Vs > 3, a note explains that below the water table Vp reflects
the pore fluid.

## Density
Never assumed. Sources:
* **Measured** — per layer, entered by the user.
* **Constant** — one value entered by the user.
* **Estimated** — only when explicitly selected; labelled ESTIMATED:
  * Gardner, Gardner & Gregory (1974): ρ = 1.74·Vp^0.25 (g/cm³, Vp in km/s); calibrated on
    brine-saturated sedimentary rocks, 1.5–6.1 km/s.
  * Brocher (2005) Nafe–Drake fit: ρ = 1.6612Vp − 0.4721Vp² + 0.0671Vp³ − 0.0043Vp⁴ + 0.000106Vp⁵;
    1.5–8.5 km/s.
  Outside the calibration range a warning is attached. Both relations were developed for rocks and
  are approximate for soils.

## Units
US customary display uses exact factors: 1 ft = 0.3048 m, 1 lbm = 0.45359237 kg (1 pcf =
16.0184634 kg/m³), standard gravity 9.80665 m/s² (1 psi = 6894.757 Pa, 1 ksf = 47 880.26 Pa).
