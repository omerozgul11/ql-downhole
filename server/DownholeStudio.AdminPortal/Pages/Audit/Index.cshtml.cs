using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Audit;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    public const int PageSize = 100;

    [BindProperty(SupportsGet = true)]
    public string? ByActor { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public List<AuditLogEntry> Rows { get; private set; } = [];
    public bool HasMore { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var query = db.AuditLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(ByActor))
        {
            var a = ByActor.Trim().ToLowerInvariant();
            query = query.Where(x => x.Actor.ToLower().Contains(a));
        }

        if (!string.IsNullOrWhiteSpace(Action))
        {
            var a = Action.Trim().ToLowerInvariant();
            query = query.Where(x => x.Action.Contains(a));
        }

        P = Math.Max(1, P);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((P - 1) * PageSize).Take(PageSize + 1).ToListAsync(ct);
        HasMore = items.Count > PageSize;
        Rows = items.Take(PageSize).ToList();
    }
}
