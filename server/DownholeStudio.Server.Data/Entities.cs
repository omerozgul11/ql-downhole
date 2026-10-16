namespace DownholeStudio.Server.Data;

/// <summary>Licence status as stored in the database.</summary>
public enum LicenseStatusCode
{
    Active,
    Suspended,
    Revoked,
    Expired,
}

/// <summary>A purchasing customer (person or company contact).</summary>
public sealed class Customer
{
    public Guid Id { get; set; }

    /// <summary>Lower-case e-mail address; unique.</summary>
    public required string Email { get; set; }

    public string? Name { get; set; }
    public string? Company { get; set; }
    public string? Notes { get; set; }

    /// <summary>Payment-provider customer reference (no card data).</summary>
    public string? ExternalCustomerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<License> Licenses { get; set; } = [];

    /// <summary>Name shown in licence tokens ("Licensed to").</summary>
    public string DisplayName => Company ?? Name ?? Email;
}

/// <summary>A sellable product (e.g. downhole-studio).</summary>
public sealed class LicenseProduct
{
    /// <summary>Product code used in tokens, e.g. "downhole-studio".</summary>
    public required string Id { get; set; }

    public required string Name { get; set; }
    public string DefaultEdition { get; set; } = "Professional";
    public bool Active { get; set; } = true;
    public int TrialDays { get; set; } = 14;
}

/// <summary>An entitlement. The raw key is never stored, only its keyed hash and last four characters.</summary>
public sealed class License
{
    public Guid Id { get; set; }

    /// <summary>HMAC-SHA256 (hex) of the normalised key with the server pepper.</summary>
    public required string LicenseKeyHash { get; set; }

    public required string KeyHint { get; set; }
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public required string Product { get; set; }
    public string Edition { get; set; } = "Professional";
    public LicenseStatusCode Status { get; set; } = LicenseStatusCode.Active;
    public int SeatCount { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>End of the entitlement; null = perpetual.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? MaintenanceUntil { get; set; }
    public string? Notes { get; set; }

    /// <summary>Self-service deactivations allowed per rolling 30 days.</summary>
    public int MaxTransfersPer30Days { get; set; } = 3;

    public List<string> Features { get; set; } = [];
    public DateTimeOffset? RenewalReminderSentAt { get; set; }

    public List<Activation> Activations { get; set; } = [];
}

/// <summary>A computer identified by its salted fingerprint hash.</summary>
public sealed class Device
{
    public Guid Id { get; set; }
    public required string DeviceHash { get; set; }
    public string? HardwareHash { get; set; }

    /// <summary>Stored only when the user allowed it to be sent.</summary>
    public string? MachineName { get; set; }

    public string? OsVersion { get; set; }
    public string? AppVersion { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool Blocked { get; set; }
    public string? BlockedReason { get; set; }

    public List<Activation> Activations { get; set; } = [];

    /// <summary>Label safe to show to customers and admins.</summary>
    public string Label => MachineName ?? $"Computer {DeviceHash[..Math.Min(8, DeviceHash.Length)]}";
}

/// <summary>One licence seat used by one device. Inactive when <see cref="DeactivatedAt"/> is set.</summary>
public sealed class Activation
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License? License { get; set; }
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastValidatedAt { get; set; }
    public string? AppVersion { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? DeactivationReason { get; set; }

    /// <summary>"user" for self-service, "admin:&lt;email&gt;" for administrators.</summary>
    public string? DeactivatedBy { get; set; }

    /// <summary>Deactivated by an administrator; not counted against the transfer limit.</summary>
    public bool AdminOverride { get; set; }

    public bool IsActive => DeactivatedAt is null;
}

/// <summary>The trial record of one device (and its hardware) for one product.</summary>
public sealed class TrialDevice
{
    public Guid Id { get; set; }
    public required string Product { get; set; }
    public required string DeviceHash { get; set; }
    public string? HardwareHash { get; set; }
    public string? Email { get; set; }
    public string? MachineName { get; set; }
    public string? AppVersion { get; set; }
    public DateTimeOffset TrialStart { get; set; }
    public DateTimeOffset TrialEnd { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool Blocked { get; set; }
    public DateTimeOffset? ReminderSentAt { get; set; }
}

/// <summary>Append-only licensing event (activation, validation failure, deactivation…).</summary>
public sealed class LicenseEvent
{
    public long Id { get; set; }
    public Guid? LicenseId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? TrialDeviceId { get; set; }
    public required string EventType { get; set; }

    /// <summary>JSON details (never contains keys or tokens).</summary>
    public string? Details { get; set; }

    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A released build available to the updater.</summary>
public sealed class SoftwareVersion
{
    public Guid Id { get; set; }
    public required string Product { get; set; }
    public required string Version { get; set; }

    /// <summary>"stable" or "beta".</summary>
    public string Channel { get; set; } = "stable";

    public string ReleaseNotes { get; set; } = "";
    public required string InstallerUrl { get; set; }
    public required string Sha256 { get; set; }
    public long Size { get; set; }
    public bool IsMajor { get; set; }
    public string? MinimumVersion { get; set; }
    public bool Published { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A payment confirmation. Holds provider references only — never card data.</summary>
public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? LicenseId { get; set; }
    public required string Provider { get; set; }
    public string? ProviderPaymentId { get; set; }
    public string? ProviderSessionId { get; set; }

    /// <summary>Amount in the currency's minor unit (e.g. cents).</summary>
    public long Amount { get; set; }

    public string Currency { get; set; } = "usd";
    public string Status { get; set; } = "paid";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Administrator action audit record.</summary>
public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Administrator account for the admin portal.</summary>
public sealed class AdminUser
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string PasswordHash { get; set; } = "";
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Transactional e-mail waiting to be (or already) sent.</summary>
public sealed class EmailMessage
{
    public Guid Id { get; set; }
    public required string ToAddress { get; set; }
    public required string Subject { get; set; }
    public required string TextBody { get; set; }
    public required string HtmlBody { get; set; }
    public required string Template { get; set; }
    public Guid? LicenseId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Processed payment-provider webhook (idempotency).</summary>
public sealed class WebhookEvent
{
    /// <summary>Provider event id, e.g. evt_….</summary>
    public required string Id { get; set; }

    public required string Provider { get; set; }
    public required string Type { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Result { get; set; }
}
