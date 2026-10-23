using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownholeStudio.Licensing;

/// <summary>Base64url without padding (RFC 4648 §5).</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException("Invalid base64url length.") };
        return Convert.FromBase64String(s);
    }
}

/// <summary>Public verification keys known to the client, by key id. Private keys never ship in the application.</summary>
public sealed class PublicKeyRing
{
    private readonly Dictionary<string, string> _pems;

    public PublicKeyRing(IReadOnlyDictionary<string, string> pemByKeyId) => _pems = new Dictionary<string, string>(pemByKeyId, StringComparer.Ordinal);

    public bool TryVerify(string keyId, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (!_pems.TryGetValue(keyId, out var pem))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            // The system's cryptography provider cannot do ECDSA P-256 here (restricted or incomplete provider,
            // compatibility layers): verify the same signature in managed code instead.
            return ManagedP256.Verify(pem, data, signature);
        }
    }

    public IEnumerable<string> KeyIds => _pems.Keys;
}

/// <summary>
/// Compact signed tokens: "dhs1.&lt;kid&gt;.&lt;base64url(claims json)&gt;.&lt;base64url(signature)&gt;".
/// The signature is ECDSA P-256 / SHA-256 (IEEE P1363 r‖s) over the ASCII text before the last dot.
/// </summary>
public static class LicenseTokenCodec
{
    public const string Prefix = "dhs1";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Signs claims (server side only — requires the private key).</summary>
    public static string Sign(LicenseClaims claims, ECDsa privateKey, string keyId)
    {
        var payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(claims, Json));
        var signingInput = $"{Prefix}.{keyId}.{payload}";
        var sig = privateKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url.Encode(sig);
    }

    /// <summary>Verifies the signature and returns the claims, or null when the token is not authentic.</summary>
    public static LicenseClaims? Verify(string token, PublicKeyRing keys)
    {
        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != Prefix)
        {
            return null;
        }

        try
        {
            var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}.{parts[2]}");
            if (!keys.TryVerify(parts[1], signingInput, Base64Url.Decode(parts[3])))
            {
                return null;
            }

            return JsonSerializer.Deserialize<LicenseClaims>(Base64Url.Decode(parts[2]), Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Reads claims without verifying (for diagnostics only; never for authorisation).</summary>
    public static LicenseClaims? ReadUnverified(string token)
    {
        var parts = token.Split('.');
        try
        {
            return parts.Length == 4 ? JsonSerializer.Deserialize<LicenseClaims>(Base64Url.Decode(parts[2]), Json) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>Signs and verifies arbitrary JSON documents (update manifests).</summary>
public static class EnvelopeCodec
{
    public static SignedEnvelope Sign<T>(T value, ECDsa privateKey, string keyId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var sig = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedEnvelope(keyId, Base64Url.Encode(payload), Base64Url.Encode(sig));
    }

    public static T? Verify<T>(SignedEnvelope envelope, PublicKeyRing keys)
    {
        try
        {
            var payload = Base64Url.Decode(envelope.Payload);
            return keys.TryVerify(envelope.KeyId, payload, Base64Url.Decode(envelope.Signature)) ? JsonSerializer.Deserialize<T>(payload) : default;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return default;
        }
    }
}

/// <summary>
/// Licence keys "DHST-XXXX-XXXX-XXXX-XXXX": 16 characters from Crockford's base-32 alphabet
/// (no I, L, O, U), i.e. 80 bits from a cryptographic random generator.
/// </summary>
public static class LicenseKeyFormat
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const string Prefix = "DHST";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[16];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var s = new string(chars);
        return $"{Prefix}-{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..]}";
    }

    /// <summary>Normalises user input (case, spaces, Crockford look-alikes) or returns null when it cannot be a key.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var raw = new string(input.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (raw.StartsWith(Prefix, StringComparison.Ordinal))
        {
            raw = raw[Prefix.Length..];
        }

        raw = raw.Replace('O', '0').Replace('I', '1').Replace('L', '1');
        if (raw.Length != 16 || raw.Any(c => !Alphabet.Contains(c)))
        {
            return null;
        }

        return $"{Prefix}-{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..]}";
    }

    /// <summary>SHA-256 of the normalised key with a server pepper (hex). Keys are stored only as hashes.</summary>
    public static string Hash(string normalizedKey, string pepper) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(pepper), Encoding.UTF8.GetBytes(normalizedKey)));

    public static string Hint(string normalizedKey) => normalizedKey[^4..];

    public static string Masked(string? hint) => hint is null ? "—" : $"{Prefix}-XXXX-XXXX-XXXX-{hint}";
}
