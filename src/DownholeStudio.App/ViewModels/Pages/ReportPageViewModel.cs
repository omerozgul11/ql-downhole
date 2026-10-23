using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.App.Services;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Persistence;
using DownholeStudio.Reporting;
using DownholeStudio.Reporting.Charts;
using DownholeStudio.Reporting.Pdf;
using DownholeStudio.Visualization;
using SkiaSharp;

namespace DownholeStudio.App.ViewModels.Pages;

public sealed partial class SectionToggle(ReportPageViewModel owner, ReportSection section, string label, bool on) : ObservableObject
{
    public ReportSection Section { get; } = section;
    public string Label { get; } = label;

    [ObservableProperty] private bool _isOn = on;

    partial void OnIsOnChanged(bool value) => owner.CommitSections();
}

public enum FigureKind
{
    TravelTime,
    Residuals,
    LayerVelocities,
    IntervalVelocities,
    Engineering,
    PWaveforms,
    SWaveforms,
    Geometry,
}

public sealed partial class ReportPageViewModel : PageViewModel
{
    private bool _loading;

    public ReportPageViewModel(WorkspaceViewModel ws) : base(ws)
    {
        foreach (var s in Enum.GetValues<ReportSection>())
        {
            Sections.Add(new SectionToggle(this, s, Label(s), true));
        }

        _paper = ws.Main.Settings.Current.PaperSize == "A4" ? PaperOptions[1] : PaperOptions[0];
        Load();
    }

    public static IReadOnlyList<Choice<FigureKind>> FigureOptions { get; } =
    [
        new(FigureKind.TravelTime, "Travel-time curves"), new(FigureKind.Residuals, "Travel-time residuals"), new(FigureKind.LayerVelocities, "Layer velocity profile"),
        new(FigureKind.IntervalVelocities, "Interval velocity profile"), new(FigureKind.Engineering, "Engineering profiles"), new(FigureKind.PWaveforms, "P waveforms with picks"),
        new(FigureKind.SWaveforms, "S waveforms with picks"), new(FigureKind.Geometry, "Survey geometry"),
    ];

    public static IReadOnlyList<Choice<ExportFormat>> FormatOptions { get; } = [new(ExportFormat.Png, "PNG image"), new(ExportFormat.Svg, "SVG (vector)"), new(ExportFormat.Pdf, "PDF (vector)")];

    public static IReadOnlyList<Choice<PaperSize>> PaperOptions { get; } = [new(PaperSize.Letter, "US Letter"), new(PaperSize.A4, "A4")];

    public override bool HasInspector => false;

    public override string NavTitle => "Report";
    public override string Title => "Report & exports";
    public override string Description => "Fill in the client and site details, choose sections and generate a PDF report that records the software version, analysis date, methods, processing, data coverage and warnings so the result can be reproduced. Tables export to Excel/CSV and every figure to PNG, SVG or PDF.";
    public override string IconKey => "ReportNav";

    public ObservableCollection<SectionToggle> Sections { get; } = [];
    public ObservableCollection<Bitmap> PreviewPages { get; } = [];
    public ObservableCollection<string> Templates { get; } = [];

    [ObservableProperty] private string? _reportTitle;
    [ObservableProperty] private string? _revision;
    [ObservableProperty] private string? _clientName;
    [ObservableProperty] private string? _clientContact;
    [ObservableProperty] private string? _clientAddress;
    [ObservableProperty] private string? _clientEmail;
    [ObservableProperty] private string? _clientPhone;
    [ObservableProperty] private string? _siteName;
    [ObservableProperty] private string? _siteAddress;
    [ObservableProperty] private double? _latitude;
    [ObservableProperty] private double? _longitude;
    [ObservableProperty] private string? _seismograph;
    [ObservableProperty] private string? _sensor;
    [ObservableProperty] private string? _sensorSerial;
    [ObservableProperty] private string? _pSource;
    [ObservableProperty] private string? _sSource;
    [ObservableProperty] private string? _trigger;
    [ObservableProperty] private string? _company;
    [ObservableProperty] private string? _companyAddress;
    [ObservableProperty] private string? _companyPhone;
    [ObservableProperty] private string? _companyEmail;
    [ObservableProperty] private string? _companyWebsite;
    [ObservableProperty] private string? _footer;
    [ObservableProperty] private string? _engineer;
    [ObservableProperty] private string? _engineerTitle;
    [ObservableProperty] private string? _registration;
    [ObservableProperty] private string? _preparedBy;
    [ObservableProperty] private string? _checkedBy;
    [ObservableProperty] private string? _conclusions;
    [ObservableProperty] private string? _processingNotes;
    [ObservableProperty] private Bitmap? _logo;
    [ObservableProperty] private Choice<PaperSize> _paper = PaperOptions[0];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _selectedTemplate;
    [ObservableProperty] private string _newTemplateName = "";
    [ObservableProperty] private Choice<FigureKind> _figure = FigureOptions[0];
    [ObservableProperty] private Choice<ExportFormat> _figureFormat = FormatOptions[0];
    [ObservableProperty] private int _figureWidth = 1600;
    [ObservableProperty] private int _figureHeight = 1100;
    [ObservableProperty] private bool _transparent;
    [ObservableProperty] private string? _lastReportPath;

