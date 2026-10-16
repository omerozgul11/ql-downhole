using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DownholeStudio.Licensing;

namespace DownholeStudio.Licensing.Tests;

public sealed class CryptoTests
{
    private static LicenseClaims Claims() => new()
    {
        Type = LicenseClaims.LicenseType, LicenseId = "L1", Product = ProductIds.DownholeStudio, DeviceHash = new string('a', 64),
        IssuedAt = ClientHarness.T0, OfflineValidUntil = ClientHarness.T0.AddDays(30), Features = ["all"],
    };

    [Fact]
    public void SignedTokenVerifiesAndRoundTripsClaims()
    {
        using var keys = new TestKeys();
        var token = keys.Sign(Claims());
        Assert.StartsWith("dhs1.test-key.", token);
        var claims = LicenseTokenCodec.Verify(token, keys.Ring);
        Assert.NotNull(claims);
        Assert.Equal("L1", claims.LicenseId);
        Assert.Equal(ClientHarness.T0.AddDays(30), claims.OfflineValidUntil);
        Assert.Equal(["all"], claims.Features);
    }

    [Fact]
    public void TamperedTokenIsRejected()
    {
        using var keys = new TestKeys();
        var parts = keys.Sign(Claims()).Split('.');
        var forged = LicenseTokenCodec.Sign(Claims() with { LicenseId = "FORGED", Features = ["everything"] }, ECDsa.Create(ECCurve.NamedCurves.nistP256), TestKeys.KeyId).Split('.');
        // Payload from a forged token with the genuine signature.
        Assert.Null(LicenseTokenCodec.Verify($"{parts[0]}.{parts[1]}.{forged[2]}.{parts[3]}", keys.Ring));
        Assert.Null(LicenseTokenCodec.Verify(string.Join('.', parts[..3]) + ".AAAA", keys.Ring));
        Assert.Null(LicenseTokenCodec.Verify("not-a-token", keys.Ring));
        Assert.Null(LicenseTokenCodec.Verify("dhs1.test-key.!!!.???", keys.Ring));
    }

    [Fact]
    public void WrongKeyIdOrKeyIsRejected()
    {
        using var keys = new TestKeys();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(LicenseTokenCodec.Verify(LicenseTokenCodec.Sign(Claims(), keys.Key, "unknown-kid"), keys.Ring));
        Assert.Null(LicenseTokenCodec.Verify(LicenseTokenCodec.Sign(Claims(), other, TestKeys.KeyId), keys.Ring));
        // The production ring does not accept a token signed with a test key.
        Assert.Null(LicenseTokenCodec.Verify(keys.Sign(Claims()), EmbeddedKeys.LicenseKeys));
    }

    [Fact]
    public void ReadUnverifiedDoesNotNeedKeys() =>
        Assert.Equal("L1", LicenseTokenCodec.ReadUnverified(new TestKeys().Sign(Claims()))!.LicenseId);

    [Fact]
    public void GeneratedKeysAreWellFormedAndUnique()
    {
        var keys = Enumerable.Range(0, 2000).Select(_ => LicenseKeyFormat.Generate()).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Matches(new Regex("^DHST-[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$"), k));
        Assert.All(keys, k => Assert.Equal(k, LicenseKeyFormat.Normalize(k)));
    }

    [Theory]
    [InlineData("dhst-7k3m-9qpz-x2ab-4hvn", "DHST-7K3M-9QPZ-X2AB-4HVN")]
    [InlineData(" 7K3M 9QPZ X2AB 4HVN ", "DHST-7K3M-9QPZ-X2AB-4HVN")]
    [InlineData("DHST-OK3M-9QPZ-X2AB-4HVN", "DHST-0K3M-9QPZ-X2AB-4HVN")] // O → 0
    [InlineData("DHST-7K3M-9QPZ-X2AB-IL00", "DHST-7K3M-9QPZ-X2AB-1100")] // I, L → 1
    public void NormalisesUserInput(string input, string expected) => Assert.Equal(expected, LicenseKeyFormat.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("DHST-7K3M-9QPZ-X2AB")]
    [InlineData("DHST-7K3M-9QPZ-X2AB-4HVU")] // U is not in the alphabet
    [InlineData("DHST-7K3M-9QPZ-X2AB-4HVN-1234")]
    public void RejectsMalformedKeys(string input) => Assert.Null(LicenseKeyFormat.Normalize(input));

    [Fact]
    public void KeyHashDependsOnPepper()
    {
        const string key = "DHST-7K3M-9QPZ-X2AB-4HVN";
        Assert.Equal(LicenseKeyFormat.Hash(key, "p1"), LicenseKeyFormat.Hash(key, "p1"));
        Assert.NotEqual(LicenseKeyFormat.Hash(key, "p1"), LicenseKeyFormat.Hash(key, "p2"));
        Assert.Equal(64, LicenseKeyFormat.Hash(key, "p1").Length);
        Assert.Equal("4HVN", LicenseKeyFormat.Hint(key));
        Assert.Equal("DHST-XXXX-XXXX-XXXX-4HVN", LicenseKeyFormat.Masked("4HVN"));
    }

    [Fact]
    public void EnvelopeVerifiesAndDetectsTampering()
    {
        using var keys = new TestKeys();
        var manifest = new UpdateManifest { Product = ProductIds.DownholeStudio, Channel = "stable", Version = "1.0.1", InstallerUrl = "https://x/setup.exe", Sha256 = new string('b', 64) };
        var envelope = EnvelopeCodec.Sign(manifest, keys.Key, TestKeys.KeyId);
        Assert.Equal("1.0.1", EnvelopeCodec.Verify<UpdateManifest>(envelope, keys.Ring)!.Version);
        var forged = EnvelopeCodec.Sign(manifest with { InstallerUrl = "https://evil/setup.exe" }, ECDsa.Create(ECCurve.NamedCurves.nistP256), TestKeys.KeyId);
        Assert.Null(EnvelopeCodec.Verify<UpdateManifest>(envelope with { Payload = forged.Payload }, keys.Ring));
        Assert.Null(EnvelopeCodec.Verify<UpdateManifest>(forged, keys.Ring));
    }

    [Fact]
    public void EmbeddedPublicKeysAreValidP256Keys()
    {
        using var k = ECDsa.Create();
        k.ImportFromPem(EmbeddedKeys.LicensePublicKey);
        Assert.Equal(256, k.KeySize);
        k.ImportFromPem(EmbeddedKeys.UpdatePublicKey);
        Assert.Equal(256, k.KeySize);
    }
}

