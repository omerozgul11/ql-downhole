using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Licenses;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public LicenseStatusCode? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public List<License> Rows { get; private set; } = [];
    public bool HasMore { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var query = db.Licenses.AsNoTracking().Include(l => l.Customer).Include(l => l.Activations).AsQueryable();
        if (Status is { } s)
        {
            query = query.Where(l => l.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim().ToLowerInvariant();
            var hint = q.Length >= 4 ? q[^4..].ToUpperInvariant() : null;
            query = query.Where(l => l.Customer!.Email.Contains(q) || (l.Customer.Company != null && l.Customer.Company.ToLower().Contains(q)) || l.KeyHint == hint || l.Id.ToString().StartsWith(q));
        }

        P = Math.Max(1, P);
        var items = await query.OrderByDescending(l => l.CreatedAt).Skip((P - 1) * PageSize).Take(PageSize + 1).ToListAsync(ct);
        HasMore = items.Count > PageSize;
        Rows = items.Take(PageSize).ToList();
    }
}
