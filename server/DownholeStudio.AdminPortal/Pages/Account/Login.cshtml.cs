using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DownholeStudio.AdminPortal.Pages.Account;

public sealed class LoginModel(AdminAccountService accounts) : PageModel
{
    [BindProperty, Required, EmailAddress]
    public string Email { get; set; } = "";

    [BindProperty, Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            Error = "Enter your e-mail address and password.";
            return Page();
        }

        var (user, error) = await accounts.SignInAsync(Email, Password, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        if (user is null)
        {
            Error = error;
            return Page();
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName ?? user.Email),
            new Claim(ClaimTypes.Email, user.Email),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(ReturnUrl is not null && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
    }
}
