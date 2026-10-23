using System.Security.Cryptography;
using DownholeStudio.Licensing;

namespace DownholeStudio.Licensing.Tests;

/// <summary>Offline activation: computer codes, signed activation keys bound to one computer, no network.</summary>
public sealed class OfflineActivationTests
{
    private static string OfflineKey(ClientHarness h, string? code = null, DateTimeOffset? expires = null, TestKeys? keys = null) =>
        (keys ?? h.Keys).Sign(OfflineActivationKey.Claims(code ?? h.Manager.ComputerCode, "Acme Geotechnical", "jane@acme.example", "lic-1", "ABCD", h.Clock.Now, expires,
            h.Clock.Now.AddYears(1)));

    [Fact]
    public void ComputerCodeIsStableFormattedAndDiffersBetweenComputers()
    {
        var a = ComputerCode.FromDeviceHash(FakeFingerprint.HashOf("device-A"));
        Assert.Equal(a, ComputerCode.FromDeviceHash(FakeFingerprint.HashOf("device-A")));
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){4}$", a);
        Assert.NotEqual(a, ComputerCode.FromDeviceHash(FakeFingerprint.HashOf("device-B")));
    }

    [Fact]
    public void ComputerCodeInputIsNormalisedAndTyposAreRejected()
    {
        var code = ComputerCode.FromDeviceHash(FakeFingerprint.HashOf("device-A"));
        Assert.Equal(code, ComputerCode.Normalize(code.ToLowerInvariant().Replace("-", " ")));
        Assert.Equal(code, ComputerCode.Normalize(" " + code.Replace("-", "") + "\n"));
        var chars = code.ToCharArray();
        var i = Array.FindIndex(chars, char.IsLetterOrDigit);
        chars[i] = chars[i] == '7' ? '8' : '7';
        Assert.Null(ComputerCode.Normalize(new string(chars)));
        Assert.Null(ComputerCode.Normalize("12345"));
        Assert.Null(ComputerCode.Normalize(null));
    }

    [Fact]
    public void ValidKeyActivatesThisComputerWithoutAnyNetworkCall()
    {
        using var h = new ClientHarness(online: false);
        h.Server.Respond = (path, _) => throw new InvalidOperationException($"No network call expected ({path}).");
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);
        Assert.False(h.Manager.OnlineAvailable);

        var r = h.Manager.ActivateOffline(OfflineKey(h));
        Assert.True(r.Success, r.Message);
        Assert.Equal(LicenseState.Licensed, h.Manager.Status.State);
        Assert.True(h.Manager.Status.CanEdit);
        Assert.Contains("perpetual", h.Manager.Status.Message);
        Assert.Equal("Acme Geotechnical", h.Manager.Status.Claims!.LicensedTo);
        Assert.Empty(h.Server.Calls);
    }

    [Fact]
    public void ActivationSurvivesRestartAndNeverNeedsTheInternet()
    {
        var store = new MemoryLicenseStore();
        using var keys = new TestKeys();
        var clock = new TestClock(ClientHarness.T0);
        using (var h = new ClientHarness(store: store, keys: keys, clock: clock, online: false))
        {
            Assert.True(h.Manager.ActivateOffline(OfflineKey(h)).Success);
        }

        clock.Advance(TimeSpan.FromDays(400));
        using var restarted = new ClientHarness(store: store, keys: keys, clock: clock, online: false);
        Assert.Equal(LicenseState.Licensed, restarted.Manager.Status.State);
        Assert.Empty(restarted.Server.Calls);
    }

    [Fact]
    public void PastedKeyWithLineBreaksAndSpacesIsAccepted()
    {
        using var h = new ClientHarness(online: false);
        var key = OfflineKey(h);
        var wrapped = "  " + string.Join("\r\n", Enumerable.Range(0, (key.Length + 63) / 64).Select(i => key.Substring(i * 64, Math.Min(64, key.Length - i * 64)))) + " \n";
        Assert.True(h.Manager.ActivateOffline(wrapped).Success);
    }

    [Fact]
    public void KeyForAnotherComputerIsRefusedAndExplained()
    {
        using var h = new ClientHarness(online: false);
        var other = ComputerCode.FromDeviceHash(FakeFingerprint.HashOf("device-B"));
        var r = h.Manager.ActivateOffline(OfflineKey(h, other));
        Assert.False(r.Success);
        Assert.Contains("another computer", r.Message);
        Assert.Contains(h.Manager.ComputerCode, r.Message);
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);

        // A stored key copied from another computer does not activate this one either.
        h.Store.Value = new StoredLicense { Token = OfflineKey(h, other) };
        Assert.Equal(LicenseState.NotActivated, h.Manager.Evaluate().State);
    }

    [Fact]
    public void TamperedForeignAndOnlineTokensAreRefused()
    {
        using var h = new ClientHarness(online: false);
        var key = OfflineKey(h);
        var parts = key.Split('.');
        var payload = parts[2].ToCharArray();
        payload[10] = payload[10] == 'A' ? 'B' : 'A';
        Assert.False(h.Manager.ActivateOffline(string.Join('.', parts[0], parts[1], new string(payload), parts[3])).Success);

        using var stranger = new TestKeys();
        Assert.Contains("not valid", h.Manager.ActivateOffline(OfflineKey(h, keys: stranger)).Message);

        var online = h.LicenseResponse().Token!;
        Assert.Contains("not an offline activation key", h.Manager.ActivateOffline(online).Message);
        Assert.Contains("Paste the activation key", h.Manager.ActivateOffline("   ").Message);
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);
    }

    [Fact]
    public void ExpiringKeyExpiresAndSettingTheClockBackDoesNotExtendIt()
    {
        using var h = new ClientHarness(online: false);
        Assert.True(h.Manager.ActivateOffline(OfflineKey(h, expires: h.Clock.Now.AddDays(10))).Success);
        Assert.Contains("until", h.Manager.Status.Message);
        h.Clock.Advance(TimeSpan.FromDays(9));
        Assert.Equal(LicenseState.Licensed, h.Manager.Evaluate().State);

        h.Clock.Advance(TimeSpan.FromDays(2));
        var expired = h.Manager.Evaluate();
        Assert.Equal(LicenseState.LicenseExpired, expired.State);
        Assert.False(expired.CanEdit);
        Assert.True(expired.CanView);

        h.Clock.Advance(TimeSpan.FromDays(-30));
        Assert.Equal(LicenseState.LicenseExpired, h.Manager.Evaluate().State);
        Assert.False(h.Manager.ActivateOffline(OfflineKey(h, expires: ClientHarness.T0.AddDays(10))).Success);
    }

    [Fact]
    public async Task RemovingTheActivationIsLocalAndOnlineCallsExplainOfflineMode()
    {
        using var h = new ClientHarness(online: false);
        h.Server.Respond = (path, _) => throw new InvalidOperationException($"No network call expected ({path}).");
        Assert.True(h.Manager.ActivateOffline(OfflineKey(h)).Success);

        var refresh = await h.Manager.RefreshAsync();
        Assert.True(refresh.Success);
        Assert.Contains("offline", refresh.Message);

        var removed = await h.Manager.DeactivateAsync();
        Assert.True(removed.Success);
        Assert.Contains(h.Manager.ComputerCode, removed.Message);
        Assert.Equal(LicenseState.NotActivated, h.Manager.Status.State);

        var online = await h.Manager.ActivateAsync("DHST-7K3M-9QPZ-X2AB-4HVN", "jane@acme.example", false);
        Assert.False(online.Success);
        Assert.Contains("offline activation", online.Message);
        Assert.Empty(h.Server.Calls);
    }

    [Fact]
    public void OfflineKeyIsAcceptedEvenWhenAnOnlineServerIsConfigured()
    {
        using var h = new ClientHarness();
        Assert.True(h.Manager.ActivateOffline(OfflineKey(h)).Success);
        Assert.Equal(LicenseState.Licensed, h.Manager.Status.State);
        h.Clock.Advance(TimeSpan.FromDays(90));
        Assert.Equal(LicenseState.Licensed, h.Manager.Evaluate().State);
    }
}
