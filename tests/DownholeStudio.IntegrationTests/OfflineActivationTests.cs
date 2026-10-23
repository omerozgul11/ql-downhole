using DownholeStudio.Licensing;
using DownholeStudio.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DownholeStudio.IntegrationTests;

/// <summary>Offline activation keys issued from the admin side and used by real desktop clients without a network.</summary>
public sealed class OfflineActivationTests(LicensingEnvironment env)
{
    private static readonly AdminActor Admin = new("admin@quakelogic.test", "127.0.0.1");

    private Task<Guid> CreateLicenseAsync(int seats) => env.WithServicesAsync(async sp =>
    {
        var admin = sp.GetRequiredService<AdminService>();
        var customer = await admin.UpsertCustomerAsync(Admin, null, $"{Guid.NewGuid():N}@customer.test", "Jane Engineer", "Acme Geotechnical", null);
        return (await admin.CreateLicenseAsync(Admin, customer.Id, ProductIds.DownholeStudio, "Professional", seats, null, env.Time.Now.AddYears(1), null)).License.Id;
    });

    private Task<OfflineActivationResult> IssueAsync(Guid licenseId, string code, string? label = null, bool email = false) =>
        env.WithServicesAsync(sp => sp.GetRequiredService<AdminService>().IssueOfflineActivationAsync(Admin, licenseId, code, label, email));

    [Fact]
    public async Task IssuedKeyActivatesTheCustomersComputerWithoutTheServer()
    {
        env.RequireAvailable();
        var licenseId = await CreateLicenseAsync(seats: 1);
        var customer = new DesktopClient(env, DesktopClient.Unique("offline"));
        customer.Network.Offline = true;

        var issued = await IssueAsync(licenseId, customer.Manager.ComputerCode.ToLowerInvariant().Replace("-", " "), "Field laptop", email: true);
        Assert.Equal(customer.Manager.ComputerCode, issued.ComputerCode);
        var r = customer.Manager.ActivateOffline(issued.ActivationKey);
        Assert.True(r.Success, r.Message);
        Assert.Equal(LicenseState.Licensed, customer.Manager.Status.State);
        Assert.Equal("Acme Geotechnical", customer.Manager.Status.Claims!.LicensedTo);
        Assert.Equal(licenseId.ToString(), customer.Manager.Status.Claims.LicenseId);

        await env.WithServicesAsync(async sp =>
        {
            var db = sp.GetRequiredService<LicensingDbContext>();
            var activation = await db.Activations.Include(a => a.Device).SingleAsync(a => a.LicenseId == licenseId && a.DeactivatedAt == null);
            Assert.Equal("Field laptop", activation.Device!.MachineName);
            Assert.True(await db.LicenseEvents.AnyAsync(e => e.LicenseId == licenseId && e.EventType == "activation.offline"));
            Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "license.offline_activation" && a.EntityId == licenseId.ToString()));
            var mail = await db.EmailOutbox.SingleAsync(m => m.LicenseId == licenseId && m.Template == "activation-key");
            Assert.Contains(issued.ActivationKey, mail.TextBody);
            return 0;
        });

        // Issuing again for the same computer reuses its seat.
        Assert.Equal(issued.ComputerCode, (await IssueAsync(licenseId, issued.ComputerCode)).ComputerCode);
    }

    [Fact]
    public async Task SeatsTyposAndRevokedLicensesAreEnforced()
    {
        env.RequireAvailable();
        var licenseId = await CreateLicenseAsync(seats: 1);
        var first = new DesktopClient(env, DesktopClient.Unique("first"));
        var second = new DesktopClient(env, DesktopClient.Unique("second"));
        await IssueAsync(licenseId, first.Manager.ComputerCode);

        var full = await Assert.ThrowsAsync<InvalidOperationException>(() => IssueAsync(licenseId, second.Manager.ComputerCode));
        Assert.Contains("seat", full.Message);

        var code = second.Manager.ComputerCode.ToCharArray();
        code[0] = code[0] == '7' ? '8' : '7';
        Assert.Contains("not a valid computer code", (await Assert.ThrowsAsync<InvalidOperationException>(() => IssueAsync(licenseId, new string(code)))).Message);

        // Freeing the seat (the customer reports the old computer is retired) allows the new computer.
        await env.WithServicesAsync(async sp =>
        {
            var activation = await sp.GetRequiredService<LicensingDbContext>().Activations.SingleAsync(a => a.LicenseId == licenseId && a.DeactivatedAt == null);
            await sp.GetRequiredService<AdminService>().DeactivateActivationAsync(Admin, activation.Id, "Computer retired");
            return 0;
        });
        var moved = await IssueAsync(licenseId, second.Manager.ComputerCode);
        Assert.True(second.Manager.ActivateOffline(moved.ActivationKey).Success);
        Assert.False(first.Manager.ActivateOffline(moved.ActivationKey).Success);

        await env.WithServicesAsync(async sp =>
        {
            await sp.GetRequiredService<AdminService>().SetStatusAsync(Admin, licenseId, LicenseStatusCode.Revoked);
            return 0;
        });
        Assert.Contains("revoked", (await Assert.ThrowsAsync<InvalidOperationException>(() => IssueAsync(licenseId, second.Manager.ComputerCode))).Message);
    }
}
