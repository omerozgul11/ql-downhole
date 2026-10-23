namespace DownholeStudio.Licensing;

/// <summary>
/// The code a customer sends to QuakeLogic to receive an offline activation key for one computer:
/// 24 Crockford base-32 characters (the first 120 bits of the device hash) plus one check character,
/// written as five groups of five, e.g. <c>7K3M9-QPZX2-AB4HV-N0R5T-W8YCD</c>. Only this derived code
/// leaves the computer; it cannot be turned back into the hardware identifiers.
/// </summary>
public static class ComputerCode
{
    private const string Alphabet = LicenseKeyFormat.Alphabet;
    private const int DataChars = 24;

    public static string FromDeviceHash(string deviceHashHex)
    {
        var bytes = Convert.FromHexString(deviceHashHex[..30]);
        var chars = new char[DataChars + 1];
        var buffer = 0;
        var bits = 0;
        var n = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars[n++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }

        chars[DataChars] = Check(chars.AsSpan(0, DataChars));
        return Format(new string(chars));
    }

    /// <summary>Normalises typed or pasted input (case, spaces, dashes, O/I/L look-alikes); null when it is not a valid code.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var raw = new string(input.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace('O', '0').Replace('I', '1').Replace('L', '1');
        if (raw.Length != DataChars + 1 || raw.Any(c => !Alphabet.Contains(c)) || Check(raw.AsSpan(0, DataChars)) != raw[DataChars])
        {
            return null;
        }

        return Format(raw);
    }

    private static char Check(ReadOnlySpan<char> data)
    {
        var sum = 0;
        for (var i = 0; i < data.Length; i++)
        {
            sum += (i % 7 + 1) * Alphabet.IndexOf(data[i]);
        }

        return Alphabet[sum % 32];
    }

    private static string Format(string raw) => string.Join('-', Enumerable.Range(0, 5).Select(i => raw.Substring(i * 5, 5)));
}

/// <summary>Creates offline activation keys (signed licence tokens bound to one computer code).</summary>
public static class OfflineActivationKey
{
    /// <summary>Validity marker for tokens that never need an online check.</summary>
    public static readonly DateTimeOffset NoOnlineCheck = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public static LicenseClaims Claims(string computerCode, string licensedTo, string? email, string? licenseId, string? keyHint, DateTimeOffset issuedAt,
        DateTimeOffset? expiresAt, DateTimeOffset? maintenanceUntil, string edition = "Professional", int? seats = null, string? activationId = null) => new()
    {
        Type = LicenseClaims.OfflineLicenseType, Product = ProductIds.DownholeStudio, DeviceHash = ComputerCode.Normalize(computerCode) ?? throw new ArgumentException("Not a valid computer code.", nameof(computerCode)),
        LicensedTo = licensedTo, Email = email, LicenseId = licenseId, KeyHint = keyHint, IssuedAt = issuedAt, ExpiresAt = expiresAt, MaintenanceUntil = maintenanceUntil,
        OfflineValidUntil = NoOnlineCheck, Edition = edition, SeatCount = seats, ActivationId = activationId,
    };

    /// <summary>Removes the whitespace and line breaks that e-mail programs add to a pasted key.</summary>
    public static string Clean(string pasted) => new(pasted.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
