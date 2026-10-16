using System.Globalization;
using DownholeStudio.Analysis;
using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Analysis.Qc;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;

namespace DownholeStudio.Reporting;

/// <summary>A column of a result table. Values are SI; <see cref="Quantity"/> selects the display conversion.</summary>
public sealed record TableColumn(string Header, Quantity? Quantity = null, int Decimals = 3, bool Numeric = true)
{
    /// <summary>For times shown in milliseconds etc.: header gets the unit from the preferences.</summary>
    public string HeaderWithUnit(UnitPreferences units) => Quantity is { } q && units.Symbol(q).Length > 0 ? $"{Header} ({units.Symbol(q)})" : Header;
}

/// <summary>A table of values (SI internally). Numbers are exported unrounded in display units.</summary>
public sealed class ResultTable(string name, IReadOnlyList<TableColumn> columns)
{
    public string Name { get; } = name;
    public IReadOnlyList<TableColumn> Columns { get; } = columns;
    public List<object?[]> Rows { get; } = [];

    public void Add(params object?[] values) => Rows.Add(values);

    /// <summary>The cell converted to display units (numbers stay full precision).</summary>
    public object? DisplayValue(int row, int column, UnitPreferences units)
    {
        var v = Rows[row][column];
        var col = Columns[column];
        return v switch
        {
            double d when col.Quantity is { } q => units.ToDisplay(d, q),
            _ => v,
        };
    }

    /// <summary>Formatted text for printed tables (rounded for display only).</summary>
    public string Text(int row, int column, UnitPreferences units)
    {
        var v = DisplayValue(row, column, units);
        return v switch
        {
            null => "—",
            double d when !double.IsFinite(d) => "—",
            double d => d.ToString("F" + Columns[column].Decimals, CultureInfo.CurrentCulture),
            bool b => b ? "Yes" : "No",
            DateTimeOffset t => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            _ => v.ToString() ?? "",
        };
    }
}

/// <summary>Builds the standard result tables from an analysis.</summary>
public static class ReportTables
{
    public static ResultTable Picks(ProjectState project, AnalysisResult r)
    {
        var t = new ResultTable("Pick table",
        [
            new("Depth", Quantity.Length, 2), new("P raw", Quantity.Time, 3), new("P trigger corr.", Quantity.Time, 3), new("P corrected", Quantity.Time, 3),
            new("P vertical", Quantity.Time, 3), new("P origin", Numeric: false), new("P confidence", null, 2), new("P included", Numeric: false),
            new("S raw", Quantity.Time, 3), new("S trigger corr.", Quantity.Time, 3), new("S corrected", Quantity.Time, 3), new("S vertical", Quantity.Time, 3),
            new("S origin", Numeric: false), new("S confidence", null, 2), new("S included", Numeric: false), new("Comments", Numeric: false),
        ]);
        foreach (var station in project.Survey.StationsByDepth())
        {
            var p = r.P.Observations.FirstOrDefault(o => o.StationId == station.Id);
            var s = r.S.Observations.FirstOrDefault(o => o.StationId == station.Id);
            var comments = string.Join("; ", new[] { station.Comment, p?.Pick?.Comment, s?.Pick?.Comment, p?.Included == false ? "P: " + p.ExclusionReason : null, s?.Included == false ? "S: " + s.ExclusionReason : null }
                .Where(c => !string.IsNullOrWhiteSpace(c)));
            t.Add(project.Survey.Geometry.DepthBelowGround(station.MeasuredDepth),
                p?.RawTime, p?.TriggerCorrection, p?.Time, p?.VerticalTime, Origin(p), p?.Pick?.Confidence?.Score, p?.Included,
                s?.RawTime, s?.TriggerCorrection, s?.Time, s?.VerticalTime, Origin(s), s?.Pick?.Confidence?.Score, s?.Included, comments);
        }

        return t;
    }

    private static string? Origin(StationObservation? o) => o?.Pick?.Time is null ? null : o.Pick.Origin switch
    {
        PickOrigin.Manual => "Manual",
        PickOrigin.AcceptedSuggestion => $"Auto ({o.Pick.Algorithm}), reviewed",
        PickOrigin.CrossCorrelation => "Cross-correlation",
        _ => $"Automatic ({o.Pick.Algorithm})",
    };

