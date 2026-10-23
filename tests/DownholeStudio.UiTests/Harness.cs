using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using DownholeStudio.App;
using DownholeStudio.App.Services;
using DownholeStudio.App.ViewModels;
using DownholeStudio.Infrastructure;
using DownholeStudio.Licensing;
using Microsoft.Extensions.DependencyInjection;

[assembly: AvaloniaTestApplication(typeof(DownholeStudio.UiTests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DownholeStudio.UiTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        App.App.Services = UiHarness.Services;
        return AppBuilder.Configure<App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>
/// An in-process stand-in for the licensing server: issues trial and licence tokens signed with an
/// ephemeral key that the application under test trusts (the production key is never used in tests).
/// </summary>
public sealed class FakeLicenseServer : HttpMessageHandler
{
    public const string ValidKey = "DHST-ABCD-EFGH-JKMN-PQRS";
    public const string Email = "engineer@example.com";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public int Deactivations { get; private set; }

    public PublicKeyRing Ring => new(new Dictionary<string, string> { ["ui-test"] = Key.ExportSubjectPublicKeyInfoPem() });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement;
        var now = DateTimeOffset.UtcNow;
        LicenseResponse response;
        if (path.EndsWith("/updates/latest", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        var device = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("fingerprint", out var fp) ? fp.GetProperty("deviceHash").GetString()! : "";
        if (path.EndsWith("/trials/start", StringComparison.Ordinal))
        {
            response = Ok(new LicenseClaims
            {
                Type = LicenseClaims.TrialType, Product = ProductIds.DownholeStudio, DeviceHash = device, IssuedAt = now, TrialStart = now, TrialEnd = now.AddDays(14),
                ExpiresAt = now.AddDays(14), OfflineValidUntil = now.AddDays(3),
            }, "Trial started.");
        }
        else if (path.EndsWith("/licenses/activate", StringComparison.Ordinal))
        {
            var key = body.GetProperty("licenseKey").GetString();
            response = key == ValidKey && body.GetProperty("email").GetString() == Email
                ? Ok(new LicenseClaims
                {
                    Type = LicenseClaims.LicenseType, LicenseId = "LIC-UI-1", Product = ProductIds.DownholeStudio, DeviceHash = device, LicensedTo = "Example Geotechnical",
                    Email = Email, KeyHint = "PQRS", IssuedAt = now, OfflineValidUntil = now.AddDays(30), MaintenanceUntil = now.AddYears(1), SeatCount = 1,
                }, "Activated.")
                : new LicenseResponse { Success = false, ErrorCode = LicenseErrorCodes.InvalidKey, Message = "This license key is not valid.", ServerTime = now };
        }
        else if (path.EndsWith("/licenses/validate", StringComparison.Ordinal))
        {
            // Re-issues the presented token with a fresh validation time (as the real server does for a valid licence).
            var claims = LicenseTokenCodec.Verify(body.GetProperty("token").GetString()!, Ring);
            response = claims is null
                ? new LicenseResponse { Success = false, ErrorCode = LicenseErrorCodes.InvalidToken, Message = "Unknown token.", ServerTime = now }
                : Ok(claims with { IssuedAt = now, OfflineValidUntil = now.AddDays(claims.IsTrial ? 3 : 30) }, "License verified.");
        }
        else if (path.EndsWith("/licenses/deactivate", StringComparison.Ordinal))
        {
            Deactivations++;
            response = new LicenseResponse { Success = true, Message = "This computer has been deactivated.", ServerTime = now };
        }
        else
        {
            response = new LicenseResponse { Success = false, ErrorCode = LicenseErrorCodes.ServerError, Message = "Not available in tests.", ServerTime = now };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: Json) };
    }

    private LicenseResponse Ok(LicenseClaims claims, string message) => new()
    {
        Success = true, Token = LicenseTokenCodec.Sign(claims, Key, "ui-test"), Message = message, ServerTime = claims.IssuedAt,
    };
}

public static class UiHarness
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "dhs-ui-tests", Guid.NewGuid().ToString("N"));

    public static string Screenshots { get; } = Environment.GetEnvironmentVariable("DHS_SCREENSHOTS") ?? Path.Combine(Root, "screenshots");

    public static FakeLicenseServer Server { get; } = new();

    public static MemoryLicenseStore LicenseStore { get; } = new();

    private static IServiceProvider? _services;

    // A test licensing server is configured so online activation can be tested too; the shipped application has none.
    // DHS_CUSTOMER_UI=1 (screenshots, training video) shows the application exactly as customers get it: offline activation only.
    public static IServiceProvider Services => _services ??= AppHost.Build(new AppPaths(Path.Combine(Root, "appdata")), LicenseStore, Server, Server.Ring,
        licenseServerUrl: Environment.GetEnvironmentVariable("DHS_CUSTOMER_UI") == "1" ? "" : "https://licensing.test/");

    /// <summary>An offline activation key for this computer, signed like QuakeLogic's admin portal does (with the test key).</summary>
    public static string OfflineKey(string? computerCode = null, DateTimeOffset? expires = null) =>
        LicenseTokenCodec.Sign(OfflineActivationKey.Claims(computerCode ?? Main.LicenseManager.ComputerCode, "Example Geotechnical", FakeLicenseServer.Email, "LIC-UI-OFFLINE", null,
            DateTimeOffset.UtcNow, expires, DateTimeOffset.UtcNow.AddYears(1)), Server.Key, "ui-test");

    /// <summary>Activates through the activation window exactly as a customer does: paste the key and select Activate.</summary>
    public static async Task ActivateOfflineAsync(MainViewModel main)
    {
        main.License.IsOpen = true;
        main.License.ActivationKey = OfflineKey();
        main.License.ActivateOfflineCommand.Execute(null);
        await SettleAsync(100);
        Assert.True(main.LicenseManager.Status.CanEdit, main.License.Error ?? main.LicenseManager.Status.Message);
    }

    public static MainViewModel Main => Services.GetRequiredService<MainViewModel>();

    /// <summary>Processes queued UI work and background analysis until things are quiet.</summary>
    public static async Task SettleAsync(int milliseconds = 400)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Waits for the background analysis to deliver a result that reflects the latest edits. The analysis runs
    /// asynchronously after every change, so a fixed delay is not enough on a busy machine.
    /// </summary>
    public static async Task WaitForResultAsync(WorkspaceViewModel ws, Func<Analysis.AnalysisResult, bool> ready, int timeoutSeconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < until && (ws.Result is not { } r || !ready(r)))
        {
            await SettleAsync(100);
        }

        await SettleAsync(100);
    }

    public static void Capture(Avalonia.Controls.Window window, string name)
    {
        Directory.CreateDirectory(Screenshots);
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // The PNG overload is sufficient for test screenshots.
        window.CaptureRenderedFrame()?.Save(Path.Combine(Screenshots, name + ".png"));
#pragma warning restore CS0618
    }
}
