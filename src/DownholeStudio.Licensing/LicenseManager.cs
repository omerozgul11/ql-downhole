using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownholeStudio.Licensing;

public enum LicenseState
{
    NotActivated,
    TrialActive,
    TrialExpiring,
    TrialExpired,
    Licensed,
    LicenseOfflineGrace,
    LicenseExpired,
    LicenseRevoked,
    ActivationLimitReached,
    NetworkValidationRequired,
}

/// <summary>Current licence status with everything the UI needs to explain it.</summary>
public sealed record LicenseStatus
{
    public required LicenseState State { get; init; }
    public LicenseClaims? Claims { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public int? DaysRemaining { get; init; }
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public bool ClockRollbackSuspected { get; init; }
    /// <summary>Editing, saving, importing and report generation are allowed.</summary>
    public bool CanEdit => State is LicenseState.TrialActive or LicenseState.TrialExpiring or LicenseState.Licensed or LicenseState.LicenseOfflineGrace;
    /// <summary>Projects can always be opened and viewed.</summary>
    public bool CanView => true;
    public bool IsTrial => Claims?.IsTrial == true;
}

public sealed record LicensingOptions
{
    /// <summary>The licensing server; null when this build activates offline only (no network calls are made).</summary>
    public Uri? ServerUrl { get; init; }
    public string Product { get; init; } = ProductIds.DownholeStudio;
    public required PublicKeyRing Keys { get; init; }
    /// <summary>After this many days without online validation the status changes to Offline Grace (still fully usable).</summary>
    public int GraceNoticeDays { get; init; } = 7;
    public int TrialWarningDays { get; init; } = 3;
    /// <summary>Clock moving back more than this relative to the last seen time requires online validation.</summary>
    public TimeSpan ClockTolerance { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

public sealed record LicenseOperationResult(bool Success, string Message, string? ErrorCode = null, IReadOnlyList<ActivatedDeviceInfo>? Devices = null);

/// <summary>HTTP client for the licensing server.</summary>
public sealed class LicenseApiClient(HttpClient http, Uri? serverUrl)
{
    private Uri baseUrl => serverUrl ?? throw new InvalidOperationException("No licensing server is configured.");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<LicenseResponse> StartTrialAsync(TrialStartRequest request, CancellationToken ct) => PostAsync("api/v1/trials/start", request, ct);

    public Task<LicenseResponse> ActivateAsync(ActivationRequest request, CancellationToken ct) => PostAsync("api/v1/licenses/activate", request, ct);

    public Task<LicenseResponse> ValidateAsync(ValidationRequest request, CancellationToken ct) => PostAsync("api/v1/licenses/validate", request, ct);

    public Task<LicenseResponse> DeactivateAsync(DeactivationRequest request, CancellationToken ct) => PostAsync("api/v1/licenses/deactivate", request, ct);

    public async Task<SignedEnvelope?> LatestReleaseAsync(string product, string channel, string currentVersion, CancellationToken ct)
    {
        var url = new Uri(baseUrl, $"api/v1/updates/latest?product={Uri.EscapeDataString(product)}&channel={Uri.EscapeDataString(channel)}&current={Uri.EscapeDataString(currentVersion)}");
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SignedEnvelope>(Json, ct).ConfigureAwait(false);
    }

    private async Task<LicenseResponse> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(new Uri(baseUrl, path), body, Json, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new LicenseResponse { Success = false, ErrorCode = LicenseErrorCodes.RateLimited, Message = "Too many licensing requests from this computer. Wait a few minutes and try again." };
        }

        var content = response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await response.Content.ReadFromJsonAsync<LicenseResponse>(Json, ct).ConfigureAwait(false)
            : null;
        if (content is null)
        {
            if ((int)response.StatusCode >= 500)
            {
                throw new HttpRequestException($"The licensing server returned {(int)response.StatusCode}.", null, response.StatusCode);
            }

            return new LicenseResponse { Success = false, ErrorCode = LicenseErrorCodes.ServerError, Message = $"Unexpected response from the licensing server ({(int)response.StatusCode})." };
        }

        return content;
    }
}

/// <summary>
/// Client-side licence state machine. The server is authoritative; the client verifies signed tokens
/// offline, detects clock rollback, and never touches project data.
/// </summary>
public sealed class LicenseManager
{
    private readonly LicensingOptions _options;
    private readonly ILicenseStore _store;
    private readonly IMachineFingerprintProvider _fingerprint;
    private readonly LicenseApiClient _api;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _log;
    private readonly string _appVersion;
    private DeviceFingerprint? _cachedFingerprint;

