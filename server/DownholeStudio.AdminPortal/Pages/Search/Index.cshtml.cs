using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Search;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public List<Customer> Customers { get; private set; } = [];
    public List<License> Licenses { get; private set; } = [];
    public List<Device> Devices { get; private set; } = [];
    public List<TrialDevice> Trials { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Q) || Q.Trim().Length < 2)
        {
            return;
        }

        var q = Q.Trim().ToLowerInvariant();
        var hint = q.Replace("-", "").Length >= 4 ? q.Replace("-", "")[^4..].ToUpperInvariant() : null;
        Customers = await db.Customers.AsNoTracking()
            .Where(c => c.Email.Contains(q) || (c.Name != null && c.Name.ToLower().Contains(q)) || (c.Company != null && c.Company.ToLower().Contains(q))).Take(25).ToListAsync(ct);
        Licenses = await db.Licenses.AsNoTracking().Include(l => l.Customer)
            .Where(l => l.Id.ToString().StartsWith(q) || l.KeyHint == hint || l.Customer!.Email.Contains(q)).Take(25).ToListAsync(ct);
        Devices = await db.Devices.AsNoTracking()
            .Where(d => d.DeviceHash.StartsWith(q) || (d.HardwareHash != null && d.HardwareHash.StartsWith(q)) || (d.MachineName != null && d.MachineName.ToLower().Contains(q))).Take(25).ToListAsync(ct);
        Trials = await db.TrialDevices.AsNoTracking().Where(t => t.DeviceHash.StartsWith(q) || (t.Email != null && t.Email.Contains(q))).Take(25).ToListAsync(ct);
    }
}
