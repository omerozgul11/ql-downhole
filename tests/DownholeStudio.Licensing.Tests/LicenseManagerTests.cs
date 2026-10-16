using DownholeStudio.Licensing;

namespace DownholeStudio.Licensing.Tests;

public sealed class LicenseManagerTests
{
    [Fact]
    public async Task NewTrialStartsAndAllowsEditing()
    {
        using var h = new ClientHarness();
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);
        Assert.False(h.Manager.Status.CanEdit);
        Assert.True(h.Manager.Status.CanView);
        h.Server.Respond = (path, body) =>
        {
            Assert.Equal("/api/v1/trials/start", path);
            Assert.Equal(h.DeviceHash, body.GetProperty("fingerprint").GetProperty("deviceHash").GetString());
            Assert.False(body.TryGetProperty("machineName", out var mn) && mn.ValueKind == System.Text.Json.JsonValueKind.String, "machine name must not be sent without consent");
            return h.TrialResponse(h.Clock.Now);
        };
        var result = await h.Manager.StartTrialAsync(null, shareMachineName: false);
        Assert.True(result.Success, result.Message);
        Assert.Equal(LicenseState.TrialActive, h.Manager.Status.State);
        Assert.Equal(14, h.Manager.Status.DaysRemaining);
        Assert.Equal("14 days remaining", h.Manager.Status.Message);
        Assert.True(h.Manager.Status.CanEdit);
        Assert.True(h.Manager.Status.IsTrial);
    }

    [Fact]
    public async Task RestartingTheTrialOnTheSameDeviceKeepsTheOriginalEnd()
    {
        using var h = new ClientHarness();
        var start = h.Clock.Now;
        h.Server.Respond = (_, _) => h.TrialResponse(start);
        await h.Manager.StartTrialAsync(null, false);
        h.Clock.Advance(TimeSpan.FromDays(5));
        h.Store.Value = new StoredLicense(); // e.g. local data deleted / reinstalled
        h.Manager.Evaluate();
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);
        await h.Manager.StartTrialAsync(null, false);
        Assert.Equal(start.AddDays(14), h.Manager.Status.Claims!.TrialEnd);
        Assert.Equal(9, h.Manager.Status.DaysRemaining);
    }

    [Fact]
    public async Task TrialWarnsInTheLastThreeDaysAndExpires()
    {
        using var h = new ClientHarness();
        var start = h.Clock.Now;
        h.Server.Respond = (_, _) => h.TrialResponse(start);
        await h.Manager.StartTrialAsync(null, false);
        h.Clock.Advance(TimeSpan.FromDays(12));
        await h.Manager.RefreshAsync();
        Assert.Equal(LicenseState.TrialExpiring, h.Manager.Status.State);
        Assert.Equal(2, h.Manager.Status.DaysRemaining);
        Assert.True(h.Manager.Status.CanEdit);

        h.Clock.Advance(TimeSpan.FromDays(2.5));
        h.Manager.Evaluate();
        Assert.Equal(LicenseState.TrialExpired, h.Manager.Status.State);
        Assert.False(h.Manager.Status.CanEdit);
        Assert.True(h.Manager.Status.CanView);
        Assert.Contains("projects are kept", h.Manager.Status.Message);
    }

    [Fact]
    public async Task ServerReportedTrialExpiryIsRemembered()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (_, _) => ClientHarness.Error(LicenseErrorCodes.TrialExpired, "The trial on this computer ended.");
        var r = await h.Manager.StartTrialAsync(null, false);
        Assert.False(r.Success);
        Assert.Equal(LicenseState.TrialExpired, h.Manager.Status.State);
    }

    [Fact]
    public async Task ValidLicenseActivates()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (path, body) =>
        {
            Assert.Equal("/api/v1/licenses/activate", path);
            Assert.Equal("DHST-7K3M-9QPZ-X2AB-4HVN", body.GetProperty("licenseKey").GetString());
            Assert.Equal("TEST-PC", body.GetProperty("machineName").GetString());
            return h.LicenseResponse();
        };
        var r = await h.Manager.ActivateAsync("dhst 7k3m 9qpz x2ab 4hvn", "jane@acme.example", shareMachineName: true);
        Assert.True(r.Success);
        Assert.Equal(LicenseState.Licensed, h.Manager.Status.State);
        Assert.Equal("Acme Geotechnical", h.Manager.Status.Claims!.LicensedTo);
        Assert.Contains("today", h.Manager.Status.Message);
        Assert.Contains("DHST-XXXX-XXXX-XXXX-ABCD", h.Manager.DescribeForSupport());
        Assert.DoesNotContain(h.DeviceHash, h.Manager.DescribeForSupport());
    }

    [Fact]
    public async Task MalformedKeyIsRejectedWithoutContactingTheServer()
    {
        using var h = new ClientHarness();
        var r = await h.Manager.ActivateAsync("hello", "jane@acme.example", false);
        Assert.False(r.Success);
        Assert.Equal(LicenseErrorCodes.InvalidKey, r.ErrorCode);
        Assert.Empty(h.Server.Calls);
        var r2 = await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "not-an-email", false);
        Assert.Equal(LicenseErrorCodes.EmailMismatch, r2.ErrorCode);
    }

    [Fact]
    public async Task ServerRejectionsAreExplained()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (_, _) => ClientHarness.Error(LicenseErrorCodes.InvalidKey, "This license key is not recognised.");
        var r = await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        Assert.Equal(LicenseErrorCodes.InvalidKey, r.ErrorCode);
        Assert.Equal("This license key is not recognised.", r.Message);
        h.Server.Respond = (_, _) => ClientHarness.Error(LicenseErrorCodes.EmailMismatch, "The e-mail address does not match this license.");
        Assert.Equal(LicenseErrorCodes.EmailMismatch, (await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "x@y.z", false)).ErrorCode);
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);
    }

    [Fact]
    public async Task RevokedLicenseBecomesReadOnlyButKeepsViewing()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (_, _) => h.LicenseResponse();
        await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        h.Server.Respond = (_, _) => ClientHarness.Error(LicenseErrorCodes.Revoked, "This license has been revoked.");
        var r = await h.Manager.RefreshAsync();
        Assert.False(r.Success);
        Assert.Equal(LicenseState.LicenseRevoked, h.Manager.Status.State);
        Assert.False(h.Manager.Status.CanEdit);
        Assert.True(h.Manager.Status.CanView);
        Assert.Null(h.Store.Value.Token);
        Assert.Contains("projects are safe", h.Manager.Status.Message);
    }

    [Fact]
    public async Task ActivationLimitShowsDevices()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (_, _) => new LicenseResponse
        {
            Success = false, ErrorCode = LicenseErrorCodes.ActivationLimit, Message = "This license is already active on 1 computer (limit 1).",
            Devices = [new ActivatedDeviceInfo("WORKSTATION-02", ClientHarness.T0.AddDays(-10), ClientHarness.T0.AddDays(-1))],
        };
        var r = await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        Assert.Equal(LicenseErrorCodes.ActivationLimit, r.ErrorCode);
        Assert.Equal("WORKSTATION-02", Assert.Single(r.Devices!).DeviceLabel);
        Assert.Equal(LicenseState.ActivationLimitReached, h.Manager.Status.State);
    }

    [Fact]
    public async Task OfflineGraceThenValidationRequiredWithoutDeletingTheLicense()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (_, _) => h.LicenseResponse();
        await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        h.Server.Unreachable = true;

        h.Clock.Advance(TimeSpan.FromDays(3));
        h.Manager.Evaluate();
        Assert.Equal(LicenseState.Licensed, h.Manager.Status.State);
        Assert.Equal("License last verified 3 days ago.", h.Manager.Status.Message);

        h.Clock.Advance(TimeSpan.FromDays(5));
        var refresh = await h.Manager.RefreshAsync();
        Assert.False(refresh.Success);
        Assert.Equal(LicenseState.LicenseOfflineGrace, h.Manager.Status.State);
        Assert.True(h.Manager.Status.CanEdit);
        Assert.Contains("8 days ago", h.Manager.Status.Message);

        h.Clock.Advance(TimeSpan.FromDays(23));
        h.Manager.Evaluate();
        Assert.Equal(LicenseState.NetworkValidationRequired, h.Manager.Status.State);
        Assert.False(h.Manager.Status.CanEdit);
        Assert.True(h.Manager.Status.CanView);
        Assert.NotNull(h.Store.Value.Token);

        h.Server.Unreachable = false;
        Assert.True((await h.Manager.RefreshAsync()).Success);
        Assert.Equal(LicenseState.Licensed, h.Manager.Status.State);
    }

    [Fact]
    public async Task ClockRollbackRequiresOnlineValidationAndRecovers()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (_, _) => h.LicenseResponse();
        await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        h.Clock.Advance(TimeSpan.FromDays(10));
        h.Manager.Evaluate();
        h.Clock.Advance(TimeSpan.FromDays(-6));
        h.Manager.Evaluate();
        Assert.Equal(LicenseState.NetworkValidationRequired, h.Manager.Status.State);
        Assert.True(h.Manager.Status.ClockRollbackSuspected);
        Assert.Equal("Your system clock changed significantly. Connect to the internet to verify your license. Your projects can still be opened in read-only mode.", h.Manager.Status.Message);
        Assert.NotNull(h.Store.Value.Token);

        // The server confirms the (now correct) time: the guard is re-anchored and work continues.
        Assert.True((await h.Manager.RefreshAsync()).Success);
        Assert.Equal(LicenseState.Licensed, h.Manager.Status.State);
        Assert.False(h.Manager.Status.ClockRollbackSuspected);
    }

    [Fact]
    public async Task ClockBehindServerStaysFlagged()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (_, _) => h.LicenseResponse() with { Token = h.Keys.Sign(LicenseTokenCodec.ReadUnverified(h.LicenseResponse().Token!)! with { IssuedAt = h.Clock.Now.AddDays(5) }), ServerTime = h.Clock.Now.AddDays(5) };
        await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        Assert.Equal(LicenseState.NetworkValidationRequired, h.Manager.Status.State);
        Assert.True(h.Manager.Status.ClockRollbackSuspected);
    }

    [Fact]
    public async Task ServerUnreachableIsReportedGracefully()
    {
        using var h = new ClientHarness();
        h.Server.Unreachable = true;
        var r = await h.Manager.StartTrialAsync(null, false);
        Assert.False(r.Success);
        Assert.Equal(LicenseErrorCodes.ServerError, r.ErrorCode);
        Assert.Contains("could not be reached", r.Message);
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);
    }

    [Fact]
    public async Task ExpiredLicenseKeepsViewing()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (_, _) => h.LicenseResponse(expires: ClientHarness.T0.AddDays(20));
        await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        h.Clock.Advance(TimeSpan.FromDays(21));
        h.Manager.Evaluate();
        Assert.Equal(LicenseState.LicenseExpired, h.Manager.Status.State);
        Assert.False(h.Manager.Status.CanEdit);
    }

    [Fact]
    public async Task DeactivationClearsTheLocalLicenseOnlyWhenTheServerConfirms()
    {
        using var h = new ClientHarness();
        h.Server.Respond = (_, _) => h.LicenseResponse();
        await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        h.Server.Unreachable = true;
        var offline = await h.Manager.DeactivateAsync();
        Assert.False(offline.Success);
        Assert.NotNull(h.Store.Value.Token);
        Assert.Equal(LicenseState.Licensed, h.Manager.Status.State);

        h.Server.Unreachable = false;
        h.Server.Respond = (path, _) =>
        {
            Assert.Equal("/api/v1/licenses/deactivate", path);
            return new LicenseResponse { Success = true, Message = "This computer has been deactivated." };
        };
        var r = await h.Manager.DeactivateAsync();
        Assert.True(r.Success);
        Assert.Null(h.Store.Value.Token);
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);
    }

    [Fact]
    public async Task TokenFromAnotherComputerIsNotAccepted()
    {
        using var keys = new TestKeys();
        var store = new MemoryLicenseStore();
        using (var a = new ClientHarness("device-A", store, keys))
        {
            a.Server.Respond = (_, _) => a.LicenseResponse();
            await a.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
            Assert.Equal(LicenseState.Licensed, a.Manager.Status.State);
        }

        var b = new ClientHarness("device-B", store, keys);
        Assert.Equal(LicenseState.NotActivated, b.Manager.Status.State);
        Assert.Contains("not valid for this computer", b.Manager.Status.Message);
    }

    [Fact]
    public async Task ResponseSignedWithUnknownKeyIsRefused()
    {
        using var h = new ClientHarness();
        using var rogue = new TestKeys();
        h.Server.Respond = (_, _) => h.LicenseResponse() with { Token = rogue.Sign(LicenseTokenCodec.ReadUnverified(h.LicenseResponse().Token!)!) };
        var r = await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        Assert.False(r.Success);
        Assert.Equal(LicenseErrorCodes.InvalidToken, r.ErrorCode);
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);
    }

    [Theory]
    [InlineData(0.5, "today")]
    [InlineData(1.5, "yesterday")]
    [InlineData(3.2, "3 days ago")]
    public void AgoIsReadable(double days, string expected) =>
        Assert.Equal(expected, LicenseManager.Ago(ClientHarness.T0, ClientHarness.T0.AddDays(days)));
}
