using System.Text.Json;
using DownholeStudio.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownholeStudio.Server.Data;

/// <summary>
/// Authoritative licensing decisions for desktop clients: trials, activation, validation and
/// self-service deactivation. Every outcome is written to license_events.
/// </summary>
public sealed class LicenseService(
    LicensingDbContext db,
    TokenIssuer issuer,
    LicenseKeyHasher hasher,
    IOptions<ServerLicensingOptions> options,
    EmailOutbox outbox,
    EmailTemplates templates,
    TimeProvider time,
    ILogger<LicenseService> log)
{
    private ServerLicensingOptions O => options.Value;

    // ------------------------------------------------------------------ trials

    public async Task<LicenseResponse> StartTrialAsync(TrialStartRequest request, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (!ValidFingerprint(request.Fingerprint))
        {
            return Fail(LicenseErrorCodes.InvalidToken, "The computer identification sent by the application is not valid.", now);
        }

        var product = await db.LicenseProducts.FirstOrDefaultAsync(p => p.Id == request.Product && p.Active, ct);
        if (product is null)
        {
            return Fail(LicenseErrorCodes.WrongProduct, "This product is not available for trials.", now);
        }

        var device = await UpsertDeviceAsync(request.Fingerprint, request.MachineName, request.OsVersion, request.AppVersion, now, ct);
        if (device.Blocked)
        {
            await EventAsync("trial.blocked_device", null, device.Id, null, new { request.AppVersion }, ip, now, ct);
            return Fail(LicenseErrorCodes.DeviceBlocked, "Trials are not available on this computer. Contact QuakeLogic support.", now);
        }

        var trial = await FindTrialAsync(request.Product, request.Fingerprint, ct);
        if (trial is null)
        {
            var days = product.TrialDays > 0 ? product.TrialDays : O.TrialDays;
            trial = new TrialDevice
            {
                Id = Guid.NewGuid(), Product = request.Product, DeviceHash = request.Fingerprint.DeviceHash, HardwareHash = request.Fingerprint.HardwareHash,
                Email = NormalizeEmail(request.Email), MachineName = request.MachineName, AppVersion = request.AppVersion,
                TrialStart = now, TrialEnd = now.AddDays(days), LastSeenAt = now,
            };
            db.TrialDevices.Add(trial);
            await db.SaveChangesAsync(ct);
            await EventAsync("trial.started", null, device.Id, trial.Id, new { trial.TrialEnd, request.AppVersion }, ip, now, ct);
            log.LogInformation("Trial started for device {Device} until {End}", Short(device.DeviceHash), trial.TrialEnd);
        }
        else
        {
            if (trial.DeviceHash != request.Fingerprint.DeviceHash)
            {
                await EventAsync("trial.matched_by_hardware", null, device.Id, trial.Id, new { previous = Short(trial.DeviceHash) }, ip, now, ct);
            }

            trial.LastSeenAt = now;
            trial.AppVersion = request.AppVersion;
            trial.Email ??= NormalizeEmail(request.Email);
            await db.SaveChangesAsync(ct);
        }

        return await TrialTokenAsync(trial, request.Fingerprint.DeviceHash, ip, now, ct);
    }

    private async Task<LicenseResponse> TrialTokenAsync(TrialDevice trial, string deviceHash, string? ip, DateTimeOffset now, CancellationToken ct)
    {
        if (trial.Blocked)
        {
            return Fail(LicenseErrorCodes.DeviceBlocked, "Trials are not available on this computer. Contact QuakeLogic support.", now);
        }

        if (now >= trial.TrialEnd)
        {
            await EventAsync("trial.expired_request", null, null, trial.Id, null, ip, now, ct);
            return Fail(LicenseErrorCodes.TrialExpired, $"The trial on this computer ended on {trial.TrialEnd:MMMM d, yyyy}. Activate a license to continue.", now);
        }

        var offline = now.AddDays(O.TrialOfflineDays);
        var claims = new LicenseClaims
        {
            Type = LicenseClaims.TrialType, Product = trial.Product, Edition = "Professional", DeviceHash = deviceHash, Email = trial.Email,
            IssuedAt = now, ExpiresAt = trial.TrialEnd, TrialStart = trial.TrialStart, TrialEnd = trial.TrialEnd,
            OfflineValidUntil = offline < trial.TrialEnd ? offline : trial.TrialEnd, Features = ["all"],
        };
        var days = Math.Max(0, (int)Math.Ceiling((trial.TrialEnd - now).TotalDays));
        return new LicenseResponse { Success = true, Token = issuer.Sign(claims), ServerTime = now, Message = $"Trial active: {days} day{(days == 1 ? "" : "s")} remaining." };
    }

    private async Task<TrialDevice?> FindTrialAsync(string product, DeviceFingerprint fp, CancellationToken ct)
    {
        var byDevice = await db.TrialDevices.FirstOrDefaultAsync(t => t.Product == product && t.DeviceHash == fp.DeviceHash, ct);
        if (byDevice is not null || string.IsNullOrEmpty(fp.HardwareHash))
        {
            return byDevice;
        }

        // Same hardware with a new operating-system identity (e.g. Windows reinstalled): same trial.
        return await db.TrialDevices.Where(t => t.Product == product && t.HardwareHash == fp.HardwareHash).OrderBy(t => t.TrialStart).FirstOrDefaultAsync(ct);
    }

    // ------------------------------------------------------------------ activation

    public async Task<LicenseResponse> ActivateAsync(ActivationRequest request, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (!ValidFingerprint(request.Fingerprint))
        {
            return Fail(LicenseErrorCodes.InvalidToken, "The computer identification sent by the application is not valid.", now);
        }

        var key = LicenseKeyFormat.Normalize(request.LicenseKey);
        var license = key is null ? null : await db.Licenses.Include(l => l.Customer).FirstOrDefaultAsync(l => l.LicenseKeyHash == HashKey(key), ct);
        if (license is null)
        {
            await EventAsync("activation.invalid_key", null, null, null, new { hint = key is null ? null : LicenseKeyFormat.Hint(key) }, ip, now, ct);
            return Fail(LicenseErrorCodes.InvalidKey, "This license key is not recognised. Check the key in your license e-mail.", now);
        }

        if (!string.Equals(license.Customer!.Email, NormalizeEmail(request.Email), StringComparison.Ordinal))
        {
            await EventAsync("activation.email_mismatch", license.Id, null, null, null, ip, now, ct);
            return Fail(LicenseErrorCodes.EmailMismatch, "The e-mail address does not match this license. Use the address the license was issued to.", now);
        }

        if (license.Product != request.Product)
        {
            return Fail(LicenseErrorCodes.WrongProduct, "This license key is for a different product.", now);
        }

        var statusError = StatusError(license, now);
        if (statusError is not null)
        {
            await EventAsync("activation.refused", license.Id, null, null, new { statusError.ErrorCode }, ip, now, ct);
            return statusError;
        }

        var device = await UpsertDeviceAsync(request.Fingerprint, request.MachineName, request.OsVersion, request.AppVersion, now, ct);
        if (device.Blocked)
        {
            return Fail(LicenseErrorCodes.DeviceBlocked, "This computer cannot be activated. Contact QuakeLogic support.", now);
        }

        var active = await db.Activations.Include(a => a.Device).Where(a => a.LicenseId == license.Id && a.DeactivatedAt == null).ToListAsync(ct);
        var existing = active.FirstOrDefault(a => a.DeviceId == device.Id);
        if (existing is null)
        {
            if (active.Count >= license.SeatCount)
            {
                await EventAsync("activation.limit", license.Id, device.Id, null, new { seats = license.SeatCount }, ip, now, ct);
                return new LicenseResponse
                {
                    Success = false, ErrorCode = LicenseErrorCodes.ActivationLimit, ServerTime = now,
                    Message = $"This license is already active on {active.Count} computer{(active.Count == 1 ? "" : "s")} (limit {license.SeatCount}). Deactivate it on another computer first (Settings → License → Deactivate Computer), or contact QuakeLogic support.",
                    Devices = active.Select(a => new ActivatedDeviceInfo(a.Device!.Label, a.ActivatedAt, a.LastValidatedAt)).ToList(),
                };
            }

            existing = new Activation { Id = Guid.NewGuid(), LicenseId = license.Id, DeviceId = device.Id, ActivatedAt = now, LastValidatedAt = now, AppVersion = request.AppVersion };
            db.Activations.Add(existing);
            await db.SaveChangesAsync(ct);
            await EventAsync("activation.created", license.Id, device.Id, null, new { request.AppVersion, request.OsVersion }, ip, now, ct);
        }
        else
        {
            existing.LastValidatedAt = now;
            existing.AppVersion = request.AppVersion;
            await db.SaveChangesAsync(ct);
            await EventAsync("activation.repeated", license.Id, device.Id, null, null, ip, now, ct);
        }

        return LicenseToken(license, existing, request.Fingerprint.DeviceHash, now, "This computer is activated.");
    }

    // ------------------------------------------------------------------ validation

    public async Task<LicenseResponse> ValidateAsync(ValidationRequest request, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var claims = issuer.Verify(request.Token);
        if (claims is null || !ValidFingerprint(request.Fingerprint) || claims.DeviceHash != request.Fingerprint.DeviceHash)
        {
            await EventAsync("validation.invalid_token", null, null, null, null, ip, now, ct);
            return Fail(LicenseErrorCodes.InvalidToken, "The saved license information is not valid for this computer. Activate again.", now);
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceHash == claims.DeviceHash, ct);
        if (device is not null)
        {
            device.LastSeenAt = now;
            device.AppVersion = request.AppVersion;
        }

        if (claims.IsTrial)
        {
            var trial = await FindTrialAsync(claims.Product, request.Fingerprint, ct);
            if (trial is null)
            {
                return Fail(LicenseErrorCodes.InvalidToken, "No trial is registered for this computer.", now);
            }

            trial.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
            return await TrialTokenAsync(trial, request.Fingerprint.DeviceHash, ip, now, ct);
        }

        if (!Guid.TryParse(claims.LicenseId, out var licenseId))
        {
            return Fail(LicenseErrorCodes.InvalidToken, "The saved license information is not valid. Activate again.", now);
        }

        var license = await db.Licenses.Include(l => l.Customer).FirstOrDefaultAsync(l => l.Id == licenseId, ct);
        if (license is null)
        {
            return Fail(LicenseErrorCodes.InvalidToken, "This license no longer exists. Contact QuakeLogic support.", now);
        }

        var statusError = StatusError(license, now);
        if (statusError is not null)
        {
            await EventAsync("validation.refused", license.Id, device?.Id, null, new { statusError.ErrorCode }, ip, now, ct);
            await db.SaveChangesAsync(ct);
            return statusError;
        }

        var activation = device is null ? null : await db.Activations.FirstOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == device.Id && a.DeactivatedAt == null, ct);
        if (activation is null)
        {
            await EventAsync("validation.deactivated", license.Id, device?.Id, null, null, ip, now, ct);
            await db.SaveChangesAsync(ct);
            return Fail(LicenseErrorCodes.Deactivated, "This computer is no longer activated for this license. Activate it again if a seat is available.", now);
        }

        activation.LastValidatedAt = now;
        activation.AppVersion = request.AppVersion;
        await db.SaveChangesAsync(ct);
        await EventAsync("validation.ok", license.Id, device!.Id, null, new { request.AppVersion }, ip, now, ct);
        return LicenseToken(license, activation, claims.DeviceHash, now, "License verified.");
    }

    // ------------------------------------------------------------------ deactivation

    public async Task<LicenseResponse> DeactivateAsync(DeactivationRequest request, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var claims = issuer.Verify(request.Token);
        if (claims is null || claims.DeviceHash != request.Fingerprint.DeviceHash)
        {
            return Fail(LicenseErrorCodes.InvalidToken, "The saved license information is not valid for this computer.", now);
        }

        if (claims.IsTrial)
        {
            // Nothing is released: the trial stays registered to this hardware.
            return new LicenseResponse { Success = true, ServerTime = now, Message = "The trial information was removed from this computer." };
        }

        if (!Guid.TryParse(claims.LicenseId, out var licenseId))
        {
            return Fail(LicenseErrorCodes.InvalidToken, "The saved license information is not valid.", now);
        }

        var license = await db.Licenses.Include(l => l.Customer).FirstOrDefaultAsync(l => l.Id == licenseId, ct);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceHash == claims.DeviceHash, ct);
        var activation = license is null || device is null ? null
            : await db.Activations.FirstOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == device.Id && a.DeactivatedAt == null, ct);
        if (license is null || activation is null)
        {
            return Fail(LicenseErrorCodes.Deactivated, "This computer is not activated.", now);
        }

        var since = now.AddDays(-30);
        var recent = await db.Activations.CountAsync(a => a.LicenseId == license.Id && a.DeactivatedAt != null && a.DeactivatedAt >= since && !a.AdminOverride && a.DeactivatedBy == "user", ct);
        if (recent >= license.MaxTransfersPer30Days)
        {
            await EventAsync("deactivation.limit", license.Id, device!.Id, null, new { recent, limit = license.MaxTransfersPer30Days }, ip, now, ct);
            return Fail(LicenseErrorCodes.TooManyTransfers,
                $"This license has been moved between computers {recent} times in the last 30 days, which is the self-service limit. Contact QuakeLogic support to move it again.", now);
        }

        activation.DeactivatedAt = now;
        activation.DeactivatedBy = "user";
        activation.DeactivationReason = string.IsNullOrWhiteSpace(request.Reason) ? "user" : request.Reason[..Math.Min(100, request.Reason.Length)];
        outbox.Queue(license.Customer!.Email, templates.DeviceDeactivated(license.Customer.Name ?? license.Customer.Email, ProductName(license.Product), device!.Label, now), license.Id);
        await db.SaveChangesAsync(ct);
        await EventAsync("deactivation.user", license.Id, device.Id, null, null, ip, now, ct);
        return new LicenseResponse { Success = true, ServerTime = now, Message = "This computer has been deactivated. The license can now be activated on another computer." };
    }

    // ------------------------------------------------------------------ helpers

    private string HashKey(string normalizedKey) => hasher.Hash(normalizedKey);

    private LicenseResponse LicenseToken(License license, Activation activation, string deviceHash, DateTimeOffset now, string message)
    {
        var claims = new LicenseClaims
        {
            Type = LicenseClaims.LicenseType, LicenseId = license.Id.ToString(), Product = license.Product, Edition = license.Edition, DeviceHash = deviceHash,
            LicensedTo = license.Customer?.DisplayName, Email = license.Customer?.Email, KeyHint = license.KeyHint, IssuedAt = now,
            ExpiresAt = license.ExpiresAt, MaintenanceUntil = license.MaintenanceUntil, OfflineValidUntil = now.AddDays(O.PaidOfflineDays),
            SeatCount = license.SeatCount, Features = license.Features.Count > 0 ? license.Features : ["all"], ActivationId = activation.Id.ToString(),
        };
        return new LicenseResponse { Success = true, Token = issuer.Sign(claims), ServerTime = now, Message = message };
    }

    private static LicenseResponse? StatusError(License license, DateTimeOffset now) => license.Status switch
    {
        LicenseStatusCode.Revoked => Fail(LicenseErrorCodes.Revoked, "This license has been revoked.", now),
        LicenseStatusCode.Suspended => Fail(LicenseErrorCodes.Suspended, "This license is suspended. Contact QuakeLogic support.", now),
        LicenseStatusCode.Expired => Fail(LicenseErrorCodes.Expired, "This license has expired. Renew it to continue editing.", now),
        _ when license.ExpiresAt is { } e && e <= now => Fail(LicenseErrorCodes.Expired, $"This license expired on {e:MMMM d, yyyy}. Renew it to continue editing.", now),
        _ => null,
    };

    private static LicenseResponse Fail(string code, string message, DateTimeOffset now) => new() { Success = false, ErrorCode = code, Message = message, ServerTime = now };

    private async Task<Device> UpsertDeviceAsync(DeviceFingerprint fp, string? machineName, string? os, string? appVersion, DateTimeOffset now, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceHash == fp.DeviceHash, ct);
        if (device is null)
        {
            device = new Device { Id = Guid.NewGuid(), DeviceHash = fp.DeviceHash, FirstSeenAt = now };
            db.Devices.Add(device);
        }

        device.HardwareHash = fp.HardwareHash ?? device.HardwareHash;
        device.MachineName = string.IsNullOrWhiteSpace(machineName) ? device.MachineName : Trim(machineName, 128);
        device.OsVersion = os is null ? device.OsVersion : Trim(os, 200);
        device.AppVersion = appVersion is null ? device.AppVersion : Trim(appVersion, 50);
        device.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        return device;
    }

    private async Task EventAsync(string type, Guid? licenseId, Guid? deviceId, Guid? trialId, object? details, string? ip, DateTimeOffset now, CancellationToken ct)
    {
        db.LicenseEvents.Add(new LicenseEvent
        {
            EventType = type, LicenseId = licenseId, DeviceId = deviceId, TrialDeviceId = trialId, IpAddress = ip, CreatedAt = now,
            Details = details is null ? null : JsonSerializer.Serialize(details),
        });
        await db.SaveChangesAsync(ct);
    }

    private string ProductName(string product) => db.LicenseProducts.Where(p => p.Id == product).Select(p => p.Name).FirstOrDefault() ?? product;

    private static bool ValidFingerprint(DeviceFingerprint? fp) =>
        fp is not null && IsHash(fp.DeviceHash) && (fp.HardwareHash is null || IsHash(fp.HardwareHash));

    private static bool IsHash(string? s) => s is { Length: 64 } && s.All(char.IsAsciiHexDigit);

    public static string? NormalizeEmail(string? email) => string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static string Short(string hash) => hash[..Math.Min(8, hash.Length)];

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Keyed hash of licence keys (HMAC-SHA256 with the server pepper). Needs no signing key.</summary>
public sealed class LicenseKeyHasher(IOptions<ServerLicensingOptions> options)
{
    public string Hash(string normalizedKey)
    {
        var pepper = options.Value.KeyPepper;
        if (string.IsNullOrEmpty(pepper))
        {
            throw new InvalidOperationException("Licensing:KeyPepper (LICENSING_KEY_PEPPER) is not configured.");
        }

        return LicenseKeyFormat.Hash(normalizedKey, pepper);
    }
}
