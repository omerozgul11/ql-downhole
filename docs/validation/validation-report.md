# Downhole Studio — scientific validation report

Generated 2026-10-10 23:42 UTC by Downhole Studio 1.0.0 (`dhs validate`). 87 of 87 checks passed.

Expected values come from hand calculation, from an independent implementation (Fermat-principle ray timing in the synthetic generator) or from the known synthetic earth model. Tolerances are absolute unless marked %.

## 1. Formulas (hand calculation)

| Check | Input | Expected | Actual | Difference | Tolerance | Result | Expected from |
|---|---|---:|---:|---:|---:|---|---|
| Slant distance R = √((z−zs)²+x²) | z=4, zs=0, x=3 m | 5.0000 m | 5.0000 m | 0.000000 | 0.000000 m | PASS | 3-4-5 triangle |
| Vertical time t·z/R | t=10 ms, z=4, x=3 | 0.008000 s | 0.008000 s | 0.000000 | 0.000000 s | PASS | 0.010×4/5 |
| Interval velocity (R₂−R₁)/(t₂−t₁) | R 5→10 m, t 10→20 ms | 500.0000 m/s | 500.0000 m/s | 0.000000 | 0.000000 m/s | PASS | 5/0.010 |
| Vs30 = 30/Σ(h/V) | 5 m@200, 10 m@300, 15 m@500 | 339.6226 m/s | 339.6226 m/s | 0.000000 | 0.000001 m/s | PASS | 30/0.0883333 |
| Poisson ν=(Vp²−2Vs²)/(2(Vp²−Vs²)) | Vp 1500, Vs 300 | 0.479167  | 0.479167  | -0.000000 | 0.000000  | PASS | 2.07e6/4.32e6 |
| Shear modulus G=ρVs² | ρ 1900, Vs 300 | 171000000.0 Pa | 171000000.0 Pa | 0.000000 | 0.001000 Pa | PASS | 1900×300² |
| Young's modulus E=2G(1+ν) | as above | 505875000.0 Pa | 505875000.0 Pa | 0.000000 | 1.0000 Pa | PASS | ρVs²(3Vp²−4Vs²)/(Vp²−Vs²) |
| Bulk modulus K=ρ(Vp²−4Vs²/3) | as above | 4047000000.0 Pa | 4047000000.0 Pa | 0.000000 | 1.0000 Pa | PASS | 1900×(2.25e6−1.2e5) |
| Gardner density | Vp 2000 m/s | 2069.2 kg/m³ | 2069.2 kg/m³ | 0.020380 | 0.100000 kg/m³ | PASS | 1.74×2^0.25 g/cm³ |
| ft → m | 1 ft | 0.304800 m | 0.304800 m | 0.000000 | 0.000000 m | PASS | exact definition |
| pcf → kg/m³ | 1 lbm/ft³ | 16.0185 kg/m³ | 16.0185 kg/m³ | 0.000000 | 0.000000 kg/m³ | PASS | 0.45359237/0.3048³ |
| ksf → Pa | 1 kip/ft² | 47880.3 Pa | 47880.3 Pa | 0.000000 | 0.000001 Pa | PASS | 4448.2216/0.09290304 |
| psi → Pa | 1 lbf/in² | 6894.8 Pa | 6894.8 Pa | 0.000000 | 0.000001 Pa | PASS | 4.4482216/0.0254² |
| Trigger correction t − t_ref | t=15.0 ms, t_ref=1.2 ms | 0.013800 s | 0.013800 s | 0.000000 | 0.000000 s | PASS | 15.0 − 1.2 ms |

## 2. Ray tracing (independent implementations)

