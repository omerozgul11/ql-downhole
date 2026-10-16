using DownholeStudio.AdminPortal;
using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

// Command-line mode: create the first administrator and exit.
//   dotnet DownholeStudio.AdminPortal.dll create-admin --email admin@example.com --password-stdin
if (args.Length > 0 && args[0] == "create-admin")
{
    return await AdminCommands.CreateAdminAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Secure-only cookies need HTTPS. Plain HTTP is allowed only for local development.
var allowInsecureHttp = config.GetValue("AdminPortal:AllowInsecureHttp", false);
var cookiePolicy = allowInsecureHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

builder.Services.AddLicensingData(config);
builder.Services.AddDataProtection().PersistKeysToDbContext<LicensingDbContext>().SetApplicationName("DownholeStudio.AdminPortal");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/Login";
    o.Cookie.Name = "dhs_admin";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = cookiePolicy;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "dhs_af";
    o.Cookie.SecurePolicy = cookiePolicy;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.HttpOnly = true;
});
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (config.GetValue("ForwardedHeaders:TrustAllProxies", false))
    {
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }
});

var app = builder.Build();

if (config.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LicensingDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    if (!ctx.Request.IsHttps && !allowInsecureHttp)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("The Downhole Studio admin portal is only available over HTTPS. Open it through the TLS reverse proxy.");
        return;
    }

    await next();
});
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; frame-ancestors 'none'; form-action 'self'";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

await app.RunAsync();
return 0;

namespace DownholeStudio.AdminPortal
{
    /// <summary>Marker type identifying this assembly's entry point (used by integration tests).</summary>
    public sealed class AdminPortalEntryPoint;

    /// <summary>Command-line administration.</summary>
    public static class AdminCommands
    {
        public static async Task<int> CreateAdminAsync(string[] args)
        {
            string? email = null;
            var fromStdin = false;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--email" && i + 1 < args.Length)
                {
                    email = args[++i];
                }
                else if (args[i] == "--password-stdin")
                {
                    fromStdin = true;
                }
            }

            if (email is null || !fromStdin)
            {
                await Console.Error.WriteLineAsync("Usage: create-admin --email <address> --password-stdin   (the password is read from standard input)");
                return 2;
            }

            var password = (await Console.In.ReadLineAsync())?.TrimEnd('\r', '\n') ?? "";
            var builder = WebApplication.CreateBuilder([]);
            builder.Services.AddLicensingData(builder.Configuration);
            await using var app = builder.Build();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
            await db.Database.MigrateAsync();
            try
            {
                var user = await scope.ServiceProvider.GetRequiredService<AdminAccountService>().CreateAsync(email, password, null);
                Console.WriteLine($"Administrator {user.Email} created.");
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                return 1;
            }
        }
    }
}
