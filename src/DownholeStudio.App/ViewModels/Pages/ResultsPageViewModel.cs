using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Analysis.Qc;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;

namespace DownholeStudio.App.ViewModels.Pages;

public sealed record QcCard(string Title, string Value, string Detail, string Tone);

public sealed record QcFlagRow(Severity Severity, string Code, string Depth, string Message, string? Explanation, Guid? Subject, WaveType? Wave)
{
    public bool IsError => Severity == Severity.Error;
    public bool IsWarning => Severity == Severity.Warning;
    public bool IsInfo => Severity == Severity.Information;
    public string SeverityText => Severity switch { Severity.Error => "Likely problem", Severity.Warning => "Review", _ => "Note" };
    public bool HasExplanation => !string.IsNullOrEmpty(Explanation);
}

public sealed record HistoryRow(string Time, string User, string Operation, string Description, string Parameters);

public sealed record ComparisonRow(string Name, string Method, string Vs30, string PRms, string SRms, string Quality, string Layers, bool Active);

public sealed partial class ResultsPageViewModel(WorkspaceViewModel ws) : PageViewModel(ws)
{
    public override bool HasInspector => false;

    public override string NavTitle => "Results";
    public override string Title => "Results & quality control";
    public override string Description => "A single view of how trustworthy the analysis is. Every automatic check is listed with a plain explanation; click a finding to go to it. Green means acceptable, yellow review, red a likely problem — always with a symbol and text, never colour alone. The verdict is advisory and does not certify compliance with any standard.";
    public override string IconKey => "Results";
    public override PageViewModel? Next => Workspace.ReportPage;

    public ObservableCollection<QcCard> Cards { get; } = [];
    public ObservableCollection<QcFlagRow> Flags { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];
    public ObservableCollection<ComparisonRow> Comparison { get; } = [];
    public ObservableCollection<string> Reasons { get; } = [];

    [ObservableProperty] private string _quality = "";
    [ObservableProperty] private string _qualityTone = "info";
    [ObservableProperty] private bool _showErrors = true;
    [ObservableProperty] private bool _showWarnings = true;
    [ObservableProperty] private bool _showNotes;
    [ObservableProperty] private string _historyFilter = "";
    [ObservableProperty] private string _flagSummary = "";

    partial void OnShowErrorsChanged(bool value) => LoadFlags();
    partial void OnShowWarningsChanged(bool value) => LoadFlags();
    partial void OnShowNotesChanged(bool value) => LoadFlags();
    partial void OnHistoryFilterChanged(string value) => LoadHistory();

    public override void OnResultChanged()
    {
        if (!IsActive || Workspace.Result is not { } r)
        {
            return;
        }

        var qc = r.Qc;
        var s = qc.Summary;
        var u = Units;
        Quality = qc.Quality switch { AnalysisQuality.Good => "Good", AnalysisQuality.ReviewRecommended => "Review recommended", _ => "Incomplete" };
        QualityTone = qc.Quality switch { AnalysisQuality.Good => "good", AnalysisQuality.ReviewRecommended => "warn", _ => "bad" };
        Reasons.Clear();
        foreach (var reason in qc.QualityReasons)
        {
            Reasons.Add(reason);
        }

        Cards.Clear();
        Cards.Add(new QcCard("Imported records", s.ImportedRecords.ToString(), $"{s.Stations} measurement depths", "info"));
        Cards.Add(new QcCard("Missing measurements", s.MissingMeasurements.ToString(), "Depths without a P or S pick", s.MissingMeasurements == 0 ? "good" : "warn"));
        Cards.Add(new QcCard("P picks used", $"{s.PPicksAccepted} / {s.Stations}", "Included in the velocity model", s.PPicksAccepted >= s.Stations && s.Stations > 0 ? "good" : "warn"));
        Cards.Add(new QcCard("S picks used", $"{s.SPicksAccepted} / {s.Stations}", "Included in the velocity model", s.SPicksAccepted >= s.Stations && s.Stations > 0 ? "good" : "warn"));
        Cards.Add(new QcCard("Low-confidence picks", s.LowConfidencePicks.ToString(), "Automatic picks scored Low", s.LowConfidencePicks == 0 ? "good" : "warn"));
        Cards.Add(new QcCard("Trigger corrections", s.TriggerMode == TriggerCorrectionMode.None ? "Off" : s.TriggerCorrectedRecords.ToString(),
            s.TriggerMode == TriggerCorrectionMode.None ? "Times used as recorded" : "Records corrected", "info"));
        Cards.Add(new QcCard("Excluded measurements", s.ExcludedMeasurements.ToString(), "Kept and listed for audit", s.ExcludedMeasurements == 0 ? "good" : "info"));
        Cards.Add(new QcCard("Depth coverage", s.MaxDepth is null ? "—" : $"{u.Format(s.MinDepth, Quantity.Length, false)}–{u.Format(s.MaxDepth, Quantity.Length)}", "Shallowest to deepest usable pick", "info"));
        Cards.Add(new QcCard("Vs30", s.Vs30Available ? u.Format(r.Vs30.Value, Quantity.Velocity) : "Not available", s.Vs30Extrapolated ? "EXTRAPOLATED" : s.Vs30Available ? "From measured data" : r.Vs30.Reason ?? "",
            s.Vs30Available && !s.Vs30Extrapolated ? "good" : "warn"));
        Cards.Add(new QcCard("Model RMS error", $"S {Ms(s.SRms)}", $"P {Ms(s.PRms)}", "info"));
        LoadFlags();
        LoadHistory();
        LoadComparison();
    }

