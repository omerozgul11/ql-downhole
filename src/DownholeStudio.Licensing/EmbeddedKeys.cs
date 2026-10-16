namespace DownholeStudio.Licensing;

/// <summary>
/// Public verification keys shipped with the application. The matching private keys live only on
/// the licensing server (see LICENSE-SERVER.md). To rotate, add a new key id here, ship the
/// application, then switch the server to the new key; keep the old id until old tokens expire.
/// </summary>
public static class EmbeddedKeys
{
    public const string LicenseKeyId = "dhs-lic-2026a";
    public const string UpdateKeyId = "dhs-upd-2026a";

    public const string LicensePublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgvyacg+Bb+gyj6KWqlTm88IbYfqT
        Rhn+L2NeVdk3HMWM/J//Y7Rvdwvog3kNT6d8uJs/xYEOaOtLMIwrHc+UvA==
        -----END PUBLIC KEY-----
        """;

    public const string UpdatePublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE3ujKSAuAwgaU5NlCDF4ovtPzQTAT
        IPPyV03mR5iJJt7AAg1sx5F/eBt8NhnGCsj2ZpW0XRJKBnQgLwMWT051kQ==
        -----END PUBLIC KEY-----
        """;

    public static PublicKeyRing LicenseKeys { get; } = new(new Dictionary<string, string> { [LicenseKeyId] = LicensePublicKey });

    public static PublicKeyRing UpdateKeys { get; } = new(new Dictionary<string, string> { [UpdateKeyId] = UpdatePublicKey });

    /// <summary>Product salt for device fingerprints (not secret; makes hashes product-specific).</summary>
    public const string FingerprintSalt = "QuakeLogic/DownholeStudio/2026";
}
