using System.Text.Json;
using System.Threading.RateLimiting;
using DownholeStudio.LicenseServer;
using DownholeStudio.Licensing;
using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddLicensingData(config);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddProblemDetails();
if (config.GetValue("Workers:Enabled", true))
{
    builder.Services.AddHostedService<EmailDispatchWorker>();
    builder.Services.AddHostedService<ReminderWorker>();
}

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (config.GetValue("ForwardedHeaders:TrustAllProxies", false))
    {
        // Behind a reverse proxy on a private container network (see docker-compose.yml).
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }
});

var permits = config.GetValue("RateLimiting:PermitsPerMinute", 120);
var strictPermits = config.GetValue("RateLimiting:StrictPermitsPerMinute", 20);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) => await ctx.HttpContext.Response.WriteAsJsonAsync(new LicenseResponse
    {
        Success = false, ErrorCode = LicenseErrorCodes.RateLimited, ServerTime = DateTimeOffset.UtcNow,
        Message = "Too many licensing requests from this network. Wait a few minutes and try again.",
    }, ct);
    o.AddPolicy(RateLimits.Api, ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx) ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy(RateLimits.Strict, ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx) ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = strictPermits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

// Fail fast when signing keys or the key pepper are missing, rather than on the first customer request.
_ = app.Services.GetRequiredService<TokenIssuer>();
_ = app.Services.GetRequiredService<UpdateSigner>();
if (string.IsNullOrEmpty(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerLicensingOptions>>().Value.KeyPepper))
{
    throw new InvalidOperationException("LICENSING_KEY_PEPPER (Licensing:KeyPepper) must be configured.");
}

if (config.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LicensingDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var error = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LicenseServer").LogError(error, "Unhandled error on {Path}", ctx.Request.Path);
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new LicenseResponse
    {
        Success = false, ErrorCode = LicenseErrorCodes.ServerError, ServerTime = DateTimeOffset.UtcNow,
        Message = "The licensing service had a temporary problem. Try again in a few minutes.",
    });
}));
if (!app.Environment.IsDevelopment() && config.GetValue("Https:Redirect", true))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRateLimiter();

var api = app.MapGroup("/api/v1").RequireRateLimiting(RateLimits.Api);
api.MapPost("/trials/start", (TrialStartRequest request, LicenseService svc, HttpContext ctx, CancellationToken ct) =>
    svc.StartTrialAsync(request, ClientIp(ctx), ct)).RequireRateLimiting(RateLimits.Strict);
api.MapPost("/licenses/activate", Activate).RequireRateLimiting(RateLimits.Strict);
api.MapPost("/licenses/validate", (ValidationRequest request, LicenseService svc, HttpContext ctx, CancellationToken ct) =>
    svc.ValidateAsync(request, ClientIp(ctx), ct));
api.MapPost("/licenses/deactivate", (DeactivationRequest request, LicenseService svc, HttpContext ctx, CancellationToken ct) =>
    svc.DeactivateAsync(request, ClientIp(ctx), ct)).RequireRateLimiting(RateLimits.Strict);
api.MapGet("/updates/latest", async (string? product, string? channel, string? current, UpdateService updates, CancellationToken ct) =>
{
    var envelope = await updates.LatestAsync(product ?? ProductIds.DownholeStudio, channel ?? "stable", current, ct);
    return envelope is null ? Results.NoContent() : Results.Ok(envelope);
});
api.MapPost("/payments/stripe/webhook", async (HttpRequest request, StripePaymentProvider stripe, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var payload = await reader.ReadToEndAsync(ct);
    var result = await stripe.HandleWebhookAsync(payload, request.Headers["Stripe-Signature"].ToString(), ct);
    return result.Accepted ? Results.Ok(new { received = true, message = result.Message }) : Results.BadRequest(new { received = false, message = result.Message });
}).DisableRateLimiting();

// Alias using the wording of the product specification.
app.MapPost("/api/licenses/activate", Activate).RequireRateLimiting(RateLimits.Strict);

app.MapGet("/health", async (LicensingDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.Json(new { status = "database unavailable" }, statusCode: 503));
app.MapGet("/", () => Results.Text("QuakeLogic Downhole Studio licensing service"));

await app.RunAsync();

static Task<LicenseResponse> Activate(ActivationRequest request, LicenseService svc, HttpContext ctx, CancellationToken ct) =>
    svc.ActivateAsync(request, ClientIp(ctx), ct);

static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

namespace DownholeStudio.LicenseServer
{
    /// <summary>Rate-limit policy names.</summary>
    public static class RateLimits
    {
        public const string Api = "api";
        public const string Strict = "strict";
    }

    /// <summary>Marker type identifying this assembly's entry point (used by integration tests).</summary>
    public sealed class LicenseServerEntryPoint;
}
