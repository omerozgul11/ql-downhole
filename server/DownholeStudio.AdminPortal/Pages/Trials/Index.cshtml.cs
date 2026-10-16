using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Trials;

public sealed class IndexModel(LicensingDbContext db, AdminService admin, TimeProvider time) : AdminPageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Filter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public List<TrialDevice> Rows { get; private set; } = [];
    public bool HasMore { get; private set; }
    public DateTimeOffset Now { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Now = time.GetUtcNow();
        var now = Now;
        var query = db.TrialDevices.AsNoTracking();
        query = Filter switch
        {
            "active" => query.Where(t => t.TrialEnd > now && !t.Blocked),
            "expired" => query.Where(t => t.TrialEnd <= now),
            "blocked" => query.Where(t => t.Blocked),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim().ToLowerInvariant();
            query = query.Where(t => t.DeviceHash.StartsWith(q) || (t.Email != null && t.Email.Contains(q)) || (t.MachineName != null && t.MachineName.ToLower().Contains(q)));
        }

        P = Math.Max(1, P);
        var items = await query.OrderByDescending(t => t.TrialStart).Skip((P - 1) * PageSize).Take(PageSize + 1).ToListAsync(ct);
        HasMore = items.Count > PageSize;
        Rows = items.Take(PageSize).ToList();
    }

    public Task<IActionResult> OnPostExtendAsync(Guid trialId, int days, CancellationToken ct) =>
        days is < 1 or > 365
            ? Task.FromResult<IActionResult>(BadRequest())
            : Act(() => admin.ExtendTrialAsync(Actor, trialId, days, ct), $"Trial extended by {days} day(s).", new { Filter, Q });

    public Task<IActionResult> OnPostBlockAsync(Guid trialId, CancellationToken ct) =>
        Act(() => admin.SetTrialBlockedAsync(Actor, trialId, true, ct), "Trial blocked.", new { Filter, Q });

    public Task<IActionResult> OnPostUnblockAsync(Guid trialId, CancellationToken ct) =>
        Act(() => admin.SetTrialBlockedAsync(Actor, trialId, false, ct), "Trial unblocked.", new { Filter, Q });
}
