using System.Text.Json.Serialization;

namespace DownholeStudio.Licensing;

/// <summary>Device identity sent to the licensing server. Only salted hashes leave the computer.</summary>
public sealed record DeviceFingerprint
{
    /// <summary>SHA-256 (hex) of the normalised Windows MachineGuid, SMBIOS system UUID and the product salt.</summary>
    public required string DeviceHash { get; init; }
    /// <summary>SHA-256 (hex) of hardware identifiers only (survives OS reinstall); used to prevent trial resets. May be null.</summary>
    public string? HardwareHash { get; init; }
}

public sealed record TrialStartRequest
{
    public required string Product { get; init; }
    public required DeviceFingerprint Fingerprint { get; init; }
    public required string AppVersion { get; init; }
    public string? OsVersion { get; init; }
    /// <summary>Sent only when the user allowed it.</summary>
    public string? MachineName { get; init; }
    public string? Email { get; init; }
}

public sealed record ActivationRequest
{
    public required string LicenseKey { get; init; }
    public required string Email { get; init; }
    public required string Product { get; init; }
    public required DeviceFingerprint Fingerprint { get; init; }
    public required string AppVersion { get; init; }
    public string? OsVersion { get; init; }
    public string? MachineName { get; init; }
}

public sealed record ValidationRequest
{
    public required string Token { get; init; }
    public required DeviceFingerprint Fingerprint { get; init; }
    public required string AppVersion { get; init; }
}

public sealed record DeactivationRequest
{
    public required string Token { get; init; }
    public required DeviceFingerprint Fingerprint { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Machine-readable outcome codes returned by the server.</summary>
public static class LicenseErrorCodes
{
    public const string InvalidKey = "invalid_key";
    public const string EmailMismatch = "email_mismatch";
    public const string Revoked = "license_revoked";
    public const string Suspended = "license_suspended";
    public const string Expired = "license_expired";
    public const string ActivationLimit = "activation_limit";
    public const string TooManyTransfers = "too_many_transfers";
    public const string TrialExpired = "trial_expired";
    public const string TrialUnavailable = "trial_unavailable";
    public const string DeviceBlocked = "device_blocked";
    public const string Deactivated = "device_deactivated";
    public const string InvalidToken = "invalid_token";
    public const string RateLimited = "rate_limited";
    public const string WrongProduct = "wrong_product";
    public const string ServerError = "server_error";
}

/// <summary>Server response to every licensing call.</summary>
public sealed record LicenseResponse
{
    public bool Success { get; init; }
    /// <summary>Signed token (see <see cref="LicenseTokenCodec"/>) when successful.</summary>
    public string? Token { get; init; }
    public string? ErrorCode { get; init; }
    /// <summary>Plain-language message suitable for display.</summary>
    public string? Message { get; init; }
    public DateTimeOffset ServerTime { get; init; }
    /// <summary>For activation_limit: the devices currently using the seats (names only if the users allowed them).</summary>
    public IReadOnlyList<ActivatedDeviceInfo>? Devices { get; init; }
}

public sealed record ActivatedDeviceInfo(string DeviceLabel, DateTimeOffset ActivatedAt, DateTimeOffset? LastSeenAt);

/// <summary>Claims inside a signed licence or trial token.</summary>
public sealed record LicenseClaims
{
    [JsonPropertyName("typ")] public required string Type { get; init; }
    [JsonPropertyName("lid")] public string? LicenseId { get; init; }
    [JsonPropertyName("prd")] public required string Product { get; init; }
    [JsonPropertyName("ed")] public string Edition { get; init; } = "Professional";
    [JsonPropertyName("dev")] public required string DeviceHash { get; init; }
    [JsonPropertyName("to")] public string? LicensedTo { get; init; }
    [JsonPropertyName("em")] public string? Email { get; init; }
    /// <summary>Last four characters of the licence key, for display.</summary>
    [JsonPropertyName("kh")] public string? KeyHint { get; init; }
    [JsonPropertyName("iat")] public required DateTimeOffset IssuedAt { get; init; }
    /// <summary>End of the entitlement (subscription end or trial end); null = perpetual.</summary>
    [JsonPropertyName("exp")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("mnt")] public DateTimeOffset? MaintenanceUntil { get; init; }
    /// <summary>The token must be refreshed online before this time.</summary>
    [JsonPropertyName("ovu")] public required DateTimeOffset OfflineValidUntil { get; init; }
    [JsonPropertyName("ts")] public DateTimeOffset? TrialStart { get; init; }
    [JsonPropertyName("te")] public DateTimeOffset? TrialEnd { get; init; }
    [JsonPropertyName("seats")] public int? SeatCount { get; init; }
    [JsonPropertyName("feat")] public IReadOnlyList<string> Features { get; init; } = [];
    [JsonPropertyName("act")] public string? ActivationId { get; init; }

    public const string TrialType = "trial";
    public const string LicenseType = "license";

    [JsonIgnore]
    public bool IsTrial => Type == TrialType;
}

/// <summary>Release information for the updater, signed with the update key.</summary>
public sealed record UpdateManifest
{
    public required string Product { get; init; }
    public required string Channel { get; init; }
    public required string Version { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public required string InstallerUrl { get; init; }
    /// <summary>Lower-case hex SHA-256 of the installer.</summary>
    public required string Sha256 { get; init; }
    public long Size { get; init; }
    public bool IsMajor { get; init; }
    public string? MinimumVersion { get; init; }
}

/// <summary>A JSON payload with a detached signature: base64url(payload) and base64url(signature).</summary>
public sealed record SignedEnvelope(string KeyId, string Payload, string Signature);

public static class ProductIds
{
    public const string DownholeStudio = "downhole-studio";
}