    public LicenseManager(LicensingOptions options, ILicenseStore store, IMachineFingerprintProvider fingerprint, LicenseApiClient api, string appVersion,
        Func<DateTimeOffset>? clock = null, ILogger<LicenseManager>? logger = null)
    {
        _options = options;
        _store = store;
        _fingerprint = fingerprint;
        _api = api;
        _appVersion = appVersion;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = (ILogger?)logger ?? NullLogger.Instance;
        Status = Evaluate();
    }

    public LicenseStatus Status { get; private set; }

    public event EventHandler<LicenseStatus>? StatusChanged;

    public DeviceFingerprint Fingerprint => _cachedFingerprint ??= _fingerprint.GetFingerprint();

    public string MachineName => _fingerprint.MachineName;

    /// <summary>The code the customer sends to QuakeLogic for an offline activation key.</summary>
    public string ComputerCode => Licensing.ComputerCode.FromDeviceHash(Fingerprint.DeviceHash);

    /// <summary>Online activation and validation are available (a licensing server is configured).</summary>
    public bool OnlineAvailable => _options.ServerUrl is not null;

    private static readonly LicenseOperationResult NoServer = new(false,
        "Online activation is not available in this version of Downhole Studio. Use offline activation: send your computer code to QuakeLogic and enter the activation key you receive.", LicenseErrorCodes.ServerError);

    /// <summary>Re-evaluates the stored token against the local clock (no network).</summary>
    public LicenseStatus Evaluate()
    {
        var now = _clock();
        var stored = _store.Load();

        // Track the highest clock reading ever seen; a large step backwards needs online confirmation.
        var rollback = stored.LastSeenAt is { } seen && now < seen - _options.ClockTolerance;
        if (!rollback && (stored.LastSeenAt is null || now > stored.LastSeenAt))
        {
            stored = stored with { LastSeenAt = now };
            TrySave(stored);
        }

        LicenseStatus status;
        if (stored.ServerStatus is LicenseErrorCodes.Revoked or LicenseErrorCodes.Suspended)
        {
            status = new LicenseStatus
            {
                State = LicenseState.LicenseRevoked, Title = stored.ServerStatus == LicenseErrorCodes.Suspended ? "License suspended" : "License revoked",
                Message = (stored.ServerMessage ?? "This license is no longer valid.") + " Your projects are safe and can still be opened in read-only mode. Contact QuakeLogic support or activate another license.",
            };
        }
        else if (stored.Token is null)
        {
            status = stored.ServerStatus == LicenseErrorCodes.TrialExpired
                ? new LicenseStatus { State = LicenseState.TrialExpired, Title = "Trial ended", Message = "The 14-day trial on this computer has ended. Activate a license to continue editing. Your projects are kept and can still be opened." }
                : new LicenseStatus { State = LicenseState.NotActivated, Title = "Activate Downhole Studio", Message = "Enter the activation key from QuakeLogic to start using Downhole Studio on this computer." };
        }
        else
        {
            var claims = LicenseTokenCodec.Verify(stored.Token, _options.Keys);
            if (claims is null || claims.Product != _options.Product || !BelongsToThisComputer(claims))
            {
                status = new LicenseStatus
                {
                    State = LicenseState.NotActivated, Title = "Activation needed",
                    Message = "The saved license information is not valid for this computer. Activate again; if the license is in use elsewhere it can be moved here.",
                };
            }
            else if (claims.IsOfflineLicense)
            {
                status = ClassifyOffline(claims, stored, now);
            }
            else
            {
                status = Classify(claims, stored, now, rollback || now < claims.IssuedAt - _options.ClockTolerance);
            }
        }

        SetStatus(status);
        return status;
    }