public sealed class FingerprintTests
{
    [Theory]
    [InlineData("{6f1d1a39-9c5d-4b0e-9a1f-1b2c3d4e5f60}", "6F1D1A399C5D4B0E9A1F1B2C3D4E5F60")]
    [InlineData(" abc-123 ", "ABC123")]
    public void NormalisesIdentifiers(string raw, string expected) => Assert.Equal(expected, MachineFingerprintProvider.Normalise(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
    [InlineData("03000200-0400-0500-0006-000700080009")]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("Default string")]
    [InlineData("AAAAAAAA")]
    public void RejectsPlaceholderIdentifiers(string? raw) => Assert.Null(MachineFingerprintProvider.Normalise(raw));

    [Fact]
    public void FingerprintIsStableSaltedAndHashed()
    {
        var a = new MachineFingerprintProvider("salt-1").GetFingerprint();
        var b = new MachineFingerprintProvider("salt-1").GetFingerprint();
        var c = new MachineFingerprintProvider("salt-2").GetFingerprint();
        Assert.Equal(a, b);
        Assert.NotEqual(a.DeviceHash, c.DeviceHash);
        Assert.Matches("^[0-9a-f]{64}$", a.DeviceHash);
        Assert.DoesNotContain(Environment.MachineName.ToLowerInvariant(), a.DeviceHash);
    }

    [Fact]
    public void ParsesSmbiosSystemUuidAndBoardSerial()
    {
        var table = new List<byte>();
        // Type 0 (BIOS), 0x12 bytes formatted area, one string.
        table.AddRange([0, 0x12, 0x00, 0x00]);
        table.AddRange(new byte[0x12 - 4]);
        table.AddRange("BIOS\0\0"u8.ToArray());
        // Type 1 (System), 0x1B bytes: UUID at offset 8.
        var type1 = new byte[0x1B];
        type1[0] = 1;
        type1[1] = 0x1B;
        for (var i = 0; i < 16; i++)
        {
            type1[8 + i] = (byte)(0x10 + i);
        }

        table.AddRange(type1);
        table.AddRange("\0\0"u8.ToArray()); // no strings
        // Type 2 (Baseboard), 0x0F bytes: manufacturer=1, product=2, version=3, serial=4.
        var type2 = new byte[0x0F];
        type2[0] = 2;
        type2[1] = 0x0F;
        type2[4] = 1;
        type2[5] = 2;
        type2[6] = 3;
        type2[7] = 4;
        table.AddRange(type2);
        table.AddRange("Maker\0Board X\0v1\0SN-778899\0\0"u8.ToArray());
        // Type 127 (end of table).
        table.AddRange([127, 4, 0, 0, 0, 0]);

        var raw = new byte[8 + table.Count];
        BitConverter.GetBytes(table.Count).CopyTo(raw, 4);
        table.CopyTo(raw, 8);
        var (uuid, serial) = MachineFingerprintProvider.ParseSmbios(raw);
        Assert.Equal("101112131415161718191A1B1C1D1E1F", uuid);
        Assert.Equal("SN-778899", serial);
    }

    [Fact]
    public void ParseSmbiosToleratesGarbage()
    {
        Assert.Equal((null, null), MachineFingerprintProvider.ParseSmbios([1, 2, 3]));
        Assert.Equal((null, null), MachineFingerprintProvider.ParseSmbios(new byte[64]));
    }
}

public sealed class StoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "dhs-lic-tests", Guid.NewGuid().ToString("N"), "license.dat");

    [Fact]
    public void RoundTripsEncrypted()
    {
        var path = TempPath();
        var store = new FileLicenseStore(path, new string('a', 64));
        var value = new StoredLicense { Token = "dhs1.k.p.s", LastVerifiedAt = ClientHarness.T0, LastSeenAt = ClientHarness.T0.AddHours(1) };
        store.Save(value);
        Assert.Equal(value, store.Load());
        Assert.DoesNotContain("dhs1.k.p.s", File.ReadAllText(path));
    }

    [Fact]
    public void FileFromAnotherComputerOrDamagedIsIgnored()
    {
        var path = TempPath();
        new FileLicenseStore(path, new string('a', 64)).Save(new StoredLicense { Token = "t" });
        Assert.Null(new FileLicenseStore(path, new string('b', 64)).Load().Token);
        File.WriteAllBytes(path, [2, 1, 2, 3]);
        Assert.Null(new FileLicenseStore(path, new string('a', 64)).Load().Token);
        Assert.Null(new FileLicenseStore(TempPath(), new string('a', 64)).Load().Token);
    }
}
