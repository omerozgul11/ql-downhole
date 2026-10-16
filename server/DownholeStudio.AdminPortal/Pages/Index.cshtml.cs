using DownholeStudio.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages;

public sealed class IndexModel(LicensingDbContext db, TimeProvider time) : AdminPageModel
{
    public int ActiveLicenses { get; private set; }
    public int Customers { get; private set; }
    public int ActiveDevices { get; private set; }
    public int TrialsActive { get; private set; }
    public int TrialsStarted30 { get; private set; }
    public int TrialsExpired30 { get; private set; }
    public int PendingEmails { get; private set; }
    public int FailedEmails { get; private set; }
    public List<LicenseEvent> Events { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = now.AddDays(-30);
        ActiveLicenses = await db.Licenses.CountAsync(l => l.Status == LicenseStatusCode.Active && (l.ExpiresAt == null || l.ExpiresAt > now), ct);
        Customers = await db.Customers.CountAsync(ct);
        ActiveDevices = await db.Activations.Where(a => a.DeactivatedAt == null).Select(a => a.DeviceId).Distinct().CountAsync(ct);
        TrialsActive = await db.TrialDevices.CountAsync(t => t.TrialEnd > now && !t.Blocked, ct);
        TrialsStarted30 = await db.TrialDevices.CountAsync(t => t.TrialStart >= since, ct);
        TrialsExpired30 = await db.TrialDevices.CountAsync(t => t.TrialEnd <= now && t.TrialEnd >= since, ct);
        PendingEmails = await db.EmailOutbox.CountAsync(m => m.SentAt == null && m.Attempts < EmailDispatchWorker.MaxAttempts, ct);
        FailedEmails = await db.EmailOutbox.CountAsync(m => m.SentAt == null && m.Attempts >= EmailDispatchWorker.MaxAttempts, ct);
        Events = await db.LicenseEvents.OrderByDescending(e => e.CreatedAt).Take(25).ToListAsync(ct);
    }
}