    private bool BelongsToThisComputer(LicenseClaims claims) => claims.IsOfflineLicense
        ? string.Equals(claims.DeviceHash, ComputerCode, StringComparison.Ordinal)
        : string.Equals(claims.DeviceHash, Fingerprint.DeviceHash, StringComparison.Ordinal);

    /// <summary>
    /// Offline activations never need the internet. An end date is checked against the latest time this computer
    /// has ever seen, so setting the clock back cannot extend the licence.
    /// </summary>
    private static LicenseStatus ClassifyOffline(LicenseClaims c, StoredLicense stored, DateTimeOffset now)
    {
        var effectiveNow = stored.LastSeenAt is { } seen && seen > now ? seen : now;
        if (c.ExpiresAt is { } exp && effectiveNow >= exp)
        {
            return new LicenseStatus
            {
                State = LicenseState.LicenseExpired, Claims = c, LastVerifiedAt = c.IssuedAt, Title = "License expired",
                Message = $"The license expired on {exp:MMMM d, yyyy}. Contact QuakeLogic to renew it; your projects can still be opened.",
            };
        }

        return new LicenseStatus
        {
            State = LicenseState.Licensed, Claims = c, LastVerifiedAt = c.IssuedAt, Title = "Licensed",
            Message = c.ExpiresAt is { } until ? $"Activated on this computer until {until:MMMM d, yyyy}." : "Activated on this computer (perpetual license).",
        };
    }

    /// <summary>
    /// Activates this computer with an offline activation key from QuakeLogic (no network). The key must be signed by
    /// QuakeLogic and issued for this computer's code.
    /// </summary>
    public LicenseOperationResult ActivateOffline(string activationKey)
    {
        var token = OfflineActivationKey.Clean(activationKey ?? "");
        if (token.Length == 0)
        {
            return new LicenseOperationResult(false, "Paste the activation key from your QuakeLogic e-mail, or open the activation file.", LicenseErrorCodes.InvalidToken);
        }

        var claims = LicenseTokenCodec.Verify(token, _options.Keys);
        if (claims is null)
        {
            return new LicenseOperationResult(false, "This activation key is not valid. Copy the whole key from the e-mail (it starts with dhs1.) or open the activation file instead.", LicenseErrorCodes.InvalidToken);
        }

        if (claims.Product != _options.Product)
        {
            return new LicenseOperationResult(false, "This activation key is for a different product.", LicenseErrorCodes.WrongProduct);
        }

        if (!claims.IsOfflineLicense)
        {
            return new LicenseOperationResult(false, "This is not an offline activation key. Contact QuakeLogic for an activation key for this computer.", LicenseErrorCodes.InvalidToken);
        }

        if (!BelongsToThisComputer(claims))
        {
            return new LicenseOperationResult(false, $"This activation key was issued for another computer ({claims.DeviceHash}). The code of this computer is {ComputerCode}; send it to QuakeLogic to receive a key for this computer.", LicenseErrorCodes.InvalidToken);
        }

        var stored = _store.Load();
        var effectiveNow = stored.LastSeenAt is { } seen && seen > _clock() ? seen : _clock();
        if (claims.ExpiresAt is { } exp && effectiveNow >= exp)
        {
            return new LicenseOperationResult(false, $"This activation key expired on {exp:MMMM d, yyyy}. Contact QuakeLogic for a new key.", LicenseErrorCodes.Expired);
        }

        TrySave(stored with { Token = token, LastVerifiedAt = claims.IssuedAt, ServerStatus = null, ServerMessage = null });
        Evaluate();
        return new LicenseOperationResult(true, $"Downhole Studio is activated on this computer for {claims.LicensedTo ?? "your organisation"}.");
    }

