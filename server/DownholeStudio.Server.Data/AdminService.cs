using System.Text.Json;
using DownholeStudio.Licensing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DownholeStudio.Server.Data;

/// <summary>Who performed an administrative action and from where.</summary>
public sealed record AdminActor(string Name, string? IpAddress)
{
    public static AdminActor System(string name) => new(name, null);
}

/// <summary>Writes the audit trail of administrative actions.</summary>
public sealed class AuditService(LicensingDbContext db, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Adds an audit entry to the context; saved with the caller's changes.</summary>
    public void Record(AdminActor actor, string action, string? entityType, object? entityId, object? before, object? after)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            Actor = actor.Name, Action = action, EntityType = entityType, EntityId = entityId?.ToString(), IpAddress = actor.IpAddress, CreatedAt = time.GetUtcNow(),
            Before = before is null ? null : JsonSerializer.Serialize(before, Json), After = after is null ? null : JsonSerializer.Serialize(after, Json),
        });
    }
}

/// <summary>Result of creating or reissuing a licence: the raw key is returned exactly once.</summary>
public sealed record IssuedLicense(License License, string LicenseKey);

/// <summary>Administrative operations used by the admin portal and payment webhooks. Every change is audited.</summary>
public sealed class AdminService(
    LicensingDbContext db,
    LicenseKeyHasher hasher,
    AuditService audit,
    EmailOutbox outbox,
    EmailTemplates templates,
    IOptions<ServerLicensingOptions> options,
    TimeProvider time)
{
    private static object Snapshot(License l) => new
    {
        l.Status, l.SeatCount, l.ExpiresAt, l.MaintenanceUntil, l.Edition, l.Product, l.KeyHint, l.MaxTransfersPer30Days, l.Notes,
    };

    public async Task<Customer> UpsertCustomerAsync(AdminActor actor, Guid? id, string email, string? name, string? company, string? notes, CancellationToken ct = default)
    {
        var normalized = LicenseService.NormalizeEmail(email) ?? throw new ArgumentException("An e-mail address is required.", nameof(email));
        var now = time.GetUtcNow();
        var customer = id is { } cid ? await db.Customers.FirstOrDefaultAsync(c => c.Id == cid, ct) : await db.Customers.FirstOrDefaultAsync(c => c.Email == normalized, ct);
        object? before = null;
        if (customer is null)
        {
            customer = new Customer { Id = Guid.NewGuid(), Email = normalized, CreatedAt = now };
            db.Customers.Add(customer);
        }
        else
        {
            before = new { customer.Email, customer.Name, customer.Company, customer.Notes };
            if (customer.Email != normalized && await db.Customers.AnyAsync(c => c.Email == normalized && c.Id != customer.Id, ct))
            {
                throw new InvalidOperationException("Another customer already uses this e-mail address.");
            }
        }

        customer.Email = normalized;
        customer.Name = Blank(name);
        customer.Company = Blank(company);
        customer.Notes = Blank(notes);
        customer.UpdatedAt = now;
        audit.Record(actor, before is null ? "customer.create" : "customer.update", "customer", customer.Id, before, new { customer.Email, customer.Name, customer.Company, customer.Notes });
        await db.SaveChangesAsync(ct);
        return customer;
    }

    /// <summary>Creates a licence with a cryptographically random key, queues the License Key e-mail, and returns the key once.</summary>
    public async Task<IssuedLicense> CreateLicenseAsync(AdminActor actor, Guid customerId, string product, string edition, int seats, DateTimeOffset? expiresAt,
        DateTimeOffset? maintenanceUntil, string? notes, bool sendEmail = true, CancellationToken ct = default)
    {
        if (seats < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(seats), "A license needs at least one seat.");
        }

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct) ?? throw new InvalidOperationException("Customer not found.");
        if (!await db.LicenseProducts.AnyAsync(p => p.Id == product, ct))
        {
            throw new InvalidOperationException($"Unknown product '{product}'.");
        }

        var now = time.GetUtcNow();
        var (key, hash) = await NewKeyAsync(ct);
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKeyHash = hash, KeyHint = LicenseKeyFormat.Hint(key), CustomerId = customer.Id, Product = product, Edition = edition,
            SeatCount = seats, CreatedAt = now, UpdatedAt = now, ExpiresAt = expiresAt, MaintenanceUntil = maintenanceUntil, Notes = Blank(notes),
            MaxTransfersPer30Days = options.Value.DefaultMaxTransfersPer30Days, Status = LicenseStatusCode.Active,
        };
        db.Licenses.Add(license);
        audit.Record(actor, "license.create", "license", license.Id, null, Snapshot(license));
        db.LicenseEvents.Add(new LicenseEvent { EventType = "license.created", LicenseId = license.Id, CreatedAt = now, IpAddress = actor.IpAddress, Details = JsonSerializer.Serialize(new { by = actor.Name }) });
        if (sendEmail)
        {
            outbox.Queue(customer.Email, templates.LicenseKey(customer.Name ?? customer.Email, await ProductNameAsync(product, ct), edition, key, seats, expiresAt, maintenanceUntil), license.Id);
        }

        await db.SaveChangesAsync(ct);
        license.Customer = customer;
        return new IssuedLicense(license, key);
    }

    /// <summary>Generates a new key for a licence (the old key stops working). The new key is returned once and e-mailed.</summary>
    public async Task<IssuedLicense> ReissueKeyAsync(AdminActor actor, Guid licenseId, bool sendEmail = true, CancellationToken ct = default)
    {
        var license = await LoadAsync(licenseId, ct);
        var before = Snapshot(license);
        var (key, hash) = await NewKeyAsync(ct);
        license.LicenseKeyHash = hash;
        license.KeyHint = LicenseKeyFormat.Hint(key);
        license.UpdatedAt = time.GetUtcNow();
        audit.Record(actor, "license.reissue_key", "license", license.Id, before, Snapshot(license));
        if (sendEmail)
        {
            outbox.Queue(license.Customer!.Email, templates.LicenseKey(license.Customer.Name ?? license.Customer.Email, await ProductNameAsync(license.Product, ct),
                license.Edition, key, license.SeatCount, license.ExpiresAt, license.MaintenanceUntil), license.Id);
        }

        await db.SaveChangesAsync(ct);
        return new IssuedLicense(license, key);
    }

    public Task SetStatusAsync(AdminActor actor, Guid licenseId, LicenseStatusCode status, CancellationToken ct = default) =>
        ChangeAsync(actor, licenseId, $"license.{status.ToString().ToLowerInvariant()}", l => l.Status = status, ct);

    public Task SetExpiryAsync(AdminActor actor, Guid licenseId, DateTimeOffset? expiresAt, CancellationToken ct = default) =>
        ChangeAsync(actor, licenseId, "license.expiry", l =>
        {
            l.ExpiresAt = expiresAt;
            l.RenewalReminderSentAt = null;
            if (l.Status == LicenseStatusCode.Expired && (expiresAt is null || expiresAt > time.GetUtcNow()))
            {
                l.Status = LicenseStatusCode.Active;
            }
        }, ct);

    public Task SetMaintenanceAsync(AdminActor actor, Guid licenseId, DateTimeOffset? until, CancellationToken ct = default) =>
        ChangeAsync(actor, licenseId, "license.maintenance", l => l.MaintenanceUntil = until, ct);

    public Task SetSeatsAsync(AdminActor actor, Guid licenseId, int seats, CancellationToken ct = default) =>
        seats < 1 ? throw new ArgumentOutOfRangeException(nameof(seats)) : ChangeAsync(actor, licenseId, "license.seats", l => l.SeatCount = seats, ct);

    public Task SetTransferLimitAsync(AdminActor actor, Guid licenseId, int perThirtyDays, CancellationToken ct = default) =>
        ChangeAsync(actor, licenseId, "license.transfer_limit", l => l.MaxTransfersPer30Days = Math.Max(0, perThirtyDays), ct);

    public Task SetNotesAsync(AdminActor actor, Guid licenseId, string? notes, CancellationToken ct = default) =>
        ChangeAsync(actor, licenseId, "license.notes", l => l.Notes = Blank(notes), ct);

    /// <summary>Deactivates every active seat (admin override: not counted against the transfer limit).</summary>
    public async Task<int> ResetActivationsAsync(AdminActor actor, Guid licenseId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var active = await db.Activations.Where(a => a.LicenseId == licenseId && a.DeactivatedAt == null).ToListAsync(ct);
        foreach (var a in active)
        {
            a.DeactivatedAt = now;
            a.DeactivatedBy = "admin:" + actor.Name;
            a.DeactivationReason = "Activations reset by administrator";
            a.AdminOverride = true;
        }

        audit.Record(actor, "license.reset_activations", "license", licenseId, new { active = active.Count }, new { active = 0 });
        db.LicenseEvents.Add(new LicenseEvent { EventType = "activation.reset_by_admin", LicenseId = licenseId, CreatedAt = now, IpAddress = actor.IpAddress });
        await db.SaveChangesAsync(ct);
        return active.Count;
    }

    /// <summary>Deactivates one activation as an administrator (override).</summary>
    public async Task DeactivateActivationAsync(AdminActor actor, Guid activationId, string? reason, CancellationToken ct = default)
    {
        var a = await db.Activations.FirstOrDefaultAsync(x => x.Id == activationId, ct) ?? throw new InvalidOperationException("Activation not found.");
        if (a.DeactivatedAt is not null)
        {
            return;
        }

        var now = time.GetUtcNow();
        a.DeactivatedAt = now;
        a.DeactivatedBy = "admin:" + actor.Name;
        a.DeactivationReason = Blank(reason) ?? "Deactivated by administrator";
        a.AdminOverride = true;
        audit.Record(actor, "activation.deactivate", "activation", a.Id, new { active = true }, new { active = false, a.DeactivationReason });
        db.LicenseEvents.Add(new LicenseEvent { EventType = "deactivation.admin", LicenseId = a.LicenseId, DeviceId = a.DeviceId, CreatedAt = now, IpAddress = actor.IpAddress });
        await db.SaveChangesAsync(ct);
    }

    public async Task ExtendTrialAsync(AdminActor actor, Guid trialId, int days, CancellationToken ct = default)
    {
        var t = await db.TrialDevices.FirstOrDefaultAsync(x => x.Id == trialId, ct) ?? throw new InvalidOperationException("Trial not found.");
        var before = new { t.TrialEnd };
        var now = time.GetUtcNow();
        t.TrialEnd = (t.TrialEnd > now ? t.TrialEnd : now).AddDays(days);
        t.ReminderSentAt = null;
        audit.Record(actor, "trial.extend", "trial", t.Id, before, new { t.TrialEnd, days });
        db.LicenseEvents.Add(new LicenseEvent { EventType = "trial.extended", TrialDeviceId = t.Id, CreatedAt = now, IpAddress = actor.IpAddress, Details = JsonSerializer.Serialize(new { days }) });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetTrialBlockedAsync(AdminActor actor, Guid trialId, bool blocked, CancellationToken ct = default)
    {
        var t = await db.TrialDevices.FirstOrDefaultAsync(x => x.Id == trialId, ct) ?? throw new InvalidOperationException("Trial not found.");
        audit.Record(actor, blocked ? "trial.block" : "trial.unblock", "trial", t.Id, new { t.Blocked }, new { Blocked = blocked });
        t.Blocked = blocked;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetDeviceBlockedAsync(AdminActor actor, Guid deviceId, bool blocked, string? reason, CancellationToken ct = default)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new InvalidOperationException("Device not found.");
        audit.Record(actor, blocked ? "device.block" : "device.unblock", "device", d.Id, new { d.Blocked, d.BlockedReason }, new { Blocked = blocked, BlockedReason = reason });
        d.Blocked = blocked;
        d.BlockedReason = blocked ? Blank(reason) : null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<SoftwareVersion> CreateReleaseAsync(AdminActor actor, string product, string version, string channel, string notes, string installerUrl,
        string sha256, long size, bool isMajor, string? minimumVersion, bool publish, CancellationToken ct = default)
    {
        if (!UpdateService.TryParseVersion(version, out _))
        {
            throw new ArgumentException("The version must look like 1.2.3 or 1.2.3-beta.1.", nameof(version));
        }

        if (channel is not ("stable" or "beta"))
        {
            throw new ArgumentException("The channel must be 'stable' or 'beta'.", nameof(channel));
        }

        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The SHA-256 must be 64 hexadecimal characters.", nameof(sha256));
        }

        if (!Uri.TryCreate(installerUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The installer URL must be an absolute https:// URL.", nameof(installerUrl));
        }

        var now = time.GetUtcNow();
        var release = new SoftwareVersion
        {
            Id = Guid.NewGuid(), Product = product, Version = version.Trim(), Channel = channel, ReleaseNotes = notes ?? "", InstallerUrl = installerUrl, Sha256 = sha256.ToLowerInvariant(),
            Size = size, IsMajor = isMajor, MinimumVersion = Blank(minimumVersion), Published = publish, PublishedAt = publish ? now : null, CreatedAt = now,
        };
        db.SoftwareVersions.Add(release);
        audit.Record(actor, "release.create", "release", release.Id, null, new { release.Product, release.Version, release.Channel, release.Published, release.Sha256 });
        await db.SaveChangesAsync(ct);
        return release;
    }

    public async Task SetReleasePublishedAsync(AdminActor actor, Guid releaseId, bool published, CancellationToken ct = default)
    {
        var r = await db.SoftwareVersions.FirstOrDefaultAsync(x => x.Id == releaseId, ct) ?? throw new InvalidOperationException("Release not found.");
        audit.Record(actor, published ? "release.publish" : "release.unpublish", "release", r.Id, new { r.Published }, new { Published = published });
        r.Published = published;
        r.PublishedAt = published ? r.PublishedAt ?? time.GetUtcNow() : r.PublishedAt;
        await db.SaveChangesAsync(ct);
    }

    private async Task ChangeAsync(AdminActor actor, Guid licenseId, string action, Action<License> change, CancellationToken ct)
    {
        var license = await LoadAsync(licenseId, ct);
        var before = Snapshot(license);
        change(license);
        license.UpdatedAt = time.GetUtcNow();
        audit.Record(actor, action, "license", license.Id, before, Snapshot(license));
        db.LicenseEvents.Add(new LicenseEvent { EventType = action, LicenseId = license.Id, CreatedAt = license.UpdatedAt, IpAddress = actor.IpAddress, Details = JsonSerializer.Serialize(new { by = actor.Name }) });
        await db.SaveChangesAsync(ct);
    }

    private async Task<License> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Licenses.Include(l => l.Customer).FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new InvalidOperationException("License not found.");

    private async Task<(string Key, string Hash)> NewKeyAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var key = LicenseKeyFormat.Generate();
            var hash = hasher.Hash(key);
            if (!await db.Licenses.AnyAsync(l => l.LicenseKeyHash == hash, ct))
            {
                return (key, hash);
            }
        }

        throw new InvalidOperationException("Could not generate a unique license key.");
    }

    private async Task<string> ProductNameAsync(string product, CancellationToken ct) =>
        await db.LicenseProducts.Where(p => p.Id == product).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? product;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Admin portal accounts: PBKDF2 password hashes (ASP.NET Core Identity format), lockout after repeated failures.</summary>
