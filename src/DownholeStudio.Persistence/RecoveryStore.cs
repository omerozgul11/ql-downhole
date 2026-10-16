using System.Diagnostics;
using System.Text.Json;

namespace DownholeStudio.Persistence;

public sealed record RecoveryInfo
{
    public required Guid SessionId { get; init; }
    public string? OriginalPath { get; init; }
    public required string ProjectName { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public int ProcessId { get; init; }
    public DateTimeOffset ProcessStart { get; init; }
    public string PackagePath { get; init; } = "";
}

/// <summary>
/// Auto-save and crash-recovery copies, kept in the user's local application data (never next to
/// the user's own project file). A copy whose process is no longer running is offered for recovery.
/// </summary>
public sealed class RecoveryStore(string directory)
{
    public string Directory { get; } = directory;

    public void Save(Guid sessionId, ProjectDocument document, string? originalPath, string appVersion, DateTimeOffset now)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var package = Path.Combine(Directory, $"{sessionId:N}.dhsx");
        var temp = package + ".tmp";
        ProjectPackage.WriteCopy(document, temp, appVersion, now);
        File.Move(temp, package, overwrite: true);
        using var self = Process.GetCurrentProcess();
        var info = new RecoveryInfo
        {
            SessionId = sessionId, OriginalPath = originalPath, ProjectName = document.State.Metadata.Name, SavedAt = now,
            ProcessId = Environment.ProcessId, ProcessStart = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero), PackagePath = package,
        };
        File.WriteAllText(Path.Combine(Directory, $"{sessionId:N}.json"), JsonSerializer.Serialize(info, ProjectJson.Options));
    }

    /// <summary>Recovery copies left behind by sessions that are no longer running.</summary>
    public IReadOnlyList<RecoveryInfo> FindOrphans()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var result = new List<RecoveryInfo>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                var info = JsonSerializer.Deserialize<RecoveryInfo>(File.ReadAllText(file), ProjectJson.Options);
                if (info is not null && File.Exists(info.PackagePath) && !IsAlive(info))
                {
                    result.Add(info);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A damaged marker is ignored; the package (if any) is left in place for manual recovery.
            }
        }

        return result.OrderByDescending(r => r.SavedAt).ToList();
    }

    public void Discard(Guid sessionId)
    {
        foreach (var f in new[] { $"{sessionId:N}.dhsx", $"{sessionId:N}.json", $"{sessionId:N}.dhsx.tmp" })
        {
            var p = Path.Combine(Directory, f);
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }
    }

    private static bool IsAlive(RecoveryInfo info)
    {
        if (info.ProcessId == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var p = Process.GetProcessById(info.ProcessId);
            return Math.Abs((new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero) - info.ProcessStart).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
