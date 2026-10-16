using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Customers;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public List<(Customer Customer, int Licenses)> Rows { get; private set; } = [];
    public bool HasMore { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var query = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim().ToLowerInvariant();
            query = query.Where(c => c.Email.Contains(q) || (c.Name != null && c.Name.ToLower().Contains(q)) || (c.Company != null && c.Company.ToLower().Contains(q)));
        }

        P = Math.Max(1, P);
        var items = await query.OrderBy(c => c.Email).Skip((P - 1) * PageSize).Take(PageSize + 1)
            .Select(c => new { Customer = c, Count = c.Licenses.Count }).ToListAsync(ct);
        HasMore = items.Count > PageSize;
        Rows = items.Take(PageSize).Select(x => (x.Customer, x.Count)).ToList();
    }
}
