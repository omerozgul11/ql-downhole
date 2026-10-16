using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DownholeStudio.Server.Data;

/// <summary>A rendered e-mail.</summary>
public sealed record EmailContent(string Template, string Subject, string Text, string Html);

/// <summary>Transactional e-mail templates (plain text + HTML). All user-supplied values are HTML-encoded.</summary>
public sealed class EmailTemplates(IOptions<EmailContentOptions> options)
{
    private EmailContentOptions O => options.Value;

    public EmailContent PurchaseConfirmation(string name, string product, string edition, int seats, long amountMinor, string currency) => Build(
        "purchase-confirmation", $"Your {product} purchase is confirmed",
        $"Hello {name},",
        [
            $"Thank you for purchasing {product} {edition} ({seats} computer{(seats == 1 ? "" : "s")}).",
            $"Amount: {amountMinor / 100.0:0.00} {currency.ToUpperInvariant()}.",
            "Your license key follows in a separate message together with activation instructions.",
            "No payment-card details are stored by QuakeLogic; your payment was processed by our payment provider.",
        ]);

    public EmailContent LicenseKey(string name, string product, string edition, string licenseKey, int seats, DateTimeOffset? expires, DateTimeOffset? maintenance) => Build(
        "license-key", $"Your {product} license key",
        $"Hello {name},",
        [
            $"Here is your {product} {edition} license key:",
            $"KEY::{licenseKey}",
            $"It can be activated on {seats} computer{(seats == 1 ? "" : "s")} at a time." + (expires is { } e ? $" The license is valid until {e:MMMM d, yyyy}." : " The license does not expire.")
                + (maintenance is { } m ? $" Updates are included until {m:MMMM d, yyyy}." : ""),
            "To activate:",
            $"1. Download and install {product} from {O.DownloadUrl}.",
            "2. Start the application and choose Activate License (or Settings → License).",
            "3. Enter this e-mail address and the license key, then select Activate.",
            "An internet connection is needed for activation. Afterwards the software works offline for up to 30 days between online checks.",
            "To move the license to another computer, use Settings → License → Deactivate Computer on the old one first.",
            "Keep this key safe. QuakeLogic stores only a one-way fingerprint of it and cannot resend the same key.",
        ]);

    public EmailContent TrialReminder(string product, DateTimeOffset trialEnd) => Build(
        "trial-reminder", $"Your {product} trial ends {trialEnd:MMMM d}",
        "Hello,",
        [
            $"Your 14-day {product} trial on this computer ends on {trialEnd:MMMM d, yyyy}.",
            "Your projects stay on your computer and remain readable after the trial ends.",
            $"To keep editing and reporting, purchase a license at {O.DownloadUrl} and activate it in Settings → License.",
        ]);

    public EmailContent RenewalReminder(string name, string product, DateTimeOffset expires) => Build(
        "license-renewal", $"Your {product} license expires on {expires:MMMM d, yyyy}",
        $"Hello {name},",
        [
            $"Your {product} license expires on {expires:MMMM d, yyyy}.",
            $"Renew before then to continue without interruption: {O.DownloadUrl}.",
            "Your projects remain readable after expiry; editing requires an active license.",
        ]);

    public EmailContent DeviceDeactivated(string name, string product, string deviceLabel, DateTimeOffset when) => Build(
        "device-deactivated", $"{product} was deactivated on {deviceLabel}",
        $"Hello {name},",
        [
            $"{product} was deactivated on \"{deviceLabel}\" on {when:MMMM d, yyyy 'at' HH:mm} UTC.",
            "The license seat is free and can be activated on another computer.",
            $"If you did not do this, contact {O.SupportEmail}.",
        ]);

