using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownholeStudio.Licensing;
using DownholeStudio.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DownholeStudio.IntegrationTests;

/// <summary>The licensing validation list, end to end: real desktop client ↔ real server ↔ PostgreSQL.</summary>
public sealed partial class LicensingFlowTests(LicensingEnvironment env)
{
    private static readonly AdminActor Admin = new("admin@quakelogic.test", "127.0.0.1");

    private async Task<(Guid LicenseId, string Key, string Email)> CreateLicenseAsync(int seats = 1, DateTimeOffset? expires = null)
    {
        var email = $"{Guid.NewGuid():N}@customer.test";
        return await env.WithServicesAsync(async sp =>
        {
            var admin = sp.GetRequiredService<AdminService>();
            var customer = await admin.UpsertCustomerAsync(Admin, null, email, "Jane Engineer", "Acme Geotechnical", null);
            var issued = await admin.CreateLicenseAsync(Admin, customer.Id, ProductIds.DownholeStudio, "Professional", seats, expires, env.Time.Now.AddYears(1), null);
            return (issued.License.Id, issued.LicenseKey, email);
        });
    }

    [Fact]
    public async Task NewTrialRestartAndReinstallKeepTheSameTrial()
    {
        env.RequireAvailable();
        var device = DesktopClient.Unique("dev");
        var first = new DesktopClient(env, device, device + "-hw");
        var r = await first.Manager.StartTrialAsync("trial@user.test", shareMachineName: true);
        Assert.True(r.Success, r.Message);
        Assert.Equal(LicenseState.TrialActive, first.Manager.Status.State);
        Assert.Equal(14, first.Manager.Status.DaysRemaining);
        var trialEnd = first.Manager.Status.Claims!.TrialEnd;

        // Trial restart on the same computer.
        Assert.True((await first.Manager.StartTrialAsync(null, false)).Success);
        Assert.Equal(trialEnd, first.Manager.Status.Claims!.TrialEnd);

        // Application reinstalled / local data deleted three days later: a fresh client store, same device.
        env.Time.Advance(TimeSpan.FromDays(3));
        var reinstalled = new DesktopClient(env, device, device + "-hw");
        Assert.Equal(LicenseState.NotActivated, reinstalled.Manager.Status.State);
        Assert.True((await reinstalled.Manager.StartTrialAsync(null, false)).Success);
        Assert.Equal(trialEnd, reinstalled.Manager.Status.Claims!.TrialEnd);
        Assert.Equal(11, reinstalled.Manager.Status.DaysRemaining);

        var count = await env.WithServicesAsync(sp => sp.GetRequiredService<LicensingDbContext>().TrialDevices.CountAsync(t => t.DeviceHash == first.DeviceHash));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OsReinstallOnSameHardwareDoesNotResetTheTrial()
    {
        env.RequireAvailable();
        var hardware = DesktopClient.Unique("hw");
        var before = new DesktopClient(env, DesktopClient.Unique("dev"), hardware);
        await before.Manager.StartTrialAsync(null, false);
        var end = before.Manager.Status.Claims!.TrialEnd;
        env.Time.Advance(TimeSpan.FromDays(1));

        // Windows reinstalled: new MachineGuid → new device hash, same SMBIOS hardware hash.
        var after = new DesktopClient(env, DesktopClient.Unique("dev"), hardware);
        Assert.True((await after.Manager.StartTrialAsync(null, false)).Success);
        Assert.Equal(end, after.Manager.Status.Claims!.TrialEnd);
        Assert.Equal(after.DeviceHash, after.Manager.Status.Claims.DeviceHash);

        env.Time.Advance(TimeSpan.FromDays(14));
        var again = new DesktopClient(env, DesktopClient.Unique("dev"), hardware);
        var r = await again.Manager.StartTrialAsync(null, false);
        Assert.False(r.Success);
        Assert.Equal(LicenseErrorCodes.TrialExpired, r.ErrorCode);
        Assert.Equal(LicenseState.TrialExpired, again.Manager.Status.State);
    }

    [Fact]
    public async Task TrialExpiresAndCannotRestart()
    {
        env.RequireAvailable();
        var c = new DesktopClient(env, DesktopClient.Unique("dev"));
        await c.Manager.StartTrialAsync(null, false);
        env.Time.Advance(TimeSpan.FromDays(12));
        Assert.True((await c.Manager.RefreshAsync()).Success);
        Assert.Equal(LicenseState.TrialExpiring, c.Manager.Status.State);
        env.Time.Advance(TimeSpan.FromDays(3));
        var refresh = await c.Manager.RefreshAsync();
        Assert.Equal(LicenseErrorCodes.TrialExpired, refresh.ErrorCode);
        Assert.Equal(LicenseState.TrialExpired, c.Manager.Status.State);
        Assert.False(c.Manager.Status.CanEdit);
        Assert.Equal(LicenseErrorCodes.TrialExpired, (await c.Manager.StartTrialAsync(null, false)).ErrorCode);
    }

    [Fact]
    public async Task ValidLicenseActivatesAndRefreshes()
    {
        env.RequireAvailable();
        var (licenseId, key, email) = await CreateLicenseAsync();
        var c = new DesktopClient(env, DesktopClient.Unique("dev"), machineName: "WORKSTATION-02");
        var r = await c.Manager.ActivateAsync(key.ToLowerInvariant().Replace("-", " "), email.ToUpperInvariant(), shareMachineName: true);
        Assert.True(r.Success, r.Message);
        var status = c.Manager.Status;
        Assert.Equal(LicenseState.Licensed, status.State);
        Assert.Equal(licenseId.ToString(), status.Claims!.LicenseId);
        Assert.Equal("Acme Geotechnical", status.Claims.LicensedTo);
        Assert.Equal(key[^4..], status.Claims.KeyHint);
        Assert.Equal(env.Time.Now.AddDays(30), status.Claims.OfflineValidUntil);

        env.Time.Advance(TimeSpan.FromDays(2));
        Assert.True((await c.Manager.RefreshAsync()).Success);
        Assert.Equal(env.Time.Now, c.Manager.Status.LastVerifiedAt);

        // Activating the same computer again does not consume another seat.
        Assert.True((await c.Manager.ActivateAsync(key, email, true)).Success);
        var seats = await env.WithServicesAsync(sp => sp.GetRequiredService<LicensingDbContext>().Activations.CountAsync(a => a.LicenseId == licenseId && a.DeactivatedAt == null));
        Assert.Equal(1, seats);
        var device = await env.WithServicesAsync(sp => sp.GetRequiredService<LicensingDbContext>().Devices.SingleAsync(d => d.DeviceHash == c.DeviceHash));
        Assert.Equal("WORKSTATION-02", device.MachineName);
    }

    [Fact]
    public async Task InvalidKeyAndWrongEmailAreRejected()
    {
        env.RequireAvailable();
        var (_, key, _) = await CreateLicenseAsync();
        var c = new DesktopClient(env, DesktopClient.Unique("dev"));
        var unknown = await c.Manager.ActivateAsync(LicenseKeyFormat.Generate(), "someone@x.test", false);
        Assert.Equal(LicenseErrorCodes.InvalidKey, unknown.ErrorCode);
        var wrongEmail = await c.Manager.ActivateAsync(key, "someone-else@x.test", false);
        Assert.Equal(LicenseErrorCodes.EmailMismatch, wrongEmail.ErrorCode);
        Assert.Equal(LicenseState.NotActivated, c.Manager.Status.State);
    }

    [Fact]
    public async Task RevokedLicenseStopsWorkingOnNextCheck()
    {
        env.RequireAvailable();
        var (licenseId, key, email) = await CreateLicenseAsync();
        var c = new DesktopClient(env, DesktopClient.Unique("dev"));
        await c.Manager.ActivateAsync(key, email, false);
        await env.WithServicesAsync(async sp =>
        {
            await sp.GetRequiredService<AdminService>().SetStatusAsync(Admin, licenseId, LicenseStatusCode.Revoked);
            return 0;
        });
        await c.Manager.RefreshAsync();
        Assert.Equal(LicenseState.LicenseRevoked, c.Manager.Status.State);
        Assert.True(c.Manager.Status.CanView);
        Assert.False(c.Manager.Status.CanEdit);
        var other = new DesktopClient(env, DesktopClient.Unique("dev"));
        Assert.Equal(LicenseErrorCodes.Revoked, (await other.Manager.ActivateAsync(key, email, false)).ErrorCode);
    }

    [Fact]
    public async Task ActivationLimitDeactivationAndMoveToReplacementComputer()
    {
        env.RequireAvailable();
        var (_, key, email) = await CreateLicenseAsync(seats: 1);
        var oldPc = new DesktopClient(env, DesktopClient.Unique("dev"), machineName: "OLD-PC");
        var newPc = new DesktopClient(env, DesktopClient.Unique("dev"), machineName: "NEW-PC");
        Assert.True((await oldPc.Manager.ActivateAsync(key, email, true)).Success);

        var limit = await newPc.Manager.ActivateAsync(key, email, true);
        Assert.Equal(LicenseErrorCodes.ActivationLimit, limit.ErrorCode);
        Assert.Equal("OLD-PC", Assert.Single(limit.Devices!).DeviceLabel);
        Assert.Equal(LicenseState.ActivationLimitReached, newPc.Manager.Status.State);

        var deactivated = await oldPc.Manager.DeactivateAsync();
        Assert.True(deactivated.Success, deactivated.Message);
        Assert.Equal(LicenseState.NotActivated, oldPc.Manager.Status.State);
        Assert.True((await newPc.Manager.ActivateAsync(key, email, true)).Success);
        Assert.Equal(LicenseState.Licensed, newPc.Manager.Status.State);

        var mail = await env.WithServicesAsync(sp => sp.GetRequiredService<LicensingDbContext>().EmailOutbox.Where(m => m.ToAddress == email).Select(m => m.Template).ToListAsync());
        Assert.Contains("device-deactivated", mail);
        Assert.Contains("license-key", mail);
    }

    [Fact]
    public async Task RapidTransfersAreLimitedButAdminCanOverride()
    {
        env.RequireAvailable();
        var (licenseId, key, email) = await CreateLicenseAsync(seats: 1);
        var pc = new DesktopClient(env, DesktopClient.Unique("dev"));
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await pc.Manager.ActivateAsync(key, email, false)).Success);
            Assert.True((await pc.Manager.DeactivateAsync()).Success);
        }

        Assert.True((await pc.Manager.ActivateAsync(key, email, false)).Success);
        var blocked = await pc.Manager.DeactivateAsync();
        Assert.False(blocked.Success);
        Assert.Equal(LicenseErrorCodes.TooManyTransfers, blocked.ErrorCode);
        Assert.Equal(LicenseState.Licensed, pc.Manager.Status.State);

        // An administrator can still move the licence for a legitimate customer.
        await env.WithServicesAsync(async sp => await sp.GetRequiredService<AdminService>().ResetActivationsAsync(Admin, licenseId));
        var replacement = new DesktopClient(env, DesktopClient.Unique("dev"));
        Assert.True((await replacement.Manager.ActivateAsync(key, email, false)).Success);
        await pc.Manager.RefreshAsync();
        Assert.Equal(LicenseState.NotActivated, pc.Manager.Status.State);

        // After 30 days the self-service allowance is available again.
        env.Time.Advance(TimeSpan.FromDays(31));
        Assert.True((await replacement.Manager.RefreshAsync()).Success);
        Assert.True((await replacement.Manager.DeactivateAsync()).Success);
    }

    [Fact]
    public async Task OfflineModeAndServerUnreachable()
    {
        env.RequireAvailable();
        var (_, key, email) = await CreateLicenseAsync();
        var c = new DesktopClient(env, DesktopClient.Unique("dev"));
        await c.Manager.ActivateAsync(key, email, false);
        c.Network.Offline = true;

        env.Time.Advance(TimeSpan.FromDays(3));
        var r = await c.Manager.RefreshAsync();
        Assert.False(r.Success);
        Assert.Contains("could not be reached", r.Message);
        Assert.Equal(LicenseState.Licensed, c.Manager.Status.State);
        Assert.Equal("License last verified 3 days ago.", c.Manager.Status.Message);

        env.Time.Advance(TimeSpan.FromDays(7));
        c.Manager.Evaluate();
        Assert.Equal(LicenseState.LicenseOfflineGrace, c.Manager.Status.State);
        Assert.True(c.Manager.Status.CanEdit);

        env.Time.Advance(TimeSpan.FromDays(21));
        c.Manager.Evaluate();
        Assert.Equal(LicenseState.NetworkValidationRequired, c.Manager.Status.State);
        Assert.True(c.Manager.Status.CanView);
        Assert.NotNull(c.Store.Value.Token);

        c.Network.Offline = false;
        Assert.True((await c.Manager.RefreshAsync()).Success);
        Assert.Equal(LicenseState.Licensed, c.Manager.Status.State);

        var unreachable = new DesktopClient(env, DesktopClient.Unique("dev"));
        unreachable.Network.Offline = true;
        var trial = await unreachable.Manager.StartTrialAsync(null, false);
        Assert.Equal(LicenseErrorCodes.ServerError, trial.ErrorCode);
        Assert.Equal(LicenseState.NotActivated, unreachable.Manager.Status.State);
    }

    [Fact]
    public async Task ClockRollbackNeedsOnlineValidation()
    {
        env.RequireAvailable();
        var (_, key, email) = await CreateLicenseAsync();
        var c = new DesktopClient(env, DesktopClient.Unique("dev"));
        await c.Manager.ActivateAsync(key, email, false);
        var real = env.Time.Now;
        env.Time.Now = real.AddDays(-5); // only the client sees this: the user turned the clock back
        c.Manager.Evaluate();
        Assert.Equal(LicenseState.NetworkValidationRequired, c.Manager.Status.State);
        Assert.True(c.Manager.Status.ClockRollbackSuspected);
        env.Time.Now = real;
        Assert.True((await c.Manager.RefreshAsync()).Success);
        Assert.Equal(LicenseState.Licensed, c.Manager.Status.State);
    }

    [Fact]
    public async Task ExpiredSubscriptionIsReported()
    {
        env.RequireAvailable();
        var (_, key, email) = await CreateLicenseAsync(expires: env.Time.Now.AddDays(10));
        var c = new DesktopClient(env, DesktopClient.Unique("dev"));
        await c.Manager.ActivateAsync(key, email, false);
        env.Time.Advance(TimeSpan.FromDays(11));
        c.Manager.Evaluate();
        Assert.Equal(LicenseState.LicenseExpired, c.Manager.Status.State);
        Assert.Equal(LicenseErrorCodes.Expired, (await new DesktopClient(env, DesktopClient.Unique("dev")).Manager.ActivateAsync(key, email, false)).ErrorCode);
    }

    [Fact]
    public async Task ForgedOrForeignTokensAreRejectedByTheServer()
    {
        env.RequireAvailable();
        var (_, key, email) = await CreateLicenseAsync();
        var c = new DesktopClient(env, DesktopClient.Unique("dev"));
        await c.Manager.ActivateAsync(key, email, false);
        var http = env.Server.CreateClient();
        // The same token presented from a different computer.
        var other = await http.PostAsJsonAsync("/api/v1/licenses/validate", new ValidationRequest
        {
            Token = c.Store.Value.Token!, AppVersion = "1.0.0", Fingerprint = new DeviceFingerprint { DeviceHash = DesktopClient.Hash("someone-else") },
        });
        var body = await other.Content.ReadFromJsonAsync<LicenseResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(LicenseErrorCodes.InvalidToken, body!.ErrorCode);
        // A token signed with an unknown key.
        using var rogue = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var forged = LicenseTokenCodec.Sign(LicenseTokenCodec.ReadUnverified(c.Store.Value.Token!)! with { SeatCount = 99 }, rogue, "test-lic");
        var r = await http.PostAsJsonAsync("/api/v1/licenses/validate", new ValidationRequest { Token = forged, AppVersion = "1.0.0", Fingerprint = new DeviceFingerprint { DeviceHash = c.DeviceHash } });
        Assert.Equal(LicenseErrorCodes.InvalidToken, (await r.Content.ReadFromJsonAsync<LicenseResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.ErrorCode);
    }

    [Fact]
    public async Task SpecAliasActivationEndpointWorks()
    {
        env.RequireAvailable();
        var (_, key, email) = await CreateLicenseAsync();
        var http = env.Server.CreateClient();
        var response = await http.PostAsJsonAsync("/api/licenses/activate", new ActivationRequest
        {
            LicenseKey = key, Email = email, Product = ProductIds.DownholeStudio, AppVersion = "1.0.0",
            Fingerprint = new DeviceFingerprint { DeviceHash = DesktopClient.Hash(DesktopClient.Unique("dev")) },
        });
        var body = await response.Content.ReadFromJsonAsync<LicenseResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(body!.Success);
        Assert.NotNull(LicenseTokenCodec.Verify(body.Token!, env.LicenseRing));
    }

    [Fact]
    public async Task HealthEndpointReportsDatabase()
    {
        env.RequireAvailable();
        var response = await env.Server.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReminderWorkerQueuesEachReminderOnce()
    {
        env.RequireAvailable();
        var c = new DesktopClient(env, DesktopClient.Unique("dev"));
        var email = $"{Guid.NewGuid():N}@trial.test";
        await c.Manager.StartTrialAsync(email, false);
        env.Time.Advance(TimeSpan.FromDays(12));
        var worker = ActivatorUtilities.CreateInstance<ReminderWorker>(env.Server.Services);
        await worker.RunOnceAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);
        var reminders = await env.WithServicesAsync(sp => sp.GetRequiredService<LicensingDbContext>().EmailOutbox.CountAsync(m => m.ToAddress == email && m.Template == "trial-reminder"));
        Assert.Equal(1, reminders);
    }

    [GeneratedRegex(@"DHST-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}")]
    internal static partial Regex KeyPattern();
}