    public bool HasLogo => Logo is not null;
    public bool HasPreview => PreviewPages.Count > 0;

    private static string Label(ReportSection s) => s switch
    {
        ReportSection.CoverPage => "Cover page",
        ReportSection.ProjectInformation => "Project information",
        ReportSection.ClientInformation => "Client information",
        ReportSection.SiteInformation => "Site information",
        ReportSection.SurveyConfiguration => "Survey configuration",
        ReportSection.InstrumentInformation => "Instrument information",
        ReportSection.SurveyGeometry => "Survey geometry",
        ReportSection.ProcessingMethodology => "Processing methodology",
        ReportSection.QualityControl => "Quality control",
        ReportSection.PWaveAnalysis => "P-wave analysis",
        ReportSection.SWaveAnalysis => "S-wave analysis",
        ReportSection.TravelTimeCurves => "Travel-time curves",
        ReportSection.VelocityProfiles => "Velocity profiles",
        ReportSection.LayerModel => "Layer model",
        ReportSection.EngineeringParameters => "Engineering parameters",
        ReportSection.Vs30 => "Vs30",
        ReportSection.ProcessingNotes => "Processing notes",
        ReportSection.Warnings => "Warnings",
        ReportSection.Conclusions => "Conclusions",
        ReportSection.AppendixPickTable => "Appendix — pick table",
        _ => "Appendix — processing history",
    };

    public override void OnStateChanged() => Load();

    private void Load()
    {
        _loading = true;
        var r = State.Report;
        ReportTitle = r.Title;
        Revision = r.Revision;
        ClientName = r.Client.Name;
        ClientContact = r.Client.Contact;
        ClientAddress = r.Client.Address;
        ClientEmail = r.Client.Email;
        ClientPhone = r.Client.Phone;
        SiteName = r.Site.Name;
        SiteAddress = r.Site.Address;
        Latitude = r.Site.Latitude;
        Longitude = r.Site.Longitude;
        Seismograph = r.Instrument.Seismograph;
        Sensor = r.Instrument.BoreholeSensor;
        SensorSerial = r.Instrument.SensorSerial;
        PSource = r.Instrument.PSource;
        SSource = r.Instrument.SSource;
        Trigger = r.Instrument.TriggerType;
        var b = r.Branding;
        Company = b.CompanyName;
        CompanyAddress = b.Address;
        CompanyPhone = b.Phone;
        CompanyEmail = b.Email;
        CompanyWebsite = b.Website;
        Footer = b.Footer;
        Engineer = b.EngineerName;
        EngineerTitle = b.EngineerTitle;
        Registration = b.Registration;
        PreparedBy = r.PreparedBy;
        CheckedBy = r.CheckedBy;
        Conclusions = r.Conclusions;
        ProcessingNotes = r.ProcessingNotes;
        foreach (var t in Sections)
        {
            t.IsOn = r.Sections.Contains(t.Section);
        }

        var logoBytes = b.LogoAsset is { } asset ? Workspace.Session.Document.Assets.GetFile(asset) : null;
        Logo = logoBytes is null ? null : new Bitmap(new MemoryStream(logoBytes));
        OnPropertyChanged(nameof(HasLogo));
        Templates.Clear();
        foreach (var t in Workspace.Main.Catalog.Templates())
        {
            Templates.Add(t.Name);
        }

        _loading = false;
    }

