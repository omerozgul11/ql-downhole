using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DownholeStudio.Domain;

namespace DownholeStudio.Persistence;

/// <summary>JSON settings shared by project files and exports.</summary>
public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("The document is empty.");
}

/// <summary>Upgrades project JSON written by older versions to the current schema.</summary>
public static class ProjectMigrations
{
    /// <summary>Migrates in place and returns the resulting schema version.</summary>
    public static int Migrate(JsonObject root)
    {
        var version = root["schemaVersion"]?.GetValue<int>() ?? 1;
        if (version > ProjectState.CurrentSchemaVersion)
        {
            throw new ProjectFileException(
                "This project was saved by a newer version of Downhole Studio. Update the application to open it.",
                $"Project schema {version}, this version supports up to {ProjectState.CurrentSchemaVersion}.");
        }

        // Each future schema change adds a step here: if (version == 1) { …; version = 2; }
        root["schemaVersion"] = version;
        return version;
    }
}

/// <summary>A project file could not be opened or saved. The message is written for users.</summary>
public sealed class ProjectFileException(string message, string? technicalDetails = null, Exception? inner = null) : Exception(message, inner)
{
    public string? TechnicalDetails { get; } = technicalDetails;
}