    private LicenseStatus Classify(LicenseClaims c, StoredLicense stored, DateTimeOffset now, bool rollback)
    {
        var verified = stored.LastVerifiedAt ?? c.IssuedAt;
        if (rollback)
        {
            return new LicenseStatus
            {
                State = LicenseState.NetworkValidationRequired, Claims = c, LastVerifiedAt = verified, ClockRollbackSuspected = true,
                Title = "Clock check needed",
                Message = "Your system clock changed significantly. Connect to the internet to verify your license. Your projects can still be opened in read-only mode.",
            };
        }

        if (c.IsTrial)
        {
            var end = c.TrialEnd ?? c.ExpiresAt ?? c.OfflineValidUntil;
            var days = (int)Math.Ceiling((end - now).TotalDays);
            if (now >= end)
            {
                return new LicenseStatus
                {
                    State = LicenseState.TrialExpired, Claims = c, DaysRemaining = 0, LastVerifiedAt = verified, Title = "Trial ended",
                    Message = "The 14-day trial has ended. Activate a license to continue editing. Your projects are kept and can still be opened.",
                };
            }

            if (now > c.OfflineValidUntil)
            {
                return Offline(c, verified, "Connect to the internet to confirm your trial.");
            }

            return new LicenseStatus
            {
                State = days <= _options.TrialWarningDays ? LicenseState.TrialExpiring : LicenseState.TrialActive, Claims = c, DaysRemaining = Math.Max(days, 0),
                LastVerifiedAt = verified, Title = $"{c.Edition} Trial",
                Message = days == 1 ? "1 day remaining" : $"{days} days remaining",
            };
        }

        if (c.ExpiresAt is { } exp && now >= exp)
        {
            return new LicenseStatus
            {
                State = LicenseState.LicenseExpired, Claims = c, LastVerifiedAt = verified, Title = "License expired",
                Message = $"The license expired on {exp:MMMM d, yyyy}. Renew it to continue editing; your projects can still be opened.",
            };
        }

        if (now > c.OfflineValidUntil)
        {
            return Offline(c, verified, "The offline period has ended. Connect to the internet to verify your license.");
        }

        var since = (now - verified).TotalDays;
        if (since > _options.GraceNoticeDays)
        {
            var left = Math.Max(0, (int)Math.Floor((c.OfflineValidUntil - now).TotalDays));
            return new LicenseStatus
            {
                State = LicenseState.LicenseOfflineGrace, Claims = c, LastVerifiedAt = verified, Title = "Licensed (offline)",
                Message = $"License last verified {Ago(verified, now)}. Connect to the internet within {left} day{(left == 1 ? "" : "s")} to keep working without interruption.",
            };
        }

        return new LicenseStatus
        {
            State = LicenseState.Licensed, Claims = c, LastVerifiedAt = verified, Title = "Licensed",
            Message = $"License last verified {Ago(verified, now)}.",
        };
    }

    private static LicenseStatus Offline(LicenseClaims c, DateTimeOffset verified, string message) => new()
    {
        State = LicenseState.NetworkValidationRequired, Claims = c, LastVerifiedAt = verified, Title = "Online check needed",
        Message = message + " Your projects can still be opened in read-only mode.",
    };

    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var d = (now - then).TotalDays;
        return d < 1 ? "today" : d < 2 ? "yesterday" : $"{(int)Math.Floor(d)} days ago";
    }

    public async Task<LicenseOperationResult> StartTrialAsync(string? email, bool shareMachineName, CancellationToken ct = default)
    {
        if (!OnlineAvailable)
        {
            return NoServer;
        }

        var request = new TrialStartRequest
        {
            Product = _options.Product, Fingerprint = Fingerprint, AppVersion = _appVersion, OsVersion = _fingerprint.OsDescription,
            MachineName = shareMachineName ? _fingerprint.MachineName : null, Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
        };
        return await CallAsync(() => _api.StartTrialAsync(request, ct), "trial").ConfigureAwait(false);
    }

