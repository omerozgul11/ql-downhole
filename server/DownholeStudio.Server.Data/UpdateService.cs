using System.Globalization;
using DownholeStudio.Licensing;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.Server.Data;

/// <summary>A parsed release version: numeric parts plus an optional pre-release label (1.2.0-beta.1).</summary>
public readonly record struct ReleaseVersion(int[] Parts, string? PreRelease) : IComparable<ReleaseVersion>
{
    public int CompareTo(ReleaseVersion other)
    {
        for (var i = 0; i < Math.Max(Parts.Length, other.Parts.Length); i++)
        {
            var a = i < Parts.Length ? Parts[i] : 0;
            var b = i < other.Parts.Length ? other.Parts[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        // A release ranks above its pre-releases (1.2.0 > 1.2.0-beta.1).
        if (PreRelease is null || other.PreRelease is null)
        {
            return (PreRelease is null ? 1 : 0) - (other.PreRelease is null ? 1 : 0);
        }

        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }
}

/// <summary>Chooses the newest published release for a channel and signs its manifest with the update key.</summary>
public sealed class UpdateService(LicensingDbContext db, UpdateSigner signer)
{
    public static bool TryParseVersion(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            s = s[..plus];
        }

        string? pre = null;
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0)
            {
                return false;
            }
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(numbers, pre);
        return true;
    }

    /// <summary>Signed manifest of the newest release newer than <paramref name="currentVersion"/>, or null.</summary>
    public async Task<SignedEnvelope?> LatestAsync(string product, string channel, string? currentVersion, CancellationToken ct = default)
    {
        var channels = channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? new[] { "stable", "beta" } : ["stable"];
        var candidates = await db.SoftwareVersions.Where(v => v.Product == product && v.Published && channels.Contains(v.Channel)).ToListAsync(ct);
        TryParseVersion(currentVersion, out var current);
        var hasCurrent = currentVersion is not null && current.Parts is not null;
        var best = candidates
            .Select(v => (Release: v, Ok: TryParseVersion(v.Version, out var parsed), Parsed: parsed))
            .Where(x => x.Ok && (!hasCurrent || x.Parsed.CompareTo(current) > 0))
            .OrderByDescending(x => x.Parsed)
            .Select(x => x.Release)
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        return signer.Sign(new UpdateManifest
        {
            Product = best.Product, Channel = best.Channel, Version = best.Version, PublishedAt = best.PublishedAt ?? best.CreatedAt, ReleaseNotes = best.ReleaseNotes,
            InstallerUrl = best.InstallerUrl, Sha256 = best.Sha256, Size = best.Size, IsMajor = best.IsMajor, MinimumVersion = best.MinimumVersion,
        });
    }
}
