using System.IO.Compression;
using DownholeStudio.Domain;
using DownholeStudio.Reporting;
using DownholeStudio.Reporting.Pdf;
using DownholeStudio.Synthetics;
using DownholeStudio.Visualization;

namespace DownholeStudio.Core.Tests;

public sealed class ReportTests
{
    [Theory]
    [InlineData("")]
    [InlineData("first")]
    [InlineData("a few short words first")]
    public void AVeryLongWordIsBrokenQuicklyWhereverItFalls(string before)
    {
        // The checksums of every imported file are one 8000-character word. Wrapping it once took minutes, and only
        // when it came first in the cell; after other text it was not broken at all and ran out of the column.
        var word = string.Concat(Enumerable.Range(0, 800).Select(i => $"{i:d4}abcdef"));
        var text = before.Length == 0 ? word : before + " " + word;
        using var font = PlotFonts.Font(6.4f);
        const float width = 150;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lines = PdfLayout.Wrap(text, font, width);
        clock.Stop();
        Assert.True(clock.Elapsed.TotalSeconds < 5, $"took {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.All(lines, l => Assert.True(font.MeasureText(l) <= width + 0.01f, $"line is {font.MeasureText(l)} wide: {l}"));
        Assert.Equal(text.Replace(" ", ""), string.Concat(lines).Replace(" ", ""));
    }

    [Fact]
    public void PrintedHistoryShortensLongValuesAndExportsKeepThemWhole()
    {
        var hashes = new string('a', 7839);
        var entry = new HistoryEntry
        {
            Timestamp = Pipeline.T0, Operation = "import", Description = "Imported 96 files", User = "tester", AppVersion = "1.0.0",
            Parameters = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("sha256", hashes).Add("files", "96"),
        };
        var units = DownholeStudio.Domain.Units.UnitPreferences.For(DownholeStudio.Domain.Units.UnitSystem.SI);
        var printed = ReportTables.History([entry], maxValueLength: 120);
        var cell = printed.Text(0, printed.Columns.Count - 1, units);
        Assert.StartsWith("files=96, sha256=aaa", cell); // always in the same order
        Assert.Contains("7839 characters", cell);
        Assert.True(cell.Length < 250);
        var full = ReportTables.History([entry]);
        Assert.Contains(hashes, full.Text(0, full.Columns.Count - 1, units));
    }

    [Theory]
    [InlineData("Short footer")]
    [InlineData("Sample report generated from synthetic data for demonstration purposes.")]
    [InlineData("A very long company footer that could never fit on one line of the page however small the type is, because it goes on and on and on and on and on and on and on and on and on and on")]
    public void FooterPartsNeverOverlap(string text)
    {
        const float width = 504, gap = 12;
        const string center = "Rev. 0 · 2026-11-06", page = "Page 12 of 17";
        using var font = PlotFonts.Font(7.5f);
        var (left, centerX) = PdfLayout.FitFooter(text, center, page, font, width, gap);
        Assert.True(font.MeasureText(left) + gap <= centerX + 0.01f, $"left part reaches {font.MeasureText(left)}, middle starts at {centerX}");
        Assert.True(centerX + font.MeasureText(center) + gap <= width - font.MeasureText(page) + 0.01f);
        if (font.MeasureText(text) + gap <= (width - font.MeasureText(center)) / 2)
        {
            Assert.Equal(text, left);
            Assert.Equal((width - font.MeasureText(center)) / 2, centerX, 0.01f);
        }
    }

    [Fact]
    public async Task GeneratesPdfXlsxAndCsv()
    {
        var (session, analysis, survey, _) = await Pipeline.BuildAsync(SyntheticOptions.For(SyntheticDataset.Clean));
        Pipeline.UseTrueLayers(session, survey.Truth.Layers);
        session.Execute(Edits.SetDensityModel(new DensityModel { Source = DensitySource.Constant, ConstantDensity = 1900 }));
        session.Execute(Edits.SetReport(session.State.Report with
        {
            Title = "Downhole Seismic Survey — Synthetic Test Site", Client = new ContactInfo { Name = "Example Client" },
            Branding = new ReportBranding { CompanyName = "QuakeLogic Inc.", EngineerName = "Test Engineer" }, Conclusions = "The model matches the synthetic truth.",
        }));
        var result = analysis.CalculateNow();
        var ctx = new ReportContext
        {
            Project = session.State, Result = result, Traces = analysis.Builder(), History = session.History, AppVersion = "1.0.0-test",
            GeneratedAt = Pipeline.T0, GeneratedBy = "tester",
        };
        var pdf = new ReportGenerator().Generate(ctx);
        Assert.True(pdf.Length > 20_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        var outDir = Environment.GetEnvironmentVariable("DHS_TEST_OUTPUT");
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            await File.WriteAllBytesAsync(Path.Combine(outDir, "report.pdf"), pdf);
        }

        var tables = new[] { ReportTables.Picks(session.State, result), ReportTables.Layers(result), ReportTables.Engineering(result), ReportTables.Qc(result.Qc), ReportTables.History(session.History) };
        var xlsx = XlsxWriter.Write(tables, Domain.Units.UnitPreferences.Si);
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet5.xml"));
        if (outDir is not null)
        {
            await File.WriteAllBytesAsync(Path.Combine(outDir, "tables.xlsx"), xlsx);
        }

        // The audit record must be valid JSON naming the raw files, methods and software version.
        var audit = System.Text.Json.JsonDocument.Parse(AuditExporter.Create(session.State, result, session.History, "1.0.0-test", Pipeline.T0)).RootElement;
        Assert.Equal("1.0.0-test", audit.GetProperty("software").GetProperty("version").GetString());
        Assert.Equal(session.State.Survey.Records.Count, audit.GetProperty("rawFiles").GetArrayLength());
        Assert.True(audit.GetProperty("analysis").TryGetProperty("vs30Settings", out _));

        var csv = CsvExporter.Write(ReportTables.Layers(result), Domain.Units.UnitPreferences.UsCustomary);
        Assert.Contains("Top (ft)", csv);
        Assert.Contains("Vs (ft/s)", csv);
        Assert.Equal(6, csv.Trim().Split('\n').Length);
    }
}
