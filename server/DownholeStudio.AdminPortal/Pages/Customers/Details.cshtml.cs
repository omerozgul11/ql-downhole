using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Customers;

public sealed class DetailsModel(LicensingDbContext db) : AdminPageModel
{
    public Customer Customer { get; private set; } = null!;
    public List<License> Licenses { get; private set; } = [];
    public List<Payment> Payments { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return NotFound();
        }

        Customer = c;
        Licenses = await db.Licenses.AsNoTracking().Include(l => l.Activations).Where(l => l.CustomerId == id).OrderByDescending(l => l.CreatedAt).ToListAsync(ct);
        Payments = await db.Payments.AsNoTracking().Where(p => p.CustomerId == id).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        return Page();
    }
}
