using System.ComponentModel.DataAnnotations;
using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DownholeStudio.AdminPortal.Pages.Customers;

public sealed class EditModel(LicensingDbContext db, AdminService admin) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? Id { get; set; }

    [BindProperty, Required, EmailAddress]
    public string Email { get; set; } = "";

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? Company { get; set; }

    [BindProperty]
    public string? Notes { get; set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Id is { } id)
        {
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null)
            {
                return NotFound();
            }

            (Email, Name, Company, Notes) = (c.Email, c.Name, c.Company, c.Notes);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            Error = "Enter a valid e-mail address.";
            return Page();
        }

        try
        {
            var c = await admin.UpsertCustomerAsync(Actor, Id, Email, Name, Company, Notes, ct);
            StatusMessage = "Customer saved.";
            return RedirectToPage("Details", new { id = c.Id });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
