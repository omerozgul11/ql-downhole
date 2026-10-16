using System.Security.Cryptography;
using DownholeStudio.Licensing;

namespace DownholeStudio.Server.Data;

/// <summary>Licensing configuration (section "Licensing"). Secrets come from environment variables or a secret store.</summary>
public sealed class ServerLicensingOptions
{
    public const string Section = "Licensing";

    /// <summary>Path to the PKCS#8 PEM ECDSA P-256 private key used to sign tokens.</summary>
    public string? SigningKeyPath { get; set; }

    /// <summary>The PEM itself (alternative to the path; env LICENSING_SIGNING_KEY_PEM).</summary>
    public string? SigningKeyPem { get; set; }

    public string SigningKeyId { get; set; } = "dhs-lic-2026a";

    /// <summary>Older public keys (key id → PEM) still accepted when validating client tokens during key rotation.</summary>
    public Dictionary<string, string> PreviousPublicKeys { get; set; } = [];

    /// <summary>Secret pepper for licence-key hashes (env LICENSING_KEY_PEPPER). Changing it invalidates every key.</summary>
    public string? KeyPepper { get; set; }

    public int TrialDays { get; set; } = 14;
    public int TrialOfflineDays { get; set; } = 3;
    public int PaidOfflineDays { get; set; } = 30;
    public int DefaultMaxTransfersPer30Days { get; set; } = 3;
}

/// <summary>Update-manifest signing (section "Updates").</summary>
public sealed class UpdateSigningOptions
{
    public const string Section = "Updates";
    public string? SigningKeyPath { get; set; }
    public string? SigningKeyPem { get; set; }
    public string SigningKeyId { get; set; } = "dhs-upd-2026a";
}

/// <summary>Stripe configuration (section "Stripe").</summary>
public sealed class StripeOptions
{
    public const string Section = "Stripe";

    /// <summary>Webhook signing secret (whsec_…); env Stripe__WebhookSecret.</summary>
    public string? WebhookSecret { get; set; }

    public int ToleranceSeconds { get; set; } = 300;
    public string DefaultProduct { get; set; } = ProductIds.DownholeStudio;
    public string DefaultEdition { get; set; } = "Professional";
    public int MaintenanceMonths { get; set; } = 12;

    /// <summary>Licence term in months for purchases; null = perpetual licence.</summary>
    public int? TermMonths { get; set; }
}

/// <summary>SMTP settings (section "Smtp").</summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public bool StartTls { get; set; } = true;
    public string From { get; set; } = "licensing@quakelogic.net";
    public string FromName { get; set; } = "QuakeLogic Downhole Studio";
}

/// <summary>Text used in transactional e-mails (section "Email").</summary>
public sealed class EmailContentOptions
{
    public const string Section = "Email";
    public string SupportEmail { get; set; } = "support@quakelogic.net";
    public string DownloadUrl { get; set; } = "https://quakelogic.net/downhole-studio";
    public string PrivacyUrl { get; set; } = "https://quakelogic.net/privacy";
    public string CompanyName { get; set; } = "QuakeLogic Inc.";
}

/// <summary>Loads ECDSA keys from PEM text or a file.</summary>
public static class SigningKeyLoader
{
    public static ECDsa Load(string? pem, string? path, string what)
    {
        string? text = pem;
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"The {what} signing key file '{path}' does not exist.");
            }

            try
            {
                text = File.ReadAllText(path);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    $"The {what} signing key file '{path}' cannot be read by this process (user {Environment.UserName}). Grant read access (e.g. chmod 644 inside a 700 directory).", ex);
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"No {what} signing key is configured. Set the PEM or the key path (see LICENSE-SERVER.md).");
        }

        var key = ECDsa.Create();
        key.ImportFromPem(text);
        if (key.KeySize != 256)
        {
            throw new InvalidOperationException($"The {what} signing key must be ECDSA P-256.");
        }

        return key;
    }

    public static string PublicPem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();
}

/// <summary>Signs licence and trial tokens and verifies tokens presented by clients.</summary>
public sealed class TokenIssuer : IDisposable
{
    private readonly ECDsa _key;

    public TokenIssuer(ServerLicensingOptions options)
    {
        _key = SigningKeyLoader.Load(options.SigningKeyPem, options.SigningKeyPath, "licence");
        KeyId = options.SigningKeyId;
        var ring = new Dictionary<string, string>(options.PreviousPublicKeys) { [KeyId] = SigningKeyLoader.PublicPem(_key) };
        Keys = new PublicKeyRing(ring);
    }

    public string KeyId { get; }

    /// <summary>Current and previous public keys.</summary>
    public PublicKeyRing Keys { get; }

    public string Sign(LicenseClaims claims) => LicenseTokenCodec.Sign(claims, _key, KeyId);

    public LicenseClaims? Verify(string token) => LicenseTokenCodec.Verify(token, Keys);

    public void Dispose() => _key.Dispose();
}

/// <summary>Signs update manifests with the update key (separate from the licence key).</summary>
public sealed class UpdateSigner : IDisposable
{
    private readonly ECDsa _key;

    public UpdateSigner(UpdateSigningOptions options)
    {
        _key = SigningKeyLoader.Load(options.SigningKeyPem, options.SigningKeyPath, "update");
        KeyId = options.SigningKeyId;
    }

    public string KeyId { get; }

    public SignedEnvelope Sign(UpdateManifest manifest) => EnvelopeCodec.Sign(manifest, _key, KeyId);

    public string PublicKeyPem => SigningKeyLoader.PublicPem(_key);

    public void Dispose() => _key.Dispose();
}