    private EmailContent Build(string template, string subject, string greeting, IReadOnlyList<string> paragraphs)
    {
        var footer = $"{O.CompanyName} · {O.SupportEmail} · Privacy: {O.PrivacyUrl}";
        var text = greeting + "\n\n" + string.Join("\n\n", paragraphs.Select(p => p.StartsWith("KEY::", StringComparison.Ordinal) ? "    " + p[5..] : p))
            + "\n\nRegards,\nQuakeLogic Licensing\n\n" + footer + "\n";
        var body = string.Join("", paragraphs.Select(p => p.StartsWith("KEY::", StringComparison.Ordinal)
            ? $"<p style=\"font-family:Consolas,Menlo,monospace;font-size:20px;letter-spacing:1px;background:#F5F6F8;border:1px solid #E1E4EA;border-radius:6px;padding:14px 18px;color:#0B0F18\">{E(p[5..])}</p>"
            : $"<p style=\"margin:0 0 14px\">{E(p)}</p>"));
        var html = $"""
            <!doctype html><html><body style="margin:0;background:#F5F6F8;font-family:Inter,Segoe UI,Arial,sans-serif;color:#1F2430">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px">
            <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="background:#FFFFFF;border-radius:10px;overflow:hidden">
            <tr><td style="background:#0B0F18;padding:18px 28px;color:#FFFFFF;font-size:18px;font-weight:600">Downhole Studio <span style="color:#F26522">●</span> QuakeLogic</td></tr>
            <tr><td style="padding:28px;font-size:15px;line-height:1.55"><p style="margin:0 0 14px">{E(greeting)}</p>{body}<p style="margin:18px 0 0">Regards,<br>QuakeLogic Licensing</p></td></tr>
            <tr><td style="padding:16px 28px;background:#F5F6F8;color:#6B7280;font-size:12px">{E(footer)}</td></tr>
            </table></td></tr></table></body></html>
            """;
        return new EmailContent(template, subject, text, html);
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
}

/// <summary>Sends an e-mail immediately.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>SMTP delivery with MailKit.</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Host))
        {
            throw new InvalidOperationException("SMTP is not configured (Smtp:Host).");
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.From));
        mime.To.Add(MailboxAddress.Parse(message.ToAddress));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port, o.StartTls ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.None, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(o.User))
        {
            await client.AuthenticateAsync(o.User, o.Password ?? "", ct).ConfigureAwait(false);
        }

        await client.SendAsync(mime, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
    }
}

/// <summary>Queues e-mails in the outbox table (sent by <see cref="EmailDispatchWorker"/>).</summary>
public sealed class EmailOutbox(LicensingDbContext db, TimeProvider time)
{
    /// <summary>Adds the message to the context; the caller saves changes in its own transaction.</summary>
    public EmailMessage Queue(string to, EmailContent content, Guid? licenseId = null)
    {
        var now = time.GetUtcNow();
        var message = new EmailMessage
        {
            Id = Guid.NewGuid(), ToAddress = to, Subject = content.Subject, TextBody = content.Text, HtmlBody = content.Html, Template = content.Template,
            LicenseId = licenseId, CreatedAt = now, NextAttemptAt = now,
        };
        db.EmailOutbox.Add(message);
        return message;
    }
}

/// <summary>Delivers queued e-mails with exponential back-off (up to 10 attempts).</summary>
public sealed class EmailDispatchWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<EmailDispatchWorker> log) : BackgroundService
{
    public const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "E-mail dispatch cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends every due message once. Returns the number sent.</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var now = time.GetUtcNow();
        var due = await db.EmailOutbox.Where(m => m.SentAt == null && m.Attempts < MaxAttempts && m.NextAttemptAt <= now)
            .OrderBy(m => m.CreatedAt).Take(25).ToListAsync(ct).ConfigureAwait(false);
        var sent = 0;
        foreach (var m in due)
        {
            m.Attempts++;
            try
            {
                await sender.SendAsync(m, ct).ConfigureAwait(false);
                m.SentAt = time.GetUtcNow();
                m.LastError = null;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                m.NextAttemptAt = now + TimeSpan.FromMinutes(Math.Pow(2, Math.Min(m.Attempts, 10)));
                log.LogWarning("E-mail {Template} {Id} failed (attempt {Attempt}): {Error}", m.Template, m.Id, m.Attempts, m.LastError);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return sent;
    }
}

/// <summary>Queues trial-ending (3 days) and licence-renewal (30 days) reminders, once each.</summary>
public sealed class ReminderWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<ReminderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Reminder scan failed");
            }

            await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<EmailOutbox>();
        var templates = scope.ServiceProvider.GetRequiredService<EmailTemplates>();
        var now = time.GetUtcNow();
        var count = 0;
        var trialLimit = now.AddDays(3);
        var trials = await db.TrialDevices.Where(t => t.Email != null && t.ReminderSentAt == null && !t.Blocked && t.TrialEnd > now && t.TrialEnd <= trialLimit)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in trials)
        {
            var product = await db.LicenseProducts.Where(p => p.Id == t.Product).Select(p => p.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? t.Product;
            outbox.Queue(t.Email!, templates.TrialReminder(product, t.TrialEnd));
            t.ReminderSentAt = now;
            count++;
        }

        var renewLimit = now.AddDays(30);
        var licenses = await db.Licenses.Include(l => l.Customer)
            .Where(l => l.Status == LicenseStatusCode.Active && l.RenewalReminderSentAt == null && l.ExpiresAt != null && l.ExpiresAt > now && l.ExpiresAt <= renewLimit)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var l in licenses)
        {
            var product = await db.LicenseProducts.Where(p => p.Id == l.Product).Select(p => p.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? l.Product;
            outbox.Queue(l.Customer!.Email, templates.RenewalReminder(l.Customer.Name ?? l.Customer.Email, product, l.ExpiresAt!.Value), l.Id);
            l.RenewalReminderSentAt = now;
            count++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return count;
    }
}