| Check | Input | Expected | Actual | Difference | Tolerance | Result | Expected from |
|---|---|---:|---:|---:|---:|---|---|
| S travel time, Snell (engine) vs Fermat (generator) | z=1 m, x=2 m | 0.013975 s | 0.013975 s | 0.000000 | 0.000000 s | PASS | Fermat minimisation |
| S travel time, Snell (engine) vs Fermat (generator) | z=2.5 m, x=2 m | 0.020010 s | 0.020010 s | 0.000000 | 0.000000 s | PASS | Fermat minimisation |
| S travel time, Snell (engine) vs Fermat (generator) | z=6 m, x=3 m | 0.033627 s | 0.033627 s | 0.000000 | 0.000000 s | PASS | Fermat minimisation |
| S travel time, Snell (engine) vs Fermat (generator) | z=12 m, x=2 m | 0.049108 s | 0.049108 s | 0.000000 | 0.000000 s | PASS | Fermat minimisation |
| S travel time, Snell (engine) vs Fermat (generator) | z=20 m, x=4 m | 0.067138 s | 0.067138 s | 0.000000 | 0.000000 s | PASS | Fermat minimisation |
| S travel time, Snell (engine) vs Fermat (generator) | z=31 m, x=2 m | 0.083058 s | 0.083058 s | 0.000000 | 0.000000 s | PASS | Fermat minimisation |
| S travel time, Snell (engine) vs Fermat (generator) | z=31 m, x=8 m | 0.085081 s | 0.085081 s | 0.000000 | 0.000000 s | PASS | Fermat minimisation |

## 3. Synthetic dataset — Clean

| Check | Input | Expected | Actual | Difference | Tolerance | Result | Expected from |
|---|---|---:|---:|---:|---:|---|---|
| P pick RMS error vs true onset | 32 picks | 0.000000 s | 0.000065 s | 0.000065 | 0.000250 s | PASS | generator onset times |
| S pick RMS error vs true onset | 32 picks | 0.000000 s | 0.000192 s | 0.000192 | 0.000500 s | PASS | generator onset times |
| Vs 0–3 m (refracted ray) | true layer model | 160.0000 m/s | 159.2894 m/s | -0.710563 | 4 % | PASS | synthetic model |
| Vp 0–3 m (refracted ray) | true layer model | 420.0000 m/s | 418.8895 m/s | -1.1105 | 6 % | PASS | synthetic model |
| Vs 3–8 m (refracted ray) | true layer model | 260.0000 m/s | 259.3576 m/s | -0.642424 | 4 % | PASS | synthetic model |
| Vp 3–8 m (refracted ray) | true layer model | 900.0000 m/s | 899.3360 m/s | -0.663983 | 6 % | PASS | synthetic model |
| Vs 8–15 m (refracted ray) | true layer model | 380.0000 m/s | 379.2540 m/s | -0.745987 | 4 % | PASS | synthetic model |
| Vp 8–15 m (refracted ray) | true layer model | 1600.0 m/s | 1588.8 m/s | -11.2003 | 6 % | PASS | synthetic model |
| Vs 15–24 m (refracted ray) | true layer model | 520.0000 m/s | 516.7169 m/s | -3.2831 | 4 % | PASS | synthetic model |
| Vp 15–24 m (refracted ray) | true layer model | 1850.0 m/s | 1841.8 m/s | -8.2353 | 6 % | PASS | synthetic model |
| Vs 24–32 m (refracted ray) | true layer model | 760.0000 m/s | 765.1997 m/s | 5.1997 | 4 % | PASS | synthetic model |
| Vp 24–32 m (refracted ray) | true layer model | 2400.0 m/s | 2401.3 m/s | 1.2645 | 6 % | PASS | synthetic model |
| Vs30 | true layer model | 367.6279 m/s | 366.6223 m/s | -1.0056 | 2 % | PASS | 30/Σ(h/Vs) of the model |

## 3. Synthetic dataset — Noisy