public sealed class PaymentAndUpdateTests(LicensingEnvironment env)
{
    private static string CheckoutEvent(string eventId, string email, int seats = 2) => JsonSerializer.Serialize(new
    {
        id = eventId, type = "checkout.session.completed",
        data = new
        {
            @object = new
            {
                id = "cs_test_" + eventId, payment_status = "paid", amount_total = 249900, currency = "usd", customer = "cus_test_123", payment_intent = "pi_test_" + eventId,
                customer_details = new { email, name = "Pat Buyer" }, metadata = new { product = ProductIds.DownholeStudio, edition = "Professional", seats = seats.ToString() },
            },
        },
    });

    private async Task<HttpResponseMessage> PostWebhookAsync(string payload, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/stripe/webhook") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (signature is not null)
        {
            request.Headers.Add("Stripe-Signature", signature);
        }

        return await env.Server.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task StripeCheckoutCreatesLicenseOnceAndEmailsTheKey()
    {
        env.RequireAvailable();
        var email = $"{Guid.NewGuid():N}@buyer.test";
        var eventId = "evt_" + Guid.NewGuid().ToString("N");
        var payload = CheckoutEvent(eventId, email);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostWebhookAsync(payload, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWebhookAsync(payload, StripeSignature.Sign(payload, "whsec_wrong", env.Time.Now))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWebhookAsync(payload, StripeSignature.Sign(payload, LicensingEnvironment.WebhookSecret, env.Time.Now.AddMinutes(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWebhookAsync(payload + " ", StripeSignature.Sign(payload, LicensingEnvironment.WebhookSecret, env.Time.Now))).StatusCode);

        var signature = StripeSignature.Sign(payload, LicensingEnvironment.WebhookSecret, env.Time.Now);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(payload, signature)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(payload, signature)).StatusCode); // Stripe retry

        var (licenses, payments, templates) = await env.WithServicesAsync(async sp =>
        {
            var db = sp.GetRequiredService<LicensingDbContext>();
            var l = await db.Licenses.Include(x => x.Customer).Where(x => x.Customer!.Email == email).ToListAsync();
            var p = await db.Payments.Where(x => x.ProviderSessionId == "cs_test_" + eventId).ToListAsync();
            var t = await db.EmailOutbox.Where(m => m.ToAddress == email).Select(m => m.Template).ToListAsync();
            return (l, p, t);
        });
        var license = Assert.Single(licenses);
        Assert.Equal(2, license.SeatCount);
        Assert.Equal("Pat Buyer", license.Customer!.Name);
        var payment = Assert.Single(payments);
        Assert.Equal(249900, payment.Amount);
        Assert.Equal(["license-key", "purchase-confirmation"], templates.Order().ToList());

        // Deliver the outbox and activate with the key the customer received.
        var worker = ActivatorUtilities.CreateInstance<EmailDispatchWorker>(env.Server.Services);
        await worker.DispatchOnceAsync(CancellationToken.None);
        EmailMessage keyMail;
        lock (env.Mail.Sent)
        {
            keyMail = env.Mail.Sent.Single(m => m.ToAddress == email && m.Template == "license-key");
        }

        var key = LicensingFlowTests.KeyPattern().Match(keyMail.TextBody).Value;
        Assert.Equal(license.KeyHint, key[^4..]);
        Assert.Contains(key, keyMail.HtmlBody);
        var client = new DesktopClient(env, DesktopClient.Unique("dev"));
        Assert.True((await client.Manager.ActivateAsync(key, email, false)).Success);
    }

    [Fact]
    public async Task UpdatesFeedReturnsSignedManifestForChannel()
    {
        env.RequireAvailable();
        var product = "update-test-" + Guid.NewGuid().ToString("N")[..8];
        await env.WithServicesAsync(async sp =>
        {
            var db = sp.GetRequiredService<LicensingDbContext>();
            db.LicenseProducts.Add(new LicenseProduct { Id = product, Name = "Update test" });
            await db.SaveChangesAsync();
            var admin = sp.GetRequiredService<AdminService>();
            var actor = new AdminActor("releaser@quakelogic.test", null);
            string Sha(char c) => new(c, 64);
            await admin.CreateReleaseAsync(actor, product, "1.0.1", "stable", "Bug fixes", "https://downloads.test/1.0.1.exe", Sha('a'), 1000, false, null, true);
            await admin.CreateReleaseAsync(actor, product, "1.1.0-beta.1", "beta", "Preview", "https://downloads.test/1.1.0b1.exe", Sha('b'), 1000, false, null, true);
            await admin.CreateReleaseAsync(actor, product, "1.2.0", "stable", "Unpublished", "https://downloads.test/1.2.0.exe", Sha('c'), 1000, true, null, false);
            return 0;
        });
        var http = env.Server.CreateClient();
        var stable = await http.GetFromJsonAsync<SignedEnvelope>($"/api/v1/updates/latest?product={product}&channel=stable&current=1.0.0");
        var manifest = EnvelopeCodec.Verify<UpdateManifest>(stable!, env.UpdateRing);
        Assert.Equal("1.0.1", manifest!.Version);
        Assert.Equal(new string('a', 64), manifest.Sha256);
        Assert.Null(EnvelopeCodec.Verify<UpdateManifest>(stable!, env.LicenseRing));

        var beta = await http.GetFromJsonAsync<SignedEnvelope>($"/api/v1/updates/latest?product={product}&channel=beta&current=1.0.0");
        Assert.Equal("1.1.0-beta.1", EnvelopeCodec.Verify<UpdateManifest>(beta!, env.UpdateRing)!.Version);
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync($"/api/v1/updates/latest?product={product}&channel=stable&current=1.0.1")).StatusCode);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("1.2.0-beta.1", "1.2.0", -1)]
    [InlineData("v2.0", "2.0.0", 0)]
    public void ReleaseVersionsCompare(string a, string b, int sign)
    {
        Assert.True(UpdateService.TryParseVersion(a, out var va));
        Assert.True(UpdateService.TryParseVersion(b, out var vb));
        Assert.Equal(sign, Math.Sign(va.CompareTo(vb)));
    }
}