public sealed class AdminAccountService(LicensingDbContext db, TimeProvider time)
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly PasswordHasher<AdminUser> Hasher = new();

    public async Task<AdminUser> CreateAsync(string email, string password, string? displayName, CancellationToken ct = default)
    {
        var normalized = LicenseService.NormalizeEmail(email) ?? throw new ArgumentException("An e-mail address is required.");
        ValidatePassword(password);
        if (await db.AdminUsers.AnyAsync(a => a.Email == normalized, ct))
        {
            throw new InvalidOperationException($"An administrator with e-mail {normalized} already exists.");
        }

        var user = new AdminUser { Id = Guid.NewGuid(), Email = normalized, DisplayName = displayName, CreatedAt = time.GetUtcNow(), Active = true };
        user.PasswordHash = Hasher.HashPassword(user, password);
        db.AdminUsers.Add(user);
        db.AuditLog.Add(new AuditLogEntry { Actor = "system", Action = "admin.create", EntityType = "admin_user", EntityId = user.Id.ToString(), CreatedAt = user.CreatedAt, After = JsonSerializer.Serialize(new { user.Email }) });
        await db.SaveChangesAsync(ct);
        return user;
    }

    public static void ValidatePassword(string password)
    {
        if (password.Length < 12)
        {
            throw new ArgumentException("Administrator passwords must be at least 12 characters long.");
        }
    }

    /// <summary>Checks credentials, applying lockout. Returns the user on success, otherwise null and a reason.</summary>
    public async Task<(AdminUser? User, string? Error)> SignInAsync(string email, string password, string? ip, CancellationToken ct = default)
    {
        var normalized = LicenseService.NormalizeEmail(email);
        var now = time.GetUtcNow();
        var user = normalized is null ? null : await db.AdminUsers.FirstOrDefaultAsync(a => a.Email == normalized, ct);
        if (user is null || !user.Active)
        {
            db.AuditLog.Add(new AuditLogEntry { Actor = normalized ?? "unknown", Action = "admin.login_failed", IpAddress = ip, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            return (null, "Invalid e-mail address or password.");
        }

        if (user.LockoutUntil is { } until && until > now)
        {
            return (null, $"Too many failed attempts. Try again after {until:HH:mm} UTC.");
        }

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLogins++;
            if (user.FailedLogins >= MaxFailedLogins)
            {
                user.LockoutUntil = now + LockoutDuration;
                user.FailedLogins = 0;
            }

            db.AuditLog.Add(new AuditLogEntry { Actor = user.Email, Action = "admin.login_failed", IpAddress = ip, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            return (null, "Invalid e-mail address or password.");
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = Hasher.HashPassword(user, password);
        }

        user.FailedLogins = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        db.AuditLog.Add(new AuditLogEntry { Actor = user.Email, Action = "admin.login", IpAddress = ip, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return (user, null);
    }
}
