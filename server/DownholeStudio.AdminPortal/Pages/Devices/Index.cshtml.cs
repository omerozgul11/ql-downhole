using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Devices;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public List<(Device Device, int Active)> Rows { get; private set; } = [];
    public bool HasMore { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var query = db.Devices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim().ToLowerInvariant();
            query = query.Where(d => d.DeviceHash.StartsWith(q) || (d.HardwareHash != null && d.HardwareHash.StartsWith(q)) || (d.MachineName != null && d.MachineName.ToLower().Contains(q)));
        }

        P = Math.Max(1, P);
        var items = await query.OrderByDescending(d => d.LastSeenAt).Skip((P - 1) * PageSize).Take(PageSize + 1)
            .Select(d => new { Device = d, Active = d.Activations.Count(a => a.DeactivatedAt == null) }).ToListAsync(ct);
        HasMore = items.Count > PageSize;
        Rows = items.Take(PageSize).Select(x => (x.Device, x.Active)).ToList();
    }
}
