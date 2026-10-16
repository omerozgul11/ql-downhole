using System.ComponentModel.DataAnnotations;
using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Releases;

public sealed class IndexModel(LicensingDbContext db, AdminService admin) : AdminPageModel
{
    public List<SoftwareVersion> Rows { get; private set; } = [];

    [BindProperty, Required]
    public string Product { get; set; } = Licensing.ProductIds.DownholeStudio;

    [BindProperty, Required]
    public string Version { get; set; } = "";

    [BindProperty, Required]
    public string Channel { get; set; } = "stable";

    [BindProperty]
    public string? Notes { get; set; }

    [BindProperty, Required]
    public string InstallerUrl { get; set; } = "";

    [BindProperty, Required]
    public string Sha256 { get; set; } = "";

    [BindProperty]
    public long Size { get; set; }

    [BindProperty]
    public bool IsMajor { get; set; }

    [BindProperty]
    public string? MinimumVersion { get; set; }

    [BindProperty]
    public bool Publish { get; set; }

    public async Task OnGetAsync(CancellationToken ct) =>
        Rows = await db.SoftwareVersions.AsNoTracking().OrderByDescending(v => v.CreatedAt).Take(200).ToListAsync(ct);

    public Task<IActionResult> OnPostCreateAsync(CancellationToken ct) =>
        Act(() => admin.CreateReleaseAsync(Actor, Product, Version, Channel, Notes ?? "", InstallerUrl, Sha256.Trim(), Size, IsMajor, MinimumVersion, Publish, ct),
            $"Release {Version} ({Channel}) saved.");

    public Task<IActionResult> OnPostPublishAsync(Guid releaseId, CancellationToken ct) =>
        Act(() => admin.SetReleasePublishedAsync(Actor, releaseId, true, ct), "Release published.");

    public Task<IActionResult> OnPostUnpublishAsync(Guid releaseId, CancellationToken ct) =>
        Act(() => admin.SetReleasePublishedAsync(Actor, releaseId, false, ct), "Release withdrawn.");
}
