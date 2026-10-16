using System.Collections.Immutable;
using System.Text.Json;
using DownholeStudio.Domain;
using Microsoft.Data.Sqlite;

namespace DownholeStudio.Persistence;

public sealed record RecentProject(string Path, string Name, DateTimeOffset LastOpened, bool Pinned);

/// <summary>A saved report branding template (company details reused across projects).</summary>
public sealed record ReportTemplate(string Name, ReportBranding Branding, byte[]? Logo, string? LogoExtension);

/// <summary>
/// Per-user SQLite catalogue: recent projects, instrument/channel profiles and report templates.
/// User preferences are stored separately (settings.json); project data never lives here.
/// </summary>
public sealed class AppCatalog
{
    private readonly string _connectionString;

    public AppCatalog(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var c = Open();
        Execute(c, """
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS recent_projects (
                path TEXT PRIMARY KEY, name TEXT NOT NULL, last_opened TEXT NOT NULL, pinned INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS instrument_profiles (
                name TEXT PRIMARY KEY, json TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS report_templates (
                name TEXT PRIMARY KEY, json TEXT NOT NULL, logo BLOB, logo_ext TEXT, updated TEXT NOT NULL);
            """);
        if (Scalar(c, "SELECT COUNT(*) FROM schema_info") == 0)
        {
            Execute(c, "INSERT INTO schema_info(version) VALUES (1)");
        }
    }

    /// <summary>Profiles shipped with the application.</summary>
    public static IReadOnlyList<InstrumentProfile> BuiltInProfiles { get; } =
    [
        new InstrumentProfile
        {
            Name = "QuakeLogic 3C Borehole", BuiltIn = true,
            Description = "Channel 1 vertical, 2 horizontal X, 3 horizontal Y, 4 reference geophone at the source.",
            Channels =
            [
                new ChannelAssignment { ChannelNumber = 1, Role = ComponentRole.Vertical, Label = "Z" },
                new ChannelAssignment { ChannelNumber = 2, Role = ComponentRole.HorizontalX, Label = "X" },
                new ChannelAssignment { ChannelNumber = 3, Role = ComponentRole.HorizontalY, Label = "Y" },
                new ChannelAssignment { ChannelNumber = 4, Role = ComponentRole.Reference, Label = "Ref" },
            ],
        },
        new InstrumentProfile
        {
            Name = "3C sensor, no reference (Z, X, Y)", BuiltIn = true,
            Description = "Channel 1 vertical, 2 horizontal X, 3 horizontal Y.",
            Channels =
            [
                new ChannelAssignment { ChannelNumber = 1, Role = ComponentRole.Vertical, Label = "Z" },
                new ChannelAssignment { ChannelNumber = 2, Role = ComponentRole.HorizontalX, Label = "X" },
                new ChannelAssignment { ChannelNumber = 3, Role = ComponentRole.HorizontalY, Label = "Y" },
            ],
        },
        new InstrumentProfile
        {
            Name = "Reference first (Ref, Z, X, Y)", BuiltIn = true,
            Description = "Channel 1 reference/trigger geophone, 2 vertical, 3 horizontal X, 4 horizontal Y.",
            Channels =
            [
                new ChannelAssignment { ChannelNumber = 1, Role = ComponentRole.Reference, Label = "Ref" },
                new ChannelAssignment { ChannelNumber = 2, Role = ComponentRole.Vertical, Label = "Z" },
                new ChannelAssignment { ChannelNumber = 3, Role = ComponentRole.HorizontalX, Label = "X" },
                new ChannelAssignment { ChannelNumber = 4, Role = ComponentRole.HorizontalY, Label = "Y" },
            ],
        },
    ];

    public IReadOnlyList<RecentProject> RecentProjects(int limit = 12)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, name, last_opened, pinned FROM recent_projects ORDER BY pinned DESC, last_opened DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<RecentProject>();
        while (r.Read())
        {
            list.Add(new RecentProject(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture), r.GetInt32(3) != 0));
        }

        return list;
    }

    public void TouchRecent(string path, string name, DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recent_projects(path, name, last_opened) VALUES ($p, $n, $t)
            ON CONFLICT(path) DO UPDATE SET name = excluded.name, last_opened = excluded.last_opened
            """;
        cmd.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void RemoveRecent(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM recent_projects WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    public void SetPinned(string path, bool pinned)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE recent_projects SET pinned = $v WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$v", pinned ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<InstrumentProfile> Profiles()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM instrument_profiles ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = BuiltInProfiles.ToList();
        while (r.Read())
        {
            if (JsonSerializer.Deserialize<InstrumentProfile>(r.GetString(0), ProjectJson.Options) is { } p)
            {
                list.RemoveAll(x => x.Name == p.Name && !x.BuiltIn);
                list.Add(p with { BuiltIn = false });
            }
        }

        return list;
    }

    public void SaveProfile(InstrumentProfile profile, DateTimeOffset now)
    {
        if (BuiltInProfiles.Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"\"{profile.Name}\" is a built-in profile name. Choose another name.");
        }

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO instrument_profiles(name, json, updated) VALUES ($n, $j, $t)
            ON CONFLICT(name) DO UPDATE SET json = excluded.json, updated = excluded.updated
            """;
        cmd.Parameters.AddWithValue("$n", profile.Name);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(profile with { BuiltIn = false }, ProjectJson.Compact));
        cmd.Parameters.AddWithValue("$t", now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void DeleteProfile(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM instrument_profiles WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ReportTemplate> Templates()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, json, logo, logo_ext FROM report_templates ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<ReportTemplate>();
        while (r.Read())
        {
            var branding = JsonSerializer.Deserialize<ReportBranding>(r.GetString(1), ProjectJson.Options) ?? new ReportBranding();
            list.Add(new ReportTemplate(r.GetString(0), branding, r.IsDBNull(2) ? null : (byte[])r[2], r.IsDBNull(3) ? null : r.GetString(3)));
        }

        return list;
    }

    public void SaveTemplate(ReportTemplate template, DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO report_templates(name, json, logo, logo_ext, updated) VALUES ($n, $j, $l, $e, $t)
            ON CONFLICT(name) DO UPDATE SET json = excluded.json, logo = excluded.logo, logo_ext = excluded.logo_ext, updated = excluded.updated
            """;
        cmd.Parameters.AddWithValue("$n", template.Name);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(template.Branding with { LogoAsset = null }, ProjectJson.Compact));
        cmd.Parameters.AddWithValue("$l", (object?)template.Logo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)template.LogoExtension ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void DeleteTemplate(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM report_templates WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Execute(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