    public static ResultTable Layers(AnalysisResult r)
    {
        var t = new ResultTable("Layer model",
        [
            new("Top", Quantity.Length, 2), new("Bottom", Quantity.Length, 2), new("Thickness", Quantity.Length, 2),
            new("Vp", Quantity.Velocity, 0), new("Vp ±1σ", Quantity.Velocity, 0), new("Vp points", null, 0), new("Vp R²", null, 4),
            new("Vs", Quantity.Velocity, 0), new("Vs ±1σ", Quantity.Velocity, 0), new("Vs points", null, 0), new("Vs R²", null, 4), new("Notes", Numeric: false),
        ]);
        foreach (var l in r.LayerResults)
        {
            var notes = string.Join("; ", new[] { l.P?.Reason is { } a ? "Vp: " + a : null, l.S?.Reason is { } b ? "Vs: " + b : null }.Where(x => x is not null));
            t.Add(l.Layer.Top, l.Layer.Bottom, l.Layer.Thickness, l.P?.Velocity, l.P?.VelocityStandardError, (double?)l.P?.PointCount, Finite(l.P?.Fit?.RSquared),
                l.S?.Velocity, l.S?.VelocityStandardError, (double?)l.S?.PointCount, Finite(l.S?.Fit?.RSquared), notes);
        }

        return t;
    }

    public static ResultTable Engineering(AnalysisResult r)
    {
        var t = new ResultTable("Engineering parameters",
        [
            new("Top", Quantity.Length, 2), new("Bottom", Quantity.Length, 2), new("Vp", Quantity.Velocity, 0), new("Vs", Quantity.Velocity, 0),
            new("Vp/Vs", null, 2), new("Poisson's ratio", null, 3), new("Density", Quantity.Density, 0), new("Density source", Numeric: false),
            new("Shear modulus G", Quantity.Modulus, 1), new("Young's modulus E", Quantity.Modulus, 1), new("Bulk modulus K", Quantity.Modulus, 1), new("Issues", Numeric: false),
        ]);
        foreach (var l in r.LayerResults)
        {
            var p = l.Properties;
            var source = l.Density.Value is null ? null : l.Density.Estimated ? $"ESTIMATED — {l.Density.Method}" : l.Density.Method;
            t.Add(l.Layer.Top, l.Layer.Bottom, p.Vp, p.Vs, p.VpVsRatio, p.PoissonRatio, p.Density, source, p.ShearModulus, p.YoungModulus, p.BulkModulus,
                string.Join("; ", p.Issues.Where(i => i.Severity != Severity.Information).Select(i => i.Message)));
        }

        return t;
    }

    public static ResultTable Intervals(AnalysisResult r)
    {
        var t = new ResultTable("Interval velocities",
        [
            new("Wave", Numeric: false), new("Top", Quantity.Length, 2), new("Bottom", Quantity.Length, 2), new("Pseudo-interval", Quantity.Velocity, 0),
            new("True interval", Quantity.Velocity, 0), new("Note", Numeric: false),
        ]);
        foreach (var w in new[] { r.P, r.S })
        {
            for (var i = 0; i < w.PseudoIntervals.Count; i++)
            {
                var pi = w.PseudoIntervals[i];
                var ti = w.TrueIntervals.FirstOrDefault(x => x.UpperStation == pi.UpperStation && x.LowerStation == pi.LowerStation);
                t.Add(w.Wave.ToString(), pi.Top, pi.Bottom, pi.Velocity, ti?.Velocity, pi.Reason ?? ti?.Reason);
            }
        }

        return t;
    }

    public static ResultTable Qc(QcReport qc)
    {
        var t = new ResultTable("Quality control", [new("Severity", Numeric: false), new("Check", Numeric: false), new("Depth", Quantity.Length, 2), new("Finding", Numeric: false), new("Explanation", Numeric: false)]);
        foreach (var f in qc.Flags.OrderByDescending(f => f.Severity).ThenBy(f => f.Depth ?? -1))
        {
            t.Add(f.Severity switch { Severity.Error => "Likely problem", Severity.Warning => "Review", _ => "Note" }, f.Code, f.Depth, f.Message, f.Explanation);
        }

        return t;
    }

    public static ResultTable History(IEnumerable<HistoryEntry> history)
    {
        var t = new ResultTable("Processing history", [new("Time", Numeric: false), new("User", Numeric: false), new("Version", Numeric: false), new("Scenario", Numeric: false),
            new("Operation", Numeric: false), new("Description", Numeric: false), new("Parameters", Numeric: false)]);
        foreach (var h in history)
        {
            t.Add(h.Timestamp, h.User, h.AppVersion, h.Scenario, h.Operation, h.Description, string.Join(", ", h.Parameters.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        return t;
    }

    public static ResultTable Vs(AnalysisResult r)
    {
        var t = new ResultTable("Time-averaged Vs", [new("Interval", Numeric: false), new("Top", Quantity.Length, 2), new("Bottom", Quantity.Length, 2), new("Velocity", Quantity.Velocity, 0),
            new("Status", Numeric: false)]);
        foreach (var v in new[] { r.Vs30 }.Concat(r.CustomIntervals))
        {
            t.Add(v.Label, v.Top, v.Bottom, v.Value, v.Value is null ? v.Reason : v.Extrapolated ? $"EXTRAPOLATED below {v.ExtrapolatedFrom:0.##} m" : "Measured");
        }

        return t;
    }

    private static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? x : null;
}
