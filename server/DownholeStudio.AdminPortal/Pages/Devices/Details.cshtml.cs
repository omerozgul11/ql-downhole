using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Devices;

public sealed class DetailsModel(LicensingDbContext db, AdminService admin) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public Device Device { get; private set; } = null!;
    public List<Activation> Activations { get; private set; } = [];
    public List<TrialDevice> Trials { get; private set; } = [];
    public List<LicenseEvent> Events { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var d = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Id, ct);
        if (d is null)
        {
            return NotFound();
        }

        Device = d;
        Activations = await db.Activations.AsNoTracking().Include(a => a.License).ThenInclude(l => l!.Customer).Where(a => a.DeviceId == Id).OrderByDescending(a => a.ActivatedAt).ToListAsync(ct);
        Trials = await db.TrialDevices.AsNoTracking().Where(t => t.DeviceHash == d.DeviceHash || (d.HardwareHash != null && t.HardwareHash == d.HardwareHash)).ToListAsync(ct);
        Events = await db.LicenseEvents.AsNoTracking().Where(e => e.DeviceId == Id).OrderByDescending(e => e.CreatedAt).Take(100).ToListAsync(ct);
        return Page();
    }

    public Task<IActionResult> OnPostBlockAsync(string? reason, CancellationToken ct) =>
        Act(() => admin.SetDeviceBlockedAsync(Actor, Id, true, reason, ct), "Device blocked.", new { Id });

    public Task<IActionResult> OnPostUnblockAsync(CancellationToken ct) =>
        Act(() => admin.SetDeviceBlockedAsync(Actor, Id, false, null, ct), "Device unblocked.", new { Id });

    public Task<IActionResult> OnPostDeactivateAsync(Guid activationId, CancellationToken ct) =>
        Act(() => admin.DeactivateActivationAsync(Actor, activationId, "Deactivated by administrator", ct), "Activation deactivated.", new { Id });
}