    public async Task<LicenseOperationResult> ActivateAsync(string licenseKey, string email, bool shareMachineName, CancellationToken ct = default)
    {
        if (!OnlineAvailable)
        {
            return NoServer;
        }

        var key = LicenseKeyFormat.Normalize(licenseKey);
        if (key is null)
        {
            return new LicenseOperationResult(false, "That does not look like a Downhole Studio license key. Keys look like DHST-XXXX-XXXX-XXXX-XXXX.", LicenseErrorCodes.InvalidKey);
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return new LicenseOperationResult(false, "Enter the email address the license was issued to.", LicenseErrorCodes.EmailMismatch);
        }

        var request = new ActivationRequest
        {
            LicenseKey = key, Email = email.Trim(), Product = _options.Product, Fingerprint = Fingerprint, AppVersion = _appVersion,
            OsVersion = _fingerprint.OsDescription, MachineName = shareMachineName ? _fingerprint.MachineName : null,
        };
        var result = await CallAsync(() => _api.ActivateAsync(request, ct), "activation").ConfigureAwait(false);
        if (!result.Success && result.ErrorCode == LicenseErrorCodes.ActivationLimit)
        {
            SetStatus(Status with { State = LicenseState.ActivationLimitReached, Title = "Activation limit reached", Message = result.Message });
        }

        return result;
    }

    /// <summary>Online validation. Network failures keep the current (offline) state; nothing is deleted.</summary>
    public async Task<LicenseOperationResult> RefreshAsync(CancellationToken ct = default)
    {
        var stored = _store.Load();
        if (stored.Token is null)
        {
            return new LicenseOperationResult(false, "There is no license on this computer to verify.");
        }

        if (LicenseTokenCodec.ReadUnverified(stored.Token)?.IsOfflineLicense == true)
        {
            Evaluate();
            return new LicenseOperationResult(true, "This computer is activated offline; no online check is needed.");
        }

        if (!OnlineAvailable)
        {
            return NoServer;
        }

        var request = new ValidationRequest { Token = stored.Token, Fingerprint = Fingerprint, AppVersion = _appVersion };
        return await CallAsync(() => _api.ValidateAsync(request, ct), "validation").ConfigureAwait(false);
    }

    public async Task<LicenseOperationResult> DeactivateAsync(CancellationToken ct = default)
    {
        var stored = _store.Load();
        if (stored.Token is null)
        {
            return new LicenseOperationResult(false, "This computer is not activated.");
        }

        if (LicenseTokenCodec.ReadUnverified(stored.Token)?.IsOfflineLicense == true)
        {
            // Offline activations cannot be released remotely; QuakeLogic frees the seat when the customer reports the move.
            TrySave(stored with { Token = null, ServerStatus = null, ServerMessage = null, LastVerifiedAt = null });
            Evaluate();
            return new LicenseOperationResult(true, $"The activation was removed from this computer. To use the license on another computer, send that computer's code to QuakeLogic and mention that this computer ({ComputerCode}) no longer uses it.");
        }

        if (!OnlineAvailable)
        {
            return NoServer;
        }

        try
        {
            var response = await _api.DeactivateAsync(new DeactivationRequest { Token = stored.Token, Fingerprint = Fingerprint, Reason = "user" }, ct).ConfigureAwait(false);
            if (!response.Success)
            {
                return new LicenseOperationResult(false, response.Message ?? "The license could not be deactivated.", response.ErrorCode);
            }

            TrySave(stored with { Token = null, ServerStatus = null, ServerMessage = null, LastVerifiedAt = null });
            Evaluate();
            return new LicenseOperationResult(true, response.Message ?? "This computer has been deactivated. The license can now be activated on another computer.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Deactivation failed: {Error}", ex.Message);
            return new LicenseOperationResult(false, "The licensing server could not be reached. Deactivation needs an internet connection; nothing was changed.", LicenseErrorCodes.ServerError);
        }
    }

