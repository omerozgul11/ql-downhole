using System.Net;
using System.Text.RegularExpressions;
using DownholeStudio.Licensing;
using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DownholeStudio.IntegrationTests;

public sealed partial class AdminPortalTests(LicensingEnvironment env)
{
    private HttpClient NewBrowser() => env.Portal.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true,
    });

    private async Task<string> CreateAdminAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@quakelogic.test";
        using var scope = env.Portal.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminAccountService>().CreateAsync(email, "correct horse battery staple", "Test Admin");
        return email;
    }

    private static async Task<string> TokenAsync(HttpClient browser, string url)
    {
        var html = await browser.GetStringAsync(url);
        return AntiForgery().Match(html).Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string url, string formUrl, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await TokenAsync(browser, formUrl);
        return await browser.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    private async Task LoginAsync(HttpClient browser, string email)
    {
        var response = await PostFormAsync(browser, "/Account/Login", "/Account/Login", new() { ["Email"] = email, ["Password"] = "correct horse battery staple" });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task PagesRequireSignIn()
    {
        env.RequireAvailable();
        var browser = NewBrowser();
        var response = await browser.GetAsync("/Licenses");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task WrongPasswordIsRejectedAndLocksOutAfterRepeatedFailures()
    {
        env.RequireAvailable();
        var email = await CreateAdminAsync();
        var browser = NewBrowser();
        for (var i = 0; i < AdminAccountService.MaxFailedLogins; i++)
        {
            var bad = await PostFormAsync(browser, "/Account/Login", "/Account/Login", new() { ["Email"] = email, ["Password"] = "not the password" });
            Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
            Assert.Contains("Invalid e-mail address or password.", await bad.Content.ReadAsStringAsync());
        }

        var locked = await PostFormAsync(browser, "/Account/Login", "/Account/Login", new() { ["Email"] = email, ["Password"] = "correct horse battery staple" });
        Assert.Contains("Too many failed attempts", await locked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostWithoutAntiforgeryTokenIsRefused()
    {
        env.RequireAvailable();
        var browser = NewBrowser();
        var response = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "x@y.z", ["Password"] = "whatever" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminCreatesLicenseRevokesItAndEverythingIsAudited()
    {
        env.RequireAvailable();
        var admin = await CreateAdminAsync();
        var browser = NewBrowser();
        await LoginAsync(browser, admin);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);

        var customerEmail = $"{Guid.NewGuid():N}@portal-customer.test";
        var created = await PostFormAsync(browser, "/Licenses/Create", "/Licenses/Create", new()
        {
            ["CustomerEmail"] = customerEmail, ["CustomerName"] = "Sam Surveyor", ["CustomerCompany"] = "Borehole Bros", ["Product"] = ProductIds.DownholeStudio,
            ["Edition"] = "Professional", ["Seats"] = "2", ["MaintenanceUntil"] = "2027-10-01", ["SendEmail"] = "true",
        });
        var html = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var key = LicensingFlowTests.KeyPattern().Match(html).Value;
        Assert.NotEmpty(key);
        var licenseUrl = LicenseLink().Match(html).Groups[0].Value;
        Assert.NotEmpty(licenseUrl);

        // The key works in the desktop client …
        var client = new DesktopClient(env, DesktopClient.Unique("dev"));
        Assert.True((await client.Manager.ActivateAsync(key, customerEmail, false)).Success);
        // … and is never shown again.
        var details = await browser.GetStringAsync(licenseUrl);
        Assert.DoesNotContain(key, details);
        Assert.Contains($"DHST-XXXX-XXXX-XXXX-{key[^4..]}", details);

        var revoke = await PostFormAsync(browser, licenseUrl + "?handler=Revoke", licenseUrl, []);
        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        await client.Manager.RefreshAsync();
        Assert.Equal(LicenseState.LicenseRevoked, client.Manager.Status.State);

        var audit = await env.WithServicesAsync(sp => sp.GetRequiredService<LicensingDbContext>().AuditLog.Where(a => a.Actor == admin).Select(a => a.Action).ToListAsync());
        Assert.Contains("admin.login", audit);
        Assert.Contains("customer.create", audit);
        Assert.Contains("license.create", audit);
        Assert.Contains("license.revoked", audit);
        var auditPage = await browser.GetStringAsync("/Audit?ByActor=" + Uri.EscapeDataString(admin));
        Assert.Contains("license.revoked", auditPage);

        foreach (var page in new[] { "/Customers", "/Licenses", "/Devices", "/Activations", "/Trials", "/Releases", "/Audit", "/Search?q=" + customerEmail[..8] })
        {
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(page)).StatusCode);
        }

        Assert.Contains(customerEmail, await browser.GetStringAsync("/Search?q=" + Uri.EscapeDataString(customerEmail)));
    }

    [Fact]
    public async Task AdminExtendsTrialAndManagesReleases()
    {
        env.RequireAvailable();
        var admin = await CreateAdminAsync();
        var browser = NewBrowser();
        await LoginAsync(browser, admin);
        var c = new DesktopClient(env, DesktopClient.Unique("dev"));
        await c.Manager.StartTrialAsync(null, false);
        var trialId = await env.WithServicesAsync(sp => sp.GetRequiredService<LicensingDbContext>().TrialDevices.Where(t => t.DeviceHash == c.DeviceHash).Select(t => t.Id).SingleAsync());
        var extend = await PostFormAsync(browser, "/Trials?handler=Extend", "/Trials", new() { ["trialId"] = trialId.ToString(), ["days"] = "7" });
        Assert.Equal(HttpStatusCode.Redirect, extend.StatusCode);
        await c.Manager.RefreshAsync();
        Assert.Equal(21, c.Manager.Status.DaysRemaining);

        var version = $"9.{Random.Shared.Next(1000)}.{Random.Shared.Next(1000)}";
        var release = await PostFormAsync(browser, "/Releases?handler=Create", "/Releases", new()
        {
            ["Product"] = ProductIds.DownholeStudio, ["Version"] = version, ["Channel"] = "beta", ["InstallerUrl"] = "https://downloads.test/setup.exe",
            ["Sha256"] = new string('d', 64), ["Size"] = "123", ["Notes"] = "Test", ["Publish"] = "false",
        });
        Assert.Equal(HttpStatusCode.Redirect, release.StatusCode);
        Assert.Contains(version, await browser.GetStringAsync("/Releases"));
        var bad = await PostFormAsync(browser, "/Releases?handler=Create", "/Releases", new()
        {
            ["Product"] = ProductIds.DownholeStudio, ["Version"] = "not-a-version!", ["Channel"] = "stable", ["InstallerUrl"] = "http://insecure/setup.exe", ["Sha256"] = "abc",
        });
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Contains("version must look like", await browser.GetStringAsync("/Releases"));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiForgery();

    [GeneratedRegex("/Licenses/Details/[0-9a-f-]{36}")]
    private static partial Regex LicenseLink();
}
