# Algorithms

Every calculation in Downhole Studio is listed here with its equation, assumptions, limitations and
the source of the method. Code references point to the implementation; the tests in
`tests/DownholeStudio.*.Tests` and the validation report in `docs/validation` check each one.

| Topic | Document |
|---|---|
| Signal processing (filters, AGC, resampling, spectra, correlation, rotation) | [signal-processing.md](signal-processing.md) |
| First-arrival picking and pick confidence | [picking.md](picking.md) |
| Reference geophone / trigger correction | [trigger-correction.md](trigger-correction.md) |
| Travel-time geometry and velocity methods | [velocity.md](velocity.md) |
| Layer interpretation and boundary suggestions | [layers.md](layers.md) |
| Vs30, engineering parameters and density | [engineering.md](engineering.md) |
| Quality control | [quality-control.md](quality-control.md) |

Units: all calculations use SI (m, s, m/s, kg/m³, Pa) in double precision. Display units are applied
only at the user interface and in exports (`Domain/Units/Units.cs`); converted values are never fed
back into calculations.
