using System.Security.Cryptography;
using DownholeStudio.Licensing;

namespace DownholeStudio.Licensing.Tests;

/// <summary>The managed P-256 verifier agrees with the platform's ECDSA implementation and rejects every alteration.</summary>
public sealed class ManagedP256Tests
{
    private static byte[] Sign(ECDsa key, byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    [Fact]
    public void AcceptsSignaturesMadeByThePlatformForManyKeysAndMessages()
    {
        var rng = new Random(2026);
        for (var i = 0; i < 40; i++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pem = key.ExportSubjectPublicKeyInfoPem();
            var data = new byte[rng.Next(0, 700)];
            rng.NextBytes(data);
            Assert.True(ManagedP256.Verify(pem, data, Sign(key, data)), $"case {i}");
        }
    }

    [Fact]
    public void RejectsAlteredDataSignaturesAndWrongKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var data = "dhs1.kid.payload"u8.ToArray();
        var sig = Sign(key, data);
        Assert.False(ManagedP256.Verify(other.ExportSubjectPublicKeyInfoPem(), data, sig));
        Assert.False(ManagedP256.Verify(pem, "dhs1.kid.payloaD"u8, sig));
        for (var i = 0; i < sig.Length; i += 7)
        {
            var bad = sig.ToArray();
            bad[i] ^= 0x01;
            Assert.False(ManagedP256.Verify(pem, data, bad), $"byte {i}");
        }

        Assert.False(ManagedP256.Verify(pem, data, sig[..63]));
        Assert.False(ManagedP256.Verify(pem, data, new byte[64])); // r = s = 0
        var maxed = Enumerable.Repeat((byte)0xFF, 64).ToArray(); // r, s >= n
        Assert.False(ManagedP256.Verify(pem, data, maxed));
    }

    [Fact]
    public void RejectsKeysThatAreNotP256PointsOnTheCurve()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = "probe"u8.ToArray();
        var sig = Sign(key, data);
        var der = key.ExportSubjectPublicKeyInfo();
        der[^1] ^= 0x01; // y no longer on the curve
        Assert.False(ManagedP256.Verify(PemEncoding.WriteString("PUBLIC KEY", der), data, sig));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(ManagedP256.Verify(p384.ExportSubjectPublicKeyInfoPem(), data, sig));
        Assert.False(ManagedP256.Verify("not a key", data, sig));
    }

    [Fact]
    public void VerifiesARealLicenceTokenLikeThePlatform()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claims = OfflineActivationKey.Claims(ComputerCode.FromDeviceHash(new string('c', 64)), "Acme", null, null, null, DateTimeOffset.UtcNow, null, null);
        var token = LicenseTokenCodec.Sign(claims, key, "kid");
        var parts = token.Split('.');
        var input = System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}.{parts[2]}");
        Assert.True(ManagedP256.Verify(key.ExportSubjectPublicKeyInfoPem(), input, Base64Url.Decode(parts[3])));
    }
}
