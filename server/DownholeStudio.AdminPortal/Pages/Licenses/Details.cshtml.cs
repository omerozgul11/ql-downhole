using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Licenses;

public sealed class DetailsModel(LicensingDbContext db, AdminService admin) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public License License { get; private set; } = null!;
    public List<Activation> Activations { get; private set; } = [];
    public List<LicenseEvent> Events { get; private set; } = [];
    public List<AuditLogEntry> Audit { get; private set; } = [];

    /// <summary>Set only in the response to "Reissue key".</summary>
    public string? NewKey { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await LoadAsync(ct) ? Page() : NotFound();

    public Task<IActionResult> OnPostRevokeAsync(CancellationToken ct) =>
        Act(() => admin.SetStatusAsync(Actor, Id, LicenseStatusCode.Revoked, ct), "License revoked. Activated computers lose access at their next online check.", new { Id });

    public Task<IActionResult> OnPostSuspendAsync(CancellationToken ct) =>
        Act(() => admin.SetStatusAsync(Actor, Id, LicenseStatusCode.Suspended, ct), "License suspended.", new { Id });

    public Task<IActionResult> OnPostReactivateAsync(CancellationToken ct) =>
        Act(() => admin.SetStatusAsync(Actor, Id, LicenseStatusCode.Active, ct), "License reactivated.", new { Id });

    public Task<IActionResult> OnPostExpiryAsync(string? expiresOn, CancellationToken ct) =>
        Act(() => admin.SetExpiryAsync(Actor, Id, ParseDate(expiresOn), ct), "Expiry updated.", new { Id });

    public Task<IActionResult> OnPostMaintenanceAsync(string? maintenanceUntil, CancellationToken ct) =>
        Act(() => admin.SetMaintenanceAsync(Actor, Id, ParseDate(maintenanceUntil), ct), "Maintenance updated.", new { Id });

    public Task<IActionResult> OnPostSeatsAsync(int seats, CancellationToken ct) =>
        Act(() => admin.SetSeatsAsync(Actor, Id, seats, ct), "Seat count updated.", new { Id });

    public Task<IActionResult> OnPostTransfersAsync(int transfers, CancellationToken ct) =>
        Act(() => admin.SetTransferLimitAsync(Actor, Id, transfers, ct), "Transfer limit updated.", new { Id });

    public Task<IActionResult> OnPostNotesAsync(string? notes, CancellationToken ct) =>
        Act(() => admin.SetNotesAsync(Actor, Id, notes, ct), "Notes saved.", new { Id });

    public Task<IActionResult> OnPostResetAsync(CancellationToken ct) =>
        Act(async () => await admin.ResetActivationsAsync(Actor, Id, ct), "All activations were reset (administrator override).", new { Id });

    public Task<IActionResult> OnPostDeactivateAsync(Guid activationId, CancellationToken ct) =>
        Act(() => admin.DeactivateActivationAsync(Actor, activationId, "Deactivated by administrator", ct), "Computer deactivated (administrator override).", new { Id });

    public async Task<IActionResult> OnPostReissueAsync(bool sendEmail, CancellationToken ct)
    {
        try
        {
            NewKey = (await admin.ReissueKeyAsync(Actor, Id, sendEmail, ct)).LicenseKey;
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage(new { Id });
        }

        return await LoadAsync(ct) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var license = await db.Licenses.AsNoTracking().Include(l => l.Customer).FirstOrDefaultAsync(l => l.Id == Id, ct);
        if (license is null)
        {
            return false;
        }

        License = license;
        Activations = await db.Activations.AsNoTracking().Include(a => a.Device).Where(a => a.LicenseId == Id).OrderByDescending(a => a.ActivatedAt).ToListAsync(ct);
        Events = await db.LicenseEvents.AsNoTracking().Where(e => e.LicenseId == Id).OrderByDescending(e => e.CreatedAt).Take(100).ToListAsync(ct);
        var id = Id.ToString();
        Audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "license" && a.EntityId == id).OrderByDescending(a => a.CreatedAt).Take(50).ToListAsync(ct);
        return true;
    }
}
