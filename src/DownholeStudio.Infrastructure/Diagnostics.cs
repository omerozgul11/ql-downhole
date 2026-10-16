using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace DownholeStudio.Infrastructure;

/// <summary>Removes licence keys, tokens and e-mail addresses from log events.</summary>
public sealed partial class RedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            if (value is ScalarValue { Value: string s } && Redact(s) is var r && r != s)
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, r));
            }
        }
    }

    public static string Redact(string text)
    {
        text = KeyPattern().Replace(text, "DHST-****-****-****-****");
        text = TokenPattern().Replace(text, "dhs1.[token]");
        return EmailPattern().Replace(text, "[email]");
    }

    [GeneratedRegex(@"DHST-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}", RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"dhs1\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();
}

public static class Logging
{
    /// <summary>Structured JSON-lines logs, one file per day, 14 days kept.</summary>
    public static ILoggerFactory Create(AppPaths paths, bool debug)
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Is(debug ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.With(new RedactingEnricher())
            .Enrich.WithProperty("App", "Downhole Studio")
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.Logs, "downhole-studio-.jsonl"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, fileSizeLimitBytes: 20_000_000, rollOnFileSizeLimit: true, shared: true)
            .CreateLogger();
        return LoggerFactory.Create(b => b.AddSerilog(serilog, dispose: true).SetMinimumLevel(debug ? LogLevel.Debug : LogLevel.Information));
    }
}

/// <summary>Writes crash reports. Never uploads anything.</summary>
public sealed class CrashReporter(AppPaths paths, string appVersion)
{
    public string Write(Exception exception, string source)
    {
        var file = Path.Combine(paths.Crashes, $"crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"Downhole Studio {appVersion} — unhandled error ({source})");
        sb.AppendLine($"Time (UTC): {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine(RedactingEnricher.Redact(exception.ToString()));
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    public IReadOnlyList<string> Recent(TimeSpan within) =>
        Directory.EnumerateFiles(paths.Crashes, "crash-*.txt").Where(f => File.GetLastWriteTimeUtc(f) > DateTime.UtcNow - within).OrderDescending().ToList();
}

/// <summary>Help → Export Diagnostic Package: logs, version, OS and basic hardware; survey files only if selected.</summary>
public static class DiagnosticPackage
{
    public static void Create(string zipPath, AppPaths paths, string appVersion, string licenseSummary, IEnumerable<string>? projectFiles = null)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var info = new
        {
            Product = "Downhole Studio", Version = appVersion, CreatedUtc = DateTimeOffset.UtcNow,
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount, MemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            License = RedactingEnricher.Redact(licenseSummary),
        };
        Text(zip, "system.json", JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(paths.SettingsFile))
        {
            Text(zip, "settings.json", File.ReadAllText(paths.SettingsFile));
        }

        foreach (var log in Directory.EnumerateFiles(paths.Logs).OrderDescending().Take(7))
        {
            Text(zip, "logs/" + Path.GetFileName(log), RedactingEnricher.Redact(ReadShared(log)));
        }

        foreach (var crash in Directory.EnumerateFiles(paths.Crashes).OrderDescending().Take(10))
        {
            Text(zip, "crashes/" + Path.GetFileName(crash), File.ReadAllText(crash));
        }

        foreach (var project in projectFiles ?? [])
        {
            zip.CreateEntryFromFile(project, "projects/" + Path.GetFileName(project));
        }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    private static void Text(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(content);
    }
}
