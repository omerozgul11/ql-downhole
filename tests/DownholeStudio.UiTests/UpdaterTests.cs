using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DownholeStudio.Infrastructure;
using DownholeStudio.Licensing;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownholeStudio.UiTests;

/// <summary>The updater accepts only correctly signed manifests and installers that match them.</summary>
public sealed class UpdaterTests
{
    private sealed class Feed(SignedEnvelope? envelope, byte[] installer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/updates/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(envelope is null ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(envelope) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        }
    }

    private static (UpdateService Service, AppPaths Paths) Create(SignedEnvelope? envelope, byte[] installer, PublicKeyRing keys)
    {
        var http = new HttpClient(new Feed(envelope, installer));
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "dhs-update-tests", Guid.NewGuid().ToString("N")));
        var api = new LicenseApiClient(http, new Uri("https://licensing.test/"));
        return (new UpdateService(api, http, paths, "1.0.0", NullLogger<UpdateService>.Instance, keys), paths);
    }

    private static UpdateManifest Manifest(byte[] installer, string version = "1.1.0") => new()
    {
        Product = ProductIds.DownholeStudio, Channel = "stable", Version = version, InstallerUrl = "https://downloads.test/DownholeStudio-Setup.exe",
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(installer)), Size = installer.Length, ReleaseNotes = "Fixes",
    };

    [Fact]
    public async Task DownloadsAndVerifiesASignedUpdate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ring = new PublicKeyRing(new Dictionary<string, string> { ["upd"] = key.ExportSubjectPublicKeyInfoPem() });
        var installer = RandomNumberGenerator.GetBytes(4096);
        var (service, _) = Create(EnvelopeCodec.Sign(Manifest(installer), key, "upd"), installer, ring);
        var check = await service.CheckAsync(UpdateChannel.Stable);
        Assert.Equal("1.1.0", check.Available?.Version);
        var path = await service.DownloadAsync(check.Available!, null);
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task IgnoresManifestWithWrongSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ring = new PublicKeyRing(new Dictionary<string, string> { ["upd"] = key.ExportSubjectPublicKeyInfoPem() });
        var installer = RandomNumberGenerator.GetBytes(1024);
        var (service, _) = Create(EnvelopeCodec.Sign(Manifest(installer), other, "upd"), installer, ring);
        var check = await service.CheckAsync(UpdateChannel.Stable);
        Assert.Null(check.Available);
        Assert.True(check.Failed);
    }

    [Fact]
    public async Task RejectsTamperedInstallerAndDeletesIt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ring = new PublicKeyRing(new Dictionary<string, string> { ["upd"] = key.ExportSubjectPublicKeyInfoPem() });
        var genuine = RandomNumberGenerator.GetBytes(2048);
        var tampered = genuine.ToArray();
        tampered[100] ^= 0xFF;
        var (service, paths) = Create(EnvelopeCodec.Sign(Manifest(genuine), key, "upd"), tampered, ring);
        var check = await service.CheckAsync(UpdateChannel.Stable);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(check.Available!, null));
        Assert.Contains("does not match", ex.Message);
        Assert.Empty(Directory.GetFiles(paths.Updates));
    }

    [Fact]
    public async Task NoUpdateWhenCurrent()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ring = new PublicKeyRing(new Dictionary<string, string> { ["upd"] = key.ExportSubjectPublicKeyInfoPem() });
        var (service, _) = Create(null, [], ring);
        var check = await service.CheckAsync(UpdateChannel.Stable);
        Assert.Null(check.Available);
        Assert.False(check.Failed);
        Assert.True(UpdateService.IsNewer("1.0.1", "1.0.0"));
        Assert.True(UpdateService.IsNewer("1.0.0", "1.0.0-beta.2"));
        Assert.False(UpdateService.IsNewer("1.0.0", "1.0.0"));
    }
}