| Check | Input | Expected | Actual | Difference | Tolerance | Result | Expected from |
|---|---|---:|---:|---:|---:|---|---|
| P pick RMS error vs true onset | 32 picks | 0.000000 s | 0.000627 s | 0.000627 | 0.000750 s | PASS | generator onset times |
| S pick RMS error vs true onset | 32 picks | 0.000000 s | 0.000943 s | 0.000943 | 0.001500 s | PASS | generator onset times |
| Vs 0–3 m (refracted ray) | true layer model | 160.0000 m/s | 155.6718 m/s | -4.3282 | 8 % | PASS | synthetic model |
| Vp 0–3 m (refracted ray) | true layer model | 420.0000 m/s | 410.5518 m/s | -9.4482 | 12 % | PASS | synthetic model |
| Vs 3–8 m (refracted ray) | true layer model | 260.0000 m/s | 258.8661 m/s | -1.1339 | 8 % | PASS | synthetic model |
| Vp 3–8 m (refracted ray) | true layer model | 900.0000 m/s | 871.1064 m/s | -28.8936 | 12 % | PASS | synthetic model |
| Vs 8–15 m (refracted ray) | true layer model | 380.0000 m/s | 373.5752 m/s | -6.4248 | 8 % | PASS | synthetic model |
| Vp 8–15 m (refracted ray) | true layer model | 1600.0 m/s | 1595.7 m/s | -4.3345 | 12 % | PASS | synthetic model |
| Vs 15–24 m (refracted ray) | true layer model | 520.0000 m/s | 516.1414 m/s | -3.8586 | 8 % | PASS | synthetic model |
| Vp 15–24 m (refracted ray) | true layer model | 1850.0 m/s | 1783.1 m/s | -66.8894 | 12 % | PASS | synthetic model |
| Vs 24–32 m (refracted ray) | true layer model | 760.0000 m/s | 764.6861 m/s | 4.6861 | 8 % | PASS | synthetic model |
| Vp 24–32 m (refracted ray) | true layer model | 2400.0 m/s | 2112.9 m/s | -287.1074 | 12 % | PASS | synthetic model |
| Vs30 | true layer model | 367.6279 m/s | 363.1626 m/s | -4.4653 | 5 % | PASS | 30/Σ(h/Vs) of the model |

## 3. Synthetic dataset — MissingTraces

| Check | Input | Expected | Actual | Difference | Tolerance | Result | Expected from |
|---|---|---:|---:|---:|---:|---|---|
| P pick RMS error vs true onset | 31 picks | 0.000000 s | 0.000084 s | 0.000084 | 0.000250 s | PASS | generator onset times |
| S pick RMS error vs true onset | 32 picks | 0.000000 s | 0.000198 s | 0.000198 | 0.000500 s | PASS | generator onset times |
| Vs 0–3 m (refracted ray) | true layer model | 160.0000 m/s | 159.2894 m/s | -0.710563 | 4 % | PASS | synthetic model |
| Vp 0–3 m (refracted ray) | true layer model | 420.0000 m/s | 418.8895 m/s | -1.1105 | 6 % | PASS | synthetic model |
| Vs 3–8 m (refracted ray) | true layer model | 260.0000 m/s | 259.4429 m/s | -0.557104 | 4 % | PASS | synthetic model |
| Vp 3–8 m (refracted ray) | true layer model | 900.0000 m/s | 892.8007 m/s | -7.1993 | 6 % | PASS | synthetic model |
| Vs 8–15 m (refracted ray) | true layer model | 380.0000 m/s | 377.9514 m/s | -2.0486 | 4 % | PASS | synthetic model |
| Vp 8–15 m (refracted ray) | true layer model | 1600.0 m/s | 1604.9 m/s | 4.8840 | 6 % | PASS | synthetic model |
| Vs 15–24 m (refracted ray) | true layer model | 520.0000 m/s | 520.8402 m/s | 0.840190 | 4 % | PASS | synthetic model |
| Vp 15–24 m (refracted ray) | true layer model | 1850.0 m/s | 1830.3 m/s | -19.7116 | 6 % | PASS | synthetic model |
| Vs 24–32 m (refracted ray) | true layer model | 760.0000 m/s | 754.7071 m/s | -5.2929 | 4 % | PASS | synthetic model |
| Vp 24–32 m (refracted ray) | true layer model | 2400.0 m/s | 2379.8 m/s | -20.2067 | 6 % | PASS | synthetic model |
| Vs30 | true layer model | 367.6279 m/s | 366.4951 m/s | -1.1328 | 2 % | PASS | 30/Σ(h/Vs) of the model |
| Missing P record reported, not invented | record 6:P omitted | 1.0000 depths | 1.0000 depths | 0.000000 | 0.000000 depths | PASS | dataset definition |

## 3. Synthetic dataset — TriggerErrors

