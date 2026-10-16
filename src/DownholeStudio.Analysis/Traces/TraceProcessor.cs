using DownholeStudio.Domain;
using DownholeStudio.SignalProcessing;

namespace DownholeStudio.Analysis.Traces;

/// <summary>A processed copy of a trace. Time of sample i is Delay + i·SampleInterval.</summary>
public sealed record ProcessedTrace(double[] Samples, double SampleInterval, double Delay, IReadOnlyList<string> Warnings)
{
    public double TimeAt(int index) => Delay + index * SampleInterval;

    public int IndexAt(double time) => (int)Math.Round((time - Delay) / SampleInterval);

    public double EndTime => Delay + (Samples.Length - 1) * SampleInterval;

    public int ClampIndex(double time) => Math.Clamp(IndexAt(time), 0, Math.Max(0, Samples.Length - 1));
}

/// <summary>Applies a processing description to a copy of raw samples. Raw arrays are never modified.</summary>
public static class TraceProcessor
{
    /// <summary>
    /// Order: channel-map polarity → pipeline steps (enabled only, in order) → per-trace polarity →
    /// per-trace static shift. Display gain is not applied. Steps that cannot be applied (for
    /// example a corner frequency above Nyquist) are skipped and reported in <see cref="ProcessedTrace.Warnings"/>.
    /// </summary>
    public static ProcessedTrace Apply(ReadOnlySpan<double> raw, double sampleInterval, double delay, ProcessingPipeline pipeline,
        bool channelReversed = false, TraceAdjustment? adjustment = null)
    {
        var warnings = new List<string>();
        var x = channelReversed ? TimeDomain.Invert(raw) : raw.ToArray();
        var dt = sampleInterval;
        foreach (var step in pipeline.Steps)
        {
            if (!step.Enabled)
            {
                continue;
            }

            try
            {
                (x, dt) = ApplyStep(step, x, dt, delay);
            }
            catch (ArgumentException ex)
            {
                warnings.Add($"{step.DisplayName} was skipped: {FirstSentence(ex.Message)}");
            }
        }

        if (adjustment is not null)
        {
            if (adjustment.ReversePolarity)
            {
                x = TimeDomain.Invert(x);
            }

            if (adjustment.TimeShift != 0)
            {
                x = TimeDomain.Shift(x, adjustment.TimeShift, dt);
            }
        }

        return new ProcessedTrace(x, dt, delay, warnings);
    }

    private static (double[] Samples, double Interval) ApplyStep(ProcessingStep step, double[] x, double dt, double delay)
    {
        var fs = 1.0 / dt;
        switch (step)
        {
            case RemoveMeanStep:
                return (TimeDomain.RemoveMean(x), dt);
            case LinearDetrendStep:
                return (TimeDomain.LinearDetrend(x), dt);
            case TaperStep t:
                return (TimeDomain.Taper(x, t.Fraction, t.Shape == TaperShape.Hann), dt);
            case HighPassStep h:
                return (Run(Butterworth.HighPass(h.Order, h.CornerHz, fs), x, h.ZeroPhase), dt);
            case LowPassStep l:
                return (Run(Butterworth.LowPass(l.Order, l.CornerHz, fs), x, l.ZeroPhase), dt);
            case BandPassStep b:
                return (Run(Butterworth.BandPass(b.Order, b.LowHz, b.HighHz, fs), x, b.ZeroPhase), dt);
            case NotchStep n:
            {
                var y = x;
                var applied = 0;
                for (var k = 1; k <= Math.Max(1, n.Harmonics); k++)
                {
                    var f = n.FrequencyHz * k;
                    if (f >= fs / 2)
                    {
                        break;
                    }

                    y = Run(Butterworth.Notch(f, n.QualityFactor, fs), y, n.ZeroPhase);
                    applied++;
                }

                if (applied == 0)
                {
                    throw new ArgumentException($"{n.FrequencyHz:0.###} Hz is above the Nyquist frequency ({fs / 2:0.###} Hz).");
                }

                return (y, dt);
            }

            case NormalizeStep nm:
                return (nm.Mode == NormalizationMode.PeakAmplitude ? TimeDomain.NormalizePeak(x) : TimeDomain.NormalizeRms(x), dt);
            case BalanceStep bal:
            {
                var start = (int)Math.Round((bal.WindowStart - delay) / dt);
                var end = bal.WindowEnd is { } e ? (int)Math.Round((e - delay) / dt) + 1 : x.Length;
                return (TimeDomain.Balance(x, start, end), dt);
            }

            case AgcStep agc:
            {
                if (!(agc.WindowLength > 0))
                {
                    throw new ArgumentException("The AGC window must be longer than zero.");
                }

                return (TimeDomain.Agc(x, Math.Max(1, (int)Math.Round(agc.WindowLength / dt))), dt);
            }

            case PolarityStep:
                return (TimeDomain.Invert(x), dt);
            case TimeShiftStep ts:
                return (TimeDomain.Shift(x, ts.Shift, dt), dt);
            case ResampleStep rs:
            {
                if (!(rs.TargetInterval > 0))
                {
                    throw new ArgumentException("The target sample interval must be positive.");
                }

                return (Resampler.Resample(x, dt, rs.TargetInterval), rs.TargetInterval);
            }

            default:
                throw new ArgumentException($"Unknown processing step {step.GetType().Name}.");
        }
    }

    private static double[] Run(SosFilter filter, double[] x, bool zeroPhase) => zeroPhase ? filter.ApplyZeroPhase(x) : filter.Apply(x);

    private static string FirstSentence(string message)
    {
        var idx = message.IndexOf(" (Parameter", StringComparison.Ordinal);
        return idx > 0 ? message[..idx] : message;
    }
}