    private void Commit(Func<ReportConfiguration, ReportConfiguration> change, string description = "Report settings changed")
    {
        if (_loading)
        {
            return;
        }

        var updated = change(State.Report);
        if (updated != State.Report)
        {
            Execute(Edits.SetReport(updated, description));
        }
    }

    private static string? C(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    partial void OnReportTitleChanged(string? value) => Commit(r => r with { Title = C(value) });
    partial void OnRevisionChanged(string? value) => Commit(r => r with { Revision = C(value) });
    partial void OnClientNameChanged(string? value) => Commit(r => r with { Client = r.Client with { Name = C(value) } });
    partial void OnClientContactChanged(string? value) => Commit(r => r with { Client = r.Client with { Contact = C(value) } });
    partial void OnClientAddressChanged(string? value) => Commit(r => r with { Client = r.Client with { Address = C(value) } });
    partial void OnClientEmailChanged(string? value) => Commit(r => r with { Client = r.Client with { Email = C(value) } });
    partial void OnClientPhoneChanged(string? value) => Commit(r => r with { Client = r.Client with { Phone = C(value) } });
    partial void OnSiteNameChanged(string? value) => Commit(r => r with { Site = r.Site with { Name = C(value) } });
    partial void OnSiteAddressChanged(string? value) => Commit(r => r with { Site = r.Site with { Address = C(value) } });
    partial void OnLatitudeChanged(double? value) => Commit(r => r with { Site = r.Site with { Latitude = value } });
    partial void OnLongitudeChanged(double? value) => Commit(r => r with { Site = r.Site with { Longitude = value } });
    partial void OnSeismographChanged(string? value) => Commit(r => r with { Instrument = r.Instrument with { Seismograph = C(value) } });
    partial void OnSensorChanged(string? value) => Commit(r => r with { Instrument = r.Instrument with { BoreholeSensor = C(value) } });
    partial void OnSensorSerialChanged(string? value) => Commit(r => r with { Instrument = r.Instrument with { SensorSerial = C(value) } });
    partial void OnPSourceChanged(string? value) => Commit(r => r with { Instrument = r.Instrument with { PSource = C(value) } });
    partial void OnSSourceChanged(string? value) => Commit(r => r with { Instrument = r.Instrument with { SSource = C(value) } });
    partial void OnTriggerChanged(string? value) => Commit(r => r with { Instrument = r.Instrument with { TriggerType = C(value) } });
    partial void OnCompanyChanged(string? value) => Commit(r => r with { Branding = r.Branding with { CompanyName = C(value) } });
    partial void OnCompanyAddressChanged(string? value) => Commit(r => r with { Branding = r.Branding with { Address = C(value) } });
    partial void OnCompanyPhoneChanged(string? value) => Commit(r => r with { Branding = r.Branding with { Phone = C(value) } });
    partial void OnCompanyEmailChanged(string? value) => Commit(r => r with { Branding = r.Branding with { Email = C(value) } });
    partial void OnCompanyWebsiteChanged(string? value) => Commit(r => r with { Branding = r.Branding with { Website = C(value) } });
    partial void OnFooterChanged(string? value) => Commit(r => r with { Branding = r.Branding with { Footer = C(value) } });
    partial void OnEngineerChanged(string? value) => Commit(r => r with { Branding = r.Branding with { EngineerName = C(value) } });
    partial void OnEngineerTitleChanged(string? value) => Commit(r => r with { Branding = r.Branding with { EngineerTitle = C(value) } });
    partial void OnRegistrationChanged(string? value) => Commit(r => r with { Branding = r.Branding with { Registration = C(value) } });
    partial void OnPreparedByChanged(string? value) => Commit(r => r with { PreparedBy = C(value) });
    partial void OnCheckedByChanged(string? value) => Commit(r => r with { CheckedBy = C(value) });
    partial void OnConclusionsChanged(string? value) => Commit(r => r with { Conclusions = C(value) });
    partial void OnProcessingNotesChanged(string? value) => Commit(r => r with { ProcessingNotes = C(value) });

    public void CommitSections() => Commit(r => r with { Sections = [.. Sections.Where(s => s.IsOn).Select(s => s.Section)] }, "Report sections changed");

    [RelayCommand]
    private async Task ChooseLogo()
    {
        var files = await Workspace.Ui.OpenFilesAsync("Choose company logo", false, [UiService.ImageFilter]);
        if (files.Count == 0 || !Workspace.CanModify())
        {
            return;
        }

        var bytes = await File.ReadAllBytesAsync(files[0]);
        using (var probe = SKImage.FromEncodedData(bytes))
        {
            if (probe is null)
            {
                await Workspace.Ui.ErrorAsync("Unsupported image", "The logo must be a PNG or JPEG image.");
                return;
            }
        }

        var asset = $"assets/logo-{ProjectPackage.Hash(bytes)[..12]}{Path.GetExtension(files[0]).ToLowerInvariant()}";
        Workspace.Session.Document.Assets.AddFile(asset, bytes);
        Commit(r => r with { Branding = r.Branding with { LogoAsset = asset } }, "Report logo changed");
    }

    [RelayCommand]
    private void ClearLogo() => Commit(r => r with { Branding = r.Branding with { LogoAsset = null } }, "Report logo removed");

    [RelayCommand]
    private void SaveTemplate()
    {
        var name = string.IsNullOrWhiteSpace(NewTemplateName) ? Company ?? "Company template" : NewTemplateName.Trim();
        var b = State.Report.Branding;
        var logo = b.LogoAsset is { } a ? Workspace.Session.Document.Assets.GetFile(a) : null;
        Workspace.Main.Catalog.SaveTemplate(new ReportTemplate(name, b, logo, b.LogoAsset is { } a2 ? Path.GetExtension(a2) : null), Workspace.Main.Clock.Now);
        NewTemplateName = "";
        Load();
        Workspace.Ui.Notify($"Template \"{name}\" saved. Use it in future projects.", "success");
    }

    [RelayCommand]
    private void ApplyTemplate()
    {
        if (SelectedTemplate is not { } name || Workspace.Main.Catalog.Templates().FirstOrDefault(t => t.Name == name) is not { } t || !Workspace.CanModify())
        {
            return;
        }

        string? asset = null;
        if (t.Logo is { } logo)
        {
            asset = $"assets/logo-{ProjectPackage.Hash(logo)[..12]}{t.LogoExtension ?? ".png"}";
            Workspace.Session.Document.Assets.AddFile(asset, logo);
        }

        Commit(r => r with { Branding = t.Branding with { LogoAsset = asset } }, $"Applied report template \"{name}\"");
    }

    private ReportContext Context()
    {
        var result = Workspace.Analysis.CalculateNow();
        var logo = State.Report.Branding.LogoAsset is { } a ? Workspace.Session.Document.Assets.GetFile(a) : null;
        return new ReportContext
        {
            Project = State, Result = result, Traces = Workspace.Analysis.Builder(), History = Workspace.Session.History, Units = Units, Logo = logo,
            AppVersion = Workspace.Main.Version, GeneratedAt = Workspace.Main.Clock.Now, GeneratedBy = Workspace.Main.Identity.UserName, Paper = Paper.Value,
        };
    }

    [RelayCommand]
    private async Task Preview()
    {
        IsBusy = true;
        Status = "Rendering preview…";
        try
        {
            var ctx = Context();
            var pages = await Task.Run(() => new ReportGenerator().RenderPreview(ctx, 1.1f));
            PreviewPages.Clear();
            foreach (var p in pages)
            {
                PreviewPages.Add(new Bitmap(new MemoryStream(p)));
            }

            OnPropertyChanged(nameof(HasPreview));
            Status = $"Preview: {pages.Count} page(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Generate()
    {
        if (!Workspace.Main.CanEdit)
        {
            Workspace.Ui.Notify("Report generation needs an active license or trial.", "warning");
            return;
        }

        var name = $"{string.Concat(State.Metadata.Name.Split(Path.GetInvalidFileNameChars()))} report {DateTime.Now:yyyy-MM-dd}.pdf";
        var path = await Workspace.Ui.SaveFileAsync("Save PDF report", name, UiService.PdfFilter, Workspace.Session.FilePath is { } f ? Path.GetDirectoryName(f) : null);
        if (path is null)
        {
            return;
        }

        IsBusy = true;
        Status = "Generating report…";
        try
        {
            var ctx = Context();
            if (ctx.Result.Qc.Quality == Analysis.Qc.AnalysisQuality.Incomplete)
            {
                var answer = await Workspace.Ui.AskAsync("The analysis is incomplete", "The report will state that the analysis is incomplete: " + string.Join(" ", ctx.Result.Qc.QualityReasons) + " Generate anyway?",
                    ["Generate", "Cancel"], kind: "warning");
                if (answer != "Generate")
                {
                    return;
                }
            }

            var bytes = await Task.Run(() => new ReportGenerator().Generate(ctx));
            await File.WriteAllBytesAsync(path, bytes);
            Workspace.Session.Record("report.generate", $"PDF report generated: {Path.GetFileName(path)}",
                new Dictionary<string, string> { ["method"] = ctx.Result.Method.ToString(), ["quality"] = ctx.Result.Qc.Quality.ToString(), ["vs30"] = ctx.Result.Vs30.Value?.ToString("0.###") ?? "n/a" });
            Workspace.MarkReportGenerated();
            LastReportPath = path;
            Status = $"Report saved: {path}";
            Workspace.Ui.Notify("Report generated.", "success");
        }
        catch (IOException ex)
        {
            await Workspace.Ui.ErrorAsync("The report could not be saved", $"\"{path}\" could not be written. It may be open in another program.", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (LastReportPath is { } p)
        {
            Workspace.Ui.OpenUrl(p);
        }
    }

    [RelayCommand]
    private async Task ExportXlsx()
    {
        if (Workspace.Result is not { } r)
        {
            return;
        }

        var path = await Workspace.Ui.SaveFileAsync("Export result tables", $"{State.Metadata.Name} results.xlsx", UiService.XlsxFilter);
        if (path is null)
        {
            return;
        }

        var bytes = XlsxWriter.Write([ReportTables.Picks(State, r), ReportTables.Layers(r), ReportTables.Intervals(r), ReportTables.Engineering(r), ReportTables.Vs(r),
            ReportTables.Qc(r.Qc), ReportTables.History(Workspace.Session.History)], Units, State.Metadata.Name);
        await File.WriteAllBytesAsync(path, bytes);
        Workspace.Session.Record("export.xlsx", $"Tables exported to {Path.GetFileName(path)}");
        Workspace.Ui.Notify("Tables exported to Excel.", "success");
    }

    [RelayCommand]
    private async Task ExportCsv(string which)
    {
        if (Workspace.Result is not { } r)
        {
            return;
        }

        var table = which switch
        {
            "layers" => ReportTables.Layers(r),
            "engineering" => ReportTables.Engineering(r),
            "intervals" => ReportTables.Intervals(r),
            "qc" => ReportTables.Qc(r.Qc),
            "history" => ReportTables.History(Workspace.Session.History),
            _ => ReportTables.Picks(State, r),
        };
        var path = await Workspace.Ui.SaveFileAsync($"Export {table.Name}", $"{State.Metadata.Name} {table.Name.ToLowerInvariant()}.csv", UiService.CsvFilter);
        if (path is null)
        {
            return;
        }

        await File.WriteAllBytesAsync(path, CsvExporter.WriteBytes(table, Units));
        Workspace.Session.Record("export.csv", $"{table.Name} exported to {Path.GetFileName(path)}");
        Workspace.Ui.Notify($"{table.Name} exported.", "success");
    }

    /// <summary>The drawing action for a figure (white background unless transparent).</summary>
    public Action<SKCanvas, SKRect>? FigureDrawer(FigureKind kind, bool transparent, float scale)
    {
        if (Workspace.Result is not { } r)
        {
            return null;
        }

        var theme = transparent ? PlotTheme.Print.WithTransparentBackground() : PlotTheme.Print;
        var u = Units;
        switch (kind)
        {
            case FigureKind.TravelTime:
                return (c, b) => XyPlotRenderer.Render(c, b, ChartFactory.TravelTime(r, u, theme, qc: r.Qc), theme, scale);
            case FigureKind.Residuals:
                return (c, b) => XyPlotRenderer.Render(c, b, ChartFactory.Residuals(r, u, theme), theme, scale);
            case FigureKind.LayerVelocities:
                return (c, b) => XyPlotRenderer.Render(c, b, ChartFactory.VelocityProfile(r, u, theme, ProfileKind.Layer), theme, scale);
            case FigureKind.IntervalVelocities:
                return (c, b) => XyPlotRenderer.Render(c, b, ChartFactory.VelocityProfile(r, u, theme, ProfileKind.Interval, labels: false), theme, scale);
            case FigureKind.Engineering:
                return (c, b) =>
                {
                    var props = new[] { EngineeringProperty.Vs, EngineeringProperty.Poisson, EngineeringProperty.Shear, EngineeringProperty.Young };
                    var w = b.Width / props.Length;
                    for (var i = 0; i < props.Length; i++)
                    {
                        var cell = new SKRect(b.Left + i * w, b.Top, b.Left + (i + 1) * w, b.Bottom);
                        c.Save();
                        c.ClipRect(cell);
                        XyPlotRenderer.Render(c, cell, ChartFactory.Property(r, props[i], u, theme), theme, scale);
                        c.Restore();
                    }
                };
            case FigureKind.PWaveforms or FigureKind.SWaveforms:
            {
                var section = SectionFactory.Build(State, r, Workspace.Analysis.Builder(), kind == FigureKind.PWaveforms ? WaveType.P : WaveType.S, theme, u);
                section.SpaceByPosition = true;
                return (c, b) => WaveformRenderer.Render(c, b, section, theme, scale);
            }

            default:
            {
                var g = State.Survey.Geometry;
                var m = new BoreholeDiagramModel
                {
                    BoreholeName = g.Borehole.Name, TotalDepth = g.Borehole.TotalDepth ?? r.MeasuredBottom, CollarHeight = g.Borehole.CollarHeight, POffset = g.PSource.HorizontalOffset,
                    PSourceDepth = g.PSource.SourceDepth, SOffset = g.SSource.HorizontalOffset, SSourceDepth = g.SSource.SourceDepth, LengthUnit = u.Symbol(Quantity.Length), LengthScale = u.ToDisplay(1, Quantity.Length),
                };
                foreach (var s in State.Survey.StationsByDepth())
                {
                    m.Receivers.Add(new DiagramReceiver(g.DepthBelowGround(s.MeasuredDepth), true, true, s.Excluded, false, s.Id));
                }

                return (c, b) => BoreholeDiagramRenderer.Render(c, b, m, theme, scale);
            }
        }
    }

    [RelayCommand]
    private async Task ExportFigure()
    {
        var w = Math.Clamp(FigureWidth, 200, 12000);
        var h = Math.Clamp(FigureHeight, 150, 12000);
        // Logical size is in points (1/72 in); for PNG the pixel size is exactly what the user asked for.
        var logicalWidth = 800f;
        var logicalHeight = logicalWidth * h / w;
        var drawer = FigureDrawer(Figure.Value, Transparent, 1f);
        if (drawer is null)
        {
            return;
        }

        var (filter, ext) = FigureFormat.Value switch
        {
            ExportFormat.Svg => (UiService.SvgFilter, ".svg"),
            ExportFormat.Pdf => (UiService.PdfFilter, ".pdf"),
            _ => (UiService.PngFilter, ".png"),
        };
        var path = await Workspace.Ui.SaveFileAsync("Export figure", $"{State.Metadata.Name} {Figure.Label.ToLowerInvariant()}{ext}", filter);
        if (path is null)
        {
            return;
        }

        var bytes = await Task.Run(() => ChartExporter.Export(drawer, FigureFormat.Value, logicalWidth, logicalHeight, w / logicalWidth));
        await File.WriteAllBytesAsync(path, bytes);
        Workspace.Session.Record("export.figure", $"{Figure.Label} exported to {Path.GetFileName(path)}");
        Workspace.Ui.Notify("Figure exported.", "success");
    }

    [RelayCommand]
    private async Task CopyFigure()
    {
        var drawer = FigureDrawer(Figure.Value, false, 1f);
        if (drawer is null)
        {
            return;
        }

        var png = ChartExporter.Export(drawer, ExportFormat.Png, 800, 800f * FigureHeight / Math.Max(1, FigureWidth), 2);
        await ClipboardImage.CopyPngAsync(Workspace.Ui, png);
    }
}