| Check | Input | Expected | Actual | Difference | Tolerance | Result | Expected from |
|---|---|---:|---:|---:|---:|---|---|
| P pick RMS error vs true onset | 32 picks | 0.000000 s | 0.000057 s | 0.000057 | 0.000250 s | PASS | generator onset times |
| S pick RMS error vs true onset | 32 picks | 0.000000 s | 0.000087 s | 0.000087 | 0.000500 s | PASS | generator onset times |
| Vs 0–3 m (refracted ray) | true layer model | 160.0000 m/s | 160.7160 m/s | 0.716024 | 4 % | PASS | synthetic model |
| Vp 0–3 m (refracted ray) | true layer model | 420.0000 m/s | 421.6210 m/s | 1.6210 | 6 % | PASS | synthetic model |
| Vs 3–8 m (refracted ray) | true layer model | 260.0000 m/s | 258.4251 m/s | -1.5749 | 4 % | PASS | synthetic model |
| Vp 3–8 m (refracted ray) | true layer model | 900.0000 m/s | 900.6537 m/s | 0.653698 | 6 % | PASS | synthetic model |
| Vs 8–15 m (refracted ray) | true layer model | 380.0000 m/s | 380.9839 m/s | 0.983860 | 4 % | PASS | synthetic model |
| Vp 8–15 m (refracted ray) | true layer model | 1600.0 m/s | 1578.3 m/s | -21.7148 | 6 % | PASS | synthetic model |
| Vs 15–24 m (refracted ray) | true layer model | 520.0000 m/s | 515.2687 m/s | -4.7313 | 4 % | PASS | synthetic model |
| Vp 15–24 m (refracted ray) | true layer model | 1850.0 m/s | 1838.8 m/s | -11.1929 | 6 % | PASS | synthetic model |
| Vs 24–32 m (refracted ray) | true layer model | 760.0000 m/s | 762.8393 m/s | 2.8393 | 4 % | PASS | synthetic model |
| Vp 24–32 m (refracted ray) | true layer model | 2400.0 m/s | 2368.5 m/s | -31.5470 | 6 % | PASS | synthetic model |
| Vs30 | true layer model | 367.6279 m/s | 367.1078 m/s | -0.520155 | 2 % | PASS | 30/Σ(h/Vs) of the model |

## 3. Synthetic dataset — ReversedPolarityS

| Check | Input | Expected | Actual | Difference | Tolerance | Result | Expected from |
|---|---|---:|---:|---:|---:|---|---|
| P pick RMS error vs true onset | 32 picks | 0.000000 s | 0.000071 s | 0.000071 | 0.000250 s | PASS | generator onset times |
| S pick RMS error vs true onset | 32 picks | 0.000000 s | 0.000208 s | 0.000208 | 0.000500 s | PASS | generator onset times |
| Vs 0–3 m (refracted ray) | true layer model | 160.0000 m/s | 159.0698 m/s | -0.930174 | 4 % | PASS | synthetic model |
| Vp 0–3 m (refracted ray) | true layer model | 420.0000 m/s | 418.8895 m/s | -1.1105 | 6 % | PASS | synthetic model |
| Vs 3–8 m (refracted ray) | true layer model | 260.0000 m/s | 259.9139 m/s | -0.086134 | 4 % | PASS | synthetic model |
| Vp 3–8 m (refracted ray) | true layer model | 900.0000 m/s | 899.3360 m/s | -0.663983 | 6 % | PASS | synthetic model |
| Vs 8–15 m (refracted ray) | true layer model | 380.0000 m/s | 378.6849 m/s | -1.3151 | 4 % | PASS | synthetic model |
| Vp 8–15 m (refracted ray) | true layer model | 1600.0 m/s | 1590.0 m/s | -9.9691 | 6 % | PASS | synthetic model |
| Vs 15–24 m (refracted ray) | true layer model | 520.0000 m/s | 517.7024 m/s | -2.2976 | 4 % | PASS | synthetic model |
| Vp 15–24 m (refracted ray) | true layer model | 1850.0 m/s | 1826.0 m/s | -23.9547 | 6 % | PASS | synthetic model |
| Vs 24–32 m (refracted ray) | true layer model | 760.0000 m/s | 758.8371 m/s | -1.1629 | 4 % | PASS | synthetic model |
| Vp 24–32 m (refracted ray) | true layer model | 2400.0 m/s | 2421.4 m/s | 21.3970 | 6 % | PASS | synthetic model |
| Vs30 | true layer model | 367.6279 m/s | 366.4205 m/s | -1.2074 | 2 % | PASS | 30/Σ(h/Vs) of the model |
