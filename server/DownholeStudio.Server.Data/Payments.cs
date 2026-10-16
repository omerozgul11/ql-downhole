using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownholeStudio.Server.Data;

/// <summary>Outcome of a payment webhook.</summary>
/// <param name="Accepted">The request was authentic (return 200); false means reject (400).</param>
/// <param name="Message">Short description for logs and the response body.</param>
/// <param name="LicenseId">Licence created by this event, if any.</param>
public sealed record WebhookResult(bool Accepted, string Message, Guid? LicenseId = null);

/// <summary>A payment provider integration. Replace or add providers without touching licensing logic.</summary>
public interface IPaymentProvider
{
    string Name { get; }

    Task<WebhookResult> HandleWebhookAsync(string payload, string? signatureHeader, CancellationToken ct);
}

/// <summary>Stripe webhook signature verification ("Stripe-Signature: t=…,v1=…").</summary>
public static class StripeSignature
{
    /// <summary>
    /// Verifies HMAC-SHA256(secret, "&lt;t&gt;.&lt;payload&gt;") against every v1 signature with a
    /// constant-time comparison, and rejects timestamps outside the tolerance (replay protection).
    /// </summary>
    public static bool Verify(string payload, string? header, string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        long? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            var key = kv[0].Trim();
            var value = kv[1].Trim();
            if (key == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
            }
            else if (key == "v1")
            {
                try
                {
                    signatures.Add(Convert.FromHexString(value));
                }
                catch (FormatException)
                {
                    // Ignore malformed signature entries.
                }
            }
        }

        if (timestamp is null || signatures.Count == 0)
        {
            return false;
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp.Value) > tolerance.TotalSeconds)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp.Value}.{payload}"));
        return signatures.Any(s => CryptographicOperations.FixedTimeEquals(s, expected));
    }

    /// <summary>Builds a header value (used by tests and local tooling).</summary>
    public static string Sign(string payload, string secret, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds();
        var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{payload}")));
        return $"t={t},v1={sig}";
    }
}

/// <summary>
/// Stripe Checkout integration: on checkout.session.completed (paid) a customer and licence are
/// created and the Purchase Confirmation and License Key e-mails are queued. Processing is idempotent
/// per event id. No card data is ever received or stored.
/// </summary>
public sealed class StripePaymentProvider(
    LicensingDbContext db,
    AdminService admin,
    EmailOutbox outbox,
    EmailTemplates templates,
    IOptions<StripeOptions> options,
    TimeProvider time,
    ILogger<StripePaymentProvider> log) : IPaymentProvider
{
    public string Name => "stripe";

    public async Task<WebhookResult> HandleWebhookAsync(string payload, string? signatureHeader, CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        if (string.IsNullOrEmpty(o.WebhookSecret))
        {
            log.LogError("Stripe webhook received but Stripe:WebhookSecret is not configured");
            return new WebhookResult(false, "Webhook secret not configured.");
        }

        if (!StripeSignature.Verify(payload, signatureHeader, o.WebhookSecret, now, TimeSpan.FromSeconds(o.ToleranceSeconds)))
        {
            log.LogWarning("Rejected Stripe webhook with an invalid signature");
            return new WebhookResult(false, "Invalid signature.");
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var eventId = Str(root, "id");
        var type = Str(root, "type") ?? "";
        if (eventId is null)
        {
            return new WebhookResult(false, "Event id missing.");
        }

        // One transaction per event: customer, licence, payment, e-mails and the idempotency record
        // commit together, so a failed delivery can be retried by Stripe without duplicating licences.
        var strategy = db.Database.CreateExecutionStrategy();
        var payloadRoot = root.Clone();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            if (await db.WebhookEvents.AnyAsync(e => e.Id == eventId, ct))
            {
                return new WebhookResult(true, "Duplicate event ignored.");
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var record = new WebhookEvent { Id = eventId, Provider = Name, Type = type, ReceivedAt = now };
            db.WebhookEvents.Add(record);
            Guid? licenseId = null;
            if (type == "checkout.session.completed" && payloadRoot.TryGetProperty("data", out var data) && data.TryGetProperty("object", out var session))
            {
                var paymentStatus = Str(session, "payment_status");
                if (paymentStatus is "paid" or "no_payment_required")
                {
                    licenseId = await FulfilAsync(session, now, o, ct);
                    record.Result = $"license {licenseId}";
                }
                else
                {
                    record.Result = $"ignored: payment_status={paymentStatus}";
                }
            }
            else
            {
                record.Result = "ignored event type";
            }

            record.ProcessedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new WebhookResult(true, record.Result ?? "ok", licenseId);
        });
    }

    private async Task<Guid> FulfilAsync(JsonElement session, DateTimeOffset now, StripeOptions o, CancellationToken ct)
    {
        var details = session.TryGetProperty("customer_details", out var cd) ? cd : default;
        var email = (details.ValueKind == JsonValueKind.Object ? Str(details, "email") : null) ?? Str(session, "customer_email")
            ?? throw new InvalidOperationException("The checkout session has no customer e-mail.");
        var name = details.ValueKind == JsonValueKind.Object ? Str(details, "name") : null;
        var metadata = session.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object ? md : default;
        string? Meta(string key) => metadata.ValueKind == JsonValueKind.Object ? Str(metadata, key) : null;
        var product = Meta("product") ?? o.DefaultProduct;
        var edition = Meta("edition") ?? o.DefaultEdition;
        var seats = int.TryParse(Meta("seats"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;
        var company = Meta("company");
        var actor = AdminActor.System("stripe");

        var normalized = LicenseService.NormalizeEmail(email)!;
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Email == normalized, ct);
        var customer = await admin.UpsertCustomerAsync(actor, existing?.Id, normalized, existing?.Name ?? name, existing?.Company ?? company, existing?.Notes, ct);
        if (Str(session, "customer") is { } stripeCustomer)
        {
            customer.ExternalCustomerId = stripeCustomer;
        }

        DateTimeOffset? expires = o.TermMonths is { } months ? now.AddMonths(months) : null;
        var issued = await admin.CreateLicenseAsync(actor, customer.Id, product, edition, seats, expires, now.AddMonths(o.MaintenanceMonths),
            $"Stripe checkout {Str(session, "id")}", sendEmail: false, ct);
        var amount = session.TryGetProperty("amount_total", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : 0;
        var currency = Str(session, "currency") ?? "usd";
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseId = issued.License.Id, Provider = Name, ProviderPaymentId = Str(session, "payment_intent"),
            ProviderSessionId = Str(session, "id"), Amount = amount, Currency = currency, Status = "paid", CreatedAt = now,
        });
        var productName = await db.LicenseProducts.Where(p => p.Id == product).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? product;
        var greeting = customer.Name ?? customer.Email;
        outbox.Queue(customer.Email, templates.PurchaseConfirmation(greeting, productName, edition, seats, amount, currency), issued.License.Id);
        outbox.Queue(customer.Email, templates.LicenseKey(greeting, productName, edition, issued.LicenseKey, seats, expires, issued.License.MaintenanceUntil), issued.License.Id);
        log.LogInformation("Stripe checkout fulfilled: license {License} for customer {Customer}", issued.License.Id, customer.Id);
        return issued.License.Id;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
