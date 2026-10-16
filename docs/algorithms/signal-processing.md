# Signal processing

Code: `src/DownholeStudio.SignalProcessing`, applied by `Analysis/Traces/TraceProcessor.cs`.
All operations return new arrays; recorded samples are never modified.

## Order of operations
channel-map polarity → enabled pipeline steps in order → per-trace polarity → per-trace static shift.
Display gain is applied only when drawing.

## Mean removal and linear detrend
Mean: x′ᵢ = xᵢ − x̄ (Kahan-compensated sum). Detrend: least-squares line a + b·i removed, with the
index centred so the normal equations are diagonal.

## Taper
Hann (raised-cosine) ramp w = ½(1 − cos(πi/m)) over m = ⌊f·N⌋ samples at each end (f ≤ 0.5).
Limitation: arrivals inside the tapered portion are attenuated; the UI warns about this.

## Butterworth filters
Analogue prototype poles pₖ = exp(iπ(2k + N + 1)/(2N)), k = 0…N−1. Low-pass s = ωc·p, high-pass
s = ωc/p, band-pass from the roots of s² − p·B·s + ω₀² = 0 (B = ω₂ − ω₁, ω₀² = ω₁ω₂). Corners are
pre-warped, ω = 2fs·tan(πf/fs), and mapped with the bilinear transform z = (2fs + s)/(2fs − s).
Zeros at s = ∞ map to z = −1, zeros at s = 0 to z = +1. Conjugate poles are paired into second-order
sections and the gain is normalised at DC (low-pass), Nyquist (high-pass) or the pre-warped
geometric centre (band-pass). The corner frequency is the −3 dB point.

Causal filtering uses direct form II transposed. Zero-phase filtering runs the cascade forward and
backward over an odd reflection of length 3(2·sections + 1) with steady-state initial conditions
(for a biquad: z₂ = b₂ − a₂G, z₁ = b₁ − a₁G + z₂ for unit input, G the DC gain), giving |H|² and
zero phase. Results match SciPy 1.18 `butter(..., output='sos')`, `sosfilt` and `sosfiltfilt`
to 1e−8 (tests: `FilterTests`).

**Limitation:** zero-phase filters are acausal — energy can appear slightly before a sharp onset;
causal filters delay the waveform. Pick first breaks on raw or lightly processed data.

## Notch
Bilinear notch with exact −3 dB bandwidth f₀/Q: β = tan(ω₀/(2Q)), g = 1/(1 + β),
b = g·[1, −2cos ω₀, 1], a = [1, −2g cos ω₀, 2g − 1] (equivalent to SciPy `iirnotch`). Optional
integer harmonics below Nyquist.

## Normalisation, balance and AGC
Peak: divide by max |x|. RMS: divide by √(Σx²/N). Balance: RMS in a user window = 1. AGC: each
sample divided by the RMS in a centred window (prefix sums; samples whose RMS is below 10⁻⁶ of the
global RMS are set to zero). AGC alters relative amplitudes and is a display aid.

## Time shift
Whole-sample shifts are exact. Fractional shifts multiply the spectrum of a zero-padded copy
(length ≥ 2N + |shift|) by exp(−2πi·f·τ) with a real Nyquist bin, so the waveform does not wrap.

## Resampling
Windowed-sinc interpolation (Blackman window, ±16 zero crossings) normalised by the kernel sum.
When downsampling, the kernel cut-off is 0.9 × the new Nyquist frequency (anti-aliasing).

## Spectrum
Single-sided amplitude |X(f)|·2/N of a Hann-windowed trace with window-gain compensation. Dominant
frequency = largest amplitude above the minimum frequency; bandwidth = contiguous band around the
peak above a fraction of the peak (−3 dB: 1/√2; −6 dB: 1/2). FFT: radix-2 for powers of two,
Bluestein's chirp-z for other lengths (exact for any N).

## Envelope
|x + iH{x}| from the FFT analytic signal.

## Cross-correlation
r(τ) = Σ a(t)·b(t + τ) / √(Σa²·Σb²) via FFT, peak refined by parabolic interpolation. The reported
coefficient for windowed analyses is the Pearson correlation of the window of *a* with *b* shifted
by the best lag. Positive lag ⇒ *b* is later than *a*. Polarity-insensitive mode maximises |r|.

## Horizontal rotation
R = X cos θ + Y sin θ, T = −X sin θ + Y cos θ (θ from X towards Y). Applied only when the user
chooses an angle; the angle is stored per depth and reported.