    private async Task<LicenseOperationResult> CallAsync(Func<Task<LicenseResponse>> call, string what)
    {
        LicenseResponse response;
        try
        {
            response = await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning("Licensing {What} failed: {Error}", what, ex.Message);
            var stored = _store.Load();
            TrySave(stored with { LastAttemptAt = _clock() });
            Evaluate();
            return new LicenseOperationResult(false, "The licensing server could not be reached. Check the internet connection and try again. If you are offline, Downhole Studio keeps working until the offline period ends.", LicenseErrorCodes.ServerError);
        }

        var current = _store.Load();
        if (response.Success && response.Token is not null)
        {
            var claims = LicenseTokenCodec.Verify(response.Token, _options.Keys);
            if (claims is null || claims.DeviceHash != Fingerprint.DeviceHash)
            {
                _log.LogWarning("Licensing server returned a token that failed verification.");
                return new LicenseOperationResult(false, "The licensing server's response could not be verified. Make sure you are connected to the official QuakeLogic licensing service.", LicenseErrorCodes.InvalidToken);
            }

            // A confirmed server time re-anchors the clock guard when the local clock agrees with it; otherwise
            // (local clock still far off) the highest reading is kept so rollback stays detectable.
            var serverTime = response.ServerTime == default ? claims.IssuedAt : response.ServerTime;
            var local = _clock();
            var lastSeen = (local - serverTime).Duration() <= _options.ClockTolerance ? local : Max(current.LastSeenAt, local);
            TrySave(current with
            {
                Token = response.Token, LastVerifiedAt = serverTime, LastAttemptAt = local,
                ServerStatus = null, ServerMessage = null, LastSeenAt = lastSeen,
            });
            Evaluate();
            return new LicenseOperationResult(true, response.Message ?? "Done.");
        }

        // Authoritative negative answers from the server change the stored state; others are just reported.
        switch (response.ErrorCode)
        {
            case LicenseErrorCodes.Revoked or LicenseErrorCodes.Suspended:
                TrySave(current with { Token = null, ServerStatus = response.ErrorCode, ServerMessage = response.Message, LastAttemptAt = _clock() });
                break;
            case LicenseErrorCodes.Deactivated or LicenseErrorCodes.InvalidToken:
                TrySave(current with { Token = null, ServerStatus = null, ServerMessage = response.Message, LastAttemptAt = _clock() });
                break;
            case LicenseErrorCodes.TrialExpired:
                TrySave(current with { Token = current.Token is not null && LicenseTokenCodec.ReadUnverified(current.Token)?.IsTrial == true ? current.Token : null,
                    ServerStatus = LicenseErrorCodes.TrialExpired, LastAttemptAt = _clock() });
                break;
            case LicenseErrorCodes.Expired:
                TrySave(current with { LastAttemptAt = _clock() });
                break;
        }

        Evaluate();
        return new LicenseOperationResult(false, response.Message ?? "The request was not accepted.", response.ErrorCode, response.Devices);
    }

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x > b ? x : b;

    private void TrySave(StoredLicense value)
    {
        try
        {
            _store.Save(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not save license state: {Error}", ex.Message);
        }
    }

    private void SetStatus(LicenseStatus status)
    {
        var changed = Status is null || Status.State != status.State || Status.Message != status.Message;
        Status = status;
        if (changed)
        {
            StatusChanged?.Invoke(this, status);
        }
    }

    /// <summary>Plain text for "Copy License Information" (no secrets).</summary>
    public string DescribeForSupport()
    {
        var c = Status.Claims;
        return string.Join(Environment.NewLine,
            $"Product: Downhole Studio {c?.Edition ?? ""}".TrimEnd(),
            $"Version: {_appVersion}",
            $"Status: {Status.Title} — {Status.Message}",
            $"Licensed to: {c?.LicensedTo ?? "—"}",
            $"License ID: {c?.LicenseId ?? "—"}",
            $"License key: {LicenseKeyFormat.Masked(c?.KeyHint)}",
            $"Device: {_fingerprint.MachineName}",
            $"Computer code: {ComputerCode}",
            $"Device ID: {Fingerprint.DeviceHash[..16]}…",
            $"Last validation: {(Status.LastVerifiedAt is { } v ? v.ToString("MMMM d, yyyy HH:mm 'UTC'") : "—")}",
            $"Maintenance until: {(c?.MaintenanceUntil is { } m ? m.ToString("MMMM d, yyyy") : "—")}");
    }
}
