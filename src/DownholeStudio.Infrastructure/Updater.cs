using System.Diagnostics;
using System.Security.Cryptography;
using DownholeStudio.Licensing;
using Microsoft.Extensions.Logging;

namespace DownholeStudio.Infrastructure;

public sealed record UpdateCheckResult(UpdateManifest? Available, string Message, bool Failed = false);

/// <summary>
/// Checks for and installs updates. Manifests must carry a valid signature from the update key and
/// the downloaded installer must match the signed SHA-256 and size before it is ever started.
/// </summary>
public sealed class UpdateService(LicenseApiClient api, HttpClient http, AppPaths paths, string currentVersion, ILogger<UpdateService> log, PublicKeyRing? updateKeys = null)
{
    private readonly PublicKeyRing _keys = updateKeys ?? EmbeddedKeys.UpdateKeys;

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        try
        {
            var envelope = await api.LatestReleaseAsync(ProductIds.DownholeStudio, channel == UpdateChannel.Beta ? "beta" : "stable", currentVersion, ct).ConfigureAwait(false);
            if (envelope is null)
            {
                return new UpdateCheckResult(null, $"Downhole Studio {currentVersion} is up to date.");
            }

            var manifest = EnvelopeCodec.Verify<UpdateManifest>(envelope, _keys);
            if (manifest is null)
            {
                log.LogWarning("Update manifest failed signature verification.");
                return new UpdateCheckResult(null, "An update was offered but its signature could not be verified, so it was ignored.", true);
            }

            if (manifest.Product != ProductIds.DownholeStudio || !IsNewer(manifest.Version, currentVersion))
            {
                return new UpdateCheckResult(null, $"Downhole Studio {currentVersion} is up to date.");
            }

            return new UpdateCheckResult(manifest, $"Downhole Studio {manifest.Version} is available.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogInformation("Update check failed: {Error}", ex.Message);
            return new UpdateCheckResult(null, "Could not check for updates. Check the internet connection and try again later.", true);
        }
    }

    /// <summary>Downloads and verifies the installer; returns its path.</summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(manifest.InstallerUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The update location is not a secure (HTTPS) address.");
        }

        var target = Path.Combine(paths.Updates, $"DownholeStudio-Setup-{manifest.Version}.exe");
        var temp = target + ".part";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? manifest.Size;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(temp);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        }

        await using (var check = File.OpenRead(temp))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
            if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase) || (manifest.Size > 0 && check.Length != manifest.Size))
            {
                File.Delete(temp);
                throw new InvalidOperationException("The downloaded update does not match its signed checksum and was deleted. Nothing was installed.");
            }
        }

        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>Starts the verified installer. The caller must close the application afterwards.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART /RELAUNCH")
        {
            UseShellExecute = true,
        });
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Core(candidate), out var a) && Version.TryParse(Core(current), out var b) && (a > b || (a == b && IsPre(current) && !IsPre(candidate)));

    private static string Core(string v) => v.Split('-', '+')[0];

    private static bool IsPre(string v) => v.Contains('-', StringComparison.Ordinal);
}
