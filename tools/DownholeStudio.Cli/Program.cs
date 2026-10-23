using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DownholeStudio.Analysis;
using DownholeStudio.Cli;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Licensing;
using DownholeStudio.Persistence;
using DownholeStudio.Reporting;
using DownholeStudio.Reporting.Pdf;
using DownholeStudio.Synthetics;

// Downhole Studio command-line tool: batch analysis, report generation, sample project,
// scientific validation and release tooling. Uses the same engine as the desktop application.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        Downhole Studio command-line tool

          dhs analyze <project.dhsx> [--json <out.json>]     Analyse a project and print the layer model and Vs30
          dhs report <project.dhsx> --out <report.pdf>       Generate the PDF report without the desktop application
          dhs export <project.dhsx> --out <tables.xlsx>      Export all result tables
          dhs sample --out <file.dhsx>                       Build the shipped sample project from synthetic data
          dhs synth --out <folder> [--dataset <name>]        Write a synthetic survey as SEG-2 files with its true model
                    [--depths <n>]                           (datasets: Clean, Noisy, MissingTraces, TriggerErrors, ReversedPolarityS)
          dhs validate --out <folder>                        Run the scientific validation and write the report
          dhs keygen --out <folder> [--name <prefix>]        Create an ECDSA P-256 key pair (PEM) for signing
          dhs release-info <installer.exe>                   Print SHA-256 and size for a release record
        """);
    return 0;
}

try
{
    var command = args[0];
    var options = Args.Parse(args.Skip(1).ToArray());
    var clock = new SystemClock();
    var identity = new AppIdentity(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", Environment.UserName);
    switch (command)
    {
        case "analyze":
        {
            var session = ProjectSession.Open(options.Positional(0, "project"), clock, identity);
            var result = new AnalysisService(session, new ProcessedTraceCache()).CalculateNow();
            Console.WriteLine($"{session.State.Metadata.Name} — {result.MethodDescription}");
            foreach (var l in result.LayerResults)
            {
                Console.WriteLine($"  {l.Layer.Top,7:0.00}–{l.Layer.Bottom,7:0.00} m   Vp {Fmt(l.P?.Velocity),7}   Vs {Fmt(l.S?.Velocity),7}   ν {Fmt(l.Properties.PoissonRatio, "0.000"),6}");
            }

            Console.WriteLine(result.Vs30.Value is { } v ? $"  Vs30 = {v:0.0} m/s{(result.Vs30.Extrapolated ? " (EXTRAPOLATED)" : "")}" : $"  Vs30: {result.Vs30.Reason}");
            Console.WriteLine($"  Quality: {result.Qc.Quality} — {string.Join(" ", result.Qc.QualityReasons)}");
            if (options.Value("json") is { } json)
            {
                await File.WriteAllTextAsync(json, AuditExporter.Create(session.State, result, session.History, identity.Version, clock.Now));
                Console.WriteLine($"  Audit record written to {json}");
            }

            return 0;
        }

        case "report":
        {
            var session = ProjectSession.Open(options.Positional(0, "project"), clock, identity);
            var analysis = new AnalysisService(session, new ProcessedTraceCache());
            var result = analysis.CalculateNow();
            var logo = session.State.Report.Branding.LogoAsset is { } a ? session.Document.Assets.GetFile(a) : null;
            var units = DownholeStudio.Domain.Units.UnitPreferences.For(session.State.Metadata.DisplayUnits);
            var pdf = new ReportGenerator().Generate(new ReportContext
            {
                Project = session.State, Result = result, Traces = analysis.Builder(), History = session.History, Units = units, Logo = logo,
                AppVersion = identity.Version, GeneratedAt = clock.Now, GeneratedBy = identity.UserName,
            });
            var output = options.Required("out");
            await File.WriteAllBytesAsync(output, pdf);
            Console.WriteLine($"Report written to {output} ({pdf.Length / 1024} KB).");
            return 0;
        }

        case "export":
        {
            var session = ProjectSession.Open(options.Positional(0, "project"), clock, identity);
            var result = new AnalysisService(session, new ProcessedTraceCache()).CalculateNow();
            var units = DownholeStudio.Domain.Units.UnitPreferences.For(session.State.Metadata.DisplayUnits);
            var bytes = XlsxWriter.Write([ReportTables.Picks(session.State, result), ReportTables.Layers(result), ReportTables.Intervals(result), ReportTables.Engineering(result),
                ReportTables.Vs(result), ReportTables.Qc(result.Qc), ReportTables.History(session.History)], units, session.State.Metadata.Name);
            await File.WriteAllBytesAsync(options.Required("out"), bytes);
            Console.WriteLine($"Tables written to {options.Required("out")}.");
            return 0;
        }

        case "sample":
        {
            var output = options.Required("out");
            var info = await SampleProject.BuildAsync(output, identity);
            Console.WriteLine(info);
            return 0;
        }

        case "synth":
        {
            var folder = options.Required("out");
            var dataset = Enum.Parse<SyntheticDataset>(options.Value("dataset") ?? nameof(SyntheticDataset.Clean), ignoreCase: true);
            var synthetic = SyntheticOptions.For(dataset);
            if (options.Value("depths") is { } depths)
            {
                synthetic = synthetic with { DepthCount = int.Parse(depths, CultureInfo.InvariantCulture) };
            }

            var survey = SyntheticSurveyGenerator.Generate(synthetic);
            var files = SyntheticSurveyGenerator.WriteTo(survey, folder);
            var truth = survey.Truth;
            var lines = new List<string>
            {
                $"Synthetic downhole survey ({dataset}) generated by Downhole Studio {identity.Version}. All data is synthetic.",
                $"P source offset {truth.POffset} m, S source offset {truth.SOffset} m. Channels: 1 vertical, 2 horizontal X, 3 horizontal Y, 4 reference.",
                truth.Vs30 is { } vs30 ? $"True Vs30: {vs30:0.0} m/s" : "True Vs30: not defined (survey shallower than 30 m)",
                "True layers (top-bottom m: Vp, Vs m/s):",
            };
            lines.AddRange(truth.Layers.Select(l => $"  {l.Top:0.##}-{(double.IsInfinity(l.Bottom) ? "below" : l.Bottom.ToString("0.##", CultureInfo.InvariantCulture))}: Vp {l.Vp:0}, Vs {l.Vs:0}"));
            await File.WriteAllLinesAsync(Path.Combine(folder, "TRUE-MODEL.txt"), lines);
            Console.WriteLine($"{files.Count} SEG-2 files written to {folder} ({dataset}).");
            return 0;
        }

        case "validate":
        {
            var folder = options.Required("out");
            Directory.CreateDirectory(folder);
            var report = await ScientificValidation.RunAsync(identity);
            await File.WriteAllTextAsync(Path.Combine(folder, "validation-report.md"), report.ToMarkdown(identity.Version));
            await File.WriteAllTextAsync(Path.Combine(folder, "validation-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{report.Rows.Count(r => r.Pass)} of {report.Rows.Count} checks passed. Report written to {folder}.");
            return report.Rows.All(r => r.Pass) ? 0 : 2;
        }

        case "keygen":
        {
            var folder = options.Required("out");
            var name = options.Value("name") ?? "signing-key";
            Directory.CreateDirectory(folder);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var priv = Path.Combine(folder, name + ".pem");
            if (File.Exists(priv))
            {
                throw new InvalidOperationException($"{priv} already exists; refusing to overwrite a signing key.");
            }

            await File.WriteAllTextAsync(priv, key.ExportPkcs8PrivateKeyPem());
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".pub.pem"), key.ExportSubjectPublicKeyInfoPem());
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(priv, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            Console.WriteLine($"Private key: {priv} (keep secret; never commit)\nPublic key:  {Path.Combine(folder, name + ".pub.pem")}");
            return 0;
        }

        case "release-info":
        {
            var file = options.Positional(0, "installer");
            await using var fs = File.OpenRead(file);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));
            Console.WriteLine($"File:    {Path.GetFileName(file)}\nSize:    {new FileInfo(file).Length}\nSHA-256: {hash}");
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unknown command \"{command}\". Run dhs --help.");
            return 1;
    }
}
catch (Exception ex) when (ex is ProjectFileException or IOException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static string Fmt(double? v, string format = "0") => v is { } x ? x.ToString(format, CultureInfo.InvariantCulture) : "—";

namespace DownholeStudio.Cli
{
    internal sealed class Args
    {
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _positional = [];

        public static Args Parse(string[] args)
        {
            var a = new Args();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    a._named[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
                }
                else
                {
                    a._positional.Add(args[i]);
                }
            }

            return a;
        }

        public string? Value(string name) => _named.GetValueOrDefault(name);

        public string Required(string name) => Value(name) ?? throw new ArgumentException($"--{name} is required.");

        public string Positional(int index, string what) => index < _positional.Count ? _positional[index] : throw new ArgumentException($"Missing {what}.");
    }
}
