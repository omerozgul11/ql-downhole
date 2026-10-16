using System.ComponentModel.DataAnnotations;
using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Licenses;

public sealed class CreateModel(LicensingDbContext db, AdminService admin, TimeProvider time) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? CustomerId { get; set; }

    /// <summary>Used when no existing customer is chosen.</summary>
    [BindProperty, EmailAddress]
    public string? CustomerEmail { get; set; }

    [BindProperty]
    public string? CustomerName { get; set; }

    [BindProperty]
    public string? CustomerCompany { get; set; }

    [BindProperty, Required]
    public string Product { get; set; } = Licensing.ProductIds.DownholeStudio;

    [BindProperty, Required]
    public string Edition { get; set; } = "Professional";

    [BindProperty, Range(1, 1000)]
    public int Seats { get; set; } = 1;

    [BindProperty]
    public string? ExpiresOn { get; set; }

    [BindProperty]
    public string? MaintenanceUntil { get; set; }

    [BindProperty]
    public string? Notes { get; set; }

    [BindProperty]
    public bool SendEmail { get; set; } = true;

    public Customer? Customer { get; private set; }
    public List<LicenseProduct> Products { get; private set; } = [];
    public IssuedLicense? Issued { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        MaintenanceUntil ??= Day(time.GetUtcNow().AddYears(1));
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!ModelState.IsValid)
        {
            Error = "Check the highlighted values.";
            return Page();
        }

        try
        {
            var customerId = CustomerId;
            if (customerId is null)
            {
                if (string.IsNullOrWhiteSpace(CustomerEmail))
                {
                    Error = "Choose a customer or enter the new customer's e-mail address.";
                    return Page();
                }

                var existing = await db.Customers.FirstOrDefaultAsync(c => c.Email == CustomerEmail.Trim().ToLower(), ct);
                customerId = (existing ?? await admin.UpsertCustomerAsync(Actor, null, CustomerEmail, CustomerName, CustomerCompany, null, ct)).Id;
            }

            Issued = await admin.CreateLicenseAsync(Actor, customerId.Value, Product, Edition, Seats, ParseDate(ExpiresOn), ParseDate(MaintenanceUntil), Notes, SendEmail, ct);
            Customer = Issued.License.Customer;
            return Page();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Error = ex.Message;
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Products = await db.LicenseProducts.AsNoTracking().Where(p => p.Active).OrderBy(p => p.Name).ToListAsync(ct);
        if (CustomerId is { } id)
        {
            Customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        }
    }
}
