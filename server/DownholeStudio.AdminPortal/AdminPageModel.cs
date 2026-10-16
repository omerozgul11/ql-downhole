using System.Globalization;
using System.Security.Claims;
using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DownholeStudio.AdminPortal;

/// <summary>Base page: current administrator, status messages and formatting helpers.</summary>
public abstract class AdminPageModel : PageModel
{
    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    /// <summary>The signed-in administrator, for the audit log.</summary>
    protected AdminActor Actor => new(User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name ?? "unknown", HttpContext.Connection.RemoteIpAddress?.ToString());

    /// <summary>Runs an action, turning validation errors into a red message, then redirects back.</summary>
    protected async Task<IActionResult> Act(Func<Task> action, string success, object? routeValues = null)
    {
        try
        {
            await action();
            StatusMessage = success;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(routeValues);
    }

    public static string Date(DateTimeOffset? value) => value is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    public static string Day(DateTimeOffset? value) => value is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    public static string Short(string? hash) => string.IsNullOrEmpty(hash) ? "—" : hash[..Math.Min(12, hash.Length)] + "…";

    public static DateTimeOffset? ParseDate(string? text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? new DateTimeOffset(d, TimeSpan.Zero)
            : null;

    public static string StatusCss(LicenseStatusCode status) => status switch
    {
        LicenseStatusCode.Active => "ok",
        LicenseStatusCode.Suspended => "warn",
        _ => "bad",
    };
}
