using System.Text.Json;
using System.Text.Json.Serialization;
using DownholeStudio.Domain.Units;

namespace DownholeStudio.Infrastructure;

/// <summary>Per-user locations. Project files are never stored here unless the user chooses to.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "QuakeLogic", "Downhole Studio");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string Logs => Ensure("Logs");
    public string Recovery => Ensure("Recovery");
    public string Crashes => Ensure("Crashes");
    public string Updates => Ensure("Updates");
    public string Samples => Ensure("Samples");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string CatalogFile => Path.Combine(Root, "catalog.db");
    public string LicenseFile => Path.Combine(Root, "license.dat");

    /// <summary>The user's Documents\Downhole Studio folder (default location for new projects).</summary>
    public string DefaultProjectFolder
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dir = Path.Combine(string.IsNullOrEmpty(docs) ? Root : docs, "Downhole Studio");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private string Ensure(string name)
    {
        var p = Path.Combine(Root, name);
        Directory.CreateDirectory(p);
        return p;
    }
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum UserMode
{
    Guided,
    Expert,
}

public enum UpdateMode
{
    Automatic,
    NotifyOnly,
    Manual,
}

public enum UpdateChannel
{
    Stable,
    Beta,
}

/// <summary>User preferences, stored separately from projects.</summary>
public sealed record UserSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public UserMode Mode { get; init; } = UserMode.Guided;
    public UnitSystem Units { get; init; } = UnitSystem.SI;
    public int AutoSaveMinutes { get; init; } = 5;
    public UpdateMode Updates { get; init; } = UpdateMode.NotifyOnly;
    public UpdateChannel Channel { get; init; } = UpdateChannel.Stable;
    public DateTimeOffset? LastUpdateCheck { get; init; }
    public bool ShareMachineName { get; init; } = true;
    public string? LastFolder { get; init; }
    public string? EditorName { get; init; }
    public string PaperSize { get; init; } = "Letter";
    public string? DefaultReportTemplate { get; init; }
    public Domain.PickAlgorithm DefaultPPicker { get; init; } = Domain.PickAlgorithm.Aic;
    public Domain.PickAlgorithm DefaultSPicker { get; init; } = Domain.PickAlgorithm.Aic;
    public bool DefaultBandPassForS { get; init; }
    public bool ShowWelcome { get; init; } = true;
    public string? LicenseServerOverride { get; init; }
    public double WindowWidth { get; init; } = 1440;
    public double WindowHeight { get; init; } = 900;
    public bool WindowMaximized { get; init; } = true;
}

public sealed class SettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private UserSettings? _current;

    public event EventHandler<UserSettings>? Changed;

    public UserSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current ??= Load();
            }
        }
    }

    public void Update(Func<UserSettings, UserSettings> change)
    {
        UserSettings next;
        lock (_lock)
        {
            next = change(Current);
            _current = next;
            var temp = paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, paths.SettingsFile, overwrite: true);
        }

        Changed?.Invoke(this, next);
    }

    private UserSettings Load()
    {
        try
        {
            return File.Exists(paths.SettingsFile) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(paths.SettingsFile), Json) ?? new UserSettings() : new UserSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged settings file is replaced by defaults; it never blocks start-up.
            return new UserSettings();
        }
    }
}

/// <summary>Deployment configuration (shipped next to the executable as appsettings.json).</summary>
public sealed record AppConfiguration
{
    public string LicenseServerUrl { get; init; } = "https://licensing.quakelogic.net/";
    public string PrivacyPolicyUrl { get; init; } = "https://quakelogic.net/privacy";
    public string SupportUrl { get; init; } = "https://quakelogic.net/contact";
    public string PurchaseUrl { get; init; } = "https://quakelogic.net/downhole-studio";

    public static AppConfiguration Load(string baseDirectory)
    {
        var file = Path.Combine(baseDirectory, "appsettings.json");
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(file)) ?? new AppConfiguration() : new AppConfiguration();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new AppConfiguration();
        }
    }
}
