using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Activations;

public sealed class IndexModel(LicensingDbContext db, AdminService admin) : AdminPageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public bool All { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public List<Activation> Rows { get; private set; } = [];
    public bool HasMore { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var query = db.Activations.AsNoTracking().Include(a => a.Device).Include(a => a.License).ThenInclude(l => l!.Customer).AsQueryable();
        if (!All)
        {
            query = query.Where(a => a.DeactivatedAt == null);
        }

        P = Math.Max(1, P);
        var items = await query.OrderByDescending(a => a.ActivatedAt).Skip((P - 1) * PageSize).Take(PageSize + 1).ToListAsync(ct);
        HasMore = items.Count > PageSize;
        Rows = items.Take(PageSize).ToList();
    }

    public Task<IActionResult> OnPostDeactivateAsync(Guid activationId, CancellationToken ct) =>
        Act(() => admin.DeactivateActivationAsync(Actor, activationId, "Deactivated by administrator", ct), "Activation deactivated (administrator override).", new { All });
}