    public override void OnStateChanged()
    {
        if (IsActive)
        {
            LoadHistory();
        }
    }

    private static string Ms(double? v) => v is { } x ? $"{x * 1000:0.000} ms" : "—";

    private void LoadFlags()
    {
        Flags.Clear();
        if (Workspace.Result is not { } r)
        {
            return;
        }

        foreach (var f in r.Qc.Flags.OrderByDescending(f => f.Severity).ThenBy(f => f.Depth ?? -1))
        {
            if ((f.Severity == Severity.Error && !ShowErrors) || (f.Severity == Severity.Warning && !ShowWarnings) || (f.Severity == Severity.Information && !ShowNotes))
            {
                continue;
            }

            Flags.Add(new QcFlagRow(f.Severity, f.Code, f.Depth is { } d ? Units.Format(d, Quantity.Length) : "", f.Message, f.Explanation, f.SubjectId, f.Wave));
        }

        FlagSummary = $"{r.Qc.Flags.Count(f => f.Severity == Severity.Error)} likely problem(s) · {r.Qc.Flags.Count(f => f.Severity == Severity.Warning)} to review · {r.Qc.Flags.Count(f => f.Severity == Severity.Information)} note(s)";
    }

    private void LoadHistory()
    {
        History.Clear();
        var filter = HistoryFilter.Trim();
        foreach (var h in Workspace.Session.History.Reverse())
        {
            var parameters = string.Join(", ", h.Parameters.Select(kv => $"{kv.Key}={kv.Value}"));
            if (filter.Length > 0 && !(h.Description.Contains(filter, StringComparison.OrdinalIgnoreCase) || h.Operation.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                       parameters.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            History.Add(new HistoryRow(h.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), h.User ?? "", h.Operation, h.Description, parameters));
            if (History.Count >= 500)
            {
                break;
            }
        }
    }

    [RelayCommand]
    private void LoadComparison()
    {
        Comparison.Clear();
        if (State.Scenarios.Count < 2)
        {
            return;
        }

        var u = Units;
        foreach (var s in ScenarioComparison.Compare(State, Workspace.Analysis.Traces, Workspace.Analysis.Cache, Workspace.Main.Clock.Now))
        {
            var layers = string.Join("; ", s.Layers.Select(l => $"{u.Format(l.Top, Quantity.Length, false)}–{u.Format(l.Bottom, Quantity.Length, false)}: Vs {u.Format(l.Vs, Quantity.Velocity, false)}"));
            Comparison.Add(new ComparisonRow(s.Name, s.Method.ToString(), s.Vs30 is { } v ? u.Format(v, Quantity.Velocity) + (s.Vs30Extrapolated ? " (EXTRAPOLATED)" : "") : s.Vs30Reason ?? "—",
                Ms(s.PRms), Ms(s.SRms), s.Quality.ToString(), layers, s.ScenarioId == State.ActiveScenario.Id));
        }
    }

    [RelayCommand]
    private void GoToFlag(QcFlagRow row)
    {
        if (row.Subject is not { } id)
        {
            return;
        }

        if (State.Survey.FindStation(id) is not null)
        {
            Workspace.SelectedStationId = id;
            Workspace.Navigate(row.Wave == WaveType.S ? Workspace.SPicksPage : Workspace.PPicksPage);
        }
        else if (State.Survey.FindRecord(id) is not null)
        {
            Workspace.SelectedRecordId = id;
            Workspace.ProcessingPage.SelectedTab = 1;
            Workspace.Navigate(Workspace.ProcessingPage);
        }
        else
        {
            Workspace.SelectedLayerId = id;
            Workspace.Navigate(row.Code.StartsWith("eng.", StringComparison.Ordinal) || row.Code.StartsWith("density", StringComparison.Ordinal) ? Workspace.EngineeringPage : Workspace.VelocityPage);
        }
    }

    [RelayCommand]
    private void ManageScenarios() => Workspace.Navigate(Workspace.ProjectPage);
}
